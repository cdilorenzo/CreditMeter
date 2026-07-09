using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreditMeter;

/// <summary>
/// Plain settings shape persisted to disk as JSON. The GitHub PAT is never
/// stored in plaintext — only its DPAPI-encrypted, Base64-encoded form lives
/// in <see cref="EncryptedPat"/>.
/// </summary>
public sealed class AppSettings
{
    public string? EncryptedPat { get; set; }
    public int PollIntervalMinutes { get; set; } = 5;
    public string? GitHubUsername { get; set; }

    /// <summary>
    /// Optional local-only monthly AI credit limit the user configures via
    /// --set-credit-limit. Never fetched from any API — purely a local budget
    /// the tray tooltip/popup compare usage against.
    /// </summary>
    public decimal? MonthlyCreditLimit { get; set; }
}

/// <summary>
/// Source-generated JSON (de)serialization context. Required for Native AOT:
/// it lets System.Text.Json work without runtime reflection.
/// </summary>
[JsonSerializable(typeof(AppSettings))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads/writes settings.json under %APPDATA%\CreditMeter\ and encrypts the
/// GitHub PAT at rest using Windows DPAPI (CurrentUser scope), so the token
/// is unreadable by anyone but this Windows account — even if the file is
/// copied elsewhere.
/// </summary>
internal static class SettingsStore
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CreditMeter");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new AppSettings();
        }

        try
        {
            byte[] jsonBytes = File.ReadAllBytes(SettingsPath);
            return JsonSerializer.Deserialize(jsonBytes, SettingsJsonContext.Default.AppSettings)
                   ?? new AppSettings();
        }
        catch (Exception)
        {
            // Corrupt or unreadable settings file — fall back to defaults
            // rather than crashing a tray app the user can't see a stack trace from.
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);
        File.WriteAllBytes(SettingsPath, jsonBytes);
    }

    /// <summary>Encrypts a plaintext PAT with DPAPI and stores it on the settings object.</summary>
    public static void SetPat(AppSettings settings, string plainTextPat)
    {
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainTextPat);
        byte[] encryptedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        settings.EncryptedPat = Convert.ToBase64String(encryptedBytes);
    }

    /// <summary>Decrypts the stored PAT for use in an API call. Returns null if none is configured or it can't be decrypted.</summary>
    public static string? GetPlainTextPat(AppSettings settings)
    {
        if (string.IsNullOrEmpty(settings.EncryptedPat))
        {
            return null;
        }

        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(settings.EncryptedPat);
            byte[] plainBytes = ProtectedData.Unprotect(encryptedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            // Happens if settings.json was copied to a different machine or
            // Windows account — DPAPI ties the encryption to both.
            return null;
        }
    }
}
