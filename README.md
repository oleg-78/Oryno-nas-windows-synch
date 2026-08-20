# Oryno Sync Windows W.0

Native Windows desktop client foundation for a full local mirror. The UI host is WPF on .NET 10 because the installed SDK does not include the WinUI workload; all synchronization behavior lives in `OrynoSync.Core` and is UI-independent.

## Run

```powershell
dotnet build OrynoSync.Windows.sln
dotnet test OrynoSync.Windows.sln
dotnet run --project OrynoSync.App
```

The first launch suggests `%USERPROFILE%\Oryno NAS` but does not create it. Use `Choose folder` to select an existing directory. Production UI uses `OrynoNasSyncApi`; the mock remains test-only. Enter the one-time device token issued by the S.1 browser/admin flow. The token is stored with Windows CurrentUser DPAPI and is never written to SQLite or logs.

## Projects

- `OrynoSync.Core`: SQLite state, watcher, reconciliation, debounce, queue, suppression, retry engine, mock API, path/ignore rules.
- `OrynoSync.App`: native WPF window, tray host, single-instance activation, folder selection, pause/resume.
- `OrynoSync.Tests`: Core unit/integration coverage.

See [docs/WINDOWS_CLIENT_ARCHITECTURE.md](docs/WINDOWS_CLIENT_ARCHITECTURE.md) for the design and W.1 integration boundary.
