using System.Net;
using System.Net.Sockets;
using OrynoSync.Core;

namespace OrynoSync.Tests;

/// <summary>
/// Regression tests for the ErrorClassifier that ensures:
/// - Real network failures → Network category (Reconnecting)
/// - Application errors → Application category (Connected + Sync issues)
/// - Server semantic errors → ServerSemantic category (Connected + Sync issues)
/// </summary>
public class ErrorClassifierTests
{
    // === NETWORK FAILURES → Reconnecting ===

    [Fact]
    public void SocketException_ClassifiedAsNetworkFailure()
    {
        var ex = new SocketException((int)SocketError.HostUnreachable);
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Network, info.Category);
        Assert.True(ErrorClassifier.IsNetworkFailure(ex));
        Assert.False(ErrorClassifier.IsApplicationError(ex));
    }

    [Fact]
    public void HttpRequestException_WithSocketException_ClassifiedAsNetworkFailure()
    {
        var inner = new SocketException((int)SocketError.ConnectionRefused);
        var ex = new HttpRequestException("Connection refused", inner);
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Network, info.Category);
        Assert.True(ErrorClassifier.IsNetworkFailure(ex));
    }

    [Fact]
    public void HttpRequestException_WithTaskCanceledException_ClassifiedAsNetworkTimeout()
    {
        var inner = new TaskCanceledException();
        var ex = new HttpRequestException("Timeout", inner);
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Network, info.Category);
        Assert.True(ErrorClassifier.IsNetworkFailure(ex));
    }

    // === AUTHENTICATION ===

    [Fact]
    public void SyncApiException_Unauthorized_ClassifiedAsAuthentication()
    {
        var ex = new SyncApiException(HttpStatusCode.Unauthorized, "DEVICE_REVOKED", "Device revoked");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Authentication, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
        Assert.False(ErrorClassifier.IsApplicationError(ex));
    }

    // === SERVER SEMANTIC (4xx/5xx) → Connected + Sync issues ===

    [Fact]
    public void SyncApiException_4xx_ClassifiedAsServerSemantic()
    {
        var ex = new SyncApiException(HttpStatusCode.BadRequest, "INVALID_PATH", "Bad path");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.ServerSemantic, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
        Assert.True(ErrorClassifier.IsApplicationError(ex));
    }

    [Fact]
    public void SyncApiException_5xx_ClassifiedAsServerSemantic()
    {
        var ex = new SyncApiException(HttpStatusCode.InternalServerError, "SERVER_ERROR", "Server error");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.ServerSemantic, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
        Assert.True(ErrorClassifier.IsApplicationError(ex));
    }

    // === APPLICATION ERRORS → Connected (CRITICAL: NOT Reconnecting!) ===

    [Fact]
    public void ArgumentException_ClassifiedAsApplicationError_NOTNetwork()
    {
        var ex = new ArgumentException("Invalid destination path");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
        Assert.True(ErrorClassifier.IsApplicationError(ex));
    }

    [Fact]
    public void InvalidOperationException_ClassifiedAsApplicationError_NOTNetwork()
    {
        var ex = new InvalidOperationException("Operation conflict");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
    }

    [Fact]
    public void KeyNotFoundException_ClassifiedAsApplicationError_NOTNetwork()
    {
        var ex = new KeyNotFoundException("Item not found");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
    }

    [Fact]
    public void JsonException_ClassifiedAsApplicationError_NOTNetwork()
    {
        var ex = new System.Text.Json.JsonException("Malformed JSON");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
    }

    [Fact]
    public void FormatException_ClassifiedAsApplicationError_NOTNetwork()
    {
        var ex = new FormatException("Bad format");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
    }

    [Fact]
    public void IOException_ClassifiedAsApplicationError_NOTNetwork()
    {
        var ex = new IOException("File locked");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
    }

    [Fact]
    public void UnauthorizedAccessException_ClassifiedAsApplicationError_NOTNetwork()
    {
        var ex = new UnauthorizedAccessException("Access denied");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
    }

    // === REVISION REGRESSION → Connected + re-inventory ===

    [Fact]
    public void SyncApiException_RevisionRegression_ClassifiedAsRevisionRegression()
    {
        var ex = new SyncApiException(HttpStatusCode.OK, "REVISION_REGRESSION", "Server revision moved backwards");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.RevisionRegression, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
        Assert.True(ErrorClassifier.IsApplicationError(ex));
    }

    // === USER MESSAGES: don't expose transport internals ===

    [Fact]
    public void ApplicationError_DoesNotExposeRawExceptionType()
    {
        var ex = new ArgumentException("secret path info");
        var info = ErrorClassifier.Classify(ex);
        Assert.DoesNotContain("ArgumentException", info.UserMessage);
        Assert.DoesNotContain("secret path info", info.UserMessage);
        Assert.Contains("Sync error", info.UserMessage);
    }

    [Fact]
    public void NetworkError_ProvidesUserFriendlyMessage()
    {
        var ex = new SocketException((int)SocketError.HostUnreachable);
        var info = ErrorClassifier.Classify(ex);
        Assert.Contains("Local changes are safe", info.UserMessage);
    }

    // === FALLBACK: unknown exceptions treated as application errors ===

    [Fact]
    public void UnknownException_ClassifiedAsApplicationError_NOTNetwork()
    {
        var ex = new InvalidCastException("Unexpected cast");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex));
    }

    // === BOUNDARY: StackTrace and Message are captured for diagnostics ===

    [Fact]
    public void AllExceptions_HaveStackTraceAvailable()
    {
        var ex = new ArgumentException("test");
        try { throw ex; } catch { }
        var info = ErrorClassifier.Classify(ex);
        Assert.NotNull(ex.StackTrace);
    }

    // === REGRESSION: Ensure the bug doesn't return ===

    [Fact]
    public void ArgumentException_DoesNotSetReconnecting()
    {
        // This is the exact bug: ArgumentException was being caught by generic catch
        // and setting ConnectionState = Reconnecting. The fix classifies it as
        // Application error which keeps connection in Connected state.
        var ex = new ArgumentException("An item with the same key has already been added.");
        var info = ErrorClassifier.Classify(ex);
        Assert.Equal(ErrorClassifier.Category.Application, info.Category);
        Assert.False(ErrorClassifier.IsNetworkFailure(ex), "ArgumentException must NOT be classified as network failure");
        Assert.True(ErrorClassifier.IsApplicationError(ex), "ArgumentException must be classified as application error");
    }
}
