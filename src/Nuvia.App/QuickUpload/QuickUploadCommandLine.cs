using System;
using System.Collections.Generic;

namespace Nuvia.App.QuickUpload;

/// <summary>
/// Pure parsing of the process command line into a <see cref="QuickUploadRequest"/>.
/// <para>
/// Grammar (as emitted by the native shell handler):
/// <c>--upload "&lt;absolute path&gt;" [--to saved|group|ask]</c>. Both <c>--flag value</c> and
/// <c>--flag=value</c> forms are accepted. No filesystem access happens here — the path is validated for
/// existence at run time by the caller — so this stays fully unit-testable on any platform.
/// </para>
/// </summary>
public static class QuickUploadCommandLine
{
    private const string UploadFlag = "--upload";
    private const string ToFlag = "--to";

    /// <summary>True when an <c>--upload</c> flag is present, whether or not it carries a usable path.</summary>
    public static bool ContainsUploadFlag(IReadOnlyList<string> args)
    {
        if (args is null)
            return false;

        foreach (var arg in args)
        {
            if (arg is null)
                continue;
            if (Matches(arg, UploadFlag, out _))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Parses the arguments into a quick-upload request, or returns null when there is no usable
    /// <c>--upload &lt;path&gt;</c> (a normal launch, or a malformed upload flag with no path). Callers that
    /// need to tell "no flag" from "flag but no path" apart use <see cref="ContainsUploadFlag"/> as well.
    /// </summary>
    public static QuickUploadRequest? Parse(IReadOnlyList<string> args)
    {
        if (args is null || args.Count == 0)
            return null;

        string? path = null;
        var to = QuickUploadDestination.Ask;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg))
                continue;

            if (Matches(arg, UploadFlag, out var inlinePath))
            {
                path = inlinePath ?? TakeValue(args, ref i);
            }
            else if (Matches(arg, ToFlag, out var inlineTo))
            {
                var value = inlineTo ?? TakeValue(args, ref i);
                to = ParseDestination(value);
            }
        }

        if (string.IsNullOrWhiteSpace(path))
            return null;

        return new QuickUploadRequest(path.Trim(), to);
    }

    private static QuickUploadDestination ParseDestination(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "saved" => QuickUploadDestination.Saved,
        "group" => QuickUploadDestination.Group,
        _ => QuickUploadDestination.Ask,
    };

    /// <summary>
    /// Matches <paramref name="arg"/> against <paramref name="flag"/> case-insensitively. When the argument
    /// uses the <c>--flag=value</c> form the value is returned; the <c>--flag value</c> form returns a null
    /// value and the caller consumes the next token.
    /// </summary>
    private static bool Matches(string arg, string flag, out string? inlineValue)
    {
        inlineValue = null;

        if (arg.Equals(flag, StringComparison.OrdinalIgnoreCase))
            return true;

        if (arg.Length > flag.Length + 1
            && arg[flag.Length] == '='
            && arg.AsSpan(0, flag.Length).Equals(flag, StringComparison.OrdinalIgnoreCase))
        {
            inlineValue = arg[(flag.Length + 1)..];
            return true;
        }

        return false;
    }

    /// <summary>Consumes the next argument as a flag's value, unless it is itself a flag.</summary>
    private static string? TakeValue(IReadOnlyList<string> args, ref int index)
    {
        if (index + 1 >= args.Count)
            return null;

        var next = args[index + 1];
        if (string.IsNullOrEmpty(next) || next.StartsWith("--", StringComparison.Ordinal))
            return null;

        index++;
        return next;
    }
}
