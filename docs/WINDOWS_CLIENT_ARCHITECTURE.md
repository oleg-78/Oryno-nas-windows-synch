# Oryno Sync Windows Architecture

## Runtime

The solution targets .NET 10 (`OrynoSync.Core` targets `net8.0`; the WPF host targets `net10.0-windows`) and uses WPF because no WinUI workload is installed on the development machine. The host can be replaced without changing Core contracts.

## S.1 server contract

Verified against `oleg-78/oryno-nas@6610236`. The server is mounted under `/api/sync` and exposes:

- `GET /api/sync/roots` -> an array of `{root_id,name,enabled,backfill_status,anchor_revision}`.
- `GET /api/sync/roots/{root_id}/items?cursor=&limit=` -> `{anchor_revision,items,next_cursor}`.
- `GET /api/sync/roots/{root_id}/changes?after=&limit=` -> `{changes,next_revision,has_more}`.
- `POST /api/sync/devices` is browser-cookie/admin-only and returns the one-time plaintext device token.
- Client metadata endpoints accept `Authorization: Bearer <device-token>`.

S.1 does not expose content transfer or client mutations. The Windows production client therefore reports content operations as waiting for S.2 capability; it never falls back to `MockSyncApi` or marks them successful.

The feed revision is a global identity sequence filtered by root. Numeric gaps inside a root are valid; the client checks monotonicity, not contiguity. `410 SYNC_CURSOR_EXPIRED` resets only remote inventory state and preserves local files and pending operations.

## State and queue

SQLite lives at `%LOCALAPPDATA%\Oryno Sync\oryno-sync.db`. Tables are `local_items`, `pending_operations`, `sync_state`, `settings`, `remote_items`, `remote_sync_state`, and `sync_conflicts`. `remote_items` is separate from local truth and stores root/item identity, parent, path, version, revision, and planning state. Queue writes are transactional and coalesce the latest operation for the same path. A restart reopens pending rows and retries them.

`remote_sync_state` stores `last_revision`, inventory generation, anchor, cursor, and in-progress flags. Inventory writes are page transactions; the final generation switch and cursor anchor are explicit. A crash during inventory leaves `inventory_complete=0`, so startup safely re-enumerates.

## Watcher and reconciliation

`LocalWatcher` wraps recursive `FileSystemWatcher`. Events go through `DebouncedChangeProcessor` with a 750 ms debounce, stable size/mtime/open checks, and centralized ignore rules. Watcher errors including `InternalBufferOverflowException` mark the stream unreliable and invoke `LocalReconciler`. Startup always scans the configured root against SQLite. Rename events preserve old/new paths; when events are lost, reconciliation intentionally falls back to delete/create.

## Engine and API clients

`ISyncApi` remains the local-operation boundary and `MockSyncApi` remains test-only. `OrynoNasSyncApi` is the production `HttpClient` implementation and `ISyncMetadataApi` provides roots, paginated inventory, and revision feed. A reusable `BearerTokenHandler` injects credentials centrally. `MetadataSyncCoordinator` performs one-request-at-a-time metadata polling and anchor/reconnect handling. Transient failures use 2, 4, 8 ... 60 second backoff. S.2 mutations/content are represented by `ServerCapabilityException` and are never reported as success.

## Feedback suppression and atomic downloads

`LocalMutationSuppression` records expected path plus size/mtime (and an optional future hash), so suppression is metadata-confirmed rather than time-only. A future `AtomicFileWriter` should write to a sibling temporary file, verify BLAKE3, then atomically replace the target before registering the expectation.

## Lifecycle and security

The WPF host owns a named mutex and activation event. Closing the window hides it; tray Exit cancels HTTP/polling, disposes watcher/tray, and allows process shutdown. `WindowsCredentialStore` uses CurrentUser DPAPI in a separate credential directory; no token is stored in SQLite/settings/logs. Autostart uses HKCU Run without elevation. Changing the local folder stops/cancels the old watcher and starts reconciliation for the new root without application restart.

## W.1.2 multiple mappings

`sync_mappings` is the application configuration boundary. Each row binds one canonical Windows local path to at most one server root on this device. `mapping_local_items` and `mapping_pending_operations` carry `mapping_id`, so queues and local metadata are isolated. The migration creates the first mapping from the legacy `settings.sync_root` and copies existing local items and pending operations without clearing them.

`SyncMappingRuntimeManager` owns one `FileSystemWatcher` and one local reconciliation runtime per enabled mapping while sharing SQLite, credentials, HTTP client, and metadata coordination. Missing or removable local roots become `LocalFolderUnavailable`; scans abort without synthesizing DELETE operations. Overlapping local paths and duplicate server roots are rejected before a mapping is saved. Removing a mapping stops its watcher and deletes configuration metadata only; local and server files are untouched.

## Known W.1 limits

Live NAS verification is blocked while `https://oryno-nas.remo78.ru` times out from this Windows host. Device registration remains the server's browser/admin bootstrap flow because S.1 has no self-service desktop registration endpoint. The client has no content transfer, mutations, MSIX, Explorer overlays, or Files On-Demand. The existing Core hash abstraction is still the server-compatible seam; W.1 does not invent a content hash protocol absent S.2.
