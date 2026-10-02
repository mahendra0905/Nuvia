using System;
using System.Text;

namespace Nuvia.App.Services;

/// <summary>
/// The two markers Nuvia writes into Telegram so that files and the managed group can be
/// recognised again <b>after the local index is gone</b> (e.g. uninstall → reinstall, where all
/// local data under <c>%LOCALAPPDATA%\Nuvia\</c> was removed). Both live on Telegram, never locally.
/// <para>
/// Neither marker changes what Nuvia stores or how a file is transferred — they are identification
/// tags only:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="GroupAboutMarker"/> is written into a managed private group's description (<c>about</c>)
/// at creation. On a later install with an empty index, Nuvia finds its own groups by this tag.
/// </description></item>
/// <item><description>
/// <see cref="SavedMessageCaptionMarker"/> is an invisible (zero-width) signature written into the
/// caption of every file Nuvia uploads. On a later install, a Saved Messages document that carries it
/// was uploaded by Nuvia; one without it was added outside Nuvia ("external").
/// </description></item>
/// <item><description>
/// The same caption also carries the file's <b>Nuvia folder path</b>, appended to the signature as more
/// zero-width characters (<see cref="BuildUploadCaption"/>, <see cref="TryReadFolderPath"/>). Telegram
/// has no folder concept, so this is the only thing that lets a reinstalled Nuvia put every file back
/// into the folder it was in instead of flattening them all into the location root.
/// </description></item>
/// </list>
///</summary>
public static class NuviaMarkers
{
    /// <summary>
    /// Machine-detectable tag placed in a managed group's <c>about</c> text. Kept short and distinctive
    /// so a plain <see cref="string.Contains(string)"/> check is enough and accidental collisions are
    /// vanishingly unlikely.
    /// </summary>
    public const string GroupAboutMarker = "#NuviaStorage";

    /// <summary>
    /// The full <c>about</c> text set on a managed group at creation. It reads as an ordinary, honest
    /// description to anyone viewing the group in Telegram, and it ends with <see cref="GroupAboutMarker"/>
    /// so Nuvia can recognise the group later. Well within Telegram's ~255-character about limit.
    /// </summary>
    public const string GroupAboutText =
        "Nuvia private storage. Files here are managed by the Nuvia desktop app. " + GroupAboutMarker;

    /// <summary>
    /// The invisible signature written as the caption of every Nuvia upload. It is built from explicit
    /// zero-width code points — U+2060 WORD JOINER and U+200D ZERO WIDTH JOINER, alternating — so the
    /// caption renders as empty in Telegram while staying detectable byte-for-byte. Both are format
    /// characters (Unicode category Cf), not whitespace, so Telegram's leading/trailing-space trimming
    /// of message text does not touch them. The code points are spelled out numerically (rather than as
    /// literal invisible characters in the source) so this file stays reviewable and copy-paste safe.
    /// <para>
    /// NOTE: this relies on Telegram preserving these format characters through a send/receive
    /// round-trip; that must be confirmed on Windows against a real account (it cannot be verified from
    /// a build/test sandbox).
    /// </para>
    /// </summary>
    public static readonly string SavedMessageCaptionMarker = new(new[]
    {
        (char)0x2060, (char)0x200D, (char)0x2060, (char)0x200D, (char)0x2060, (char)0x200D, (char)0x2060,
    });

    /// <summary>
    /// The caption to send with a Nuvia upload: the invisible marker, plus the folder path when the file
    /// was uploaded into one.
    /// <para>
    /// With no folder the caption is exactly <see cref="SavedMessageCaptionMarker"/> — byte-for-byte what
    /// earlier builds sent, so nothing about an existing upload changes. With a folder, the encoded path is
    /// appended straight after the marker (see <see cref="AppendFolderPath"/>); there is no delimiter
    /// because the bare marker is always exactly seven characters, so "eight or more characters that are
    /// the marker followed by payload" can never be confused with "the marker alone".
    /// </para>
    /// </summary>
    /// <param name="folderPath">
    /// The "/"-joined path of the folder inside the file's own location, or null/empty for the location
    /// root. Must satisfy <see cref="FolderNames"/>.
    /// </param>
    /// <exception cref="FolderPathTooLongException">
    /// The encoded path would not fit Telegram's caption limit. Refused rather than truncated, because a
    /// shortened path would restore the file into the wrong folder.
    /// </exception>
    public static string BuildUploadCaption(string? folderPath = null)
        => string.IsNullOrEmpty(folderPath)
            ? SavedMessageCaptionMarker
            : SavedMessageCaptionMarker + EncodeFolderPath(folderPath);

    /// <summary>True when a message caption carries the Nuvia upload signature.</summary>
    public static bool CaptionIndicatesNuvia(string? caption)
        => !string.IsNullOrEmpty(caption)
           && caption.Contains(SavedMessageCaptionMarker, StringComparison.Ordinal);

    /// <summary>
    /// The folder path hidden in a caption, or <c>null</c> when the caption carries none.
    /// <para>
    /// Null covers every "there is no folder to restore" case, and that is deliberate — a caption that is
    /// missing, unmarked, truncated, encoded with characters Nuvia does not use, not valid UTF-8, or
    /// holding a segment that breaks <see cref="FolderNames"/> all resolve to "this file belongs at the
    /// location root". Nothing here throws on malformed input: bad data degrades to a flat file, it never
    /// corrupts the index or blocks an import.
    /// </para>
    /// </summary>
    public static string? TryReadFolderPath(string? caption)
    {
        if (string.IsNullOrEmpty(caption)) return null;

        var anchor = caption.IndexOf(SavedMessageCaptionMarker, StringComparison.Ordinal);
        if (anchor < 0) return null;

        // Everything after the marker is the payload. A bare marker (the root-level upload caption, and
        // every file uploaded before folders existed) has nothing after it, so it reads as "no folder".
        var payloadStart = anchor + SavedMessageCaptionMarker.Length;
        var payloadLength = caption.Length - payloadStart;
        if (payloadLength <= 0 || payloadLength % CharsPerByte != 0) return null;

        var path = DecodePath(caption, payloadStart, payloadLength / CharsPerByte);

        // Only a path Nuvia itself could have written is accepted; anything else falls back to the root.
        return FolderNames.IsEncodablePath(path) ? path : null;
    }

    /// <summary>True when a group's <c>about</c> text carries the Nuvia storage tag.</summary>
    public static bool AboutIndicatesNuviaGroup(string? about)
        => !string.IsNullOrEmpty(about)
           && about.Contains(GroupAboutMarker, StringComparison.Ordinal);

    // ------------------------------------------------------- folder-path encoding

    /// <summary>
    /// The four zero-width format characters the folder payload is built from. Two of them (U+2060 and
    /// U+200D) are the ones <see cref="SavedMessageCaptionMarker"/> already uses and therefore already
    /// proven to survive a Telegram send/receive round-trip; the other two (ZWSP and ZWNJ) are the most
    /// widely used invisible characters there are. Every one is Unicode category Cf, so Telegram's
    /// leading/trailing whitespace trim cannot touch them.
    /// </summary>
    private static readonly char[] PayloadAlphabet =
    {
        (char)0x2060, (char)0x200D, (char)0x200B, (char)0x200C,
    };

    /// <summary>Two bits per character, so one UTF-8 byte is always exactly four caption characters.</summary>
    private const int CharsPerByte = 4;

    /// <summary>Strict UTF-8: a payload that does not decode cleanly is treated as "no folder".</summary>
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Encodes a folder path as a run of zero-width characters, two bits per character, most significant
    /// bit first. The character count is always exactly four times the UTF-8 byte count, so the byte count
    /// is recoverable from the payload length alone and no length header is needed.
    /// </summary>
    private static string EncodeFolderPath(string folderPath)
    {
        if (!FolderNames.IsEncodablePath(folderPath))
        {
            // Distinguish "too long to fit" from "not a path Nuvia would write" so the UI can say which.
            if (FolderNames.Split(folderPath) is null)
                throw new ArgumentException(
                    "That is not a valid Nuvia folder path.", nameof(folderPath));

            throw new FolderPathTooLongException();
        }

        var bytes = Encoding.UTF8.GetBytes(folderPath);
        var encoded = new StringBuilder(bytes.Length * CharsPerByte);

        foreach (var b in bytes)
        {
            encoded.Append(PayloadAlphabet[(b >> 6) & 0b11]);
            encoded.Append(PayloadAlphabet[(b >> 4) & 0b11]);
            encoded.Append(PayloadAlphabet[(b >> 2) & 0b11]);
            encoded.Append(PayloadAlphabet[b & 0b11]);
        }

        return encoded.ToString();
    }

    /// <summary>
    /// Decodes <paramref name="byteCount"/> bytes from <paramref name="payloadLength"/> characters starting
    /// at <paramref name="start"/>. Returns null when any character is not one of the four payload
    /// characters, or when the decoded bytes are not valid UTF-8.
    /// </summary>
    private static string? DecodePath(string caption, int start, int byteCount)
    {
        var bytes = new byte[byteCount];

        for (var i = 0; i < byteCount; i++)
        {
            var value = 0;
            for (var nibble = 0; nibble < CharsPerByte; nibble++)
            {
                var index = Array.IndexOf(PayloadAlphabet, caption[start + i * CharsPerByte + nibble]);
                if (index < 0) return null;
                value = (value << 2) | index;
            }

            bytes[i] = (byte)value;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
