// File: SshConnectionWindow.xaml.cs | Path: Dialogs/SshConnectionWindow.xaml.cs | Version: 2026-10-19
using System;
using System.Windows;
using Microsoft.Win32;

namespace R2Cmd;

public partial class SshConnectionWindow : Window
{
    public SshSession? Result { get; private set; }

    public SshConnectionWindow(SshSession? existing = null)
    {
        InitializeComponent();

        var s = existing ?? new SshSession();
        txtName.Text = s.Name;
        txtHost.Text = s.Host;
        txtPort.Text = s.Port.ToString();
        txtUser.Text = s.Username;
        txtKeyPath.Text = s.PrivateKeyPath;
        txtRemote.Text = string.IsNullOrEmpty(s.RemotePath) ? "/" : s.RemotePath;
        pwdPassword.Password = s.Password;
        pwdPassphrase.Password = s.Passphrase;

        if (s.AuthMethod == SshAuthMethod.Agent) rbAgent.IsChecked = true;
        else if (s.AuthMethod == SshAuthMethod.PrivateKey) rbKey.IsChecked = true;
        else rbPassword.IsChecked = true;

        UpdateAuthState();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Apply title bar theme matching the current active theme
        Helpers.SetTitleBarTheme(this, ThemeManager.IsDarkTheme);
    }

    private void AuthChanged(object sender, RoutedEventArgs e) => UpdateAuthState();

    private void UpdateAuthState()
    {
        bool key = rbKey.IsChecked == true;
        // Agent authentication uses neither a password nor a private key file.
        pwdPassword.IsEnabled = rbPassword.IsChecked == true;
        txtKeyPath.IsEnabled = key;
        btnBrowse.IsEnabled = key;
        pwdPassphrase.IsEnabled = key;
    }

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select private key file",
            Filter = "Private key files|*.pem;*.ppk;*.key;id_rsa;id_ed25519|All files|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog(this) == true) txtKeyPath.Text = dlg.FileName;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        string host = txtHost.Text.Trim();
        string user = txtUser.Text.Trim();

        if (string.IsNullOrWhiteSpace(host)) { MessageDialog.Show(this, "Host / IP is required.", "SSH"); return; }
        if (string.IsNullOrWhiteSpace(user)) { MessageDialog.Show(this, "Username is required.", "SSH"); return; }
        if (!int.TryParse(txtPort.Text.Trim(), out int port) || port < 1 || port > 65535)
        { MessageDialog.Show(this, "Port must be a number between 1 and 65535.", "SSH"); return; }

        bool key = rbKey.IsChecked == true;
        if (key && string.IsNullOrWhiteSpace(txtKeyPath.Text))
        { MessageDialog.Show(this, "Private key file is required for key authentication.", "SSH"); return; }

        string name = txtName.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = $"{user}@{host}";

        // The session name becomes part of the "ssh://{name}/..." virtual path,
        // so '/' or '\' inside it would corrupt path parsing in the provider.
        if (name.Contains('/') || name.Contains('\\'))
        {
            MessageDialog.Show(this, "Session name cannot contain '/' or '\\' characters.", "SSH");
            return;
        }

        string remote = txtRemote.Text.Trim();
        if (string.IsNullOrEmpty(remote)) remote = "/";

        Result = new SshSession
        {
            Name = name,
            Host = host,
            Port = port,
            Username = user,
            AuthMethod = rbAgent.IsChecked == true ? SshAuthMethod.Agent
                : key ? SshAuthMethod.PrivateKey : SshAuthMethod.Password,
            Password = pwdPassword.Password,
            PrivateKeyPath = txtKeyPath.Text.Trim(),
            Passphrase = pwdPassphrase.Password,
            RemotePath = remote
        };
        DialogResult = true;
    }
}
