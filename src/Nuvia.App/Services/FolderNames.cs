using System;
using System.Collections.Generic;
using System.Text;

namespace Nuvia.App.Services;

/// <summary>
/// The rules for a Nuvia folder name and for the "/"-joined folder path that is stored in the local
/// index and (invisibly) in a Telegram caption.
/// <para>
/// Nuvia folders are a purely local organisation layer — they are never a disk path and Telegram has no
/// concept of them. A path segment is therefore only ever a label, but the rules still matter: a segment
/// containing the separator would make the stored path ambiguous, and an unbounded segment would blow
/// Telegram's caption limit when the path is carried by <see cref="NuviaMarkers"/>.
/// </para>
/// <para>
/// Names are validated exactly, never repaired. A name that arrives from a Telegram caption is untrusted
/// input: if it does not satisfy these rules it is rejected outright (the file falls back to the location
/// root) rather than silently rewritten into something that would not match what the user sees locally.
/// </para>
/// </summary>
public static class FolderNames
{
    /// <summary>Longest accepted folder name. Keeps the encoded caption marker small.</summary>
    public const int MaxNameLength = 60;

    /// <summary>Deepest accepted folder nesting. A deeper path is refused rather than truncated.</summary>
    public const int MaxDepth = 8;

    /// <summary>
    /// Largest folder path Nuvia is willing to hide inside a Telegram caption. The path is UTF-8 encoded
    /// and carried in zero-width characters, so this bound is what keeps a caption inside Telegram's
    /// ~1024-character limit; see <see cref="NuviaMarkers"/> for the encoding ratio.
    /// </summary>
    public const int MaxEncodedPathBytes = 200;

    /// <summary>The character that joins folder names into a path.</summary>
    public const char Separator = '/';

    /// <summary>
    /// True when <paramref name="name"/> is usable as one folder name exactly as given: not blank, within
    /// <see cref="MaxNameLength"/>, with no surrounding whitespace, no path separator, no control
    /// character, and not a bare relative-path token.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length > MaxNameLength) return false;

        // Surrounding whitespace would be invisible in the UI and make two different-looking folders
        // collide, so a name must survive a trim unchanged.
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) return false;
        if (name is "." or "..") return false;

        foreach (var ch in name)
        {
            if (ch == Separator || ch == '\\') return false;
            if (char.IsControl(ch)) return false;
        }

        return true;
    }

    /// <summary>
    /// Splits a stored path into its segments and validates each one. Returns <c>null</c> when the path is
    /// null, empty, deeper than <see cref="MaxDepth"/>, or holds any segment that is not a valid name.
    /// </summary>
    public static IReadOnlyList<string>? Split(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var segments = path.Split(Separator);
        if (segments.Length == 0 || segments.Length > MaxDepth) return null;

        foreach (var segment in segments)
        {
            if (!IsValidName(segment)) return null;
        }

        return segments;
    }

    /// <summary>Joins already-validated segments into the stored path form.</summary>
    public static string Join(IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            if (i > 0) sb.Append(Separator);
            sb.Append(segments[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="path"/> is a valid folder path that also fits the caption-marker budget
    /// once UTF-8 encoded. The UI checks this before starting a move or an upload so a too-deep or
    /// too-long path is refused with an explanation instead of failing halfway.
    /// </summary>
    public static bool IsEncodablePath(string? path)
        => Split(path) is not null && Encoding.UTF8.GetByteCount(path!) <= MaxEncodedPathBytes;
}

/// <summary>
/// Raised when a folder path cannot be hidden in a Telegram caption because its encoded form would exceed
/// Telegram's caption limit. Raised rather than truncating: a silently shortened path would restore the
/// file into the wrong folder after a reinstall.
/// </summary>
public sealed class FolderPathTooLongException : Exception
{
    public FolderPathTooLongException()
        : base("This folder path is too long for Nuvia to remember in Telegram. "
               + "Shorten the folder name, or move the file closer to the top level.") { }

    public FolderPathTooLongException(string message) : base(message) { }

    public FolderPathTooLongException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Raised when a folder would end up with the same name as a sibling. Sibling names have to be unique
/// because the folder path stored in a Telegram caption is a list of names: two identically named folders
/// side by side would be indistinguishable when the folders are restored after a reinstall.
/// </summary>
public sealed class DuplicateFolderNameException : Exception
{
    public DuplicateFolderNameException(string name)
        : base($"There is already a folder named “{name}” here. Choose a different name.") { }
}
