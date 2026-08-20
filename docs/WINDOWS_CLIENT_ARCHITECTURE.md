# Oryno Sync Windows Architecture

## Runtime

The solution targets .NET 10 (`OrynoSync.Core` targets `net8.0`; the WPF host targets `net10.0-windows`) and uses WPF because no WinUI workload is installed on the development machine. The host can be replaced without changing Core contracts.

## State and queue

SQLite lives at `%LOCALAPPDATA%\Oryno Sync\oryno-sync.db`. Tables are `local_items`, `pending_operations`, `sync_state`, and `settings`. `local_items` uses normalized relative paths; absolute paths are derived only from the configured root. Queue writes are transactional and coalesce the latest operation for the same path. A restart reopens pending rows and retries them.

## Watcher and reconciliation

`LocalWatcher` wraps recursive `FileSystemWatcher`. Events go through `DebouncedChangeProcessor` with a 750 ms debounce, stable size/mtime/open checks, and centralized ignore rules. Watcher errors including `InternalBufferOverflowException` mark the stream unreliable and invoke `LocalReconciler`. Startup always scans the configured root against SQLite. Rename events preserve old/new paths; when events are lost, reconciliation intentionally falls back to delete/create.

## Engine and mock API

`ISyncApi` is the future NAS boundary. `MockSyncApi` supports online/offline, latency, upload failure, and conflict simulation. `SyncEngine` has Offline, Connecting, Syncing, UpToDate, Paused, and Error-compatible transitions; transient failures use 2, 4, 8 ... 60 second backoff, while conflicts are held for user action.

## Feedback suppression and atomic downloads

`LocalMutationSuppression` records expected path plus size/mtime (and an optional future hash), so suppression is metadata-confirmed rather than time-only. A future `AtomicFileWriter` should write to a sibling temporary file, verify BLAKE3, then atomically replace the target before registering the expectation.

## Lifecycle and security

The WPF host owns a named mutex and activation event. Closing the window hides it; tray Exit disposes watcher/tray and allows process shutdown. `ICredentialStore` is the Core boundary for Windows Credential Manager/DPAPI in W.1; no token is stored in SQLite. Autostart is reserved for the Settings surface and should use HKCU Run or a Startup shortcut without elevation.

## Known W.0 limits

The visible developer surface currently runs the mock API offline and does not include real authentication, HTTP, remote downloads, MSIX, Explorer overlays, or Files On-Demand. A selected folder is persisted; changing it during a running session takes effect on the next launch. The Core hash abstraction is still the server-compatible seam; the W.0 implementation does not add a third-party BLAKE3 package.
