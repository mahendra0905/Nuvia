using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nuvia.App.QuickUpload;

/// <summary>
/// Which destination a shell-launched upload should target, as a coarse hint. The Windows Explorer
/// "Nuvia" sub-menu maps its three entries onto these: "Upload to Saved Messages" → <see cref="Saved"/>,
/// "Upload to Private group" → <see cref="Group"/>, "Choose destination…" → <see cref="Ask"/>.
/// </summary>
public enum QuickUploadDestination
{
    /// <summary>Ask the user at upload time (show the small chooser). The default when unspecified.</summary>
    Ask,

    /// <summary>The account's own Saved Messages.</summary>
    Saved,

    /// <summary>The single managed private storage group (create-then-upload when none exists yet).</summary>
    Group,
}

/// <summary>
/// One quick-upload instruction: a local file path plus a coarse destination hint.
/// <para>
/// This is the ONLY thing that ever crosses the process boundary — from the command line of a
/// shell-launched <c>Nuvia.exe --upload …</c> to the already-running primary instance over the local
/// named pipe. It deliberately carries nothing sensitive: no credentials, session material, phone
/// number, password or login code — only a file path the shell already knows and a destination hint.
/// </para>
/// </summary>
public sealed record QuickUploadRequest(string Path, QuickUploadDestination To)
{
    /// <summary>
    /// A request that only asks a running instance to surface its window — no file, no upload. Sent when a
    /// second plain launch (no <c>--upload</c>) meets an instance that already owns the session.
    /// </summary>
    public static QuickUploadRequest Activate { get; } = new(string.Empty, QuickUploadDestination.Ask);

    /// <summary>True when there is no file to upload: the request is only a "bring yourself to the front".</summary>
    [JsonIgnore]
    public bool IsActivateOnly => string.IsNullOrWhiteSpace(Path);

    // ---- wire form (pipe payload) -------------------------------------------------------------
    // A tiny, explicit JSON shape. Kept separate from the record so the enum travels as a stable token
    // ("saved"/"group"/"ask") rather than a numeric value that could drift if the enum is reordered.

    private sealed class Wire
    {
        public int V { get; set; } = 1;
        public string? Path { get; set; }
        public string? To { get; set; }
    }

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>Serialises this request to the one-line JSON payload sent over the pipe.</summary>
    public string Serialize()
        => JsonSerializer.Serialize(new Wire { V = 1, Path = Path, To = ToToken(To) }, WireOptions);

    /// <summary>
    /// Parses a pipe payload back into a request. Returns false (and an <see cref="Activate"/> fallback)
    /// for anything malformed, so a garbled or hostile pipe write can never crash the primary instance.
    /// </summary>
    public static bool TryParse(string? json, out QuickUploadRequest request)
    {
        request = Activate;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(json, WireOptions);
            if (wire is null)
                return false;

            request = new QuickUploadRequest(wire.Path ?? string.Empty, ParseToken(wire.To));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ToToken(QuickUploadDestination to) => to switch
    {
        QuickUploadDestination.Saved => "saved",
        QuickUploadDestination.Group => "group",
        _ => "ask",
    };

    private static QuickUploadDestination ParseToken(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "saved" => QuickUploadDestination.Saved,
        "group" => QuickUploadDestination.Group,
        _ => QuickUploadDestination.Ask,
    };
}
