// File: RecycleHelper.cs | Path: Helpers/RecycleHelper.cs | Version: 2026-10-19.4
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace R2Cmd;

// Batch delete to the Recycle Bin with a single shell call (SHFileOperation), like in
// Total Commander. Single-item FileSystem.DeleteFile on 1000+ files creates as many
// separate shell transactions (writing recovery metadata for each file) — hence taking
// dozens of seconds. A single batch call is exponentially faster.
public static class RecycleHelper
{
    // Sends all provided paths to the Recycle Bin in a single call.
    // Returns true on success (no errors and no user cancellation).
    public static bool SendToRecycleBin(IEnumerable<string> paths, bool silent)
        => SendToRecycleBin(paths, silent, out _);

    // Keep the Shell result with the failed attempt; a shared last-error field
    // would mix up simultaneous operations. SHFileOperation does not set LastError.
    public static bool SendToRecycleBin(IEnumerable<string> paths, bool silent, out Exception? error)
    {
        error = null;
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (list.Count == 0) return true;

        // pFrom - double-null-terminated list of paths: "a\0b\0c\0\0".
        string from = string.Join("\0", list) + "\0\0";

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = from,
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_WANTNUKEWARNING | FOF_NOCONFIRMATION | FOF_NOERRORUI
                              | (silent ? FOF_SILENT : 0))
        };

        int result = SHFileOperation(ref op);
        // 0 = success. fAnyOperationsAborted = cancelled by the user.
        if (result == 0 && !op.fAnyOperationsAborted) return true;

        // Silent Shell failures can report only "aborted". Opening for DELETE
        // checks permission/sharing without deleting or changing the file.
        if ((result == 0 || result == 0x75) && list.Count == 1)
        {
            using var handle = CreateFile(list[0], 0x00010000, 0x00000007, IntPtr.Zero,
                3, 0x02000000 | 0x00200000, IntPtr.Zero);
            int nativeError = Marshal.GetLastWin32Error();
            if (handle.IsInvalid && nativeError is 5 or 32 or 33 or 1314)
            {
                error = CreateFailure(nativeError);
                return false;
            }
        }
        error = CreateFailure(result);
        return false;
    }

    internal static Exception CreateFailure(int result)
    {
        // Legacy Shell codes overlap Win32 codes. Translate only known values;
        // keep every other Shell code visible instead of inventing a diagnosis.
        int code = result & 0xFFFF;
        if (code is 5 or 0x78 or 1314)
            return new IOException("Access is denied. Administrator permission may be required.",
                unchecked((int)(code == 1314 ? 0x80070522u : 0x80070005u)));

        if (code is 32 or 33)
            return new IOException("The file is in use by another program. Close it and press Retry.",
                unchecked((int)(0x80070000u | (uint)code)));

        string reason = code switch
        {
            0 or 0x75 => "The Recycle Bin operation was stopped or could not be completed.",
            0x74 => "A drive root cannot be moved to the Recycle Bin.",
            0x79 or 0x81 or 0xB7 => "The path is too long for this Recycle Bin operation.",
            0x7C => "The path is not valid for this Recycle Bin operation.",
            _ when code is > 0 and < 0x71 => new Win32Exception(code).Message,
            _ => "The Shell could not move this item to the Recycle Bin."
        };
        return new IOException($"{reason} (Shell error: 0x{result:X8})");
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    // If recycling is unavailable, the Shell must warn before permanent deletion.
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPTStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPTStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPTStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);
}

// Reuse one STA thread for Shell operations throughout a delete operation.
internal sealed class StaFileOperationWorker : IDisposable
{
    private readonly BlockingCollection<(Action Work, TaskCompletionSource<bool> Done)> _queue = new();

    internal StaFileOperationWorker()
    {
        var thread = new Thread(() =>
        {
            foreach (var (work, done) in _queue.GetConsumingEnumerable())
            {
                try { work(); done.TrySetResult(true); }
                catch (Exception ex) { done.TrySetException(ex); }
            }
            _queue.Dispose();
        }) { IsBackground = true, Name = "R2Cmd Shell file operations" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal Task RunAsync(Action work)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add((work, done));
        return done.Task;
    }

    public void Dispose() => _queue.CompleteAdding();
}
