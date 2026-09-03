using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.IO;

namespace OrynoSync.App;

internal static class DiagnosticsLogger
{
    private static readonly object Gate = new();
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oryno Sync", "Logs");
    public static string LogPath => Path.Combine(DirectoryPath, "orynosync.log");

    public static void Write(string eventName, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O} {eventName} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    public static string NetworkState() => NetworkInterface.GetIsNetworkAvailable() ? "available" : "unavailable";
    public static string SafeEndpoint(Uri? uri) => uri is null ? "unknown" : $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : ":" + uri.Port)}";
}

internal sealed class HttpDiagnosticsHandler(Uri endpoint) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        DiagnosticsLogger.Write("HTTP_REQUEST", $"method={request.Method} endpoint={DiagnosticsLogger.SafeEndpoint(request.RequestUri ?? endpoint)} path={request.RequestUri?.AbsolutePath ?? "unknown"} network={DiagnosticsLogger.NetworkState()}");
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            DiagnosticsLogger.Write("HTTP_RESPONSE", $"method={request.Method} path={request.RequestUri?.AbsolutePath ?? "unknown"} status={(int)response.StatusCode} elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            DiagnosticsLogger.Write("HTTP_FAILURE", $"method={request.Method} path={request.RequestUri?.AbsolutePath ?? "unknown"} exception={ex.GetType().Name} socket_error={((ex as HttpRequestException)?.HttpRequestError.ToString() ?? "none")} elapsed_ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
            throw;
        }
    }
}
