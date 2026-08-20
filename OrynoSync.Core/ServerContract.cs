using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrynoSync.Core;

[Flags]
public enum SyncCapabilities { None=0, DeviceAuth=1, MetadataSync=2, ChangeFeed=4, Mutations=8, ContentDownload=16, ResumableUpload=32 }
public enum ConnectionStatus { ServerUnavailable, AuthenticationRequired, Connected, AuthenticationRevoked, TlsError, ProtocolError }
public sealed record ConnectionResult(ConnectionStatus Status, string? Code=null, string? Message=null);
public sealed record SyncRootDto(
    [property:JsonPropertyName("root_id")] Guid RootId,
    [property:JsonPropertyName("name")] string Name,
    [property:JsonPropertyName("enabled")] bool Enabled,
    [property:JsonPropertyName("backfill_status")] string BackfillStatus,
    [property:JsonPropertyName("anchor_revision")] long? AnchorRevision);
public sealed record RemoteItemDto(
    [property:JsonPropertyName("item_id")] Guid ItemId,
    [property:JsonPropertyName("parent_item_id")] Guid? ParentItemId,
    [property:JsonPropertyName("name")] string Name,
    [property:JsonPropertyName("relative_path")] string RelativePath,
    [property:JsonPropertyName("item_type")] string ItemType,
    [property:JsonPropertyName("size_bytes")] long? SizeBytes,
    [property:JsonPropertyName("mtime_utc")] DateTimeOffset? MtimeUtc,
    [property:JsonPropertyName("content_hash")] string? ContentHash,
    [property:JsonPropertyName("version")] long Version);
public sealed record InventoryPageDto(
    [property:JsonPropertyName("anchor_revision")] long AnchorRevision,
    [property:JsonPropertyName("items")] IReadOnlyList<RemoteItemDto> Items,
    [property:JsonPropertyName("next_cursor")] string? NextCursor);
public sealed record RemoteChangeDto(
    [property:JsonPropertyName("revision")] long Revision,
    [property:JsonPropertyName("change")] string Change,
    [property:JsonPropertyName("item_id")] Guid ItemId,
    [property:JsonPropertyName("version")] long Version,
    [property:JsonPropertyName("parent_item_id")] Guid? ParentItemId,
    [property:JsonPropertyName("name")] string? Name,
    [property:JsonPropertyName("relative_path")] string? RelativePath,
    [property:JsonPropertyName("item_type")] string? ItemType,
    [property:JsonPropertyName("size_bytes")] long? SizeBytes,
    [property:JsonPropertyName("mtime_utc")] DateTimeOffset? MtimeUtc,
    [property:JsonPropertyName("content_hash")] string? ContentHash,
    [property:JsonPropertyName("source_type")] string? SourceType);
public sealed record ChangesPageDto(
    [property:JsonPropertyName("changes")] IReadOnlyList<RemoteChangeDto> Changes,
    [property:JsonPropertyName("next_revision")] long? NextRevision,
    [property:JsonPropertyName("has_more")] bool HasMore);
public sealed record ApiErrorDto([property:JsonPropertyName("ok")] bool Ok,[property:JsonPropertyName("code")] string? Code,[property:JsonPropertyName("message")] string? Message,[property:JsonPropertyName("full_resync_required")] bool FullResyncRequired);

public interface ISyncMetadataApi
{
    SyncCapabilities Capabilities { get; }
    Task<ConnectionResult> TestConnectionAsync(CancellationToken ct=default);
    Task<IReadOnlyList<SyncRootDto>> GetSyncRootsAsync(CancellationToken ct=default);
    Task<InventoryPageDto> GetInventoryPageAsync(Guid rootId,string? cursor,int limit,CancellationToken ct=default);
    Task<ChangesPageDto> GetChangesPageAsync(Guid rootId,long after,int limit,CancellationToken ct=default);
}

public sealed class BearerTokenHandler(Func<CancellationToken,Task<string?>> tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
    {
        var token=await tokenProvider(ct);
        if(!string.IsNullOrWhiteSpace(token)) request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        return await base.SendAsync(request,ct);
    }
}

public sealed class OrynoNasSyncApi : ISyncApi, ISyncMetadataApi
{
    public const string ProtocolVersion="Oryno Sync API v1 (S.1)";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false};
    private readonly HttpClient _http;
    public SyncCapabilities Capabilities => SyncCapabilities.DeviceAuth|SyncCapabilities.MetadataSync|SyncCapabilities.ChangeFeed;
    public OrynoNasSyncApi(HttpClient http,Uri baseServerUrl,bool allowInsecureDevelopment=false)
    {
        if(baseServerUrl.Scheme!=Uri.UriSchemeHttps && !allowInsecureDevelopment) throw new ArgumentException("Production server URL must use HTTPS.",nameof(baseServerUrl));
        _http=http;_http.BaseAddress=new Uri(baseServerUrl,"/api/sync/");_http.Timeout=TimeSpan.FromSeconds(20);
    }
    public async Task<ConnectionResult> TestConnectionAsync(CancellationToken ct=default)
    {
        try { using var response=await _http.GetAsync("roots",ct); if(response.IsSuccessStatusCode)return new(ConnectionStatus.Connected);var error=await ReadErrorAsync(response,ct);return response.StatusCode==HttpStatusCode.Unauthorized?new(error.Code=="DEVICE_REVOKED"?ConnectionStatus.AuthenticationRevoked:ConnectionStatus.AuthenticationRequired,error.Code,error.Message):new(ConnectionStatus.ProtocolError,error.Code,error.Message); }
        catch(HttpRequestException e) when(e.InnerException is System.Security.Authentication.AuthenticationException){return new(ConnectionStatus.TlsError,"TLS_ERROR",e.InnerException.Message);}
        catch(HttpRequestException e){return new(ConnectionStatus.ServerUnavailable,"NETWORK_ERROR",e.Message);}
        catch(TaskCanceledException) when(!ct.IsCancellationRequested){return new(ConnectionStatus.ServerUnavailable,"TIMEOUT","Server request timed out.");}
    }
    public Task AuthenticateAsync(CancellationToken ct=default)=>EnsureConnectedAsync(ct);
    private async Task EnsureConnectedAsync(CancellationToken ct){var r=await TestConnectionAsync(ct);if(r.Status==ConnectionStatus.Connected)return;if(r.Status is ConnectionStatus.AuthenticationRequired or ConnectionStatus.AuthenticationRevoked)throw new SyncApiException(HttpStatusCode.Unauthorized,r.Code??"DEVICE_TOKEN_INVALID",r.Message??"Authorization required");throw new HttpRequestException(r.Message??"Server unavailable");}
    public async Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken ct=default)=>(await GetSyncRootsAsync(ct)).Select(r=>r.RootId.ToString()).ToArray();
    public Task<IReadOnlyList<LocalItem>> GetInventoryAsync(CancellationToken ct=default)=>Task.FromException<IReadOnlyList<LocalItem>>(new NotSupportedException("Use paginated root inventory through ISyncMetadataApi."));
    public async Task<IReadOnlyList<SyncRootDto>> GetSyncRootsAsync(CancellationToken ct=default)=>await GetAsync<List<SyncRootDto>>("roots",ct);
    public async Task<InventoryPageDto> GetInventoryPageAsync(Guid rootId,string? cursor,int limit,CancellationToken ct=default){var q=$"roots/{rootId:D}/items?limit={limit}"+(cursor is null?"":"&cursor="+Uri.EscapeDataString(cursor));return await GetAsync<InventoryPageDto>(q,ct);}
    public Task<ChangesPageDto> GetChangesPageAsync(Guid rootId,long after,int limit,CancellationToken ct=default)=>GetAsync<ChangesPageDto>($"roots/{rootId:D}/changes?after={after}&limit={limit}",ct);
    private async Task<T> GetAsync<T>(string path,CancellationToken ct){using var r=await _http.GetAsync(path,ct);if(!r.IsSuccessStatusCode){var e=await ReadErrorAsync(r,ct);throw new SyncApiException(r.StatusCode,e.Code??"UNKNOWN_SERVER_ERROR",e.Message??"Oryno NAS request failed.");}return await r.Content.ReadFromJsonAsync<T>(Json,ct)??throw new SyncApiException(r.StatusCode,"INVALID_RESPONSE","Empty or invalid server response.");}
    private static async Task<ApiErrorDto> ReadErrorAsync(HttpResponseMessage r,CancellationToken ct){try{return await r.Content.ReadFromJsonAsync<ApiErrorDto>(Json,ct)??new(false,"UNKNOWN_SERVER_ERROR",r.ReasonPhrase,false);}catch(JsonException){return new(false,"UNKNOWN_SERVER_ERROR",r.ReasonPhrase,false);}}
    private static Task Block()=>Task.FromException(new ServerCapabilityException("S.2 mutations/content"));
    public Task CreateUploadAsync(PendingOperation operation,CancellationToken ct=default)=>Block();public Task UploadChunkAsync(PendingOperation operation,Stream content,CancellationToken ct=default)=>Block();public Task CommitUploadAsync(PendingOperation operation,CancellationToken ct=default)=>Block();public Task CreateDirectoryAsync(PendingOperation operation,CancellationToken ct=default)=>Block();public Task MoveAsync(PendingOperation operation,CancellationToken ct=default)=>Block();public Task DeleteAsync(PendingOperation operation,CancellationToken ct=default)=>Block();
}
