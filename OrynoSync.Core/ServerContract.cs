using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Security.Cryptography;
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
    [property:JsonPropertyName("anchor_revision")] long? AnchorRevision)
{
    [JsonPropertyName("storage_node_id")] public int StorageNodeId { get; init; }
    [JsonPropertyName("relative_path")] public string RelativePath { get; init; } = "";
};
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
public sealed record CapabilitiesDto([property:JsonPropertyName("protocol_version")] int ProtocolVersion,[property:JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities);
public sealed record HealthDto([property:JsonPropertyName("git_commit")] string? GitCommit);
public sealed record UploadCreateRequest(Guid? RootId, Guid? ParentItemId, string? Name, Guid? ItemId, long? BaseVersion, long ExpectedSize, Guid? OperationId)
{
    [JsonPropertyName("root_id")] public Guid? RootId { get; init; } = RootId;
    [JsonPropertyName("parent_item_id")] public Guid? ParentItemId { get; init; } = ParentItemId;
    [JsonPropertyName("name")] public string? Name { get; init; } = Name;
    [JsonPropertyName("item_id")] public Guid? ItemId { get; init; } = ItemId;
    [JsonPropertyName("base_version")] public long? BaseVersion { get; init; } = BaseVersion;
    [JsonPropertyName("expected_size")] public long ExpectedSize { get; init; } = ExpectedSize;
    [JsonPropertyName("operation_id")] public Guid? OperationId { get; init; } = OperationId;
}
public sealed record UploadCreateResponse([property:JsonPropertyName("upload_id")] Guid UploadId,[property:JsonPropertyName("state")] string State,[property:JsonPropertyName("chunk_size_mb")] int ChunkSizeMb,[property:JsonPropertyName("received_bytes")] long ReceivedBytes,[property:JsonPropertyName("expected_size")] long ExpectedSize,[property:JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
public sealed record UploadStatusResponse([property:JsonPropertyName("upload_id")] Guid UploadId,[property:JsonPropertyName("state")] string State,[property:JsonPropertyName("received_bytes")] long ReceivedBytes,[property:JsonPropertyName("expected_size")] long ExpectedSize,[property:JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt,[property:JsonPropertyName("name")] string Name,[property:JsonPropertyName("item_id")] Guid? ItemId,[property:JsonPropertyName("content_hash")] string? ContentHash);
public sealed record UploadChunkResponse([property:JsonPropertyName("upload_id")] Guid UploadId,[property:JsonPropertyName("state")] string State,[property:JsonPropertyName("received_bytes")] long ReceivedBytes);
public sealed record UploadCommitRequest([property:JsonPropertyName("operation_id")] Guid? OperationId,[property:JsonPropertyName("expected_hash")] string? ExpectedHash);
public sealed record UploadCommitResponse([property:JsonPropertyName("item_id")] Guid ItemId,[property:JsonPropertyName("version")] long Version,[property:JsonPropertyName("revision")] long Revision,[property:JsonPropertyName("content_hash")] string ContentHash,[property:JsonPropertyName("replayed")] bool Replayed);
public sealed record FolderCreateRequest([property:JsonPropertyName("parent_item_id")] Guid? ParentItemId,[property:JsonPropertyName("name")] string Name,[property:JsonPropertyName("operation_id")] Guid? OperationId);
public sealed record MoveRequest([property:JsonPropertyName("new_parent_id")] Guid? NewParentId,[property:JsonPropertyName("new_name")] string? NewName,[property:JsonPropertyName("base_version")] long? BaseVersion,[property:JsonPropertyName("operation_id")] Guid? OperationId);
public sealed record TransferCommit(Guid ItemId,long Version,long Revision,string ContentHash,bool Replayed);

public interface ISyncMetadataApi
{
    SyncCapabilities Capabilities { get; }
    Task<ConnectionResult> TestConnectionAsync(CancellationToken ct=default);
    Task<IReadOnlyList<SyncRootDto>> GetSyncRootsAsync(CancellationToken ct=default);
    Task<InventoryPageDto> GetInventoryPageAsync(Guid rootId,string? cursor,int limit,CancellationToken ct=default);
    Task<ChangesPageDto> GetChangesPageAsync(Guid rootId,long after,int limit,CancellationToken ct=default);
}
public interface IContentTransferApi
{
    Task<RemoteItemDto> GetItemAsync(Guid itemId, CancellationToken ct = default);
    Task<UploadCreateResponse> CreateUploadAsync(UploadCreateRequest request,CancellationToken ct=default);
    Task<UploadStatusResponse> GetUploadStatusAsync(Guid uploadId,CancellationToken ct=default);
    Task<UploadChunkResponse> UploadChunkAsync(Guid uploadId,long offset,Stream content,CancellationToken ct=default);
    Task<TransferCommit> CommitUploadAsync(Guid uploadId,Guid? operationId,string expectedHash,CancellationToken ct=default);
    Task<RemoteItemDto> CreateFolderAsync(Guid rootId,Guid? parentItemId,string name,Guid operationId,CancellationToken ct=default);
    Task<RemoteItemDto> MoveItemAsync(Guid itemId,Guid? newParentId,string? newName,long? baseVersion,Guid operationId,CancellationToken ct=default);
    Task DeleteItemAsync(Guid itemId,Guid operationId,CancellationToken ct=default);
    Task DownloadRangeAsync(Guid itemId,long version,long offset,Stream destination,CancellationToken ct=default);
    Task<(long Size,string Hash,DateTimeOffset? Mtime)> GetContentMetadataAsync(Guid itemId,long version,CancellationToken ct=default);
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

public sealed class OrynoNasSyncApi : ISyncApi, ISyncMetadataApi, IContentTransferApi
{
    public const string ProtocolVersion="Oryno Sync API v1 (S.2.1)";
    public const string ExpectedServerCommit="95ea1cffbe58b367c5cc1a4c1cdfad002489ddf7";
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false};
    private readonly HttpClient _http;
    public SyncCapabilities Capabilities { get; private set; } = SyncCapabilities.None;
    public string? ServerCommit { get; private set; }
    public bool ContractVerified { get; private set; }
    public OrynoNasSyncApi(HttpClient http,Uri baseServerUrl,bool allowInsecureDevelopment=false)
    {
        if(baseServerUrl.Scheme!=Uri.UriSchemeHttps && !allowInsecureDevelopment) throw new ArgumentException("Production server URL must use HTTPS.",nameof(baseServerUrl));
        _http=http;_http.BaseAddress=new Uri(baseServerUrl,"/api/sync/");_http.Timeout=TimeSpan.FromSeconds(20);
    }
    public async Task<ConnectionResult> TestConnectionAsync(CancellationToken ct=default)
    {
        try { var health=await GetPublicAsync<HealthDto>(new Uri(_http.BaseAddress!,"../health"),ct); ServerCommit=health.GitCommit; var capabilities=await GetPublicAsync<CapabilitiesDto>(new Uri(_http.BaseAddress!,"capabilities"),ct); Capabilities=ParseCapabilities(capabilities); if(capabilities.ProtocolVersion!=1 || !Capabilities.HasFlag(SyncCapabilities.ContentDownload) || !Capabilities.HasFlag(SyncCapabilities.ResumableUpload)) return new(ConnectionStatus.ProtocolError,"UNSUPPORTED_SYNC_PROTOCOL","Server does not expose required content capabilities."); if(!string.Equals(ServerCommit,ExpectedServerCommit,StringComparison.OrdinalIgnoreCase)) return new(ConnectionStatus.ProtocolError,"SERVER_VERSION_MISMATCH","Server version does not match expected sync contract."); using var response=await _http.GetAsync("roots",ct); if(response.IsSuccessStatusCode){ContractVerified=true;return new(ConnectionStatus.Connected);}var error=await ReadErrorAsync(response,ct);return response.StatusCode==HttpStatusCode.Unauthorized?new(error.Code=="DEVICE_REVOKED"?ConnectionStatus.AuthenticationRevoked:ConnectionStatus.AuthenticationRequired,error.Code,error.Message):new(ConnectionStatus.ProtocolError,error.Code,error.Message); }
        catch(SyncApiException e) when(e.Status == HttpStatusCode.Unauthorized){return new(e.Code=="DEVICE_REVOKED"?ConnectionStatus.AuthenticationRevoked:ConnectionStatus.AuthenticationRequired,e.Code,e.Message);}
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
    private async Task<T> GetPublicAsync<T>(Uri uri,CancellationToken ct){using var r=await _http.GetAsync(uri,ct);if(!r.IsSuccessStatusCode){var e=await ReadErrorAsync(r,ct);throw new SyncApiException(r.StatusCode,e.Code??"SERVER_HEALTH_FAILED",e.Message??r.ReasonPhrase??"Server health check failed.");}return await r.Content.ReadFromJsonAsync<T>(Json,ct)??throw new SyncApiException(r.StatusCode,"INVALID_RESPONSE","Empty or invalid server response.");}
    private static SyncCapabilities ParseCapabilities(CapabilitiesDto dto){var c=SyncCapabilities.DeviceAuth|SyncCapabilities.MetadataSync|SyncCapabilities.ChangeFeed;foreach(var v in dto.Capabilities){c|=v switch{"content_upload"=>SyncCapabilities.Mutations,"content_download"=>SyncCapabilities.ContentDownload,"range_download"=>SyncCapabilities.ContentDownload,"resumable_upload"=>SyncCapabilities.ResumableUpload,"move"=>SyncCapabilities.Mutations,"delete"=>SyncCapabilities.Mutations,_=>SyncCapabilities.None};}return c;}
    private static async Task<ApiErrorDto> ReadErrorAsync(HttpResponseMessage r,CancellationToken ct){try{return await r.Content.ReadFromJsonAsync<ApiErrorDto>(Json,ct)??new(false,"UNKNOWN_SERVER_ERROR",r.ReasonPhrase,false);}catch(JsonException){return new(false,"UNKNOWN_SERVER_ERROR",r.ReasonPhrase,false);}}
    private static Task Block()=>Task.FromException(new ServerCapabilityException("S.2 mutations/content"));
    public Task CreateUploadAsync(PendingOperation operation,CancellationToken ct=default)=>Block();public Task UploadChunkAsync(PendingOperation operation,Stream content,CancellationToken ct=default)=>Block();public Task CommitUploadAsync(PendingOperation operation,CancellationToken ct=default)=>Block();public Task CreateDirectoryAsync(PendingOperation operation,CancellationToken ct=default)=>Block();public Task MoveAsync(PendingOperation operation,CancellationToken ct=default)=>Block();public Task DeleteAsync(PendingOperation operation,CancellationToken ct=default)=>Block();
    public Task<UploadCreateResponse> CreateUploadAsync(UploadCreateRequest request,CancellationToken ct=default)=>PostAsync<UploadCreateResponse>("uploads",request,ct);
    public Task<RemoteItemDto> GetItemAsync(Guid itemId,CancellationToken ct=default)=>GetAsync<RemoteItemDto>($"items/{itemId:D}",ct);
    public Task<UploadStatusResponse> GetUploadStatusAsync(Guid uploadId,CancellationToken ct=default)=>GetAsync<UploadStatusResponse>($"uploads/{uploadId:D}",ct);
    public async Task<UploadChunkResponse> UploadChunkAsync(Guid uploadId,long offset,Stream content,CancellationToken ct=default){using var form=new MultipartFormDataContent();form.Add(new StreamContent(content),"upload","chunk.bin");using var request=new HttpRequestMessage(HttpMethod.Put,$"uploads/{uploadId:D}/chunks?offset={offset}"){Content=form};using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);return await ReadResponseAsync<UploadChunkResponse>(response,ct);}
    public async Task<TransferCommit> CommitUploadAsync(Guid uploadId,Guid? operationId,string expectedHash,CancellationToken ct=default){var r=await PostAsync<UploadCommitResponse>($"uploads/{uploadId:D}/commit",new UploadCommitRequest(operationId,expectedHash),ct);return new(r.ItemId,r.Version,r.Revision,r.ContentHash,r.Replayed);}
    public Task<RemoteItemDto> CreateFolderAsync(Guid rootId,Guid? parentItemId,string name,Guid operationId,CancellationToken ct=default)=>PostAsync<RemoteItemDto>($"roots/{rootId:D}/folders",new FolderCreateRequest(parentItemId,name,operationId),ct);
    public Task<RemoteItemDto> MoveItemAsync(Guid itemId,Guid? newParentId,string? newName,long? baseVersion,Guid operationId,CancellationToken ct=default)=>PostAsync<RemoteItemDto>($"items/{itemId:D}/move",new MoveRequest(newParentId,newName,baseVersion,operationId),ct);
    public async Task DeleteItemAsync(Guid itemId,Guid operationId,CancellationToken ct=default){using var r=await _http.DeleteAsync($"items/{itemId:D}?operation_id={operationId:D}",ct);if(!r.IsSuccessStatusCode)await ThrowResponseAsync(r,ct);}
    public async Task<(long Size,string Hash,DateTimeOffset? Mtime)> GetContentMetadataAsync(Guid itemId,long version,CancellationToken ct=default){using var r=await _http.SendAsync(new HttpRequestMessage(HttpMethod.Head,$"items/{itemId:D}/content?version={version}"),HttpCompletionOption.ResponseHeadersRead,ct);if(!r.IsSuccessStatusCode)await ThrowResponseAsync(r,ct);return (r.Content.Headers.ContentLength??0,r.Headers.TryGetValues("X-Sync-Content-Hash",out var h)?h.Single():"",r.Headers.TryGetValues("X-Sync-Mtime",out var m)&&DateTimeOffset.TryParse(m.Single(),out var dt)?dt:null);}
    public async Task DownloadRangeAsync(Guid itemId,long version,long offset,Stream destination,CancellationToken ct=default){using var request=new HttpRequestMessage(HttpMethod.Get,$"items/{itemId:D}/content?version={version}");request.Headers.Range=new RangeHeaderValue(offset,null);using var r=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);await ReadResponseHeadersAsync(r,ct);await using var source=await r.Content.ReadAsStreamAsync(ct);await source.CopyToAsync(destination,131072,ct);}
    private async Task<T> PostAsync<T>(string path,object payload,CancellationToken ct){using var r=await _http.PostAsJsonAsync(path,payload,Json,ct);return await ReadResponseAsync<T>(r,ct);}
    private async Task<T> ReadResponseAsync<T>(HttpResponseMessage r,CancellationToken ct){if(!r.IsSuccessStatusCode)await ThrowResponseAsync(r,ct);return await r.Content.ReadFromJsonAsync<T>(Json,ct)??throw new SyncApiException(r.StatusCode,"INVALID_RESPONSE","Empty or invalid server response.");}
    private async Task ReadResponseHeadersAsync(HttpResponseMessage r,CancellationToken ct){if(r.IsSuccessStatusCode || r.StatusCode==HttpStatusCode.PartialContent)return;await ThrowResponseAsync(r,ct);}
    private async Task ThrowResponseAsync(HttpResponseMessage r,CancellationToken ct){var e=await ReadErrorAsync(r,ct);throw new SyncApiException(r.StatusCode,e.Code??"UNKNOWN_SERVER_ERROR",e.Message??r.ReasonPhrase??"Oryno NAS request failed.");}
}
