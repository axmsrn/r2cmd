using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace R2Cmd.Terminal;

// Wraps a Windows pseudo console (ConPTY, requires Windows 10 1809+).
// The child process believes it talks to a real console, so colours, resizing
// and Ctrl+C all behave normally, while we receive a plain VT stream.
public sealed class ConPtySession : ITerminalSession
{
    // ===================== Win32 =====================
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int STILL_ACTIVE = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, int dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    // ===================== State =====================
    private IntPtr _hPC = IntPtr.Zero;
    private IntPtr _attributeList = IntPtr.Zero;
    private PROCESS_INFORMATION _pi;
    private FileStream? _writeStream;
    private FileStream? _readStream;
    private Thread? _readThread;
    private volatile bool _disposed;

    // Raised on a background thread. The consumer is expected to buffer and
    // drain on its own schedule instead of touching the UI per chunk.
    public event Action<char[], int>? Output;
    public event Action? Exited;

    public bool IsRunning
    {
        get
        {
            if (_disposed || _pi.hProcess == IntPtr.Zero) return false;
            return GetExitCodeProcess(_pi.hProcess, out int code) && code == STILL_ACTIVE;
        }
    }

    // =========================================================================
    // Everything is built inside a try/finally.
    //
    // A failure part way through — no ConPTY on this Windows build, a shell path
    // that does not exist — used to leave four pipe handles, the pseudo console
    // and an unmanaged attribute list behind: the constructor threw, so nobody
    // ever called Dispose on the half-built object. Repeated failed attempts
    // leaked a handle set each time.
    //
    // Handles are zeroed as ownership passes on, so the cleanup only touches
    // what is still ours.
    // =========================================================================
    public ConPtySession(string commandLine, string? workingDirectory, int cols, int rows)
    {
        cols = Math.Max(cols, 8);
        rows = Math.Max(rows, 2);

        if (!CreatePipe(out IntPtr inputRead, out IntPtr inputWrite, IntPtr.Zero, 0))
            throw new IOException("CreatePipe failed for terminal input.");

        if (!CreatePipe(out IntPtr outputRead, out IntPtr outputWrite, IntPtr.Zero, 0))
        {
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            throw new IOException("CreatePipe failed for terminal output.");
        }

        // Immediately wrap our ends in SafeFileHandle. GC and Dispose will handle them safely now.
        var safeInputWrite = new SafeFileHandle(inputWrite, ownsHandle: true);
        var safeOutputRead = new SafeFileHandle(outputRead, ownsHandle: true);

        try
        {
            int hr = CreatePseudoConsole(new COORD { X = (short)cols, Y = (short)rows }, inputRead, outputWrite, 0, out _hPC);
            if (hr != 0) throw new IOException($"CreatePseudoConsole failed (HRESULT 0x{hr:X8}).");

            StartProcess(commandLine, workingDirectory);

            _writeStream = new FileStream(safeInputWrite, FileAccess.Write, 1, false);
            _readStream = new FileStream(safeOutputRead, FileAccess.Read, 4096, false);

            _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPty reader" };
            _readThread.Start();
        }
        catch
        {
            safeInputWrite.Dispose();
            safeOutputRead.Dispose();
            Dispose();
            throw;
        }
        finally
        {
            // ConPTY owns these ends now. We must close our local references to allow EOF.
            CloseHandle(inputRead);
            CloseHandle(outputWrite);
        }
    }

    private void StartProcess(string commandLine, string? workingDirectory)
    {
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

        IntPtr size = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size); // sizing call, always "fails"

        _attributeList = Marshal.AllocHGlobal(size);
        si.lpAttributeList = _attributeList;

        if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref size))
            throw new IOException("InitializeProcThreadAttributeList failed.");

        if (!UpdateProcThreadAttribute(si.lpAttributeList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            throw new IOException("UpdateProcThreadAttribute failed.");

        if (string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
            workingDirectory = null;

        if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDirectory, ref si, out _pi))
        {
            throw new IOException($"Cannot start '{commandLine}' (Win32 error {Marshal.GetLastWin32Error()}).");
        }
    }

    private void ReadLoop()
    {
        var bytes = new byte[4096];
        var chars = new char[4096];
        var decoder = new UTF8Encoding(false).GetDecoder(); // keeps state across split sequences

        // =====================================================================
        // The pipe is read until it closes, even after Dispose has started.
        //
        // ClosePseudoConsole waits for the console host to flush its last
        // output, and the host can only flush into a pipe someone is reading.
        // The loop used to stop at the first chunk after Dispose set the flag;
        // with more output pending, ClosePseudoConsole then waited forever and
        // closing a terminal froze the window (Windows 10 and Windows 11 before
        // 24H2). After Dispose the data is simply dropped.
        // =====================================================================
        try
        {
            while (true)
            {
                int read = _readStream!.Read(bytes, 0, bytes.Length);
                if (read <= 0) break;
                if (_disposed) continue;

                int decoded = decoder.GetChars(bytes, 0, read, chars, 0);
                if (decoded > 0) Output?.Invoke(chars, decoded);
            }
        }
        catch { /* pipe closed on shutdown */ }

        if (!_disposed) Exited?.Invoke();
    }

    public void Write(string text)
    {
        if (_disposed || _writeStream == null || string.IsNullOrEmpty(text)) return;

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            _writeStream.Write(data, 0, data.Length);
            _writeStream.Flush();
        }
        catch { /* the shell is gone */ }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || _hPC == IntPtr.Zero) return;
        if (cols < 8 || rows < 2) return;

        ResizePseudoConsole(_hPC, new COORD { X = (short)cols, Y = (short)rows });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Closing the pseudo console asks the client to exit on its own
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }

        try { _writeStream?.Dispose(); } catch { }
        try { _readStream?.Dispose(); } catch { }

        if (_pi.hProcess != IntPtr.Zero)
        {
            // Give the shell a moment to leave cleanly, then insist
            if (WaitForSingleObject(_pi.hProcess, 1000) != 0) TerminateProcess(_pi.hProcess, 0);
            CloseHandle(_pi.hProcess);
            _pi.hProcess = IntPtr.Zero;
        }

        if (_pi.hThread != IntPtr.Zero) { CloseHandle(_pi.hThread); _pi.hThread = IntPtr.Zero; }

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
            _attributeList = IntPtr.Zero;
        }
    }

    // ===================== Shell discovery =====================
    // pwsh if installed, then Windows PowerShell, then cmd as the last resort
    public static string ResolveDefaultShell()
    {
        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe", "cmd.exe" })
        {
            string? full = FindOnPath(candidate);
            if (full != null) return full;
        }
        return "cmd.exe";
    }

    private static string? FindOnPath(string exeName)
    {
        string paths = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                // PATH entries are sometimes quoted, and Path.Combine rejects quotes
                string candidate = Path.Combine(dir.Trim().Trim('"'), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
