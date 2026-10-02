using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Nuvia.App.Services;

/// <summary>
/// The local index: a record of which files this app uploaded, and how to find them again.
/// <para>
/// This is a <b>local index, not a mirror of Telegram history</b>. It only ever contains rows this
/// app wrote after a confirmed remote success. Files that exist in Saved Messages but were not
/// uploaded through Nuvia are invisible here, and deleting a row never deletes the remote file.
/// </para>
/// <para>
/// Every row is scoped to a Telegram account id (<c>owner_id</c>), so records belonging to one
/// account can never surface while a different account is signed in.
/// </para>
/// </summary>
public sealed class IndexStore
{
    /// <summary>Current schema version = number of migrations that should have been applied.</summary>
    public const int SchemaVersion = 4;

    /// <summary>
    /// <c>source</c> value for a row Nuvia uploaded itself, or recognised on import by its upload marker.
    /// This is the value every pre-v3 row is migrated to, and the default for the upload path.
    /// </summary>
    public const string SourceNuvia = "nuvia";

    /// <summary>
    /// <c>source</c> value for a row discovered during a remote import that was <b>not</b> uploaded by
    /// Nuvia (no upload marker) — a file added to the destination directly through Telegram.
    /// </summary>
    public const string SourceExternal = "external";

    /// <summary>
    /// Upper bound on a local display name. This is a local UI label only — it is never sent to
    /// Telegram and never used as a filesystem path — so the cap is generous. The rename dialog uses
    /// the same value, so the UI and the store agree on what is acceptable.
    /// </summary>
    public const int MaxDisplayNameLength = 255;

    private readonly string _dbPath;

    public string DatabasePath => _dbPath;

    public IndexStore(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("A database path is required.", nameof(dbPath));

        _dbPath = dbPath;

        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    private SqliteConnection OpenConnection()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Default,
        }.ToString();

        var conn = new SqliteConnection(cs);
        conn.Open();

        // Enforce declared constraints, and prefer WAL so a reader is never blocked by a writer.
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL;";
            pragma.ExecuteNonQuery();
        }

        return conn;
    }

    /// <summary>
    /// Bring the database up to <see cref="SchemaVersion"/>.
    /// Migrations are applied in order, each inside its own transaction, and the applied version is
    /// recorded with <c>PRAGMA user_version</c> so a re-run is a no-op.
    /// </summary>
    public void Migrate()
    {
        using var conn = OpenConnection();

        var current = GetUserVersion(conn);
        if (current > SchemaVersion)
        {
            throw new InvalidOperationException(
                $"This index was created by a newer version of Nuvia (schema {current}, " +
                $"this build understands {SchemaVersion}). Refusing to modify it.");
        }

        for (var target = current + 1; target <= SchemaVersion; target++)
        {
            using var tx = conn.BeginTransaction();
            foreach (var statement in Migrations[target])
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = statement;
                cmd.ExecuteNonQuery();
            }
            using (var setVersion = conn.CreateCommand())
            {
                setVersion.Transaction = tx;
                // PRAGMA does not accept parameters; target is an int we control, never user input.
                setVersion.CommandText = "PRAGMA user_version = " +
                    target.ToString(CultureInfo.InvariantCulture) + ";";
                setVersion.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Ordered, append-only migration steps. Index N is applied when <c>user_version</c> is N-1.
    /// Never edit a shipped step — add the next one instead.
    /// </summary>
    private static readonly Dictionary<int, string[]> Migrations = new()
    {
        [1] = new[]
        {
            @"CREATE TABLE indexed_files (
                  id                     INTEGER PRIMARY KEY AUTOINCREMENT,
                  owner_id               INTEGER NOT NULL,
                  peer_kind              TEXT    NOT NULL,
                  peer_id                INTEGER NOT NULL,
                  message_id             INTEGER NOT NULL,
                  document_id            INTEGER NOT NULL,
                  access_hash            INTEGER NOT NULL,
                  file_reference         BLOB    NOT NULL,
                  dc_id                  INTEGER NOT NULL,
                  original_file_name     TEXT    NOT NULL,
                  display_name           TEXT    NOT NULL,
                  size_bytes             INTEGER NOT NULL,
                  mime_type              TEXT    NULL,
                  remote_upload_utc      TEXT    NOT NULL,
                  indexed_utc            TEXT    NOT NULL,
                  metadata_refreshed_utc TEXT    NULL
              );",
            // One remote message can back at most one indexed row per account.
            @"CREATE UNIQUE INDEX ux_indexed_files_remote
                  ON indexed_files (owner_id, peer_kind, peer_id, message_id);",
            // The list view always asks for one account, newest first.
            @"CREATE INDEX ix_indexed_files_owner
                  ON indexed_files (owner_id, indexed_utc DESC, id DESC);",
        },
        [2] = new[]
        {
            // The one optional managed private storage group, scoped per account. The PRIMARY KEY on
            // account_id enforces "at most one group per account": a second create for the same account
            // replaces the row rather than accumulating groups.
            @"CREATE TABLE storage_groups (
                  account_id   INTEGER PRIMARY KEY,
                  channel_id   INTEGER NOT NULL,
                  access_hash  INTEGER NOT NULL,
                  title        TEXT    NOT NULL,
                  created_utc  TEXT    NOT NULL
              );",
            // The account's chosen default upload destination. Absent row = Saved Messages.
            @"CREATE TABLE account_prefs (
                  account_id       INTEGER PRIMARY KEY,
                  destination_kind TEXT    NOT NULL
              );",
        },
        [3] = new[]
        {
            // Tag each row with how it entered the index. Existing rows were all written by Nuvia's own
            // upload path, so 'nuvia' is the correct default for them and for the ongoing upload path.
            // A remote import writes 'external' for files it finds that Nuvia did not upload.
            @"ALTER TABLE indexed_files ADD COLUMN source TEXT NOT NULL DEFAULT '" + SourceNuvia + "';",
            // High-water mark of the remote history scan, per (account, peer). Lets a re-import fetch
            // only messages newer than the last scan instead of re-reading the whole history each time.
            // last_scanned_id is the highest message id examined for that peer, whether or not it was
            // stored (so peers full of external files do not force a full re-scan every launch).
            @"CREATE TABLE import_state (
                  account_id      INTEGER NOT NULL,
                  peer_kind       TEXT    NOT NULL,
                  peer_id         INTEGER NOT NULL,
                  last_scanned_id INTEGER NOT NULL,
                  updated_utc     TEXT    NOT NULL,
                  PRIMARY KEY (account_id, peer_kind, peer_id)
              );",
        },
        [4] = new[]
        {
            // Drop the "at most one group per account" cap. SQLite cannot alter a primary key in place,
            // so rebuild storage_groups with a composite PK (account_id, channel_id): an account may now
            // keep as many managed groups as Telegram allows, and each create adds a row instead of
            // replacing the previous one. Every existing row is copied across unchanged.
            @"CREATE TABLE storage_groups_v4 (
                  account_id   INTEGER NOT NULL,
                  channel_id   INTEGER NOT NULL,
                  access_hash  INTEGER NOT NULL,
                  title        TEXT    NOT NULL,
                  created_utc  TEXT    NOT NULL,
                  PRIMARY KEY (account_id, channel_id)
              );",
            @"INSERT INTO storage_groups_v4 (account_id, channel_id, access_hash, title, created_utc)
                  SELECT account_id, channel_id, access_hash, title, created_utc FROM storage_groups;",
            @"DROP TABLE storage_groups;",
            @"ALTER TABLE storage_groups_v4 RENAME TO storage_groups;",
            // Nuvia-local folders: a purely local organisation layer inside a single location (Saved
            // Messages or one managed group). Folders are never sent to Telegram — like a local rename,
            // they only live in this index. location_kind/location_peer_id mirror a file's peer scoping
            // ('self' + account id, or 'channel' + channel id). parent_folder_id gives nesting; a NULL
            // parent is a top-level folder in that location.
            @"CREATE TABLE folders (
                  id               INTEGER PRIMARY KEY AUTOINCREMENT,
                  account_id       INTEGER NOT NULL,
                  location_kind    TEXT    NOT NULL,
                  location_peer_id INTEGER NOT NULL,
                  parent_folder_id INTEGER NULL,
                  name             TEXT    NOT NULL,
                  created_utc      TEXT    NOT NULL
              );",
            @"CREATE INDEX ix_folders_location
                  ON folders (account_id, location_kind, location_peer_id, parent_folder_id);",
            // Which local folder a file is filed under, or NULL for the location root. Purely local, and
            // preserved across re-imports exactly like display_name (the import path never writes it).
            @"ALTER TABLE indexed_files ADD COLUMN folder_id INTEGER NULL;",
        },
    };

    // ---------------------------------------------------------------- reads

    /// <summary>
    /// Rows for one account, newest first. Rows belonging to any other account are never returned.
    /// </summary>
    public IReadOnlyList<IndexedFile> ListForAccount(long ownerId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, owner_id, peer_kind, peer_id, message_id, document_id, access_hash,
                   file_reference, dc_id, original_file_name, display_name, size_bytes,
                   mime_type, remote_upload_utc, indexed_utc, metadata_refreshed_utc, source, folder_id
            FROM indexed_files
            WHERE owner_id = @owner
            ORDER BY indexed_utc DESC, id DESC;";
        cmd.Parameters.AddWithValue("@owner", ownerId);

        var results = new List<IndexedFile>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadRow(reader, ownerId));

        return results;
    }

    // --------------------------------------------------------------- writes

    /// <summary>
    /// Insert a row for a file that is already confirmed to exist remotely.
    /// <para>
    /// Callers must pass a <see cref="SavedMessageRef"/> that came back from a real server response.
    /// This method refuses a reference without usable identifiers rather than writing a row that
    /// could never be resolved again.
    /// </para>
    /// </summary>
    public IndexedFile Insert(
        long ownerId,
        SavedMessageRef remote,
        string originalFileName,
        string displayName,
        long sizeBytes,
        string? mimeType,
        DateTime indexedUtc,
        string source = SourceNuvia,
        long? folderId = null)
    {
        if (ownerId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(ownerId));
        if (remote is null)
            throw new ArgumentNullException(nameof(remote));
        if (!remote.LooksUsable())
            throw new ArgumentException(
                "Refusing to index a reference without a usable message, document and file reference.",
                nameof(remote));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO indexed_files
                (owner_id, peer_kind, peer_id, message_id, document_id, access_hash,
                 file_reference, dc_id, original_file_name, display_name, size_bytes,
                 mime_type, remote_upload_utc, indexed_utc, metadata_refreshed_utc, source, folder_id)
            VALUES
                (@owner, @peerKind, @peerId, @messageId, @documentId, @accessHash,
                 @fileReference, @dcId, @originalName, @displayName, @size,
                 @mime, @remoteUtc, @indexedUtc, NULL, @source, @folder);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@owner", ownerId);
        cmd.Parameters.AddWithValue("@peerKind", remote.PeerKind);
        cmd.Parameters.AddWithValue("@peerId", remote.PeerId);
        cmd.Parameters.AddWithValue("@messageId", remote.MessageId);
        cmd.Parameters.AddWithValue("@documentId", remote.DocumentId);
        cmd.Parameters.AddWithValue("@accessHash", remote.AccessHash);
        cmd.Parameters.AddWithValue("@fileReference", remote.FileReference);
        cmd.Parameters.AddWithValue("@dcId", remote.DcId);
        cmd.Parameters.AddWithValue("@originalName", originalFileName);
        cmd.Parameters.AddWithValue("@displayName", displayName);
        cmd.Parameters.AddWithValue("@size", sizeBytes);
        cmd.Parameters.AddWithValue("@mime", (object?)mimeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@remoteUtc", ToDbUtc(remote.UploadedUtc));
        cmd.Parameters.AddWithValue("@indexedUtc", ToDbUtc(indexedUtc));
        cmd.Parameters.AddWithValue("@source", NormalizeSource(source));
        cmd.Parameters.AddWithValue("@folder", (object?)folderId ?? DBNull.Value);

        var newId = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);

        return new IndexedFile
        {
            Id = newId,
            OwnerId = ownerId,
            Remote = remote,
            OriginalFileName = originalFileName,
            DisplayName = displayName,
            SizeBytes = sizeBytes,
            MimeType = mimeType,
            RemoteUploadUtc = remote.UploadedUtc.ToUniversalTime(),
            IndexedUtc = indexedUtc.ToUniversalTime(),
            MetadataRefreshedUtc = null,
            Source = NormalizeSource(source),
            FolderId = folderId,
        };
    }

    /// <summary>Outcome of <see cref="UpsertByRemote"/>: whether a new row was created or an existing one updated.</summary>
    public enum UpsertOutcome { Inserted, Updated }

    /// <summary>
    /// Insert a row for an imported remote file, or update the existing row for the same remote message.
    /// <para>
    /// Matching is by the remote identity (owner, peer kind, peer id, message id) — the same key as the
    /// unique index <c>ux_indexed_files_remote</c>. On update it refreshes the remote columns (document
    /// id, access hash, file reference, dc id, size, mime, remote timestamp) and the
    /// <paramref name="source"/>, and records the refresh time — but it deliberately leaves
    /// <c>display_name</c>, <c>original_file_name</c>, <c>indexed_utc</c> <b>and <c>folder_id</c></b>
    /// untouched, so a re-import never clobbers a local rename, the first-seen time, or a folder the user
    /// since moved the file into.
    /// </para>
    /// <para>
    /// <paramref name="folderId"/> is therefore used on <b>insert only</b>. That asymmetry is the whole
    /// point of the folder feature: an existing install keeps the placement the user chose locally, while a
    /// fresh install (an empty index after a reinstall) has no row at all, so every row is an insert and
    /// every file comes back into the folder its caption names.
    /// </para>
    /// </summary>
    public UpsertOutcome UpsertByRemote(
        long ownerId, SavedMessageRef remote, string originalFileName, string displayName,
        long sizeBytes, string? mimeType, string source, DateTime indexedUtc, long? folderId = null)
    {
        if (ownerId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(ownerId));
        ArgumentNullException.ThrowIfNull(remote);
        if (!remote.LooksUsable())
            throw new ArgumentException("Refusing to index a reference without usable identifiers.", nameof(remote));

        var normalizedSource = NormalizeSource(source);

        using var conn = OpenConnection();

        using (var find = conn.CreateCommand())
        {
            find.CommandText = @"
                SELECT id FROM indexed_files
                WHERE owner_id = @owner AND peer_kind = @peerKind AND peer_id = @peerId AND message_id = @messageId;";
            find.Parameters.AddWithValue("@owner", ownerId);
            find.Parameters.AddWithValue("@peerKind", remote.PeerKind);
            find.Parameters.AddWithValue("@peerId", remote.PeerId);
            find.Parameters.AddWithValue("@messageId", remote.MessageId);
            var existing = find.ExecuteScalar();
            if (existing is not null and not DBNull)
            {
                UpdateImportedRow(conn, Convert.ToInt64(existing, CultureInfo.InvariantCulture),
                    ownerId, remote, sizeBytes, mimeType, normalizedSource, indexedUtc);
                return UpsertOutcome.Updated;
            }
        }

        InsertImportedRow(conn, ownerId, remote, originalFileName, displayName, sizeBytes, mimeType,
            normalizedSource, indexedUtc, folderId);
        return UpsertOutcome.Inserted;
    }

    private static void UpdateImportedRow(
        SqliteConnection conn, long id, long ownerId, SavedMessageRef remote,
        long sizeBytes, string? mimeType, string normalizedSource, DateTime indexedUtc)
    {
        using var upd = conn.CreateCommand();
        upd.CommandText = @"
            UPDATE indexed_files
               SET document_id = @documentId, access_hash = @accessHash, file_reference = @fileReference,
                   dc_id = @dcId, size_bytes = @size, mime_type = @mime, remote_upload_utc = @remoteUtc,
                   source = @source, metadata_refreshed_utc = @refreshedUtc
             WHERE id = @id AND owner_id = @owner;";
        upd.Parameters.AddWithValue("@documentId", remote.DocumentId);
        upd.Parameters.AddWithValue("@accessHash", remote.AccessHash);
        upd.Parameters.AddWithValue("@fileReference", remote.FileReference);
        upd.Parameters.AddWithValue("@dcId", remote.DcId);
        upd.Parameters.AddWithValue("@size", sizeBytes);
        upd.Parameters.AddWithValue("@mime", (object?)mimeType ?? DBNull.Value);
        upd.Parameters.AddWithValue("@remoteUtc", ToDbUtc(remote.UploadedUtc));
        upd.Parameters.AddWithValue("@source", normalizedSource);
        upd.Parameters.AddWithValue("@refreshedUtc", ToDbUtc(indexedUtc));
        upd.Parameters.AddWithValue("@id", id);
        upd.Parameters.AddWithValue("@owner", ownerId);
        upd.ExecuteNonQuery();
    }

    private static void InsertImportedRow(
        SqliteConnection conn, long ownerId, SavedMessageRef remote, string originalFileName,
        string displayName, long sizeBytes, string? mimeType, string normalizedSource, DateTime indexedUtc,
        long? folderId)
    {
        using var ins = conn.CreateCommand();
        ins.CommandText = @"
            INSERT INTO indexed_files
                (owner_id, peer_kind, peer_id, message_id, document_id, access_hash,
                 file_reference, dc_id, original_file_name, display_name, size_bytes,
                 mime_type, remote_upload_utc, indexed_utc, metadata_refreshed_utc, source, folder_id)
            VALUES
                (@owner, @peerKind, @peerId, @messageId, @documentId, @accessHash,
                 @fileReference, @dcId, @originalName, @displayName, @size,
                 @mime, @remoteUtc, @indexedUtc, NULL, @source, @folder);";
        ins.Parameters.AddWithValue("@owner", ownerId);
        ins.Parameters.AddWithValue("@peerKind", remote.PeerKind);
        ins.Parameters.AddWithValue("@peerId", remote.PeerId);
        ins.Parameters.AddWithValue("@messageId", remote.MessageId);
        ins.Parameters.AddWithValue("@documentId", remote.DocumentId);
        ins.Parameters.AddWithValue("@accessHash", remote.AccessHash);
        ins.Parameters.AddWithValue("@fileReference", remote.FileReference);
        ins.Parameters.AddWithValue("@dcId", remote.DcId);
        ins.Parameters.AddWithValue("@originalName", originalFileName);
        ins.Parameters.AddWithValue("@displayName", displayName);
        ins.Parameters.AddWithValue("@size", sizeBytes);
        ins.Parameters.AddWithValue("@mime", (object?)mimeType ?? DBNull.Value);
        ins.Parameters.AddWithValue("@remoteUtc", ToDbUtc(remote.UploadedUtc));
        ins.Parameters.AddWithValue("@indexedUtc", ToDbUtc(indexedUtc));
        ins.Parameters.AddWithValue("@source", normalizedSource);
        ins.Parameters.AddWithValue("@folder", (object?)folderId ?? DBNull.Value);
        ins.ExecuteNonQuery();
    }

    /// <summary>
    /// Remove every Saved Messages row for this account that was discovered as <see cref="SourceExternal"/>.
    /// Called when the "show external files in Saved Messages" preference is turned off, so the index stops
    /// retaining metadata for files the user has chosen not to see. Managed-group rows and Nuvia-uploaded
    /// rows are never touched. Local only — it never contacts Telegram.
    /// </summary>
    /// <returns>The number of external Saved Messages rows removed.</returns>
    public int DeleteExternalSelfFiles(long accountId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM indexed_files
            WHERE owner_id = @account AND peer_kind = @kind AND source = @source;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", SavedMessageRef.PeerKindSelf);
        cmd.Parameters.AddWithValue("@source", SourceExternal);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Forget this account's entire local file list in one transaction — every indexed file row, every
    /// Nuvia-local folder, and the per-peer import high-water marks — so the next rebuild re-reads the whole
    /// Telegram history from scratch. Storage-group definitions and the chosen upload destination are left
    /// intact: they point at real Telegram groups, not at the file list, and clearing them would lose the
    /// pointer to a group that still exists. Scoped strictly to <paramref name="accountId"/>, so no other
    /// account's rows can be touched. Local only — it never contacts Telegram and never deletes anything
    /// stored there; files uploaded through Nuvia come back on the next rebuild, local-only edits (custom
    /// names, folders for external files) do not.
    /// </summary>
    /// <returns>The number of indexed file rows removed.</returns>
    public int DeleteAllForAccount(long accountId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using var files = conn.CreateCommand();
        files.Transaction = tx;
        files.CommandText = "DELETE FROM indexed_files WHERE owner_id = @account;";
        files.Parameters.AddWithValue("@account", accountId);
        var removed = files.ExecuteNonQuery();

        using var folderCmd = conn.CreateCommand();
        folderCmd.Transaction = tx;
        folderCmd.CommandText = "DELETE FROM folders WHERE account_id = @account;";
        folderCmd.Parameters.AddWithValue("@account", accountId);
        folderCmd.ExecuteNonQuery();

        using var watermark = conn.CreateCommand();
        watermark.Transaction = tx;
        watermark.CommandText = "DELETE FROM import_state WHERE account_id = @account;";
        watermark.Parameters.AddWithValue("@account", accountId);
        watermark.ExecuteNonQuery();

        tx.Commit();
        return removed;
    }

    /// <summary>
    /// The highest remote message id already scanned for one (account, peer), or 0 when that peer has
    /// never been imported. A re-import passes this as the "min id" so only newer messages are fetched.
    /// </summary>
    public int GetImportWatermark(long accountId, string peerKind, long peerId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT last_scanned_id FROM import_state
            WHERE account_id = @account AND peer_kind = @kind AND peer_id = @peer;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", peerKind);
        cmd.Parameters.AddWithValue("@peer", peerId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Record the highest message id scanned for one (account, peer). Advances the mark only forward.</summary>
    public void SetImportWatermark(long accountId, string peerKind, long peerId, int lastScannedId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required.", nameof(accountId));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO import_state (account_id, peer_kind, peer_id, last_scanned_id, updated_utc)
            VALUES (@account, @kind, @peer, @last, @updated)
            ON CONFLICT(account_id, peer_kind, peer_id) DO UPDATE SET
                last_scanned_id = MAX(import_state.last_scanned_id, excluded.last_scanned_id),
                updated_utc     = excluded.updated_utc;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", peerKind);
        cmd.Parameters.AddWithValue("@peer", peerId);
        cmd.Parameters.AddWithValue("@last", lastScannedId);
        cmd.Parameters.AddWithValue("@updated", ToDbUtc(DateTime.UtcNow));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Forget the scan high-water mark for one (account, peer) so the next import re-reads its whole
    /// history. Used when the "show external files" preference is turned on, to pick up external files
    /// that earlier scans saw but did not store.
    /// </summary>
    public void ResetImportWatermark(long accountId, string peerKind, long peerId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM import_state
            WHERE account_id = @account AND peer_kind = @kind AND peer_id = @peer;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", peerKind);
        cmd.Parameters.AddWithValue("@peer", peerId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Coerce an arbitrary source label to one of the two allowed values, defaulting to 'nuvia'.</summary>
    private static string NormalizeSource(string? source)
        => string.Equals(source, SourceExternal, StringComparison.Ordinal) ? SourceExternal : SourceNuvia;

    /// <summary>
    /// Store a freshly fetched file reference for an existing row.
    /// Used when Telegram reports the stored reference as expired.
    /// Returns false when the row does not exist for this account.
    /// </summary>
    public bool ApplyRefreshedReference(long ownerId, long id, SavedMessageRef refreshed, DateTime refreshedUtc)
    {
        if (!refreshed.LooksUsable())
            throw new ArgumentException("Refusing to store an unusable reference.", nameof(refreshed));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE indexed_files
               SET file_reference         = @fileReference,
                   document_id            = @documentId,
                   access_hash            = @accessHash,
                   dc_id                  = @dcId,
                   size_bytes             = @size,
                   mime_type              = @mime,
                   remote_upload_utc      = @remoteUtc,
                   metadata_refreshed_utc = @refreshedUtc
             WHERE id = @id AND owner_id = @owner;";
        cmd.Parameters.AddWithValue("@fileReference", refreshed.FileReference);
        cmd.Parameters.AddWithValue("@documentId", refreshed.DocumentId);
        cmd.Parameters.AddWithValue("@accessHash", refreshed.AccessHash);
        cmd.Parameters.AddWithValue("@dcId", refreshed.DcId);
        cmd.Parameters.AddWithValue("@size", refreshed.SizeBytes);
        cmd.Parameters.AddWithValue("@mime", (object?)refreshed.MimeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@remoteUtc", ToDbUtc(refreshed.UploadedUtc));
        cmd.Parameters.AddWithValue("@refreshedUtc", ToDbUtc(refreshedUtc));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@owner", ownerId);
        return cmd.ExecuteNonQuery() == 1;    // 1 means the row existed AND belonged to this account
    }

    /// <summary>
    /// Change ONLY the local display name of one indexed row.
    /// <para>
    /// This is a purely local edit. It updates the single <c>display_name</c> column and nothing else:
    /// <c>original_file_name</c> (the verbatim name the file was sent with) and every remote identifier
    /// column (peer, message id, document id, access hash, file reference, dc id) are left exactly as
    /// they were. It never contacts Telegram, so the remote message, its caption and the stored document
    /// are untouched. Scoped by <paramref name="id"/> <b>and</b> <paramref name="ownerId"/>, so another
    /// account's row can never be renamed.
    /// </para>
    /// <para>Returns false when no matching row exists for this account (nothing was changed).</para>
    /// </summary>
    public bool UpdateDisplayName(long ownerId, long id, string displayName)
    {
        if (ownerId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(ownerId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A non-empty display name is required.", nameof(displayName));

        var trimmed = displayName.Trim();
        if (trimmed.Length > MaxDisplayNameLength)
            throw new ArgumentException(
                $"The display name must be at most {MaxDisplayNameLength} characters.", nameof(displayName));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        // Deliberately a single-column UPDATE. Touching original_file_name or any remote column here
        // would break the ability to download the file again, so they are never in this statement.
        cmd.CommandText = @"
            UPDATE indexed_files
               SET display_name = @displayName
             WHERE id = @id AND owner_id = @owner;";
        cmd.Parameters.AddWithValue("@displayName", trimmed);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@owner", ownerId);
        return cmd.ExecuteNonQuery() == 1;    // 1 means the row existed AND belonged to this account
    }

    /// <summary>
    /// Removes one file's row from the local index. Scoped by both <paramref name="id"/> and
    /// <paramref name="ownerId"/>, so it can never delete another account's row. This only touches the
    /// local index — it never contacts Telegram; deleting the remote message (when the user asks for it)
    /// is done separately through <c>ITelegramStorage.DeleteDocumentAsync</c>.
    /// </summary>
    /// <returns>True when a row belonging to this account was removed; false when nothing matched.</returns>
    public bool DeleteFile(long ownerId, long id)
    {
        if (ownerId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(ownerId));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM indexed_files WHERE id = @id AND owner_id = @owner;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@owner", ownerId);
        return cmd.ExecuteNonQuery() == 1;    // 1 means the row existed AND belonged to this account
    }

    /// <summary>
    /// Every managed private storage group for this account, oldest first, or an empty list if none
    /// were created. Scoped to the account id, so another account's groups can never be returned.
    /// </summary>
    public IReadOnlyList<ManagedStorageGroup> GetStorageGroups(long accountId)
    {
        if (accountId == 0) return Array.Empty<ManagedStorageGroup>();

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT account_id, channel_id, access_hash, title, created_utc
            FROM storage_groups
            WHERE account_id = @account
            ORDER BY created_utc ASC, channel_id ASC;";
        cmd.Parameters.AddWithValue("@account", accountId);

        var groups = new List<ManagedStorageGroup>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            groups.Add(new ManagedStorageGroup(
                AccountId: reader.GetInt64(0),
                ChannelId: reader.GetInt64(1),
                AccessHash: reader.GetInt64(2),
                Title: reader.GetString(3),
                CreatedUtc: FromDbUtc(reader.GetString(4))));
        }

        return groups;
    }

    /// <summary>
    /// A single managed storage group identified by its channel, or null if this account has no such
    /// group. Scoped to the account id as well, so one account can never read another's group.
    /// </summary>
    public ManagedStorageGroup? GetStorageGroup(long accountId, long channelId)
    {
        if (accountId == 0 || channelId == 0) return null;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT account_id, channel_id, access_hash, title, created_utc
            FROM storage_groups
            WHERE account_id = @account AND channel_id = @channel;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@channel", channelId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new ManagedStorageGroup(
            AccountId: reader.GetInt64(0),
            ChannelId: reader.GetInt64(1),
            AccessHash: reader.GetInt64(2),
            Title: reader.GetString(3),
            CreatedUtc: FromDbUtc(reader.GetString(4)));
    }

    /// <summary>
    /// Persist a managed group after it was created remotely. Each distinct channel is stored as its own
    /// row (composite key account_id + channel_id), so calling this for a new group accumulates rather
    /// than replacing earlier groups; calling it again for the same channel refreshes that row's details.
    /// It never creates anything remotely — it only records identifiers the caller already obtained.
    /// </summary>
    public void SaveStorageGroup(ManagedStorageGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!group.LooksUsable())
            throw new ArgumentException("Refusing to store a group without usable identifiers.", nameof(group));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO storage_groups (account_id, channel_id, access_hash, title, created_utc)
            VALUES (@account, @channel, @hash, @title, @created)
            ON CONFLICT(account_id, channel_id) DO UPDATE SET
                access_hash = excluded.access_hash,
                title       = excluded.title,
                created_utc = excluded.created_utc;";
        cmd.Parameters.AddWithValue("@account", group.AccountId);
        cmd.Parameters.AddWithValue("@channel", group.ChannelId);
        cmd.Parameters.AddWithValue("@hash", group.AccessHash);
        cmd.Parameters.AddWithValue("@title", group.Title);
        cmd.Parameters.AddWithValue("@created", ToDbUtc(group.CreatedUtc));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Remove one managed storage group row AND every local file row that lived in that group's channel,
    /// in one transaction. Any Nuvia-local folders scoped to that channel are dropped too, since their
    /// location is gone. Called after the group has been deleted remotely (or found to be already gone)
    /// so the local index stops pointing at a group that no longer exists. Scoped to both the account id
    /// and the channel id, so no other account's — and no sibling group's — rows can be touched. Local
    /// only — it never contacts Telegram.
    /// </summary>
    /// <returns>The number of orphaned file rows removed.</returns>
    public int DeleteStorageGroup(long accountId, long channelId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using var files = conn.CreateCommand();
        files.Transaction = tx;
        files.CommandText = @"
            DELETE FROM indexed_files
            WHERE owner_id = @account AND peer_kind = @kind AND peer_id = @channel;";
        files.Parameters.AddWithValue("@account", accountId);
        files.Parameters.AddWithValue("@kind", SavedMessageRef.PeerKindChannel);
        files.Parameters.AddWithValue("@channel", channelId);
        var removed = files.ExecuteNonQuery();

        using var folderCmd = conn.CreateCommand();
        folderCmd.Transaction = tx;
        folderCmd.CommandText = @"
            DELETE FROM folders
            WHERE account_id = @account AND location_kind = @kind AND location_peer_id = @channel;";
        folderCmd.Parameters.AddWithValue("@account", accountId);
        folderCmd.Parameters.AddWithValue("@kind", SavedMessageRef.PeerKindChannel);
        folderCmd.Parameters.AddWithValue("@channel", channelId);
        folderCmd.ExecuteNonQuery();

        using var groupCmd = conn.CreateCommand();
        groupCmd.Transaction = tx;
        groupCmd.CommandText = "DELETE FROM storage_groups WHERE account_id = @account AND channel_id = @channel;";
        groupCmd.Parameters.AddWithValue("@account", accountId);
        groupCmd.Parameters.AddWithValue("@channel", channelId);
        groupCmd.ExecuteNonQuery();

        tx.Commit();
        return removed;
    }

    // ---------------------------------------------------------- local folders

    /// <summary>
    /// Every Nuvia folder inside one location (Saved Messages, or one managed group), oldest name order.
    /// Folders are local-only: nothing here exists on Telegram, and another account's folders are never
    /// returned.
    /// </summary>
    public IReadOnlyList<LocalFolder> GetFolders(long accountId, string locationKind, long locationPeerId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, account_id, location_kind, location_peer_id, parent_folder_id, name, created_utc
            FROM folders
            WHERE account_id = @account AND location_kind = @kind AND location_peer_id = @peer
            ORDER BY name COLLATE NOCASE, id;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", locationKind);
        cmd.Parameters.AddWithValue("@peer", locationPeerId);

        var results = new List<LocalFolder>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(ReadFolder(reader));

        return results;
    }

    /// <summary>One folder by id, or null when it does not exist for this account.</summary>
    public LocalFolder? GetFolder(long accountId, long folderId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, account_id, location_kind, location_peer_id, parent_folder_id, name, created_utc
            FROM folders
            WHERE id = @id AND account_id = @account;";
        cmd.Parameters.AddWithValue("@id", folderId);
        cmd.Parameters.AddWithValue("@account", accountId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFolder(reader) : null;
    }

    /// <summary>
    /// Create one folder inside a location, optionally nested under <paramref name="parentFolderId"/>.
    /// </summary>
    /// <remarks>
    /// A sibling may not share its name (case-insensitively) — see <see cref="DuplicateFolderNameException"/>.
    /// Local only: this writes one row and contacts nothing.
    /// </remarks>
    /// <returns>The new folder's id.</returns>
    public long CreateFolder(
        long accountId, string locationKind, long locationPeerId, long? parentFolderId, string name)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));
        if (string.IsNullOrWhiteSpace(locationKind))
            throw new ArgumentException("A location kind is required.", nameof(locationKind));
        if (!FolderNames.IsValidName(name))
            throw new ArgumentException(
                $"A folder name must be 1–{FolderNames.MaxNameLength} characters, with no slashes. "
                + "Choose a different name.", nameof(name));

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        if (parentFolderId is { } parent)
            RequireFolderInLocation(conn, tx, accountId, parent, locationKind, locationPeerId);

        var id = FindChildFolder(conn, tx, accountId, locationKind, locationPeerId, parentFolderId, name);
        if (id is not null)
            throw new DuplicateFolderNameException(name);

        var created = InsertFolder(conn, tx, accountId, locationKind, locationPeerId, parentFolderId, name);
        tx.Commit();
        return created;
    }

    /// <summary>
    /// Find (or create) the whole chain of folders named by <paramref name="segments"/> inside one location,
    /// and return the leaf folder's id.
    /// <para>
    /// This is what a rebuild uses to restore folders from the paths hidden in Telegram captions. It is
    /// deliberately find-or-create rather than create: a re-import over messages whose folders already
    /// exist must be a no-op, never a duplicate or an error. It is called after
    /// <see cref="NuviaMarkers.TryReadFolderPath"/>, so every segment has already been validated.
    /// </para>
    /// </summary>
    public long EnsureFolderPath(
        long accountId, string locationKind, long locationPeerId, IReadOnlyList<string> segments)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0)
            throw new ArgumentException("At least one folder segment is required.", nameof(segments));

        foreach (var segment in segments)
        {
            if (!FolderNames.IsValidName(segment))
                throw new ArgumentException($"“{segment}” is not a usable folder name.", nameof(segments));
        }

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        long? parent = null;
        long leaf = 0;

        foreach (var segment in segments)
        {
            var existing = FindChildFolder(conn, tx, accountId, locationKind, locationPeerId, parent, segment);
            leaf = existing
                ?? InsertFolder(conn, tx, accountId, locationKind, locationPeerId, parent, segment);
            parent = leaf;
        }

        tx.Commit();
        return leaf;
    }

    /// <summary>
    /// Rename one folder. Purely local — a folder is a Nuvia label with no Telegram counterpart.
    /// </summary>
    /// <returns>
    /// False when the folder is not in this account's index (nothing was changed). Throws
    /// <see cref="DuplicateFolderNameException"/> when a sibling already has the new name.
    /// </returns>
    public bool RenameFolder(long accountId, long folderId, string newName)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));
        if (!FolderNames.IsValidName(newName))
            throw new ArgumentException(
                $"A folder name must be 1–{FolderNames.MaxNameLength} characters, with no slashes. "
                + "Choose a different name.", nameof(newName));

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var folder = ReadFolderById(conn, tx, accountId, folderId);
        if (folder is null)
            return false;

        var clash = FindChildFolder(conn, tx, accountId, folder.LocationKind, folder.LocationPeerId,
            folder.ParentFolderId, newName);
        if (clash is not null && clash.Value != folderId)
            throw new DuplicateFolderNameException(newName);

        using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE folders SET name = @name WHERE id = @id AND account_id = @account;";
        upd.Parameters.AddWithValue("@name", newName);
        upd.Parameters.AddWithValue("@id", folderId);
        upd.Parameters.AddWithValue("@account", accountId);
        var changed = upd.ExecuteNonQuery();

        tx.Commit();
        return changed > 0;
    }

    /// <summary>
    /// How many indexed files sit in a folder <b>or any folder nested under it</b>. Used to refuse deleting
    /// a folder that still holds files, so a delete can never make a file row point at nothing.
    /// </summary>
    public int CountFilesInFolderTree(long accountId, long folderId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = FolderTreeFileCountSql;
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@folder", folderId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Delete a folder and every folder nested under it.
    /// <para>
    /// Refuses — and deletes nothing at all — when the tree still holds files: Nuvia will not silently
    /// drop files back to the location root or orphan a row. The caller is expected to check
    /// <see cref="CountFilesInFolderTree"/> first to explain that to the user; this is the backstop.
    /// </para>
    /// </summary>
    /// <returns>How many folders were removed (the tree, including the folder itself).</returns>
    public int DeleteFolderTree(long accountId, long folderId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        var files = CountFilesInFolderTree(conn, tx, accountId, folderId);
        if (files > 0)
            throw new InvalidOperationException(
                $"This folder still holds {files} file(s), including any nested folders. "
                + "Move or delete them first.");

        using var del = conn.CreateCommand();
        del.Transaction = tx;
        del.CommandText = @"
            WITH RECURSIVE tree(id) AS (
                SELECT id FROM folders WHERE id = @folder AND account_id = @account
                UNION ALL
                SELECT f.id FROM folders f JOIN tree t ON f.parent_folder_id = t.id
                 WHERE f.account_id = @account
            )
            DELETE FROM folders WHERE account_id = @account AND id IN (SELECT id FROM tree);";
        del.Parameters.AddWithValue("@account", accountId);
        del.Parameters.AddWithValue("@folder", folderId);
        var removed = del.ExecuteNonQuery();

        tx.Commit();
        return removed;
    }

    /// <summary>
    /// File a set of local rows under <paramref name="folderId"/> (null moves them back to the location
    /// root). Purely local: no Telegram message, caption or document is touched here — the caller
    /// separately updates the hidden caption marker so the placement survives a reinstall.
    /// </summary>
    /// <param name="locationKind">
    /// The location the rows live in. Every row must match it, so a file belonging to Saved Messages can
    /// never be filed into a managed group's folder.
    /// </param>
    /// <returns>How many rows actually changed.</returns>
    public int SetFilesFolder(
        long accountId, IReadOnlyList<long> fileIds, string locationKind, long locationPeerId, long? folderId)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required to scope the record.", nameof(accountId));
        if (string.IsNullOrWhiteSpace(locationKind))
            throw new ArgumentException("A location kind is required.", nameof(locationKind));
        ArgumentNullException.ThrowIfNull(fileIds);
        if (fileIds.Count == 0) return 0;

        using var conn = OpenConnection();

        if (folderId is { } folder)
        {
            var target = GetFolder(accountId, folder)
                ?? throw new ArgumentException("That folder is no longer in the local index.", nameof(folderId));
            if (!string.Equals(target.LocationKind, locationKind, StringComparison.Ordinal)
                || target.LocationPeerId != locationPeerId)
                throw new ArgumentException(
                    "That folder belongs to a different location.", nameof(folderId));
        }

        // Build the "@f0, @f1, …" id placeholder list without LINQ (this file deliberately uses no
        // System.Linq), matching the explicit-loop style used elsewhere here.
        var placeholders = new StringBuilder();
        for (var i = 0; i < fileIds.Count; i++)
        {
            if (i > 0) placeholders.Append(", ");
            placeholders.Append("@f").Append(i);
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE indexed_files
               SET folder_id = @folder
             WHERE owner_id = @account
               AND peer_kind = @kind AND peer_id = @peer
               AND id IN ({placeholders});";
        cmd.Parameters.AddWithValue("@folder", (object?)folderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", locationKind);
        cmd.Parameters.AddWithValue("@peer", locationPeerId);
        for (var i = 0; i < fileIds.Count; i++)
            cmd.Parameters.AddWithValue("@f" + i, fileIds[i]);

        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The set of a folder plus every folder nested under it, as a recursive CTE. Written once so the
    /// count and the delete can never drift apart.
    /// </summary>
    private const string FolderTreeCte = @"
        WITH RECURSIVE tree(id) AS (
            SELECT id FROM folders WHERE id = @folder AND account_id = @account
            UNION ALL
            SELECT f.id FROM folders f JOIN tree t ON f.parent_folder_id = t.id
             WHERE f.account_id = @account
        )";

    private const string FolderTreeFileCountSql = FolderTreeCte + @"
        SELECT COUNT(*) FROM indexed_files
         WHERE owner_id = @account AND folder_id IN (SELECT id FROM tree);";

    private static int CountFilesInFolderTree(SqliteConnection conn, SqliteTransaction tx, long accountId, long folderId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = FolderTreeFileCountSql;
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@folder", folderId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The id of a sibling folder with this name, or null. Matching is case-insensitive (SQLite
    /// <c>NOCASE</c>), and a null parent is compared through the <c>IFNULL</c> sentinel because SQLite
    /// treats every NULL as distinct. Used by both create and rename so they agree on what a duplicate is.
    /// </summary>
    private static long? FindChildFolder(
        SqliteConnection conn, SqliteTransaction tx, long accountId, string locationKind,
        long locationPeerId, long? parentFolderId, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT id FROM folders
             WHERE account_id = @account AND location_kind = @kind AND location_peer_id = @peer
               AND IFNULL(parent_folder_id, -1) = IFNULL(@parent, -1)
               AND name = @name COLLATE NOCASE
             LIMIT 1;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", locationKind);
        cmd.Parameters.AddWithValue("@peer", locationPeerId);
        cmd.Parameters.AddWithValue("@parent", (object?)parentFolderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", name);

        var found = cmd.ExecuteScalar();
        return found is null or DBNull ? null : Convert.ToInt64(found, CultureInfo.InvariantCulture);
    }

    private static long InsertFolder(
        SqliteConnection conn, SqliteTransaction tx, long accountId, string locationKind,
        long locationPeerId, long? parentFolderId, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            INSERT INTO folders
                (account_id, location_kind, location_peer_id, parent_folder_id, name, created_utc)
            VALUES
                (@account, @kind, @peer, @parent, @name, @created);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", locationKind);
        cmd.Parameters.AddWithValue("@peer", locationPeerId);
        cmd.Parameters.AddWithValue("@parent", (object?)parentFolderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@created", ToDbUtc(DateTime.UtcNow));
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void RequireFolderInLocation(
        SqliteConnection conn, SqliteTransaction tx, long accountId, long folderId,
        string locationKind, long locationPeerId)
    {
        var folder = ReadFolderById(conn, tx, accountId, folderId)
            ?? throw new ArgumentException("The parent folder is no longer in the local index.", nameof(folderId));

        if (!string.Equals(folder.LocationKind, locationKind, StringComparison.Ordinal)
            || folder.LocationPeerId != locationPeerId)
            throw new ArgumentException("The parent folder belongs to a different location.", nameof(folderId));
    }

    private static LocalFolder? ReadFolderById(
        SqliteConnection conn, SqliteTransaction tx, long accountId, long folderId)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT id, account_id, location_kind, location_peer_id, parent_folder_id, name, created_utc
            FROM folders WHERE id = @id AND account_id = @account;";
        cmd.Parameters.AddWithValue("@id", folderId);
        cmd.Parameters.AddWithValue("@account", accountId);

        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadFolder(reader) : null;
    }

    private static LocalFolder ReadFolder(SqliteDataReader reader) => new(
        Id: reader.GetInt64(0),
        AccountId: reader.GetInt64(1),
        LocationKind: reader.GetString(2),
        LocationPeerId: reader.GetInt64(3),
        ParentFolderId: reader.IsDBNull(4) ? null : reader.GetInt64(4),
        Name: reader.GetString(5),
        CreatedUtc: FromDbUtc(reader.GetString(6)));

    /// <summary>
    /// The account's chosen default upload destination. Defaults to Saved Messages when no preference
    /// has been stored, or when a stored preference of "group" can no longer be honoured because the
    /// group row is gone (access lost). This never recreates a missing group.
    /// </summary>
    public StorageDestinationKind GetPreferredDestinationKind(long accountId)
    {
        if (accountId == 0) return StorageDestinationKind.SavedMessages;

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT destination_kind FROM account_prefs WHERE account_id = @account;";
        cmd.Parameters.AddWithValue("@account", accountId);

        var value = cmd.ExecuteScalar() as string;
        return string.Equals(value, "group", StringComparison.Ordinal)
            ? StorageDestinationKind.ManagedGroup
            : StorageDestinationKind.SavedMessages;
    }

    /// <summary>Record the account's default upload destination.</summary>
    public void SetPreferredDestinationKind(long accountId, StorageDestinationKind kind)
    {
        if (accountId == 0)
            throw new ArgumentException("An account id is required.", nameof(accountId));

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO account_prefs (account_id, destination_kind)
            VALUES (@account, @kind)
            ON CONFLICT(account_id) DO UPDATE SET destination_kind = excluded.destination_kind;";
        cmd.Parameters.AddWithValue("@account", accountId);
        cmd.Parameters.AddWithValue("@kind", kind == StorageDestinationKind.ManagedGroup ? "group" : "self");
        cmd.ExecuteNonQuery();
    }

    // ----------------------------------------------------------------- misc

    private static IndexedFile ReadRow(SqliteDataReader reader, long ownerId)
    {
        var fileReference = reader.GetFieldValue<byte[]>(7);
        var size = reader.GetInt64(11);
        string? mime = reader.IsDBNull(12) ? null : reader.GetString(12);
        var remoteUploadUtc = FromDbUtc(reader.GetString(13));

        var remote = new SavedMessageRef(
            PeerKind: reader.GetString(2),
            PeerId: reader.GetInt64(3),
            MessageId: reader.GetInt32(4),
            DocumentId: reader.GetInt64(5),
            AccessHash: reader.GetInt64(6),
            FileReference: fileReference,
            DcId: reader.GetInt32(8),
            SizeBytes: size,
            MimeType: mime,
            UploadedUtc: remoteUploadUtc);

        return new IndexedFile
        {
            Id = reader.GetInt64(0),
            OwnerId = ownerId,
            Remote = remote,
            OriginalFileName = reader.GetString(9),
            DisplayName = reader.GetString(10),
            SizeBytes = size,
            MimeType = mime,
            RemoteUploadUtc = remoteUploadUtc,
            IndexedUtc = FromDbUtc(reader.GetString(14)),
            MetadataRefreshedUtc = reader.IsDBNull(15) ? null : FromDbUtc(reader.GetString(15)),
            Source = reader.IsDBNull(16) ? SourceNuvia : reader.GetString(16),
            FolderId = reader.IsDBNull(17) ? null : reader.GetInt64(17),
        };
    }

    /// <summary>Timestamps are persisted as round-trip ISO-8601 in UTC, always with a Z suffix.</summary>
    internal static string ToDbUtc(DateTime value)
        => value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads back a timestamp written by <see cref="ToDbUtc"/>.
    /// <para>
    /// <c>RoundtripKind</c> alone is used deliberately: it preserves the UTC kind carried by the
    /// stored trailing "Z", and it cannot be combined with <c>AdjustToUniversal</c> — the BCL
    /// rejects that pair with an <see cref="ArgumentException"/> at parse time.
    /// </para>
    /// </summary>
    internal static DateTime FromDbUtc(string value)
        => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
           .ToUniversalTime();
}
