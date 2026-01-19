// ConfigManager.cs - Configuration file management with encryption

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ACProxyCam.Models;

namespace ACProxyCam.Daemon;

/// <summary>
/// Manages configuration loading, saving, and credential encryption.
/// </summary>
public static class ConfigManager
{
    public const string ConfigDir = "/etc/acproxycam";
    public const string ConfigFile = "/etc/acproxycam/config.json";
    private const string EncryptedPrefix = "encrypted:";
    private const string ApplicationSalt = "ACProxyCam_v1_Salt_2024";

    private static byte[]? _encryptionKey;

    /// <summary>
    /// Load configuration from file.
    /// </summary>
    public static async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(ConfigFile))
        {
            return new AppConfig();
        }

        var json = await File.ReadAllTextAsync(ConfigFile);
        var config = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();

        // Decrypt credentials
        foreach (var printer in config.Printers)
        {
            printer.SshPassword = DecryptIfNeeded(printer.SshPassword);
            printer.MqttUsername = DecryptIfNeeded(printer.MqttUsername);
            printer.MqttPassword = DecryptIfNeeded(printer.MqttPassword);
        }

        return config;
    }

    /// <summary>
    /// Save configuration to file.
    /// </summary>
    public static async Task SaveAsync(AppConfig config)
    {
        // Ensure directory exists
        if (!Directory.Exists(ConfigDir))
        {
            Directory.CreateDirectory(ConfigDir);
            // Set directory permissions (rwx for owner only)
            File.SetUnixFileMode(ConfigDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Clone config and encrypt credentials
        var saveConfig = new AppConfig
        {
            Version = config.Version,
            ListenInterfaces = config.ListenInterfaces,
            LogToFile = config.LogToFile,
            LogLevel = config.LogLevel,
            Printers = config.Printers.Select(p => new PrinterConfig
            {
                Name = p.Name,
                Ip = p.Ip,
                MjpegPort = p.MjpegPort,
                SshPort = p.SshPort,
                SshUser = p.SshUser,
                SshPassword = EncryptIfNeeded(p.SshPassword),
                MqttPort = p.MqttPort,
                MqttUsername = EncryptIfNeeded(p.MqttUsername),
                MqttPassword = EncryptIfNeeded(p.MqttPassword),
                DeviceId = p.DeviceId,
                ModelCode = p.ModelCode
            }).ToList()
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(saveConfig, options);

        await File.WriteAllTextAsync(ConfigFile, json);

        // Set file permissions (rw for owner only)
        File.SetUnixFileMode(ConfigFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Get or derive the machine-specific encryption key.
    /// </summary>
    private static byte[] GetEncryptionKey()
    {
        if (_encryptionKey != null)
            return _encryptionKey;

        string machineId;

        // Try to read machine-id
        if (File.Exists("/etc/machine-id"))
        {
            machineId = File.ReadAllText("/etc/machine-id").Trim();
        }
        else if (File.Exists("/var/lib/dbus/machine-id"))
        {
            machineId = File.ReadAllText("/var/lib/dbus/machine-id").Trim();
        }
        else
        {
            // Fallback - use hostname (less secure but works)
            machineId = Environment.MachineName;
        }

        // Derive key using PBKDF2
        var keyMaterial = Encoding.UTF8.GetBytes(machineId + ApplicationSalt);
        using var deriveBytes = new Rfc2898DeriveBytes(keyMaterial, Encoding.UTF8.GetBytes(ApplicationSalt), 10000, HashAlgorithmName.SHA256);
        _encryptionKey = deriveBytes.GetBytes(32); // 256-bit key

        return _encryptionKey;
    }

    /// <summary>
    /// Encrypt a string value if not already encrypted.
    /// </summary>
    private static string EncryptIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(EncryptedPrefix))
            return value;

        try
        {
            var key = GetEncryptionKey();
            var plainBytes = Encoding.UTF8.GetBytes(value);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            // Combine IV + encrypted data
            var combined = new byte[aes.IV.Length + encryptedBytes.Length];
            Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
            Buffer.BlockCopy(encryptedBytes, 0, combined, aes.IV.Length, encryptedBytes.Length);

            return EncryptedPrefix + Convert.ToBase64String(combined);
        }
        catch
        {
            // If encryption fails, return original (shouldn't happen)
            return value;
        }
    }

    /// <summary>
    /// Decrypt a string value if encrypted.
    /// </summary>
    private static string DecryptIfNeeded(string value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(EncryptedPrefix))
            return value;

        try
        {
            var key = GetEncryptionKey();
            var combined = Convert.FromBase64String(value.Substring(EncryptedPrefix.Length));

            // Split IV and encrypted data
            var iv = new byte[16];
            var encryptedBytes = new byte[combined.Length - 16];
            Buffer.BlockCopy(combined, 0, iv, 0, 16);
            Buffer.BlockCopy(combined, 16, encryptedBytes, 0, encryptedBytes.Length);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var plainBytes = decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // If decryption fails, return empty string
            return "";
        }
    }

    /// <summary>
    /// Check if config directory exists and is writable.
    /// </summary>
    public static bool IsConfigWritable()
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
            {
                Directory.CreateDirectory(ConfigDir);
            }
            var testFile = Path.Combine(ConfigDir, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Backup configuration to current directory.
    /// </summary>
    public static string? BackupConfig(string? customName = null)
    {
        if (!File.Exists(ConfigFile))
            return null;

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var filename = customName ?? $"acproxycam.backup.{timestamp}.json";

        File.Copy(ConfigFile, filename);
        return filename;
    }
}
