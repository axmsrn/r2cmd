using System.Buffers;
using System.IO;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace R2Cmd.Providers;

public sealed class SshFileSystemProvider : IFileSystemProvider
{
    // =========================================================================
    // One of these per session name, created once and kept for the lifetime of
    // the application. Only Client is swapped on a reconnect: the Gate has to
    // outlive the socket, otherwise the mutual exclusion it provides disappears
    // exactly when the link is unstable and calls start overlapping.
    // =========================================================================
    private sealed class SshConnection
    {
        public SftpClient Client;
        public SshSession Session;
        public readonly SemaphoreSlim Gate = new(1, 1);

        // How many callers are waiting for the Gate right now. A background
        // size scan checks it and steps aside: SemaphoreSlim is not fair, and
        // a loop that releases and immediately re-takes the Gate would win
        // every time against a pane that is waiting for a listing.
        public int Waiters;

        public void Enter(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Waiters);
            try { Gate.Wait(ct); }
            finally { Interlocked.Decrement(ref Waiters); }
        }

        // When this session was last touched, and how many transfer streams are
        // still open on it. Both are read by the idle sweeper.
        public long LastUsedTicks;
        public int Leases;

        public SshConnection(SftpClient client, SshSession session)
        {
            Client = client;
            Session = session;
            Touch();
        }

        public void Touch() => Volatile.Write(ref LastUsedTicks, DateTime.UtcNow.Ticks);
    }

    // =========================================================================
    // A stream that keeps its connection out of the sweeper's reach.
    //
    // The Gate is released as soon as the stream is handed out, so a transfer
    // running for ten minutes leaves LastUsed looking stale. Without the lease
    // the idle sweeper would close the client in the middle of a copy.
    // =========================================================================
    private sealed class LeasedStream : Stream
    {
        private readonly Stream _inner;
        private readonly SshConnection _conn;
        private int _released;

        public LeasedStream(Stream inner, SshConnection conn)
        {
            _inner = inner;
            _conn = conn;
            Interlocked.Increment(ref conn.Leases);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.ReadAsync(buffer, offset, count, ct);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            _inner.ReadAsync(buffer, ct);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.WriteAsync(buffer, offset, count, ct);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            _inner.WriteAsync(buffer, ct);

        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                Interlocked.Decrement(ref _conn.Leases);
                _conn.Touch();
            }

            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static readonly Dictionary<string, SshConnection> _connections =
        new(StringComparer.OrdinalIgnoreCase);

    private const int TransferBufferSize = 81920;

    // SSH.NET's default is small enough to cap throughput on a fast link well
    // below what the connection can carry
    private const uint SftpBufferSize = 64 * 1024;

    public static void CloseConnection(string sessionName)
    {
        SshConnection? conn;
        lock (_connections)
        {
            if (!_connections.TryGetValue(sessionName, out conn)) return;
            _connections.Remove(sessionName);
        }

        conn.Enter();
        try
        {
            try { if (conn.Client.IsConnected) conn.Client.Disconnect(); } catch { }
            conn.Client.Dispose();
        }
        finally { conn.Gate.Release(); }
    }

    // =========================================================================
    // IDLE SESSIONS
    //
    // Leaving a remote folder does not disconnect. Re-authenticating on every
    // return costs seconds with a key and a prompt with a password, and no
    // established file manager works that way: WinSCP holds the session for as
    // long as its tab is open, FileZilla and Cyberduck pool connections and drop
    // them on inactivity.
    //
    // Holding one forever is not free either — it occupies an sshd session slot,
    // and the 30 second keepalive means NAT will never time the socket out.
    //
    // So: a session open in a pane is pinned and never touched; anything else is
    // closed once it has been idle for IdleTimeout with no transfer in flight.
    // =========================================================================
    public static TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    private static readonly HashSet<string> s_pinned = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Threading.Lock s_sweeperLock = new();
    private static Timer? s_sweeper;

    /// <summary>Sessions currently displayed in a pane. Never swept.</summary>
    public static void SetPinnedSessions(IEnumerable<string> names)
    {
        lock (s_pinned)
        {
            s_pinned.Clear();
            foreach (var name in names)
            {
                if (!string.IsNullOrEmpty(name)) s_pinned.Add(name);
            }
        }
    }

    private static void EnsureSweeper()
    {
        if (s_sweeper != null) return;

        lock (s_sweeperLock)
        {
            s_sweeper ??= new Timer(_ => SweepIdle(), null,
                TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }
    }

    private static void SweepIdle()
    {
        HashSet<string> pinned;
        lock (s_pinned) pinned = new HashSet<string>(s_pinned, StringComparer.OrdinalIgnoreCase);

        long cutoff = DateTime.UtcNow.Ticks - IdleTimeout.Ticks;
        var stale = new List<string>();

        lock (_connections)
        {
            foreach (var pair in _connections)
            {
                if (pinned.Contains(pair.Key)) continue;

                // A copy may still be streaming even though nothing touched the
                // client recently
                if (Volatile.Read(ref pair.Value.Leases) > 0) continue;

                if (Volatile.Read(ref pair.Value.LastUsedTicks) > cutoff) continue;

                stale.Add(pair.Key);
            }
        }

        foreach (string name in stale)
        {
            try { CloseConnection(name); } catch { }
        }
    }

    /// <summary>True when a live connection for this session already exists.</summary>
    public static bool IsSessionOpen(string sessionName)
    {
        lock (_connections)
            return _connections.TryGetValue(sessionName, out var conn) && IsLive(conn.Client);
    }

    // Session names with a connection currently held open
    public static List<string> ActiveSessions()
    {
        lock (_connections) return _connections.Keys.ToList();
    }

    public static void CloseAll()
    {
        List<SshConnection> all;
        lock (_connections)
        {
            all = _connections.Values.ToList();
            _connections.Clear();
        }

        foreach (var conn in all)
        {
            try { if (conn.Client.IsConnected) conn.Client.Disconnect(); } catch { }
            try { conn.Client.Dispose(); } catch { }
        }
    }

    public bool CanHandle(string path) =>
        path.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase);

    public Task<(List<FileEntry> Entries, string? Error)> ReadDirectoryAsync(string path, CancellationToken ct = default)
    {
        // The token is honoured at least around the round trips: leaving a slow
        // remote folder used to keep the listing running with nobody waiting
        return Task.Run(() =>
        {
            var entries = new List<FileEntry>();
            try
            {
                ct.ThrowIfCancellationRequested();

                var (sessionName, remotePath) = SplitSshPath(path);
                var (conn, error) = GetOrCreateConnection(sessionName);
                if (conn == null) return (entries, error);

                ct.ThrowIfCancellationRequested();

                conn.Enter(ct);
                try
                {
                    try
                    {
                        ListInto(conn.Client, remotePath, sessionName, entries);
                    }
                    catch (Exception ex) when (IsConnectionError(ex))
                    {
                        entries.Clear();
                        Reconnect(conn);
                        ListInto(conn.Client, remotePath, sessionName, entries);
                    }
                }
                finally { conn.Gate.Release(); }

                return (entries, (string?)null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return (entries, $"SSH Error: {ex.Message}");
            }
        }, ct);
    }

    public ulong? GetFreeSpace(string path) => null;

    public bool Exists(string path) => RemoteExists(path);

    public void CreateDirectory(string path) => RemoteCreateDirectory(path);

    public void CreateFile(string path)
    {
        var (session, remote) = SplitSshPath(path);
        WithClient<object?>(session, c => { c.Create(remote).Dispose(); return null; });
    }

    public void Rename(string oldPath, string newPath)
    {
        var (session, oldRemote) = SplitSshPath(oldPath);
        var (_, newRemote) = SplitSshPath(newPath);
        WithClient<object?>(session, c => { c.RenameFile(oldRemote, newRemote); return null; });
    }

    public string GetParentPath(string path)
    {
        string trimmed = path.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash > 0 ? trimmed.Substring(0, lastSlash + 1) : "/";
    }

    public string CombinePaths(string parent, string name)
    {
        return parent.EndsWith('/') ? parent + name : parent + "/" + name;
    }

    public static void DownloadToStream(string sshPath, Stream dest, CancellationToken ct, Action<int> onBytes)
    {
        var (session, remote) = SplitSshPath(sshPath);
        var conn = RequireConnection(session);

        conn.Enter(ct);
        try
        {
            Stream src = OpenReadWithRetry(conn, remote);
            using (src)
            {
                // Pooled: an 80 KB array per file lands on the large object heap
                byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
                try
                {
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dest.Write(buffer, 0, n);
                        onBytes(n);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        finally { conn.Gate.Release(); }
    }

    public static void UploadFromStream(Stream source, string sshPath, CancellationToken ct, Action<int> onBytes)
    {
        var (session, remote) = SplitSshPath(sshPath);
        var conn = RequireConnection(session);

        conn.Enter(ct);
        try
        {
            Stream dst = CreateWithRetry(conn, remote);
            using (dst)
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
                try
                {
                    int n;
                    while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, n);
                        onBytes(n);
                    }
                    dst.Flush();
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
        }
        finally { conn.Gate.Release(); }
    }

    public static bool RemoteExists(string sshPath)
    {
        var (session, remote) = SplitSshPath(sshPath);
        return WithClient(session, c => { try { return c.Exists(remote); } catch { return false; } });
    }

    public static bool RemoteIsDirectory(string sshPath)
    {
        var (session, remote) = SplitSshPath(sshPath);
        return WithClient(session, c => { try { return c.GetAttributes(remote).IsDirectory; } catch { return false; } });
    }

    // =========================================================================
    // Size and last write time of one remote entry, read with a single SFTP
    // stat. Remote folders have no watcher, so after the application itself
    // uploads a file (editor save) this is how its row gets the new values
    // without re-reading the whole directory. Local time, the same kind
    // ListInto puts into FileEntry.Modified.
    // =========================================================================
    public static (long Size, DateTime Modified)? RemoteStat(string sshPath)
    {
        var (session, remote) = SplitSshPath(sshPath);
        return WithClient<(long Size, DateTime Modified)?>(session, c =>
        {
            try
            {
                var attributes = c.GetAttributes(remote);
                return (attributes.IsDirectory ? 0 : attributes.Size, attributes.LastWriteTime);
            }
            catch
            {
                return null;
            }
        });
    }

    public static void RemoteCreateDirectory(string sshPath)
    {
        var (session, remote) = SplitSshPath(sshPath);
        WithClient<object?>(session, c => { CreateDirRecursive(c, remote); return null; });
    }

    public static List<(string Name, bool IsDir, long Size)> RemoteList(string sshPath)
    {
        var (session, remote) = SplitSshPath(sshPath);
        return WithClient(session, c =>
        {
            var list = new List<(string, bool, long)>();
            foreach (var f in c.ListDirectory(remote))
            {
                if (f.Name is "." or "..") continue;
                list.Add((f.Name, f.IsDirectory, f.IsDirectory ? 0 : f.Length));
            }
            return list;
        });
    }

    public static void RemoteDelete(string sshPath)
    {
        var (session, remote) = SplitSshPath(sshPath);
        WithClient<object?>(session, c => { DeleteRecursive(c, remote); return null; });
    }

    // =========================================================================
    // Size of a remote tree, with the connection lock taken PER DIRECTORY.
    //
    // The walk used to run inside one WithClient call, holding the session's
    // Gate for the whole tree: thousands of listings, often minutes. Anything
    // else on that session, above all the pane listing a folder, queued behind
    // it, so coming back to an SSH folder during a size scan showed nothing but
    // the loading dots until the scan finished.
    //
    // Now each listing takes the Gate on its own. Between two listings a pane
    // request gets its turn, so navigation waits for one listing at most.
    // A directory that cannot be read (permission denied) is skipped instead of
    // failing the whole count, the same as local scans do.
    // =========================================================================
    public static (int Files, long Bytes) RemoteSumTree(string sshPath, CancellationToken ct = default)
    {
        var (session, remote) = SplitSshPath(sshPath);

        // =====================================================================
        // First choice: count on the server. One command, one line back, the
        // server walks its own disk locally. The SFTP walk below needs a
        // network round trip per directory and is tens of times slower on a
        // large tree. It runs on its own SSH channel, so it never touches the
        // SFTP Gate either.
        //
        // Needs GNU find (-printf). Elsewhere (BusyBox, BSD) find prints
        // nothing and awk reports 0 files; zero is therefore never trusted and
        // falls through to the SFTP walk, which is cheap for a truly empty
        // folder anyway.
        // =====================================================================
        string quoted = remote.Replace("'", "'\\''");
        string sumCmd =
            $"find '{quoted}' -type f -printf '%s\\n' 2>/dev/null | " +
            "awk '{ n++; s += $1 } END { printf \"%d %.0f\\n\", n, s }'";

        string? summary = RunCommand(session, sumCmd, ct);
        var fields = summary?.Trim().Split(' ');
        if (fields is { Length: 2 } &&
            int.TryParse(fields[0], out int serverFiles) && serverFiles > 0 &&
            long.TryParse(fields[1], out long serverBytes))
        {
            return (serverFiles, serverBytes);
        }

        ct.ThrowIfCancellationRequested();

        var conn = RequireConnection(session);

        var root = WithClient(session, c => c.GetAttributes(remote));
        if (!root.IsDirectory) return (1, root.Size);

        int files = 0;
        long bytes = 0;

        var pending = new Stack<string>();
        pending.Push(remote);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            string dir = pending.Pop();

            // Anyone else waiting (a pane listing, F3, a copy) goes first
            while (Volatile.Read(ref conn.Waiters) > 0) Thread.Sleep(10);

            (List<string> Dirs, int Files, long Bytes) part;
            try
            {
                // One directory per lock. Counted into fresh locals: WithClient
                // may run the lambda a second time after a reconnect, and the
                // totals must not include a half-counted first attempt.
                part = WithClient(session, c =>
                {
                    var dirs = new List<string>();
                    int f = 0;
                    long b = 0;

                    foreach (var item in c.ListDirectory(dir))
                    {
                        if (item.Name is "." or "..") continue;

                        if (item.IsDirectory) dirs.Add(RemoteCombine(dir, item.Name));
                        else
                        {
                            f++;
                            b += item.Length;
                        }
                    }

                    return (dirs, f, b);
                });
            }
            catch (SftpPermissionDeniedException)
            {
                continue;
            }

            files += part.Files;
            bytes += part.Bytes;
            foreach (var sub in part.Dirs) pending.Push(sub);
        }

        return (files, bytes);
    }

    private static SshConnection RequireConnection(string sessionName)
    {
        var (conn, error) = GetOrCreateConnection(sessionName);
        if (conn == null) throw new IOException(error ?? "SSH connection failed.");
        return conn;
    }

    private static T WithClient<T>(string sessionName, Func<SftpClient, T> action)
    {
        var conn = RequireConnection(sessionName);
        conn.Enter();
        try
        {
            try { return action(conn.Client); }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                Reconnect(conn);
                return action(conn.Client);
            }
        }
        finally { conn.Gate.Release(); }
    }

    private static Stream OpenReadWithRetry(SshConnection conn, string remote)
    {
        try { return conn.Client.OpenRead(remote); }
        catch (Exception ex) when (IsConnectionError(ex)) { Reconnect(conn); return conn.Client.OpenRead(remote); }
    }

    private static Stream CreateWithRetry(SshConnection conn, string remote)
    {
        try { return conn.Client.Create(remote); }
        catch (Exception ex) when (IsConnectionError(ex)) { Reconnect(conn); return conn.Client.Create(remote); }
    }

    // Walks up to the first existing ancestor, then creates downwards. The old
    // version asked Exists up to three times per level, and each question is a
    // network round trip on a link where latency dominates.
    private static void CreateDirRecursive(SftpClient c, string remote)
    {
        if (string.IsNullOrEmpty(remote) || remote == "/") return;
        if (c.Exists(remote)) return;

        var missing = new List<string>();
        string current = remote;

        while (current != "/" && !string.IsNullOrEmpty(current) && !c.Exists(current))
        {
            missing.Add(current);
            current = RemoteParent(current);
        }

        for (int i = missing.Count - 1; i >= 0; i--)
        {
            try { c.CreateDirectory(missing[i]); }
            catch (SshException) { /* created by someone else in the meantime */ }
        }
    }

    private static void DeleteRecursive(SftpClient c, string remote)
    {
        var attrs = c.GetAttributes(remote);
        if (attrs.IsDirectory)
        {
            foreach (var f in c.ListDirectory(remote))
            {
                if (f.Name is "." or "..") continue;
                DeleteRecursive(c, RemoteCombine(remote, f.Name));
            }
            c.DeleteDirectory(remote);
        }
        else c.DeleteFile(remote);
    }

    private static (string Session, string Remote) SplitSshPath(string sshPath)
    {
        string rest = sshPath.Substring(6);
        int slash = rest.IndexOf('/');
        string session = slash < 0 ? rest : rest[..slash];
        string remote = slash < 0 ? "/" : rest[slash..];
        if (string.IsNullOrEmpty(remote)) remote = "/";
        return (session, remote);
    }

    private static string RemoteCombine(string dir, string name) =>
        dir.EndsWith('/') ? dir + name : dir + "/" + name;

    private static string RemoteParent(string remote)
    {
        string trimmed = remote.TrimEnd('/');
        int i = trimmed.LastIndexOf('/');
        return i <= 0 ? "/" : trimmed[..i];
    }

    // =========================================================================
    // Hands out the connection for a session name, opening it if needed.
    //
    // The SshConnection object is never replaced once created, only its Client
    // is. Building a fresh object here would give it a fresh Gate, and any
    // thread still holding the previous one would no longer be excluded from the
    // client — the failure mode is silent and only shows up on a flaky link.
    // =========================================================================
    private static (SshConnection? Conn, string? Error) GetOrCreateConnection(string sessionName)
    {
        lock (_connections)
        {
            if (_connections.TryGetValue(sessionName, out var existing) && IsLive(existing.Client))
            {
                existing.Touch();
                return (existing, null);
            }
        }

        var session = AppSettings.Load().SshSessions.FirstOrDefault(s =>
            s.Name.Equals(sessionName, StringComparison.OrdinalIgnoreCase) ||
            $"{s.Username}@{s.Host}".Equals(sessionName, StringComparison.OrdinalIgnoreCase));

        if (session == null) return (null, "SSH session not found in settings.");

        SftpClient client;
        try
        {
            client = ConnectSftp(session);
        }
        catch (Exception ex)
        {
            return (null, $"SSH connect failed: {ex.Message}");
        }

        lock (_connections)
        {
            if (_connections.TryGetValue(sessionName, out var current))
            {
                // Another thread got there first with a working client
                if (IsLive(current.Client))
                {
                    try { client.Disconnect(); } catch { }
                    try { client.Dispose(); } catch { }
                    return (current, null);
                }

                // Revive the existing object: same Gate, new client, and the
                // settings may have been edited since it was first opened
                var dead = current.Client;
                current.Client = client;
                current.Session = session;
                current.Touch();

                try { dead.Dispose(); } catch { }

                EnsureSweeper();
                return (current, null);
            }

            var conn = new SshConnection(client, session);
            _connections[sessionName] = conn;

            EnsureSweeper();
            return (conn, null);
        }
    }

    // Connects the replacement BEFORE letting go of the old client. The previous
    // order disposed first, so a failing Connect left a disposed client in place;
    // every later call then threw ObjectDisposedException, which IsConnectionError
    // classifies as a link problem, and the retry hit the same dead object.
    private static void Reconnect(SshConnection conn)
    {
        var old = conn.Client;

        var client = ConnectSftp(conn.Session);

        conn.Client = client;

        try { if (old.IsConnected) old.Disconnect(); } catch { }
        try { old.Dispose(); } catch { }
    }

    private static SftpClient ConnectSftp(SshSession session) =>
        ConnectWithRetry(session, info => new SftpClient(info)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            BufferSize = SftpBufferSize
        });

    // =========================================================================
    // Every SSH connection in the application is made here: SFTP for the panes,
    // SshClient for remote commands (search) and for the terminal. The same
    // settings used to be built in three places.
    //
    // One automatic retry: a first attempt that times out or is reset during
    // the handshake usually succeeds on the second, which is what the user was
    // doing by clicking again. A rejected login is not retried: a wrong password
    // stays wrong, and repeated failures can get the address blocked by the
    // server (fail2ban and the like).
    // =========================================================================
    internal static T ConnectWithRetry<T>(SshSession session, Func<ConnectionInfo, T> create)
        where T : BaseClient
    {
        for (int attempt = 1; ; attempt++)
        {
            T client = create(CreateConnectionInfo(session));

            // Every connection in the application passes here, so this one
            // handler covers the panes, remote commands and the terminal
            bool keyRejected = false;
            client.HostKeyReceived += (_, e) =>
            {
                e.CanTrust = VerifyHostKey(session, e.HostKeyName, e.FingerPrintSHA256);
                if (!e.CanTrust) keyRejected = true;
            };

            try
            {
                client.Connect();
                return client;
            }
            // A rejected key is never retried: the user said no
            catch (Exception ex) when (attempt < 2 && !keyRejected && ex is not SshAuthenticationException)
            {
                try { client.Dispose(); } catch { }
                Thread.Sleep(500);
            }
            catch
            {
                try { client.Dispose(); } catch { }
                if (keyRejected)
                    throw new IOException("The server's host key was not accepted.");
                throw;
            }
        }
    }

    // =========================================================================
    // HOST KEY VERIFICATION (known hosts)
    //
    // Without it the client connected to whatever answered at the address and
    // sent it the password: anyone able to redirect the traffic (a hostile
    // Wi-Fi, a poisoned DNS answer) received the credentials.
    //
    // Trust on first use, as OpenSSH, PuTTY and WinSCP do: the first connection
    // to a server shows its key fingerprint and asks; the key is then stored in
    // %APPDATA%\R2Cmd\known_hosts and checked silently every time. A different
    // key later is a loud warning with "No" as the safe answer.
    //
    // One prompt at a time: several connections to a new server (a pane, the
    // selection count, a size scan) start together and would otherwise ask the
    // same question several times. The UI thread never blocks on that lock: if
    // it were to wait while another thread waits for the UI to show its
    // dialog, both would hang.
    // =========================================================================
    private static readonly SemaphoreSlim s_hostKeyPrompt = new(1, 1);
    private static readonly object s_knownHostsLock = new();
    private static Dictionary<string, string>? s_knownHosts;

    private static string KnownHostsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "R2Cmd", "known_hosts");

    private static bool VerifyHostKey(SshSession session, string algorithm, string fingerprint)
    {
        string host = $"{session.Host}:{session.Port}";
        string presented = $"{algorithm} SHA256:{fingerprint}";

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return false;

        bool onUiThread = dispatcher.CheckAccess();
        if (onUiThread ? !s_hostKeyPrompt.Wait(0) : !s_hostKeyPrompt.Wait(Timeout.Infinite)) return false;

        try
        {
            // Checked again after waiting: the prompt that held the lock may
            // just have stored this very key
            string? known = GetKnownHost(host);
            if (known == presented) return true;

            string message = known == null
                ? "The authenticity of this server has not been confirmed yet.\n\n" +
                  $"Server: {host}\nKey: {presented}\n\n" +
                  "If you can, compare it with the server's own fingerprint:\n" +
                  "ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub\n\n" +
                  "Trust this key and connect?"
                : "WARNING: THE SERVER'S HOST KEY HAS CHANGED!\n\n" +
                  $"Server: {host}\nStored: {known}\nPresented: {presented}\n\n" +
                  "This is expected after the server was reinstalled. Otherwise someone " +
                  "may be intercepting the connection, and you should answer No.\n\n" +
                  "Replace the stored key and connect?";

            string title = known == null ? "New SSH Server" : "SSH Host Key Changed";

            bool accepted = dispatcher.Invoke(() =>
            {
                var dialog = new ConfirmDialog(message, title)
                {
                    Owner = System.Windows.Application.Current.MainWindow
                };
                return dialog.ShowDialog() == true;
            });

            if (accepted) SetKnownHost(host, presented);
            return accepted;
        }
        catch
        {
            return false;
        }
        finally
        {
            s_hostKeyPrompt.Release();
        }
    }

    // File format, one server per line: "host:port algorithm SHA256:fingerprint"
    private static string? GetKnownHost(string host)
    {
        lock (s_knownHostsLock)
        {
            if (s_knownHosts == null)
            {
                s_knownHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(KnownHostsPath))
                    {
                        foreach (string line in File.ReadAllLines(KnownHostsPath))
                        {
                            int space = line.IndexOf(' ');
                            if (space > 0) s_knownHosts[line[..space]] = line[(space + 1)..].Trim();
                        }
                    }
                }
                catch { /* unreadable file: every server is asked about again */ }
            }

            return s_knownHosts.TryGetValue(host, out var key) ? key : null;
        }
    }

    private static void SetKnownHost(string host, string key)
    {
        lock (s_knownHostsLock)
        {
            s_knownHosts ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            s_knownHosts[host] = key;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(KnownHostsPath)!);
                File.WriteAllLines(KnownHostsPath, s_knownHosts.Select(kv => $"{kv.Key} {kv.Value}"));
            }
            catch { /* kept in memory for this session at least */ }
        }
    }

    private static ConnectionInfo CreateConnectionInfo(SshSession session)
    {
        AuthenticationMethod auth;

        if (session.AuthMethod == SshAuthMethod.PrivateKey)
        {
            var keyFile = string.IsNullOrEmpty(session.Passphrase)
                ? new PrivateKeyFile(session.PrivateKeyPath)
                : new PrivateKeyFile(session.PrivateKeyPath, session.Passphrase);
            auth = new PrivateKeyAuthenticationMethod(session.Username, keyFile);
        }
        else
        {
            auth = new PasswordAuthenticationMethod(session.Username, session.Password);
        }

        return new ConnectionInfo(session.Host, session.Port, session.Username, auth)
        {
            Timeout = TimeSpan.FromSeconds(session.TimeoutSeconds)
        };
    }

    private static void ListInto(SftpClient client, string remotePath, string sessionName, List<FileEntry> entries)
    {
        string dir = remotePath.EndsWith('/') ? remotePath : remotePath + "/";

        foreach (var f in client.ListDirectory(remotePath))
        {
            if (f.Name == ".") continue;
            if (f.Name == "..")
            {
                if (remotePath != "/") entries.Insert(0, new FileEntry { Name = "..", IsFolder = true });
                continue;
            }

            entries.Add(new FileEntry
            {
                Name = f.Name,
                FullPath = $"ssh://{sessionName}{dir}{f.Name}",
                IsFolder = f.IsDirectory,
                Size = f.IsDirectory ? 0 : f.Length,
                Modified = f.LastWriteTime
            });
        }
    }

    private static bool IsLive(SftpClient c)
    {
        try { return c.IsConnected; }
        catch { return false; }
    }

    private static bool IsConnectionError(Exception ex) =>
        ex is SshConnectionException
           or SocketException
           or ObjectDisposedException
           or IOException;

    // =========================================================================
    // I/O Operations Implementations for the new universal pipeline
    //
    // CAUTION: the Gate is released as soon as the stream exists, so the transfer
    // itself runs unsynchronised against other calls on the same client. Holding
    // the Gate until the stream is disposed would be stricter, but the copy
    // pipeline opens a read stream and a write stream one after another — on a
    // server-to-itself copy the second acquire would deadlock against the first.
    // Keep that in mind before adding a second concurrent SSH operation.
    // =========================================================================

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var (session, remote) = SplitSshPath(path);
        var conn = RequireConnection(session);

        conn.Enter(ct);
        try
        {
            return Task.FromResult<Stream>(new LeasedStream(OpenReadWithRetry(conn, remote), conn));
        }
        finally { conn.Gate.Release(); }
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
    {
        var (session, remote) = SplitSshPath(path);
        var conn = RequireConnection(session);

        conn.Enter(ct);
        try
        {
            return Task.FromResult<Stream>(new LeasedStream(CreateWithRetry(conn, remote), conn));
        }
        finally { conn.Gate.Release(); }
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() => RemoteDelete(path), ct);
    }

    /// <summary>
    /// Runs a non-interactive command on the remote host and returns stdout.
    /// Creates a short-lived SSH connection (separate from the SFTP one).
    /// </summary>
    public static string? RunCommand(string sessionName, string command, CancellationToken ct = default)
    {
        var session = AppSettings.Load().SshSessions.FirstOrDefault(s =>
            s.Name.Equals(sessionName, StringComparison.OrdinalIgnoreCase) ||
            $"{s.Username}@{s.Host}".Equals(sessionName, StringComparison.OrdinalIgnoreCase));

        if (session == null) return null;

        SshClient? client = null;

        try
        {
            var ssh = ConnectWithRetry(session, info => new SshClient(info));
            client = ssh;

            using var cmd = ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(5);

            var asyncResult = cmd.BeginExecute();
            while (!asyncResult.IsCompleted)
            {
                if (ct.IsCancellationRequested)
                {
                    try { cmd.CancelAsync(); } catch { }

                    try
                    {
                        if (ssh.IsConnected) ssh.Disconnect();
                    }
                    catch { }

                    try { ssh.Dispose(); } catch { }
                    client = null;

                    ct.ThrowIfCancellationRequested();
                }
                Thread.Sleep(50);
            }

            return cmd.EndExecute(asyncResult);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
        finally
        {
            var toClose = client;
            if (toClose != null)
            {
                try
                {
                    if (toClose.IsConnected) toClose.Disconnect();
                }
                catch { }

                try { toClose.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Fast name search via remote find.
    /// Returns (path, size) pairs. Size is 0 if the server find has no -printf.
    /// </summary>
    public static List<(string Path, long Size)> FindFiles(
        string sessionName,
        string remoteDir,
        List<string> masks,
        CancellationToken ct = default)
    {
        var result = new List<(string Path, long Size)>();

        var nameParts = new List<string>();
        foreach (var mask in masks)
        {
            string escaped = mask.Replace("'", "'\\''");
            nameParts.Add($"-name '{escaped}'");
        }

        string nameExpr = nameParts.Count == 1
            ? nameParts[0]
            : $@"\( {string.Join(" -o ", nameParts)} \)";

        string dir = remoteDir.Replace("'", "'\\''");

        // GNU find: size + path in one pass
        string cmd =
            $"find '{dir}' \\( -type f -o -type l \\) {nameExpr} -printf '%s\\t%p\\n' 2>/dev/null";

        string? output = RunCommand(sessionName, cmd, ct);

        // Fallback without -printf (busybox / old find)
        if (string.IsNullOrEmpty(output))
        {
            cmd = $"find '{dir}' \\( -type f -o -type l \\) {nameExpr} 2>/dev/null";
            output = RunCommand(sessionName, cmd, ct);
            if (string.IsNullOrEmpty(output)) return result;

            using var reader = new StringReader(output);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length > 0)
                    result.Add((line, 0));
            }
            return result;
        }

        using (var reader = new StringReader(output))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                int tab = line.IndexOf('\t');
                if (tab <= 0)
                {
                    result.Add((line, 0));
                    continue;
                }

                string sizePart = line.Substring(0, tab);
                string pathPart = line.Substring(tab + 1);

                long size = 0;
                long.TryParse(sizePart, out size);

                if (pathPart.Length > 0)
                    result.Add((pathPart, size));
            }
        }

        return result;
    }
    /// <summary>
    /// Fast content search on the remote host using rg (ripgrep) or grep -r.
    /// Returns (remote path, match count) pairs.
    /// </summary>
    public static List<(string Path, int Count)> GrepFiles(
        string sessionName,
        string remoteDir,
        List<string> masks,
        string pattern,
        bool isRegex,
        bool caseSensitive,
        CancellationToken ct = default)
    {
        var result = new List<(string Path, int Count)>();

        string Escape(string s) => s.Replace("'", "'\\''");

        var globArgs = new List<string>();
        foreach (var mask in masks)
            globArgs.Add(Escape(mask));

        string dir = Escape(remoteDir);
        string pat = Escape(pattern);

        string caseFlag = caseSensitive ? "" : "-i";
        string regexFlag = isRegex ? "" : "-F";

        // -c = print count of matches per file
        string globsRg = string.Join(" ", globArgs.Select(g => $"--glob '{g}'"));
        // --count-matches = number of matches (not just matching lines)
        string rgCmd = $"rg --count-matches {caseFlag} {regexFlag} {globsRg} -- '{pat}' '{dir}' 2>/dev/null";

        string includesGrep = string.Join(" ", globArgs.Select(g => $"--include='{g}'"));
        string grepCmd = $"grep -rc {caseFlag} {regexFlag} {includesGrep} -- '{pat}' '{dir}' 2>/dev/null";

        string? output = RunCommand(sessionName, rgCmd, ct);
        if (string.IsNullOrEmpty(output))
            output = RunCommand(sessionName, grepCmd, ct);

        if (string.IsNullOrEmpty(output)) return result;

        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (line.Length == 0) continue;

            // Format: /path/file.php:3
            int colon = line.LastIndexOf(':');
            if (colon <= 0 || colon >= line.Length - 1) continue;

            string pathPart = line.Substring(0, colon);
            string countPart = line.Substring(colon + 1);

            if (!int.TryParse(countPart, out int count) || count <= 0)
                continue;

            result.Add((pathPart, count));
        }

        return result;
    }
}
