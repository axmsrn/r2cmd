// File: ElevatedFileOperations.cs | Path: Services/ElevatedFileOperations.cs | Version: 2026-10-19.4
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace R2Cmd;

// One elevated worker per file operation, started only after access is denied.
internal sealed class ElevatedFileOperations : IDisposable
{
    internal const string WorkerArgument = "--elevated-file-operations";
    private const string PipePrefix = "R2Cmd.Transfer.";
    internal sealed record Request(string Operation, string Source, string Destination, bool Overwrite = false);
    internal sealed record Reply(string Kind, long Copied = 0, long Total = 0, int Error = 0, string? Message = null);

    // Share the elevation policy and worker lifetime between transfers and deletion.
    // Callers supply their own confirmation UI and transfer progress accounting.
    internal sealed class Session(CancellationTokenSource cancellation, Func<Request, bool> confirm) : IDisposable
    {
        private ElevatedFileOperations? _worker;

        internal Task RunAsync(Action action, Request request, bool allowElevation) =>
            RunAsync(() => { action(); return Task.CompletedTask; }, request, allowElevation);

        internal async Task RunAsync(Func<Task> action, Request request, bool allowElevation,
            Func<Request, Request>? prepareRetry = null, Action<long, long>? progress = null)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            try { await action().ConfigureAwait(false); }
            catch (Exception ex) when (allowElevation && IsAccessDenied(ex) && !IsElevated)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (prepareRetry != null) request = prepareRetry(request);

                try
                {
                    if (_worker == null)
                    {
                        if (!confirm(request)) throw new OperationCanceledException(cancellation.Token);
                        _worker = await StartAsync(cancellation.Token).ConfigureAwait(false);
                    }
                    await _worker.ExecuteAsync(request, progress, cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { cancellation.Cancel(); throw; }
            }
        }

        public void Dispose()
        {
            _worker?.Dispose();
            _worker = null;
        }
    }

    private readonly Stream _pipe;
    private readonly Process _worker;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;

    private ElevatedFileOperations(Stream pipe, Process worker)
    {
        _pipe = pipe;
        _worker = worker;
        _reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
    }

    internal static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    internal static bool IsAccessDenied(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException) return true;
            if (current is Win32Exception win32 && win32.NativeErrorCode is 5 or 1314) return true;
            if (current.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070522)) return true;
        }
        return false;
    }

    internal static async Task<ElevatedFileOperations> StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = PipePrefix + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(identity.User!);
        // Also allow an administrator entered into a standard user's UAC prompt.
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // GENERIC_WRITE requested by the client also maps to CreateNewInstance.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, security);
        Process? worker = null;
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? throw new IOException("Cannot locate R2Cmd.exe."),
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            start.ArgumentList.Add(WorkerArgument);
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            worker = Process.Start(start) ?? throw new IOException("Could not start the elevated file worker.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Task connection = pipe.WaitForConnectionAsync(timeout.Token);
            Task exited = worker.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connection, exited).ConfigureAwait(false) == exited &&
                !connection.IsCompletedSuccessfully)
            {
                cancellationToken.ThrowIfCancellationRequested();
                timeout.Token.ThrowIfCancellationRequested();
                throw new IOException(DescribeWorkerExit(worker.ExitCode));
            }
            await connection.ConfigureAwait(false);
            timeout.Cancel();
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != worker.Id)
                throw new IOException("Could not verify the elevated file worker.");
            cancellationToken.ThrowIfCancellationRequested();
            return new ElevatedFileOperations(pipe, worker);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            pipe.Dispose();
            worker?.Dispose();
            throw new OperationCanceledException("Administrator permission was declined.", ex, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            pipe.Dispose();
            worker?.Dispose();
            throw new IOException("Timed out while waiting for the elevated file worker.", ex);
        }
        catch
        {
            pipe.Dispose();
            worker?.Dispose();
            throw;
        }
    }

    internal async Task ExecuteAsync(Request request, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken).ConfigureAwait(false);
        while (true)
        {
            string? line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null) throw new IOException("The elevated file worker disconnected.");
            var reply = JsonSerializer.Deserialize<Reply>(line) ?? throw new IOException("Invalid worker response.");
            switch (reply.Kind)
            {
                case "Progress": progress?.Invoke(reply.Copied, reply.Total); break;
                case "Done": progress?.Invoke(reply.Copied, reply.Total); return;
                case "Cancelled": throw new OperationCanceledException(cancellationToken);
                case "Error": throw new IOException(reply.Message ?? "Elevated file operation failed.", reply.Error);
                default: throw new IOException("Unknown worker response.");
            }
        }
    }

    internal static string DescribeWorkerExit(int exitCode)
    {
        uint code = unchecked((uint)exitCode);
        string reason = code switch
        {
            0 => "Worker mode did not start. Rebuild R2Cmd with the updated App.xaml and App.xaml.cs.",
            0xE0434352 => "An unhandled .NET exception occurred during worker startup.",
            _ when code <= ushort.MaxValue => new Win32Exception(exitCode).Message,
            _ when (code & 0xFFFF0000) == 0x80070000 => new Win32Exception(exitCode & 0xFFFF).Message,
            _ => "Unexpected worker startup failure."
        };
        return $"The elevated file worker exited before connecting.\nExit code: 0x{code:X8}.\n{reason}";
    }

    internal static async Task<int> RunWorkerAsync(string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] != WorkerArgument ||
            !arguments[1].StartsWith(PipePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(arguments[1][PipePrefix.Length..], "N", out _) ||
            !uint.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out uint parentPid))
            return 87;

        if (!IsElevated) return 740;

        using var client = new NamedPipeClientStream(".", arguments[1], PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var cancelled = new CancellationTokenSource();
        StaFileOperationWorker? recycleWorker = null;
        Task active = Task.CompletedTask;
        try
        {
            await client.ConnectAsync(15000).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint pid)) return Marshal.GetLastWin32Error();
            if (pid != parentPid) return 5;
            using var reader = new StreamReader(client, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            try
            {
                // Keep reading during CopyFileEx: disconnect cancels it if the parent closes.
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var request = JsonSerializer.Deserialize<Request>(line) ?? throw new IOException("Invalid request.");
                    await active.ConfigureAwait(false);
                    if (request.Operation == "Recycle")
                    {
                        // SHFileOperation requires STA; the pipe reader must remain
                        // active so parent exit still cancels pending requests.
                        recycleWorker ??= new StaFileOperationWorker();
                        active = recycleWorker.RunAsync(() => ExecuteWorkerRequest(request, writer, cancelled.Token));
                    }
                    else
                        active = Task.Run(() => ExecuteWorkerRequest(request, writer, cancelled.Token));
                }
            }
            finally
            {
                cancelled.Cancel();
                await active.ConfigureAwait(false);
            }
            return 0;
        }
        catch (Exception ex) { return ex is Win32Exception win32 ? win32.NativeErrorCode : ex.HResult; }
        finally { recycleWorker?.Dispose(); }
    }

    private static void ExecuteWorkerRequest(Request request, StreamWriter writer, CancellationToken cancellationToken)
    {
        void ReplyToParent(Reply reply) => writer.WriteLine(JsonSerializer.Serialize(reply));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            long size = 0;
            switch (request.Operation)
            {
                case "CreateDirectory":
                    ValidatePath(request.Destination);
                    Directory.CreateDirectory(request.Destination);
                    break;
                case "CopyFile":
                case "MoveFile":
                    ValidatePath(request.Source);
                    ValidatePath(request.Destination);
                    if (request.Overwrite && File.Exists(request.Destination))
                        File.SetAttributes(request.Destination, FileAttributes.Normal);
                    if (request.Operation == "MoveFile")
                        File.Move(request.Source, request.Destination, request.Overwrite);
                    else
                        CopyFile(request, (copied, total) => ReplyToParent(new Reply("Progress", copied, total)), cancellationToken);
                    try { size = new FileInfo(request.Destination).Length; } catch (IOException) { }
                    break;
                case "DeleteFile":
                    ValidatePath(request.Source);
                    try { File.Delete(request.Source); }
                    catch (UnauthorizedAccessException)
                    {
                        if ((File.GetAttributes(request.Source) & FileAttributes.ReparsePoint) == 0)
                            File.SetAttributes(request.Source, FileAttributes.Normal);
                        File.Delete(request.Source);
                    }
                    break;
                case "DeleteDirectory":
                    ValidatePath(request.Source);
                    try { Directory.Delete(request.Source, recursive: false); }
                    catch (UnauthorizedAccessException)
                    {
                        var directory = new DirectoryInfo(request.Source);
                        if ((directory.Attributes & FileAttributes.ReparsePoint) == 0)
                            directory.Attributes &= ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
                        Directory.Delete(request.Source, recursive: false);
                    }
                    break;
                case "Recycle":
                    ValidatePath(request.Source);
                    if (!RecycleHelper.SendToRecycleBin(new[] { request.Source }, silent: true, out var error))
                    {
                        // The parent may have been unable to clear read-only flags.
                        var attributes = File.GetAttributes(request.Source);
                        if ((attributes & FileAttributes.ReparsePoint) == 0)
                            File.SetAttributes(request.Source,
                                attributes & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));
                        if (!RecycleHelper.SendToRecycleBin(new[] { request.Source }, silent: true, out error))
                            throw error ?? new IOException("Cannot move this item to the Recycle Bin.");
                    }
                    break;
                default: throw new IOException("Unsupported elevated operation.");
            }
            ReplyToParent(new Reply("Done", size, size));
        }
        catch (OperationCanceledException) { TryReply(new Reply("Cancelled")); }
        catch (Exception ex)
        {
            int error = ex is Win32Exception win32
                ? unchecked((int)(0x80070000u | (uint)win32.NativeErrorCode)) : ex.HResult;
            TryReply(new Reply("Error", Error: error, Message: ex.Message));
        }

        void TryReply(Reply reply)
        {
            try { ReplyToParent(reply); }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new IOException("An absolute file path is required.");
    }

    private static void CopyFile(Request request, Action<long, long> progress, CancellationToken cancellationToken)
    {
        int cancel = 0;
        long lastUpdate = 0;
        CopyProgress callback = (total, copied, _, _, _, _, _, _, _) =>
        {
            if (cancellationToken.IsCancellationRequested) return 1;
            long now = Environment.TickCount64;
            if (copied == total || now - lastUpdate >= 40)
            {
                try { progress(copied, total); }
                catch (IOException) { return 1; }
                lastUpdate = now;
            }
            return 0;
        };
        if (!CopyFileExW(request.Source, request.Destination, callback, IntPtr.Zero, ref cancel,
                         request.Overwrite ? 0u : 1u))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1235 || cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            throw new Win32Exception(error);
        }
    }

    public void Dispose()
    {
        // Closing the pipe also cancels an active copy in the worker.
        try { _writer.Dispose(); } catch (IOException) { }
        _pipe.Dispose();
        _reader.Dispose();
        _worker.Dispose();
    }

    private delegate uint CopyProgress(long total, long copied, long streamSize, long streamCopied,
        uint streamNumber, uint reason, IntPtr source, IntPtr destination, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileExW(string source, string destination, CopyProgress progress,
        IntPtr data, ref int cancel, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
