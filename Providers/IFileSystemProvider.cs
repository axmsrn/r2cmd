using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using R2Cmd;

namespace R2Cmd.Providers;

public interface IFileSystemProvider
{
    bool CanHandle(string path);
    Task<(List<FileEntry> Entries, string? Error)> ReadDirectoryAsync(string path, CancellationToken ct = default);
    ulong? GetFreeSpace(string path);

    // Legacy File and path operations
    bool Exists(string path);
    void CreateDirectory(string path);
    void CreateFile(string path);
    void Rename(string oldPath, string newPath);
    string GetParentPath(string path);
    string CombinePaths(string parent, string name);

    // =========================================================================
    // ARCHITECTURE UPGRADE: Polymorphic I/O Streams (C# 8+ Default Interface Methods)
    // This allows ProgressWindow to copy files via Streams without knowing
    // the underlying FileSystem type (SSH, Local, FTP, etc).
    // =========================================================================

    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
        => throw new NotSupportedException("Read streams not supported on this provider.");

    Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
        => throw new NotSupportedException("Write streams not supported on this provider.");

    Task DeleteAsync(string path, CancellationToken ct = default)
        => throw new NotSupportedException("Delete not supported on this provider.");
}

public static class FileSystemFactory
{
    private static readonly List<IFileSystemProvider> _providers = new()
    {
        new SshFileSystemProvider(),
        new VirtualNetworkProvider(),
        new ArchiveProvider(),
        new LocalDiskProvider()
    };

    public static IFileSystemProvider GetProvider(string path)
    {
        foreach (var provider in _providers)
        {
            if (provider.CanHandle(path)) return provider;
        }
        return _providers[^1]; // Fallback to LocalDiskProvider
    }
}

public class VirtualNetworkProvider : IFileSystemProvider
{
    // Cache the COM Type to avoid expensive Windows Registry lookups on every navigation
    private static readonly Type? _shellAppType = Type.GetTypeFromProgID("Shell.Application");

    public bool CanHandle(string path)
    {
        if (IsNetworkRoot(path)) return true;
        if (path.Equals(@"\\Network\LAN", StringComparison.OrdinalIgnoreCase)) return true;
        return IsBareNetworkComputer(path);
    }

    private static bool IsNetworkRoot(string path)
    {
        string clean = path.TrimEnd('\\');
        return clean.Equals(@"\\Network", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBareNetworkComputer(string path)
    {
        if (!path.StartsWith(@"\\")) return false;
        var parts = path.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && parts[0].Equals("Network", StringComparison.OrdinalIgnoreCase)) return false;
        return parts.Length == 1;
    }

    public async Task<(List<FileEntry> Entries, string? Error)> ReadDirectoryAsync(string path, CancellationToken ct = default)
    {
        var entries = new List<FileEntry>();
        string? error = null;

        if (IsNetworkRoot(path))
        {
            entries.Add(new FileEntry
            {
                Name = "[ Add SSH Connection ]",
                FullPath = ":::ADD_SSH:::",
                IsFolder = false,
                Size = 0,
                Modified = null,
                IconType = "AddSsh"
            });

            var settings = AppSettings.Load();
            foreach (var session in settings.SshSessions)
            {
                string displayName = string.IsNullOrWhiteSpace(session.Name)
                    ? $"{session.Username}@{session.Host}"
                    : session.Name;

                string startPath = string.IsNullOrWhiteSpace(session.RemotePath) ? "/" : session.RemotePath;
                if (!startPath.StartsWith("/")) startPath = "/" + startPath;

                entries.Add(new FileEntry
                {
                    Name = displayName,
                    FullPath = $"ssh://{displayName}{startPath}",
                    IsFolder = true,
                    Size = 0,
                    Modified = null,
                    IconType = "SshServer"
                });
            }

            entries.Add(new FileEntry
            {
                Name = "Windows Network",
                FullPath = @"\\Network\LAN",
                IsFolder = true,
                Size = 0,
                Modified = null,
                IconType = "WinNetwork"
            });

            return (entries, error);
        }

        // =====================================================================
        // NEW LOGIC: Use Hybrid Scanner for discovering PCs in the local network
        // =====================================================================
        if (path.Equals(@"\\Network\LAN", StringComparison.OrdinalIgnoreCase))
        {
            // Оставляем FullPath пустым, чтобы MainWindow вызвал NavigateUpAsync
            entries.Add(new FileEntry { Name = "..", IsFolder = true });

            try
            {
                var computers = await HybridNetworkScanner.ScanNetworkAsync(ct);
                foreach (var pc in computers)
                {
                    if (ct.IsCancellationRequested) break;
                    entries.Add(new FileEntry
                    {
                        Name = pc,
                        FullPath = $@"\\{pc}\",
                        IsFolder = true,
                        Size = 0,
                        Modified = null
                    });
                }
            }
            catch (Exception ex)
            {
                error = $"Network Scan Error: {ex.Message}";
            }

            return (entries, error);
        }

        // =====================================================================
        // EXISTING COM LOGIC: Lists shared folders on a specific computer (e.g. \\SERVER)
        // =====================================================================
        if (IsBareNetworkComputer(path))
        {
            // Оставляем FullPath пустым, чтобы MainWindow вызвал NavigateUpAsync
            entries.Add(new FileEntry { Name = "..", IsFolder = true });

            var tcs = new TaskCompletionSource<bool>();

            var thread = new Thread(() =>
            {
                object? shell = null;
                object? folder = null;
                object? items = null;

                try
                {
                    if (_shellAppType == null) return;
                    shell = Activator.CreateInstance(_shellAppType);
                    if (shell == null) return;

                    object folderIdentifier = path;

                    // Use late-binding to call COM methods
                    folder = _shellAppType.InvokeMember("NameSpace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new[] { folderIdentifier });

                    if (folder != null)
                    {
                        var folderType = folder.GetType();
                        items = folderType.InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, folder, null);

                        if (items != null)
                        {
                            foreach (dynamic item in (System.Collections.IEnumerable)items)
                            {
                                if (ct.IsCancellationRequested) break;

                                entries.Add(new FileEntry
                                {
                                    Name = item.Name,
                                    FullPath = item.Path,
                                    IsFolder = item.IsFolder,
                                    Size = 0,
                                    Modified = null
                                });

                                // Explicitly release inner COM object to prevent memory leaks
                                if (item != null && Marshal.IsComObject(item))
                                {
                                    Marshal.ReleaseComObject(item);
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = $"COM Virtual Folder Error: {ex.Message}";
                }
                finally
                {
                    // CRITICAL: Clean up COM objects to prevent Windows Explorer zombie processes
                    if (items != null && Marshal.IsComObject(items)) Marshal.ReleaseComObject(items);
                    if (folder != null && Marshal.IsComObject(folder)) Marshal.ReleaseComObject(folder);
                    if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);

                    tcs.TrySetResult(true);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            using (ct.Register(() => tcs.TrySetCanceled()))
            {
                try
                {
                    await tcs.Task;
                }
                catch (TaskCanceledException)
                {
                    return (new List<FileEntry>(), null);
                }
            }
        }

        return (entries, error);
    }

    public ulong? GetFreeSpace(string path) => null;
    public bool Exists(string path) => false;
    public void CreateDirectory(string path) => throw new NotSupportedException("Cannot create items here.");
    public void CreateFile(string path) => throw new NotSupportedException("Cannot create items here.");
    public void Rename(string oldPath, string newPath) => throw new NotSupportedException("Cannot rename items here.");

    // =========================================================================
    // Correct parent-path logic for network paths is restored here
    // =========================================================================
    public string GetParentPath(string path)
    {
        string cleanPath = path.TrimEnd('\\');

        // 1. Navigate from the local network folder up to the general network root
        if (cleanPath.Equals(@"\\Network\LAN", StringComparison.OrdinalIgnoreCase))
            return @"\\Network";

        // 2. Navigate from a specific computer (e.g., \\MI) up to the LAN folder
        if (IsBareNetworkComputer(cleanPath))
            return @"\\Network\LAN";

        // 3. Attempt to get the parent path using standard .NET methods
        string? parent = Path.GetDirectoryName(cleanPath);

        // 4. Fix for root network shares (e.g., \\MI\Share).
        // Path.GetDirectoryName returns null for these, so we manually trim the path back to the PC name:
        if (string.IsNullOrEmpty(parent) && cleanPath.StartsWith(@"\\"))
        {
            int lastSlash = cleanPath.LastIndexOf('\\');
            if (lastSlash > 1) // Ensure we don't cut off the initial double slashes
            {
                return cleanPath.Substring(0, lastSlash);
            }
        }

        return parent ?? @"\\Network";
    }

    public string CombinePaths(string parent, string name) => Path.Combine(parent, name);
}

public class ArchiveProvider : IFileSystemProvider
{
    // True for the archive file itself as well as for paths inside it, because
    // pressing Enter on "x.zip" has to open it like a folder.
    public bool CanHandle(string path)
    {
        var (archive, _) = ArchiveService.ParseVirtualPath(path);
        return archive != null && File.Exists(archive);
    }

    // =========================================================================
    // FIX: "Read streams not supported on this provider" when copying a .zip
    //
    // An archive is only a container once you step inside it. With an empty
    // internal path the request is about the .zip file itself, and then every
    // file operation on it has to behave like ordinary disk I/O.
    // =========================================================================
    private static bool IsArchiveFileItself(string path, out string filePath)
    {
        var (archive, internalPath) = ArchiveService.ParseVirtualPath(path);
        filePath = archive ?? path;
        return archive != null && string.IsNullOrEmpty(internalPath);
    }

    public Task<(List<FileEntry> Entries, string? Error)> ReadDirectoryAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var entries = new List<FileEntry>();
            var (archivePath, internalPath) = ArchiveService.ParseVirtualPath(path);

            if (archivePath == null) return (entries, (string?)"Invalid archive path");

            bool atRoot = string.IsNullOrEmpty(internalPath);
            if (!atRoot) entries.Add(new FileEntry { Name = "..", IsFolder = true });

            try
            {
                var nodes = ArchiveService.ListChildren(archivePath, internalPath);
                foreach (var node in nodes)
                {
                    if (ct.IsCancellationRequested) break;
                    entries.Add(new FileEntry
                    {
                        Name = node.Name,
                        FullPath = Path.Combine(archivePath, node.FullKey.Replace('/', '\\')),
                        IsFolder = node.IsFolder,
                        Size = node.Size,
                        Modified = node.Modified ?? DateTime.MinValue
                    });
                }
                return (entries, (string?)null);
            }
            catch (Exception ex)
            {
                return (entries, (string?)ex.Message);
            }
        });
    }

    public ulong? GetFreeSpace(string path) => null;

    // The copy engine asks this before showing the overwrite dialog. Returning a
    // flat false meant an existing archive was replaced without a prompt.
    public bool Exists(string path) =>
        IsArchiveFileItself(path, out string file) && File.Exists(file);

    public void CreateDirectory(string path) => throw new NotSupportedException("Cannot create items here.");
    public void CreateFile(string path) => throw new NotSupportedException("Cannot create items here.");

    public void Rename(string oldPath, string newPath)
    {
        if (!IsArchiveFileItself(oldPath, out string source))
            throw new NotSupportedException("Cannot rename items inside an archive.");

        File.Move(source, newPath);
    }

    public string GetParentPath(string path) => Path.GetDirectoryName(path) ?? "";
    public string CombinePaths(string parent, string name) => Path.Combine(parent, name);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        if (!IsArchiveFileItself(path, out string file))
            throw new NotSupportedException("Reading one entry inside an archive is not supported; extraction handles that.");

        Stream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
    {
        if (!IsArchiveFileItself(path, out string file))
            throw new NotSupportedException("Writing into an archive is not supported.");

        Stream stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, FileOptions.Asynchronous);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        if (!IsArchiveFileItself(path, out string file))
            throw new NotSupportedException("Deleting entries inside an archive is not supported.");

        return Task.Run(() => File.Delete(file), ct);
    }
}
// Must be marked partial to allow [LibraryImport] source generation
public partial class LocalDiskProvider : IFileSystemProvider
{
    private static readonly EnumerationOptions s_enumOptions = new()
    {
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true
    };

    public bool CanHandle(string path) => Directory.Exists(path) || string.IsNullOrEmpty(path) == false;

    public Task<(List<FileEntry> Entries, string? Error)> ReadDirectoryAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var entries = new List<FileEntry>();

            bool atRoot = string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase);

            // [FIX]: If this is a share root (\\MI\install), the OS treats it as a root,
            // but the UI must still show "..", so the user can return to the computer share list (\\MI).
            bool isUncRoot = atRoot && path.StartsWith(@"\\");

            if (!atRoot || isUncRoot)
                entries.Add(new FileEntry { Name = "..", IsFolder = true });

            try
            {
                var found = new FileSystemEnumerable<FileEntry>(
                    path,
                    (ref FileSystemEntry entry) => new FileEntry
                    {
                        Name = entry.FileName.ToString(),
                        FullPath = entry.ToFullPath(),
                        IsFolder = entry.IsDirectory,
                        Size = entry.IsDirectory ? 0 : entry.Length,
                        Modified = entry.LastWriteTimeUtc.LocalDateTime,
                        IsHidden = (entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0,
                        IsSymlink = (entry.Attributes & FileAttributes.ReparsePoint) != 0
                    },
                    s_enumOptions);

                foreach (var item in found)
                {
                    if (ct.IsCancellationRequested) break;
                    entries.Add(item);
                }

                return (entries, (string?)null);
            }
            catch (Exception ex)
            {
                return (entries, (string?)ex.Message);
            }
        });
    }

    // EntryPoint is required: kernel32 exports only GetDiskFreeSpaceExA / ...ExW,
    // and LibraryImport, unlike DllImport, does not look for the W version itself.
    // Without it the call threw "Entry point was not found" and no free space
    // was ever shown for a local drive.
    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

    public ulong? GetFreeSpace(string path)
    {
        if (GetDiskFreeSpaceEx(path, out ulong freeAvailable, out ulong total, out _) && total > 0)
        {
            const ulong bogusThreshold = 1024UL * 1024 * 1024 * 1024 * 1024;
            if (total < bogusThreshold) return freeAvailable;
        }
        return null;
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CreateFile(string path) => File.Create(path).Dispose();
    public void Rename(string oldPath, string newPath)
    {
        if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
        else File.Move(oldPath, newPath);
    }

    // [FIX]: LocalDiskProvider must also walk from \\MI\install up to \\MI
    public string GetParentPath(string path)
    {
        string cleanPath = path.TrimEnd('\\');
        string? parent = Path.GetDirectoryName(cleanPath);

        // Path.GetDirectoryName returns null for UNC share roots (\\PC\Share).
        // Handle this manually so we return the computer name:
        if (string.IsNullOrEmpty(parent) && cleanPath.StartsWith(@"\\"))
        {
            int lastSlash = cleanPath.LastIndexOf('\\');
            if (lastSlash > 1)
            {
                return cleanPath.Substring(0, lastSlash);
            }
        }

        return parent ?? "";
    }

    public string CombinePaths(string parent, string name) => Path.Combine(parent, name);

    // =========================================================================
    // I/O Operations Implementations
    // =========================================================================

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1048576, FileOptions.Asynchronous);
        return Task.FromResult<Stream>(stream);
    }

    public Task<Stream> OpenWriteAsync(string path, CancellationToken ct = default)
    {
        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1048576, FileOptions.Asynchronous);
        return Task.FromResult<Stream>(stream);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else File.Delete(path);
        }, ct);
    }
}
