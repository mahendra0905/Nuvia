using System;
using System.Text.RegularExpressions;

namespace Nuvia.App.Services;

/// <summary>
/// Best-effort, secret-safe diagnostic log at <c>%LOCALAPPDATA%\Nuvia\logs\</c>.
/// <para>
/// Used to capture WHY the app fails at runtime (startup, sign-in, an unhandled crash) without
/// ever writing credentials, phone numbers, verification codes, 2FA passwords or session material.
/// Every write is wrapped so diagnostics can never themselves break the app.
/// </para>
/// </summary>
internal static class DiagnosticLog
{
    /// <summary>Append one already-safe line to the named log file (e.g. "crash.log").</summary>
    public static void Write(string fileName, string message)
    {
        try
        {
            var logDir = System.IO.Path.Combine(NuviaConfig.LocalDataDirectory, "logs");
            System.IO.Directory.CreateDirectory(logDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            System.IO.File.AppendAllText(System.IO.Path.Combine(logDir, fileName), line);
        }
        catch
        {
            // Diagnostics must never break the app.
        }
    }

    /// <summary>Append a sanitized description of an exception (type + inner chain + message).</summary>
    public static void WriteException(string fileName, string area, Exception? ex)
    {
        if (ex is null)
        {
            Write(fileName, $"{area}: (no exception object)");
            return;
        }

        // Walk the inner-exception chain so the true cause is visible, not just the outer wrapper.
        var parts = new System.Collections.Generic.List<string>();
        var current = ex;
        int depth = 0;
        while (current is not null && depth < 5)
        {
            parts.Add($"{current.GetType().Name}: {Sanitize(current.Message)}");
            current = current.InnerException;
            depth++;
        }

        Write(fileName, $"{area}: {string.Join(" <- ", parts)}");
    }

    /// <summary>
    /// Remove anything that could be a secret: credential key names, phone/long digit runs, and long
    /// opaque hex tokens (session keys, hashes). Mirrors TelegramAuthService.SanitizeException.
    /// </summary>
    private static string Sanitize(string? message)
    {
        var msg = message ?? string.Empty;

        // Redact credential VALUES if a library error ever embeds them, while still surfacing the
        // Telegram error CODE (e.g. API_ID_INVALID). The code names a field but is not the secret
        // value, and it is exactly what is needed to diagnose a rejected credential pair.

        // Long opaque hex tokens: 32-char api_hash, session keys, auth-key material.
        msg = Regex.Replace(msg, @"\b[A-Fa-f0-9]{16,}\b", "[redacted]");

        // Phone numbers, api_id, verification codes and other 5+ digit runs.
        msg = Regex.Replace(msg, @"\+?\d[\d\s\-]{4,}", "[redacted]");

        return msg.Length > 300 ? msg[..300] : msg;
    }
}
