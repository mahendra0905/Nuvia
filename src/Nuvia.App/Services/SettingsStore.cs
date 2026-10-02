using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nuvia.App.Services;

/// <summary>
/// Local, non-sensitive user preferences, persisted as JSON beneath <c>%LOCALAPPDATA%\Nuvia\</c>.
/// <para>
/// <b>What it stores:</b> only the account-scoped default upload destination (Saved Messages or the
/// one managed private group). Preferences are keyed by Telegram account id, so one account's choice
/// can never be read for another account.
/// </para>
/// <para>
/// <b>What it never stores:</b> credentials (api_id/api_hash), phone numbers, login codes, 2FA
/// passwords or any session material. Those live only in the config file and the WTelegramClient
/// session, never here.
/// </para>
/// <para>
/// <b>Durability:</b> writes are atomic — the new content is written to a temporary file in the same
/// directory and then swapped into place, so a crash mid-write can never leave a half-written file.
/// Reads recover safely: a missing, empty or corrupt file is treated as "no preferences yet" and the
/// safe default (Saved Messages) is returned rather than throwing.
/// </para>
/// </summary>
public sealed class SettingsStore
{
    private const string SelfValue = "self";
    private const string GroupValue = "group";
    private const int CurrentVersion = 1;

    private readonly string _path;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string SettingsPath => _path;

    public SettingsStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A settings file path is required.", nameof(path));

        _path = path;

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// The account's chosen default upload destination. Returns <see cref="StorageDestinationKind.SavedMessages"/>
    /// when nothing has been stored, when the account id is not a real signed-in account, or when the
    /// settings file is missing/corrupt. This never recreates or contacts a group — it only reads a label.
    /// </summary>
    public StorageDestinationKind GetPreferredDestinationKind(long accountId)
    {
        // Account id 0 means "no real account" (demo mode); it has no persisted preference.
        if (accountId == 0)
            return StorageDestinationKind.SavedMessages;

        lock (_gate)
        {
            var data = LoadSafe();
            if (data.Accounts is not null
                && data.Accounts.TryGetValue(KeyFor(accountId), out var account)
                && string.Equals(account?.DefaultDestination, GroupValue, StringComparison.Ordinal))
            {
                return StorageDestinationKind.ManagedGroup;
            }

            return StorageDestinationKind.SavedMessages;
        }
    }

    /// <summary>
    /// Record the account's default upload destination, then persist atomically. Reading the current
    /// file first preserves any other accounts' preferences.
    /// </summary>
    public void SetPreferredDestinationKind(long accountId, StorageDestinationKind kind)
    {
        if (accountId == 0)
            throw new ArgumentException("A real account id is required to store a preference.", nameof(accountId));

        lock (_gate)
        {
            var data = LoadSafe();
            data.Version = CurrentVersion;
            data.Accounts ??= new Dictionary<string, AccountSettings>(StringComparer.Ordinal);

            var key = KeyFor(accountId);
            if (!data.Accounts.TryGetValue(key, out var account) || account is null)
            {
                account = new AccountSettings();
                data.Accounts[key] = account;
            }

            account.DefaultDestination =
                kind == StorageDestinationKind.ManagedGroup ? GroupValue : SelfValue;

            SaveAtomic(data);
        }
    }

    /// <summary>
    /// Whether the account has opted to <b>also</b> show external files (files added to Saved Messages
    /// outside Nuvia) alongside the files Nuvia itself uploaded. Defaults to <c>false</c> — Saved Messages
    /// shows only Nuvia's own files — when nothing has been stored, when the account id is not a real
    /// signed-in account, or when the settings file is missing/corrupt. This preference only ever affects
    /// the account's own Saved Messages; the managed group always shows every file and has no toggle.
    /// </summary>
    public bool GetShowExternalSavedMessages(long accountId)
    {
        // Account id 0 means "no real account" (demo mode); it has no persisted preference.
        if (accountId == 0)
            return false;

        lock (_gate)
        {
            var data = LoadSafe();
            if (data.Accounts is not null
                && data.Accounts.TryGetValue(KeyFor(accountId), out var account)
                && account?.ShowExternalSavedMessages is true)
            {
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Record whether the account wants external Saved Messages files shown too, then persist atomically.
    /// Reading the current file first preserves this account's other preferences and every other account's.
    /// </summary>
    public void SetShowExternalSavedMessages(long accountId, bool showExternal)
    {
        if (accountId == 0)
            throw new ArgumentException("A real account id is required to store a preference.", nameof(accountId));

        lock (_gate)
        {
            var data = LoadSafe();
            data.Version = CurrentVersion;
            data.Accounts ??= new Dictionary<string, AccountSettings>(StringComparer.Ordinal);

            var key = KeyFor(accountId);
            if (!data.Accounts.TryGetValue(key, out var account) || account is null)
            {
                account = new AccountSettings();
                data.Accounts[key] = account;
            }

            account.ShowExternalSavedMessages = showExternal;

            SaveAtomic(data);
        }
    }

    // --- App-global update/notification preferences (not account-scoped) --------------------------------
    // These describe the install, not a signed-in account (the update check compares the app's own version),
    // so they live as top-level fields on the settings file rather than under an account key.

    /// <summary>
    /// Whether Nuvia may check for updates in the background. Defaults to <c>true</c> (opt-out) when nothing
    /// has been stored or the file is missing/corrupt. A manual "Check for updates now" is a separate,
    /// explicit user action and is not gated by this flag.
    /// </summary>
    public bool GetAutoCheckUpdates()
    {
        lock (_gate)
        {
            return LoadSafe().AutoCheckUpdates ?? true;
        }
    }

    /// <summary>Record whether background update checks are enabled, then persist atomically.</summary>
    public void SetAutoCheckUpdates(bool enabled)
    {
        lock (_gate)
        {
            var data = LoadSafe();
            data.Version = CurrentVersion;
            data.AutoCheckUpdates = enabled;
            SaveAtomic(data);
        }
    }

    /// <summary>The newest update version the user has already seen in the notification center, or null.</summary>
    public string? GetLastSeenUpdateVersion()
    {
        lock (_gate)
        {
            return LoadSafe().LastSeenUpdateVersion;
        }
    }

    /// <summary>Record the newest update version the user has acknowledged (clears the unread badge).</summary>
    public void SetLastSeenUpdateVersion(string? version)
    {
        lock (_gate)
        {
            var data = LoadSafe();
            data.Version = CurrentVersion;
            data.LastSeenUpdateVersion = string.IsNullOrWhiteSpace(version) ? null : version;
            SaveAtomic(data);
        }
    }

    /// <summary>Announcement ids the user has already seen. Empty when none stored or the file is unreadable.</summary>
    public IReadOnlyList<string> GetSeenAnnouncementIds()
    {
        lock (_gate)
        {
            return LoadSafe().SeenAnnouncementIds ?? Array.Empty<string>();
        }
    }

    /// <summary>Add announcement ids to the "already seen" set (union, de-duplicated), then persist.</summary>
    public void AddSeenAnnouncementIds(IEnumerable<string> ids)
    {
        if (ids is null)
            return;

        lock (_gate)
        {
            var data = LoadSafe();
            var set = new HashSet<string>(data.SeenAnnouncementIds ?? Array.Empty<string>(), StringComparer.Ordinal);
            var changed = false;
            foreach (var id in ids)
            {
                if (!string.IsNullOrWhiteSpace(id) && set.Add(id))
                    changed = true;
            }

            if (!changed)
                return;

            data.Version = CurrentVersion;
            var array = new string[set.Count];
            set.CopyTo(array);
            data.SeenAnnouncementIds = array;
            SaveAtomic(data);
        }
    }

    /// <summary>When the last successful update check completed (UTC), or null if it never has.</summary>
    public DateTimeOffset? GetLastUpdateCheckUtc()
    {
        lock (_gate)
        {
            var raw = LoadSafe().LastUpdateCheckUtc;
            if (string.IsNullOrWhiteSpace(raw))
                return null;
            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt)
                ? dt
                : null;
        }
    }

    /// <summary>Record when an update check completed. Stored as a round-trippable UTC ISO 8601 string.</summary>
    public void SetLastUpdateCheckUtc(DateTimeOffset whenUtc)
    {
        lock (_gate)
        {
            var data = LoadSafe();
            data.Version = CurrentVersion;
            data.LastUpdateCheckUtc = whenUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            SaveAtomic(data);
        }
    }

    private static string KeyFor(long accountId) => accountId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Load the settings file, returning an empty settings object on any failure. A corrupt or
    /// unreadable file is deliberately treated as "no preferences" rather than throwing, so a bad file
    /// can never stop the app from starting or from reading a preference. The bad file is left on disk
    /// untouched (a later successful write replaces it).
    /// </summary>
    private SettingsData LoadSafe()
    {
        try
        {
            if (!File.Exists(_path))
                return new SettingsData();

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
                return new SettingsData();

            var data = JsonSerializer.Deserialize<SettingsData>(json, ReadOptions);
            return data ?? new SettingsData();
        }
        catch
        {
            // Malformed JSON, IO error, or unexpected shape — recover to safe defaults.
            return new SettingsData();
        }
    }

    /// <summary>
    /// Write the settings atomically: serialize to a temporary file in the same directory, flush it to
    /// disk, then swap it into place. The swap (<see cref="File.Replace(string,string,string?)"/> when a
    /// file already exists, otherwise <see cref="File.Move(string,string)"/>) is a single filesystem
    /// operation, so a reader never sees a partially-written file and a crash leaves either the old file
    /// or the new one — never a truncated one.
    /// </summary>
    private void SaveAtomic(SettingsData data)
    {
        var json = JsonSerializer.Serialize(data, WriteOptions);

        // Unique temp name in the same directory, so the swap stays on one volume (a cross-volume
        // File.Replace/Move would not be atomic).
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path)) ?? ".";
        var tempPath = Path.Combine(
            directory,
            Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                // Atomic replace, preserving the destination's attributes. No backup file is kept.
                File.Replace(tempPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(tempPath, _path);
            }
        }
        finally
        {
            // If the swap failed for any reason, do not leave the temporary file lying around.
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best effort — a leftover temp file is harmless and will be overwritten next time.
            }
        }
    }

    /// <summary>On-disk shape. Kept deliberately tiny and free of any sensitive field.</summary>
    private sealed class SettingsData
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        /// <summary>Per-account preferences, keyed by the Telegram account id as a string.</summary>
        [JsonPropertyName("accounts")]
        public Dictionary<string, AccountSettings>? Accounts { get; set; }

        /// <summary>App-global: may Nuvia check for updates in the background? Null/absent = default (true).</summary>
        [JsonPropertyName("autoCheckUpdates")]
        public bool? AutoCheckUpdates { get; set; }

        /// <summary>App-global: newest update version already seen in the notification center.</summary>
        [JsonPropertyName("lastSeenUpdateVersion")]
        public string? LastSeenUpdateVersion { get; set; }

        /// <summary>App-global: announcement ids already seen (so they stop lighting the unread badge).</summary>
        [JsonPropertyName("seenAnnouncementIds")]
        public string[]? SeenAnnouncementIds { get; set; }

        /// <summary>App-global: when the last successful update check completed (UTC ISO 8601 string).</summary>
        [JsonPropertyName("lastUpdateCheckUtc")]
        public string? LastUpdateCheckUtc { get; set; }
    }

    private sealed class AccountSettings
    {
        /// <summary>"self" (Saved Messages, the default) or "group" (the managed private group).</summary>
        [JsonPropertyName("defaultDestination")]
        public string? DefaultDestination { get; set; }

        /// <summary>
        /// True when the account opted to also show external Saved Messages files (files added outside
        /// Nuvia). Null/absent means the default: only Nuvia's own uploads are shown. Written only when set,
        /// so older settings files stay unchanged until the user flips the toggle.
        /// </summary>
        [JsonPropertyName("showExternalSavedMessages")]
        public bool? ShowExternalSavedMessages { get; set; }
    }
}
