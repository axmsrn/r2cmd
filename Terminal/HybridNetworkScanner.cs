using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace R2Cmd.Providers;

// Class must be partial to support [LibraryImport] source generation
public static partial class HybridNetworkScanner
{
    // --- PROMISE CACHING ---
    private static Task<List<string>>? _cachedTask;
    private static DateTime _cacheExpiration = DateTime.MinValue;

    // Use .NET 10 hardware-optimized lock
    private static readonly System.Threading.Lock _syncRoot = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public static void InvalidateCache()
    {
        lock (_syncRoot)
        {
            _cacheExpiration = DateTime.MinValue;
            _cachedTask = null;
        }
    }

    public static Task<List<string>> ScanNetworkAsync(CancellationToken token = default)
        => ScanNetworkAsync(forceRefresh: false, token);

    public static async Task<List<string>> ScanNetworkAsync(bool forceRefresh, CancellationToken token = default)
    {
        Task<List<string>> scanTask;

        lock (_syncRoot)
        {
            if (forceRefresh || _cachedTask == null || DateTime.UtcNow > _cacheExpiration)
            {
                // The task runs in the background. We cache the promise itself, not just the result.
                _cachedTask = PerformBackgroundScanAsync();
                _cacheExpiration = DateTime.UtcNow.Add(CacheTtl);
            }
            scanTask = _cachedTask;
        }

        // Modern UI cancellation pattern without cancelling the background work.
        // Uses native .NET WaitAsync to avoid unnecessary TaskCompletionSource allocations.
        return await scanTask.WaitAsync(token);
    }

    private static async Task<List<string>> PerformBackgroundScanAsync()
    {
        var foundHosts = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // Pass CancellationToken.None so the scan is not cancelled when leaving the folder
        var taskCache = Task.Run(() => ScanWindowsCache(foundHosts));
        var taskActive = ScanActiveSubnetsAsync(foundHosts, CancellationToken.None);

        await Task.WhenAll(taskCache, taskActive);

        return foundHosts.Keys
            .OrderBy(name => name.Contains('.') ? 1 : 0)
            .ThenBy(name => name)
            .ToList();
    }

    // =========================================================================
    // Windows Browser Cache (NetServerEnum)
    // =========================================================================
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVER_INFO_100
    {
        public int sv100_platform_id;
        public string sv100_name;
    }

    [LibraryImport("Netapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int NetServerEnum(
        string? servername,
        int level,
        out IntPtr bufptr,
        int prefmaxlen,
        out int entriesread,
        out int totalentries,
        uint servertype,
        string? domain,
        IntPtr resume_handle);

    [LibraryImport("Netapi32.dll")]
    private static partial int NetApiBufferFree(IntPtr buffer);

    private static void ScanWindowsCache(ConcurrentDictionary<string, byte> foundHosts)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int result = NetServerEnum(null, 100, out buffer, -1,
                out int entriesRead, out int _,
                0x00000001 | 0x00000002, null, IntPtr.Zero);

            if (result == 0 && buffer != IntPtr.Zero)
            {
                IntPtr currentPtr = buffer;
                int structSize = Marshal.SizeOf<SERVER_INFO_100>();

                for (int i = 0; i < entriesRead; i++)
                {
                    var info = Marshal.PtrToStructure<SERVER_INFO_100>(currentPtr);
                    if (!string.IsNullOrWhiteSpace(info.sv100_name))
                        foundHosts.TryAdd(info.sv100_name.ToUpperInvariant(), 1);

                    currentPtr = IntPtr.Add(currentPtr, structSize);
                }
            }
        }
        catch { }
        finally
        {
            if (buffer != IntPtr.Zero)
                NetApiBufferFree(buffer);
        }
    }

    // =========================================================================
    // Active Network Scanner (ARP for LAN / ICMP for VPN)
    // =========================================================================
    [LibraryImport("iphlpapi.dll")]
    private static partial int SendARP(int destIp, int srcIP, byte[] pMacAddr, ref uint phyAddrLen);

    private static async Task ScanActiveSubnetsAsync(ConcurrentDictionary<string, byte> foundHosts, CancellationToken token)
    {
        var activeIps = new ConcurrentBag<string>();
        var sweepTasks = new List<Task>();
        var scannedRanges = new HashSet<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            string desc = ni.Description.ToLowerInvariant();
            bool isVpn = ni.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                         ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                         desc.Contains("vpn") || desc.Contains("tap") ||
                         desc.Contains("tun") || desc.Contains("wireguard");

            var ipProps = ni.GetIPProperties();

            // Skip physical adapters without a gateway, but allow VPNs
            if (ipProps.GatewayAddresses.Count == 0 && !isVpn)
                continue;

            foreach (var ipInfo in ipProps.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var ip = ipInfo.Address;
                var mask = ipInfo.IPv4Mask;

                if (mask == null || mask.GetAddressBytes().Length != 4)
                    continue;

                // Generate the list of IPs to scan for this subnet
                var hostsToScan = GetHostsToScan(ip, mask);

                // Avoid scanning the same range multiple times
                string rangeKey = hostsToScan.Count > 0 ? hostsToScan[0] + "-" + hostsToScan[^1] : "";
                if (string.IsNullOrEmpty(rangeKey) || !scannedRanges.Add(rangeKey))
                    continue;

                sweepTasks.Add(SweepHostsAsync(hostsToScan, activeIps, isVpn, token));
            }
        }

        await Task.WhenAll(sweepTasks);

        // Name resolution (NetBIOS first, then DNS)
        using var dnsThrottle = new SemaphoreSlim(8, 8);
        var resolveTasks = new List<Task>();

        foreach (var ip in activeIps)
        {
            resolveTasks.Add(Task.Run(async () =>
            {
                await dnsThrottle.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;
                    string hostName = await ResolveHostnameAsync(ip, token);
                    foundHosts.TryAdd(hostName, 1);
                }
                finally
                {
                    dnsThrottle.Release();
                }
            }, token));
        }

        await Task.WhenAll(resolveTasks);
    }

    /// <summary>
    /// Returns the list of host IPs that should be scanned for the given address + mask.
    /// - For /24 and smaller → full range (excluding network and broadcast)
    /// - For larger subnets → only the /24 segment that contains the local IP
    ///   (prevents scanning tens of thousands of addresses on big VPN networks)
    /// </summary>
    private static List<string> GetHostsToScan(IPAddress ip, IPAddress mask)
    {
        byte[] ipBytes = ip.GetAddressBytes();
        byte[] maskBytes = mask.GetAddressBytes();

        // Calculate network and broadcast addresses
        byte[] networkBytes = new byte[4];
        byte[] broadcastBytes = new byte[4];

        for (int i = 0; i < 4; i++)
        {
            networkBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
            broadcastBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 0xFF));
        }

        // Count how many host bits we have
        int hostBits = 0;
        foreach (byte b in maskBytes)
        {
            for (int bit = 0; bit < 8; bit++)
            {
                if ((b & (1 << (7 - bit))) == 0)
                    hostBits++;
            }
        }

        var result = new List<string>();

        // Safety: if subnet is larger than /24 (more than 254 hosts) —
        // only scan the current /24 segment to keep the scan fast and safe.
        if (hostBits > 8)
        {
            string baseIp = $"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}";
            for (int i = 1; i <= 254; i++)
                result.Add($"{baseIp}.{i}");
            return result;
        }

        // Normal case (/24 or smaller): enumerate all usable hosts
        uint start = BytesToUInt(networkBytes);
        uint end = BytesToUInt(broadcastBytes);

        // Exclude network and broadcast addresses
        for (uint addr = start + 1; addr < end; addr++)
        {
            result.Add(UIntToIpString(addr));
        }

        return result;
    }

    private static uint BytesToUInt(byte[] bytes)
    {
        // Network byte order → uint
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static string UIntToIpString(uint value)
    {
        return $"{(value >> 24) & 0xFF}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}";
    }

    private static async Task SweepHostsAsync(
        List<string> hosts,
        ConcurrentBag<string> activeIps,
        bool useIcmpPing,
        CancellationToken token)
    {
        // PROTECTION FROM THREADPOOL STARVATION: Reduced from 48 to 16.
        // This ensures the application stays highly responsive even while scanning a huge network.
        using var throttle = new SemaphoreSlim(16, 16);
        var tasks = new List<Task>(hosts.Count);

        foreach (string targetIp in hosts)
        {
            if (token.IsCancellationRequested) break;

            tasks.Add(Task.Run(async () =>
            {
                await throttle.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;

                    if (useIcmpPing)
                    {
                        // VPN / Tunnel → ICMP Echo
                        using var ping = new Ping();
                        var reply = await ping.SendPingAsync(targetIp, 250);
                        if (reply.Status == IPStatus.Success)
                            activeIps.Add(targetIp);
                    }
                    else
                    {
                        // Physical LAN → ARP (works even if ICMP is blocked)
                        var parsedIp = IPAddress.Parse(targetIp);
                        int destIp = BitConverter.ToInt32(parsedIp.GetAddressBytes(), 0);
                        byte[] macAddr = new byte[6];
                        uint macAddrLen = 6;

                        if (SendARP(destIp, 0, macAddr, ref macAddrLen) == 0)
                            activeIps.Add(targetIp);
                    }
                }
                catch { }
                finally
                {
                    throttle.Release();
                }
            }, token));
        }

        await Task.WhenAll(tasks);
    }

    // =========================================================================
    // Hostname resolution
    // =========================================================================
    private static async Task<string> ResolveHostnameAsync(string ip, CancellationToken token)
    {
        // 1. NetBIOS (best for local Windows / many IoT devices)
        string? nbName = await GetNetBiosNameAsync(ip, token);
        if (!string.IsNullOrWhiteSpace(nbName))
            return nbName.ToUpperInvariant();

        // 2. DNS fallback
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(1500);

            var entry = await Dns.GetHostEntryAsync(ip, cts.Token);
            string host = entry.HostName;

            if (!string.IsNullOrWhiteSpace(host) &&
                !host.Equals(ip, StringComparison.OrdinalIgnoreCase))
            {
                int dot = host.IndexOf('.');
                return (dot > 0 ? host[..dot] : host).ToUpperInvariant();
            }
        }
        catch { }

        return ip;
    }

    private static async Task<string?> GetNetBiosNameAsync(string ip, CancellationToken token)
    {
        byte[] request =
        {
            0xA2, 0x48, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x20, 0x43, 0x4B, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
            0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
            0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x00, 0x00, 0x21, 0x00, 0x01
        };

        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 1100;
            udp.Client.SendTimeout = 1100;

            var endpoint = new IPEndPoint(IPAddress.Parse(ip), 137);
            await udp.SendAsync(request, request.Length, endpoint);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(1200);

            var result = await udp.ReceiveAsync(cts.Token);
            byte[] data = result.Buffer;

            if (data.Length < 57) return null;

            int nameCount = data[56];
            int offset = 57;

            for (int i = 0; i < nameCount && offset + 18 <= data.Length; i++)
            {
                string name = Encoding.ASCII.GetString(data, offset, 15).Trim('\0', ' ');
                byte nameType = data[offset + 15];

                if ((nameType == 0x00 || nameType == 0x20) &&
                    !string.IsNullOrWhiteSpace(name) &&
                    !name.StartsWith("__"))
                {
                    return name;
                }

                offset += 18;
            }
        }
        catch { }

        return null;
    }
}
