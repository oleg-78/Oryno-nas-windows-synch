# Oryno Sync Windows W.0

Native Windows desktop client foundation for a full local mirror. The UI host is WPF on .NET 10 because the installed SDK does not include the WinUI workload; all synchronization behavior lives in `OrynoSync.Core` and is UI-independent.

## Run

```powershell
dotnet build OrynoSync.Windows.sln
dotnet test OrynoSync.Windows.sln
dotnet run --project OrynoSync.App
```

The first launch suggests `%USERPROFILE%\Oryno NAS` but does not create it. Use `Choose folder` to select an existing directory. The W.0 host uses the mock API in offline mode; the API is intentionally not connected to NAS.

## Projects

- `OrynoSync.Core`: SQLite state, watcher, reconciliation, debounce, queue, suppression, retry engine, mock API, path/ignore rules.
- `OrynoSync.App`: native WPF window, tray host, single-instance activation, folder selection, pause/resume.
- `OrynoSync.Tests`: Core unit/integration coverage.

See [docs/WINDOWS_CLIENT_ARCHITECTURE.md](docs/WINDOWS_CLIENT_ARCHITECTURE.md) for the design and W.1 integration boundary.
