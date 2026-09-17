namespace OrynoSync.Core;

public sealed record MetadataProgress(EngineState State,string Message,int Processed=0,long Revision=0,int Changes=0);
public sealed class MetadataSyncCoordinator(ISyncMetadataApi api,IRemoteStateStore store)
{
    private readonly SemaphoreSlim _requestGate=new(1,1);public event Action<MetadataProgress>? Progress;
    public async Task<IReadOnlyList<SyncRootDto>> GetRootsAsync(CancellationToken ct=default){await _requestGate.WaitAsync(ct);try{return await api.GetSyncRootsAsync(ct);}finally{_requestGate.Release();}}
    public async Task RunCycleAsync(Guid rootId,CancellationToken ct=default)
    {
        await store.InitializeAsync(ct);var state=await store.GetRootStateAsync(rootId,ct);
        try{if(!state.InventoryComplete){await InitialInventoryAsync(rootId,ct);state=await store.GetRootStateAsync(rootId,ct);}await PullChangesAsync(rootId,state.LastRevision,ct);Progress?.Invoke(new(EngineState.OnlineIdle,"Metadata is current",Revision:(await store.GetRootStateAsync(rootId,ct)).LastRevision));}
        catch(SyncApiException e) when(e.Status==System.Net.HttpStatusCode.Unauthorized){Progress?.Invoke(new(EngineState.AuthenticationRequired,"Oryno NAS authorization is required."));throw;}
        catch(SyncApiException e) when(e.Status==System.Net.HttpStatusCode.Gone&&e.Code=="SYNC_CURSOR_EXPIRED"){await store.ResetInventoryAsync(rootId,ct);Progress?.Invoke(new(EngineState.Reconciling,"Server cursor expired; metadata re-inventory scheduled."));}
    }
    private async Task InitialInventoryAsync(Guid rootId,CancellationToken ct)
    {
        Progress?.Invoke(new(EngineState.InitialInventory,"Reading Oryno NAS inventory"));string? cursor=null;long anchor=0;long generation=0;var processed=0;var first=true;
        do{var page=await Request(()=>api.GetInventoryPageAsync(rootId,cursor,500,ct),ct);if(first){anchor=page.AnchorRevision;await store.BeginInventoryAsync(rootId,anchor,ct);generation=(await store.GetRootStateAsync(rootId,ct)).Generation;first=false;}await store.SaveInventoryPageAsync(rootId,generation,page.Items,page.NextCursor,ct);processed+=page.Items.Count;Progress?.Invoke(new(EngineState.InitialInventory,$"Received {processed:N0} server items",processed,anchor));cursor=page.NextCursor;}while(cursor is not null);
        await store.CompleteInventoryAsync(rootId,anchor,ct);Progress?.Invoke(new(EngineState.Reconciling,"Inventory complete; applying changes after anchor",processed,anchor));
    }
    private async Task PullChangesAsync(Guid rootId,long after,CancellationToken ct)
    {
        var more=true;while(more){Progress?.Invoke(new(EngineState.SyncingMetadata,"Checking server changes",Revision:after));var page=await Request(()=>api.GetChangesPageAsync(rootId,after,500,ct),ct);var next=page.NextRevision??after;if(next<after)throw new SyncApiException(System.Net.HttpStatusCode.OK,"REVISION_REGRESSION","Server revision cursor moved backwards.");await store.ApplyChangesPageAsync(rootId,page.Changes,next,ct);if(page.Changes.Count>0)Progress?.Invoke(new(EngineState.SyncingMetadata,$"Applied {page.Changes.Count} server change(s)",Revision:next,Changes:page.Changes.Count));after=next;more=page.HasMore;}
    }
    private async Task<T> Request<T>(Func<Task<T>> action,CancellationToken ct){await _requestGate.WaitAsync(ct);try{return await action();}finally{_requestGate.Release();}}
}

public sealed record SyncRuntimeOptions(bool Production,Uri ServerUrl,bool AllowInsecureDevelopment=false)
{
    public static SyncRuntimeOptions ProductionDefault=>new(true,new Uri("https://oryno-nas.remo78.ru"));
}
