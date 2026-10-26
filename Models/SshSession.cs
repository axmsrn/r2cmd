// File: SshSession.cs | Path: Models/SshSession.cs | Version: 2026-10-19
using System;
using System.Text.Json.Serialization;

namespace R2Cmd;

public enum SshAuthMethod
{
    Password = 0,
    PrivateKey = 1,
    Agent = 2
}

public sealed class SshSession
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;

    // ------------------------------------------------------------------------
    // RUNTIME PROPERTIES
    // [JsonIgnore] prevents storing plain text passwords in settings.json
    // ------------------------------------------------------------------------
    [JsonIgnore]
    public string Password { get; set; } = "";

    [JsonIgnore]
    public string Passphrase { get; set; } = "";

    // ------------------------------------------------------------------------
    // ENCRYPTED PROPERTIES (Serialized to settings.json)
    // ------------------------------------------------------------------------
    [JsonPropertyName("EncryptedPassword")]
    public string EncryptedPassword
    {
        get => SecurityHelper.EncryptString(Password);
        set => Password = SecurityHelper.DecryptString(value);
    }

    [JsonPropertyName("EncryptedPassphrase")]
    public string EncryptedPassphrase
    {
        get => SecurityHelper.EncryptString(Passphrase);
        set => Passphrase = SecurityHelper.DecryptString(value);
    }

    // ------------------------------------------------------------------------
    // LEGACY MIGRATION (Backward compatibility)
    // Reads old plain text passwords once, but NEVER writes them back.
    // ------------------------------------------------------------------------
    [JsonPropertyName("Password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPassword
    {
        get => null; // Returning null skips writing this field entirely
        set { if (!string.IsNullOrEmpty(value)) Password = value; } // Reads legacy plain text
    }

    [JsonPropertyName("Passphrase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPassphrase
    {
        get => null;
        set { if (!string.IsNullOrEmpty(value)) Passphrase = value; }
    }

    // ------------------------------------------------------------------------

    public string PrivateKeyPath { get; set; } = "";

    // Initial remote directory, e.g., "/" or "/home/user"
    public string RemotePath { get; set; } = "/";

    public int TimeoutSeconds { get; set; } = 15;

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Name) ? $"{Username}@{Host}" : Name;
}
