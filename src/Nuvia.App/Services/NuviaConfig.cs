using System;
using System.IO;
using System.Text.Json;

namespace Nuvia.App.Services;

/// <summary>
/// Reads application configuration (api_id, api_hash, session path) from a local JSON file.
/// The config file is stored at %LOCALAPPDATA%\Nuvia\config.json and is NOT committed to Git.
/// Missing or malformed config produces a safe error — no developer-credential form is shown to the user.
/// </summary>
public sealed class NuviaConfig
{
    private const string ConfigFileName = "config.json";
    private const string IndexFileName = "index.db";
    private const string SettingsFileName = "settings.json";

    /// <summary>
    /// Root folder for all local Nuvia state (config, session, index).
    /// Exposed statically because the index location must be knowable even when the
    /// Telegram developer config is missing or invalid.
    /// </summary>
    public static string LocalDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nuvia");

    /// <summary>
    /// Path of the local file index: <c>%LOCALAPPDATA%\Nuvia\index.db</c>.
    /// <para>
    /// This is a local index of files Nuvia uploaded — not a copy of Telegram history.
    /// </para>
    /// </summary>
    public static string GetIndexDatabasePath() => Path.Combine(LocalDataDirectory, IndexFileName);

    /// <summary>
    /// Path of the local user-preferences file: <c>%LOCALAPPDATA%\Nuvia\settings.json</c>.
    /// <para>
    /// Holds only non-sensitive UI preferences (currently the account-scoped default upload
    /// destination). It never contains credentials, login codes, 2FA passwords or session data.
    /// Exposed statically for the same reason as the index path: preferences must be reachable even
    /// when the Telegram developer config is missing or invalid.
    /// </para>
    /// </summary>
    public static string GetSettingsPath() => Path.Combine(LocalDataDirectory, SettingsFileName);

    public long ApiId { get; }
    public string ApiHash { get; }
    public string SessionDirectory { get; }

    private NuviaConfig(long apiId, string apiHash, string sessionDirectory)
    {
        ApiId = apiId;
        ApiHash = apiHash;
        SessionDirectory = sessionDirectory;
    }

    public static NuviaConfig Load()
    {
        var configDir = LocalDataDirectory;
        var configPath = Path.Combine(configDir, ConfigFileName);

        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException(
                "Configuration not found. Create " + configPath + " with api_id and api_hash. " +
                "See docs/CREDENTIALS.md for build-time injection details.");
        }

        string json;
        try
        {
            json = File.ReadAllText(configPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to read config file: " + ex.Message, ex);
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException ex)
        {
            // Deliberately not reusing ex.Message: JSON parse errors can quote the offending
            // input, which would echo credential content into an error string.
            throw new InvalidOperationException(
                $"Invalid JSON in config file (line {ex.LineNumber}, position {ex.BytePositionInLine}). " +
                "See docs/CREDENTIALS.md for the expected format.", ex);
        }

        if (!root.TryGetProperty("api_id", out var apiIdElement) || apiIdElement.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException("Missing or invalid 'api_id' in config (must be a number).");
        }
        if (!root.TryGetProperty("api_hash", out var apiHashElement) || apiHashElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Missing or invalid 'api_hash' in config (must be a string).");
        }

        long apiId = apiIdElement.GetInt64();
        // Trim so a stray space or newline (e.g. from build-time injection) does not corrupt the value.
        string apiHash = (apiHashElement.GetString() ?? string.Empty).Trim();

        if (apiId <= 0)
            throw new InvalidOperationException("api_id must be a positive integer.");
        if (string.IsNullOrWhiteSpace(apiHash))
            throw new InvalidOperationException("api_hash must be a non-empty string.");
        if (!IsValidApiHash(apiHash))
            throw new InvalidOperationException(
                "api_hash is not in the expected format. It must be the 32-character hexadecimal " +
                "string shown on my.telegram.org (characters 0-9 and a-f only). " +
                "See docs/CREDENTIALS.md.");

        var sessionDir = Path.Combine(configDir, "session");
        Directory.CreateDirectory(sessionDir);

        return new NuviaConfig(apiId, apiHash, sessionDir);
    }

    /// <summary>
    /// A Telegram api_hash is exactly 32 hexadecimal characters. Validating it here turns an
    /// otherwise cryptic downstream failure — a hex-parse <see cref="FormatException"/> deep inside
    /// the Telegram client during sign-in, followed by a session-file reconnect loop — into a clear,
    /// safe configuration error. The value itself is never logged or echoed.
    /// </summary>
    private static bool IsValidApiHash(string apiHash)
    {
        if (apiHash.Length != 32)
            return false;
        foreach (var c in apiHash)
        {
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }
        return true;
    }

    public static bool TryLoad(out NuviaConfig config)
        => TryLoad(out config, out _);

    /// <summary>
    /// Load configuration, returning a safe human-readable reason on failure.
    /// The reason never contains credential values.
    /// </summary>
    public static bool TryLoad(out NuviaConfig config, out string error)
    {
        try
        {
            config = Load();
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            config = null!;
            error = ex.Message;
            return false;
        }
    }

    public string GetSessionPath() => Path.Combine(SessionDirectory, "Nuvia.session");
}