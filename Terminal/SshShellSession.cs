using System;
using System.IO;
using System.Text;
using System.Threading;
using Renci.SshNet;

namespace R2Cmd.Terminal;

public sealed class SshShellSession : ITerminalSession
{
    private readonly SshClient _client;
    private readonly ShellStream _shell;
    private readonly Thread _readThread;
    private volatile bool _disposed;

    public event Action<char[], int>? Output;
    public event Action? Exited;
    public bool IsRunning => !_disposed && _client.IsConnected;

    public SshShellSession(SshSession session, int cols, int rows)
    {
        // Same connection settings and retry as the file panes
        _client = R2Cmd.Providers.SshFileSystemProvider.ConnectWithRetry(session, info => new SshClient(info)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        _shell = _client.CreateShellStream("xterm-256color", (uint)cols, (uint)rows, 800, 600, 65536);

        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "SSH shell reader" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[4096];
        var chars = new char[4096];
        var decoder = Encoding.UTF8.GetDecoder();

        try
        {
            while (!_disposed)
            {
                // Host reboot / network drop: do not block forever on Read()
                if (!_client.IsConnected)
                    break;

                // The Read method blocks the thread natively until data arrives.
                // This eliminates CPU polling overhead and provides instant terminal response.
                int read = _shell.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                int decoded = decoder.GetChars(buffer, 0, read, chars, 0);
                if (decoded > 0) Output?.Invoke(chars, decoded);
            }
        }
        catch (IOException) { }
        catch { }

        if (!_disposed) Exited?.Invoke();
    }

    public void Write(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;
        try { _shell.Write(text); _shell.Flush(); }
        catch (IOException) { }
        catch { }
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || !_client.IsConnected) return;
        if (cols < 8 || rows < 2) return;

        try
        {
            // Direct call for modern SSH.NET versions.
            // No reflection needed, removing huge performance overhead during window resize.
            _shell.ChangeWindowSize((uint)cols, (uint)rows, 0, 0);
        }
        catch { /* connection lost */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _shell?.Dispose(); } catch { }
        try { if (_client.IsConnected) _client.Disconnect(); _client.Dispose(); } catch { }
    }
}
