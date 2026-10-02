using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nuvia.App.Services;

/// <summary>
/// Turns a remote-supplied filename into something safe to use as a local path component.
/// <para>
/// A remote filename is untrusted input: it can contain directory separators, <c>..</c> segments,
/// control characters, Windows-reserved device names, or an absurd length. Nothing that arrives
/// from the server is ever joined onto a directory without passing through here first.
/// </para>
/// </summary>
public static class FileNameSafety
{
    private const int MaxLength = 100;

    /// <summary>Windows device names that are illegal as file names regardless of extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Reduce an arbitrary remote name to a single safe path component.
    /// The result is never empty, never contains a separator, and never resolves to a parent
    /// directory. When nothing usable survives, <paramref name="fallback"/> is used.
    /// </summary>
    public static string ToSafeFileName(string? remoteName, string fallback = "download")
    {
        var name = remoteName ?? string.Empty;

        // 1. Drop any directory component — both separators, plus a defence against
        //    bare-relative input like "..\..\Windows\System32".
        var cut = name.LastIndexOfAny(new[] { '/', '\\' });
        if (cut >= 0) name = name[(cut + 1)..];

        // 2. Rebuild the name character by character, dropping anything Windows treats as
        //    path syntax or a control character.
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsControl(ch)) continue;
            if (Path.GetInvalidFileNameChars().Contains(ch)) continue;
            if (ch == ':' || ch == '*' || ch == '?' || ch == '"' || ch == '<' || ch == '>' || ch == '|')
                continue;
            sb.Append(ch);
        }

        var cleaned = sb.ToString();

        // 3. Strip trailing dots and spaces: Windows silently removes them, which would let two
        //    different names collide on disk.
        cleaned = cleaned.TrimEnd('.', ' ').TrimStart(' ');

        // 4. Never allow a relative-path token to survive.
        if (cleaned is "." or ".." || string.IsNullOrWhiteSpace(cleaned))
            cleaned = string.Empty;

        // 5. Split the extension off before length-capping so the type is not lost.
        var extension = Path.GetExtension(cleaned);
        if (extension.Length > 16) extension = string.Empty; // not a real extension, don't preserve it
        var stem = extension.Length > 0 ? cleaned[..^extension.Length] : cleaned;

        if (stem.Length + extension.Length > MaxLength)
            stem = stem[..Math.Max(1, MaxLength - extension.Length)];

        cleaned = stem + extension;

        // 6. Reserved device names are illegal even with an extension.
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned)))
            cleaned = "_" + cleaned;

        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    /// <summary>True when the supplied name is already safe to use as a bare file name.</summary>
    public static bool IsSafeFileName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal)
           && name is not "." and not ".."
           && ToSafeFileName(name) == name;
}
