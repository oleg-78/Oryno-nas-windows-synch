namespace OrynoSync.Core;

/// <summary>
/// Turns a connection result into the exact sentence the UI shows (§5). Kept in Core so the rules
/// ("connected with a sync issue" vs "server error with a named endpoint") are unit-testable.
/// </summary>
public static class ConnectionDiagnostics
{
    public const string GenericProtocolMessage = "Server reached, but its response was invalid.";

    public static bool IsServerError(ConnectionResult result) =>
        result.Status is ConnectionStatus.ProtocolError or ConnectionStatus.TlsError;

    /// <summary>User-facing status label.</summary>
    public static string Label(ConnectionResult result) => result.Status switch
    {
        ConnectionStatus.Connected when result.Issues is { Count: > 0 } => "Connected · sync issues",
        ConnectionStatus.Connected => "Connected",
        ConnectionStatus.ProtocolError or ConnectionStatus.TlsError => "Server error",
        ConnectionStatus.AuthenticationRequired => "Sign in required",
        ConnectionStatus.AuthenticationRevoked => "Authorization expired",
        ConnectionStatus.ServerUnavailable => "Server unavailable",
        _ => "Checking..."
    };

    /// <summary>
    /// §5: a malformed *secondary* endpoint must not read as a dead server, and a critical one must
    /// name the endpoint, the status and the reason instead of the old generic sentence.
    /// </summary>
    public static string DescribeMessage(ConnectionResult result, int consecutiveFailures = 0) => result.Status switch
    {
        ConnectionStatus.Connected when result.Issues is { Count: > 0 } issues =>
            $"Connected · sync issue at {issues[^1].Endpoint} (HTTP {issues[^1].StatusCode?.ToString() ?? "n/a"}): {issues[^1].ExceptionMessage ?? "unusable response"}. Other files keep syncing.",
        ConnectionStatus.Connected => "Connected to Oryno NAS.",
        ConnectionStatus.AuthenticationRequired => "Server is reachable. Authorization is required.",
        ConnectionStatus.AuthenticationRevoked => "Authorization expired. Local changes are safe.",
        ConnectionStatus.TlsError => "TLS certificate or handshake error.",
        ConnectionStatus.ProtocolError => DescribeProtocolError(result),
        _ when consecutiveFailures < 3 => "Reconnecting to Oryno NAS...",
        _ => "Oryno NAS is unavailable. Local changes are safe."
    };

    private static string DescribeProtocolError(ConnectionResult result)
    {
        if (result.Issue is { } issue)
        {
            var action = issue.IsCritical ? "Sync is paused until this is fixed." : "Other files keep syncing.";
            return $"Server error at {issue.Endpoint} (HTTP {issue.StatusCode?.ToString() ?? "n/a"}, {issue.ContentType ?? "no content-type"}): " +
                   $"{issue.JsonError ?? issue.ExceptionMessage ?? "unusable response"} — expected {issue.ExpectedSchema}. {action}";
        }
        return string.IsNullOrWhiteSpace(result.Message) ? GenericProtocolMessage : result.Message;
    }

    /// <summary>Support-log line for a connection result (§4 field set, no secrets).</summary>
    public static string DescribeLogLine(ConnectionResult result, int consecutiveFailures)
    {
        var issue = result.Issue;
        return $"status={result.Status} code={result.Code ?? "none"} endpoint={(issue?.Endpoint ?? "n/a")} http_status={(issue?.StatusCode?.ToString() ?? "n/a")} " +
               $"content_type={(issue?.ContentType ?? "n/a")} exception_type={(issue?.ExceptionType ?? "none")} exception_message={(issue?.ExceptionMessage ?? "none")} " +
               $"json_parse_error={(issue?.JsonError ?? "none")} expected_schema={(issue?.ExpectedSchema ?? "n/a")} protocol_version={issue?.ProtocolVersion ?? "n/a"} " +
               $"server_revision={result.ServerRevision ?? "unknown"} critical={(issue?.IsCritical.ToString() ?? "n/a")} failures={consecutiveFailures} " +
               $"body_sample={(issue?.BodySample ?? "n/a")}";
    }
}
