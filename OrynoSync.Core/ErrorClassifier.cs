using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.IO;

namespace OrynoSync.Core;

/// <summary>
/// Classifies exceptions into categories that determine how the UI responds.
/// ONLY real transport/connectivity failures should trigger Reconnecting state.
/// Application/logic/file errors must keep the connection in Connected state.
/// </summary>
public static class ErrorClassifier
{
    public enum Category
    {
        /// <summary>Real network/transport failure → Reconnecting</summary>
        Network,
        /// <summary>Auth failure → AuthenticationExpired / AuthenticationRequired</summary>
        Authentication,
        /// <summary>Server returned semantic error → Sync issues (connection stays Connected)</summary>
        ServerSemantic,
        /// <summary>Application/mapping/path/database error → Sync issues (connection stays Connected)</summary>
        Application,
        /// <summary>Server transport failure (HttpRequestException with inner SocketException) → Reconnecting</summary>
        ServerTransport,
        /// <summary>Revision regression from server → sync needs re-inventory but connection is fine</summary>
        RevisionRegression,
    }

    public sealed record ErrorInfo(Category Category, string UserMessage, string LogType, bool IsTransient = false);

    public static ErrorInfo Classify(Exception ex)
    {
        return ex switch
        {
            // === NETWORK FAILURE → Reconnecting ===
            SocketException => new ErrorInfo(Category.Network, "Network is unreachable. Local changes are safe.", "NETWORK_FAILURE", true),
            HttpRequestException hre when hre.InnerException is SocketException => new ErrorInfo(Category.Network, "Network is unreachable. Local changes are safe.", "NETWORK_FAILURE", true),
            HttpRequestException hre when hre.InnerException is TaskCanceledException => new ErrorInfo(Category.Network, "Connection timed out. Local changes are safe.", "NETWORK_TIMEOUT", true),
            HttpRequestException hre when hre.InnerException is IOException ioex => new ErrorInfo(Category.Network, "Network dropped during transfer. Local changes are safe.", "NETWORK_FAILURE", true),

            // === AUTHENTICATION ===
            SyncApiException sae when sae.Status == HttpStatusCode.Unauthorized => new ErrorInfo(Category.Authentication, "Authorization expired. Local changes are safe.", "AUTH_EXPIRED"),
            SyncApiException sae when sae.Status == HttpStatusCode.Forbidden => new ErrorInfo(Category.Authentication, "Access denied. Check your credentials.", "AUTH_DENIED"),
            SyncApiException sae when sae.Code == "REVISION_REGRESSION" => new ErrorInfo(Category.RevisionRegression, "Server revision changed. Re-scanning.", "REVISION_REGRESSION"),
            SyncApiException sae when sae.Status == HttpStatusCode.TooManyRequests => new ErrorInfo(Category.ServerSemantic, "Server is rate-limiting uploads. Retrying with backoff — local changes are safe.", "UPLOAD_RATE_LIMITED", true),
            SyncApiException sae when sae.Status == HttpStatusCode.RequestTimeout => new ErrorInfo(Category.Network, "Server timed out. Retrying — local changes are safe.", "SERVER_TIMEOUT", true),
            SyncApiException sae when (int)sae.Status >= 400 && (int)sae.Status < 500 => new ErrorInfo(Category.ServerSemantic, $"Sync issue: {sae.Code ?? "server rejected an operation"}", "SERVER_SEMANTIC"),
            SyncApiException sae when (int)sae.Status >= 500 => new ErrorInfo(Category.ServerSemantic, $"Server error ({(int)sae.Status}). Local changes are safe.", "SERVER_ERROR", true),
            SyncApiException => new ErrorInfo(Category.ServerSemantic, "Sync communication issue. Local changes are safe.", "SERVER_SEMANTIC"),

            // === SERVER TRANSPORT FAILURE → Reconnecting ===
            HttpRequestException hre when hre.StatusCode is null => new ErrorInfo(Category.ServerTransport, "Server is unreachable. Local changes are safe.", "SERVER_UNREACHABLE", true),

            // === APPLICATION ERRORS → Connected + Sync issues (NOT Reconnecting!) ===
            ArgumentException => new ErrorInfo(Category.Application, "Sync error: invalid path or mapping data.", "APP_ARGUMENT"),
            InvalidOperationException => new ErrorInfo(Category.Application, "Sync error: operation conflict.", "APP_INVALID_OP"),
            KeyNotFoundException => new ErrorInfo(Category.Application, "Sync error: item lookup failed.", "APP_KEY_NOT_FOUND"),
            JsonException => new ErrorInfo(Category.Application, "Sync error: server response was malformed.", "APP_JSON"),
            FormatException => new ErrorInfo(Category.Application, "Sync error: unexpected data format.", "APP_FORMAT"),

            // === FILE-SPECIFIC ERRORS → Connected + Sync issues ===
            IOException => new ErrorInfo(Category.Application, "File operation failed. Other files continue syncing.", "APP_IO"),
            UnauthorizedAccessException => new ErrorInfo(Category.Application, "Access denied to a local file. Other files continue syncing.", "APP_ACCESS"),

            // === FALLBACK: unknown, treat as application error ===
            _ => new ErrorInfo(Category.Application, $"Sync issue: {ex.GetType().Name}. Local changes are safe.", "APP_UNKNOWN"),
        };
    }

    /// <summary>
    /// Returns true when this exception represents a real connectivity failure
    /// that warrants setting ConnectionState to Reconnecting.
    /// </summary>
    public static bool IsNetworkFailure(Exception ex) => Classify(ex).Category is Category.Network or Category.ServerTransport;

    /// <summary>
    /// Returns true when this exception is an application/sync error
    /// that should NOT affect connection state.
    /// </summary>
    public static bool IsApplicationError(Exception ex) => Classify(ex).Category is Category.Application or Category.ServerSemantic or Category.RevisionRegression;
}
