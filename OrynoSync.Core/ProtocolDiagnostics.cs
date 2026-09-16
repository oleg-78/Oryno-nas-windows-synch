using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OrynoSync.Core;

/// <summary>
/// Structured description of a server response the client could not use.
/// Carries only safe fields — never tokens, credentials, cookies or authorization headers.
/// </summary>
public sealed record ProtocolIssue(
    string Event,
    string Endpoint,
    string Method,
    int? StatusCode,
    string? ContentType,
    string BodySample,
    string? ExceptionType,
    string? ExceptionMessage,
    string? JsonError,
    string? ProtocolVersion,
    string ExpectedSchema,
    bool IsCritical,
    string UserMessage)
{
    /// <summary>Full diagnostic line for the support log (§4 field set).</summary>
    public string SafeLogLine =>
        $"endpoint={Endpoint} method={Method} status={(StatusCode?.ToString() ?? "none")} content_type={ContentType ?? "none"} " +
        $"exception_type={ExceptionType ?? "none"} exception_message={ExceptionMessage ?? "none"} json_parse_error={JsonError ?? "none"} " +
        $"protocol_version={ProtocolVersion ?? "none"} expected_schema={ExpectedSchema} critical={IsCritical} " +
        $"body_sample={BodySample} user_message={UserMessage}";
}

/// <summary>Diagnostics sink used by the Core layer; the app wires it to DiagnosticsLogger.</summary>
public static class SyncDiagnostics
{
    public static Action<string, string>? Sink { get; set; }
    public static void Report(string @event, string details)
    {
        try { Sink?.Invoke(@event, details); } catch { /* diagnostics must never break sync */ }
    }
}

/// <summary>Raised for a CRITICAL endpoint that answered with an unusable body: connection is paused.</summary>
public sealed class SyncProtocolException(ProtocolIssue issue) : Exception(issue.UserMessage)
{
    public ProtocolIssue Issue { get; } = issue;
}

/// <summary>Raised for a NON-critical endpoint that answered with an unusable body: connection stays Connected,
/// the precise problem is surfaced as a sync issue for that item instead (§5).</summary>
public sealed class SyncIssueException(ProtocolIssue issue) : Exception(issue.UserMessage)
{
    public ProtocolIssue Issue { get; } = issue;
}

public static class ProtocolEndpoints
{
    /// <summary>Connection-level endpoints: a malformed body here pauses sync (Server error + precise reason).
    /// Everything else (inventory, changes, content, uploads, items) is a per-item sync issue.</summary>
    public static bool IsCritical(string endpoint)
    {
        var e = ((endpoint ?? string.Empty).Split('?')[0]).TrimEnd('/');
        if (e.EndsWith("/health", StringComparison.OrdinalIgnoreCase) || e.Equals("health", StringComparison.OrdinalIgnoreCase)) return true;
        if (e.EndsWith("/capabilities", StringComparison.OrdinalIgnoreCase) || e.Equals("capabilities", StringComparison.OrdinalIgnoreCase)) return true;
        if (e.EndsWith("/roots", StringComparison.OrdinalIgnoreCase) || e.Equals("roots", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public static class ProtocolDiagnostics
{
    public const int BodySampleLimit = 1024;
    private static readonly string[] SecretMarkers = ["token", "authorization", "cookie", "secret", "password", "bearer", "credential", "api_key", "apikey"];

    /// <summary>Truncates and scrubs a response body so it is safe to write to the support log:
    /// значения credential-полей вырезаются, форма ответа сохраняется (§4: не логировать
    /// token/credentials/cookies).</summary>
    public static string Sanitize(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "<empty>";
        var text = body.Length > BodySampleLimit ? body[..BodySampleLimit] + "…[truncated]" : body;
        text = RedactSecretValues(text);
        text = RedactTokenShapes(text);
        var clean = new StringBuilder(text.Length);
        foreach (var ch in text) clean.Append(char.IsControl(ch) && ch is not ('\r' or '\n' or '\t') ? ' ' : ch);
        return clean.ToString().Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>Убирает значения полей с секретными именами: "token":"…", "cookie": "…", password=…</summary>
    private static string RedactSecretValues(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var hit = -1;
            foreach (var marker in SecretMarkers)
            {
                if (i + marker.Length > text.Length) continue;
                if (string.Compare(text, i, marker, 0, marker.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                var colon = -1;
                var limit = Math.Min(text.Length, i + marker.Length + 40);
                for (var k = i + marker.Length; k < limit; k++)
                {
                    // Кавычки вокруг имени поля не мешают: "cookie":"…" и cookie=… обрабатываются одинаково.
                    if (text[k] == ':' || text[k] == '=') { colon = k; break; }
                    if (text[k] is ',' or '}' or '{' or '[' or ']' or ';') break;
                }
                if (colon >= 0) { hit = colon + 1; break; }
            }
            if (hit < 0) { sb.Append(text[i]); i++; continue; }
            var v = hit;
            while (v < text.Length && char.IsWhiteSpace(text[v])) v++;
            var end = v;
            if (v < text.Length && text[v] == '"')
            {
                end = v + 1;
                while (end < text.Length && text[end] != '"') end += text[end] == '\\' ? 2 : 1;
                if (end < text.Length) end++;
            }
            else
            {
                while (end < text.Length && text[end] is not (',' or '}' or ']')) end++;
            }
            sb.Append(text, i, v - i).Append("<redacted>");
            i = end;
        }
        return sb.ToString();
    }

    /// <summary>Токены встречаются и вне JSON-полей (заголовки, query, plain text) — вырезаем по форме.</summary>
    private static string RedactTokenShapes(string text)
    {
        text = Regex.Replace(text, @"(?i)(bearer\s+)[A-Za-z0-9._\-]{8,}", "$1<redacted>");
        text = Regex.Replace(text, @"(?i)\b(crm_(?:live|test)_)[A-Za-z0-9_\-]{4,}", "$1<redacted>");
        return text;
    }

    /// <summary>Human readable location of a System.Text.Json failure (path/line/position).</summary>
    public static string? JsonErrorOf(Exception? ex) => ex switch
    {
        JsonException j => $"JsonException: {j.Message} path={(string.IsNullOrEmpty(j.Path) ? "n/a" : j.Path)} line={j.LineNumber?.ToString() ?? "n/a"} position={j.BytePositionInLine?.ToString() ?? "n/a"}",
        _ => null
    };

    public static ProtocolIssue Describe(
        string endpoint,
        string method,
        int? status,
        string? contentType,
        string? body,
        Exception? ex,
        bool critical,
        string expectedSchema,
        string? protocolVersion)
    {
        var jsonError = JsonErrorOf(ex);
        var reason = jsonError ?? $"{ex?.GetType().Name ?? "InvalidResponse"}: {ex?.Message ?? "response body could not be parsed"}";
        var userMessage = critical
            ? $"Server answered {method} {endpoint} with an unusable response (HTTP {(status?.ToString() ?? "n/a")}, {contentType ?? "no content-type"}). {reason}. Sync is paused until the contract matches again."
            : $"Server answered {method} {endpoint} with an unusable response (HTTP {(status?.ToString() ?? "n/a")}, {contentType ?? "no content-type"}). {reason}. Everything else keeps syncing.";
        return new ProtocolIssue(
            critical ? "PROTOCOL_ERROR" : "PROTOCOL_ISSUE",
            endpoint,
            method,
            status,
            contentType,
            Sanitize(body),
            ex?.GetType().FullName,
            ex?.Message,
            jsonError,
            protocolVersion,
            expectedSchema,
            critical,
            userMessage);
    }

    /// <summary>Структурно валидный JSON без обязательного поля — это тот же protocol issue,
    /// что и невалидный JSON: клиент не должен падать с JsonException/hard-cast.</summary>
    public static ProtocolIssue MissingField(string endpoint, string method, string field, bool critical) =>
        new(critical ? "PROTOCOL_ERROR" : "PROTOCOL_ISSUE",
            endpoint,
            method,
            200,
            "application/json",
            Sanitize($"required field '{field}' missing or null"),
            null,
            $"required field '{field}' was missing or null",
            $"MissingField: {field}",
            null,
            $"field '{field}' (non-null)",
            critical,
            critical
                ? $"Server answered {method} {endpoint} without the required field '{field}'. Sync is paused until the contract matches again."
                : $"Server answered {method} {endpoint} without the required field '{field}'. Everything else keeps syncing.");

    public static void Report(ProtocolIssue issue) => SyncDiagnostics.Report(issue.Event, issue.SafeLogLine);
}
