# Repository Guidelines

## Project Structure & Module Organization

- `src/BinanceStream/`: reusable `net10.0` library for WebSocket transport, subscriptions, typed market events, parsing, and file replay.
- `src/BinanceMonitor/`: `net10.0-windows` Windows Forms application. Keep display logic here and protocol logic in the library.
- `tests/BinanceStream.Tests/`: executable regression checks and an optional live smoke test.
- `samples/market.jsonl`: recorded messages copied into the monitor output for offline replay.
- `BinanceStream.slnx`: solution entry point. `Directory.Build.props` enables nullable references, implicit usings, and warnings as errors.

## Build, Test, and Development Commands

Use the .NET 10 SDK; running the Forms application requires Windows. Execute from the repository root:

```powershell
dotnet build BinanceStream.slnx -c Release
dotnet run --project src/BinanceMonitor -c Release
dotnet run --project tests/BinanceStream.Tests -c Release
dotnet run --project tests/BinanceStream.Tests -c Release -- --live
```

These commands build all projects, launch the monitor, run offline checks, and run checks plus a live Binance connection test, respectively. The live test has a 30-second deadline. If `dotnet` is unavailable on PATH, see the local SDK command in `README.md`.

## Coding Style & Naming Conventions

Use four-space indentation, file-scoped namespaces, PascalCase for types and public members, and camelCase for locals and private fields. Suffix asynchronous methods with `Async`; propagate cancellation tokens. Use `decimal` and invariant parsing for prices and quantities. Match existing record-based event models. No dedicated formatter or linter is configured; keep builds warning-free.

## Testing Guidelines

Tests use a dependency-free console harness, not xUnit or NUnit; run them with `dotnet run`, not `dotnet test`. Add behavior-focused checks with descriptive labels such as `Replay cancellation`. Cover affected parsing, validation, precision, and lifecycle behavior. Keep offline tests deterministic and network checks optional. No numerical coverage threshold is configured. For UI changes, manually verify connection, disconnection, replay, and closing during an active session.

## Commit & Pull Request Guidelines

This repository is newly initialized and has no established commit history conventions. Use concise imperative subjects, for example `Fix cancellation during reconnect`. Keep changes focused. PRs should explain behavior changes, list validation commands and results, link relevant issues, and include screenshots for visible UI changes.

## Architecture & Configuration

Each stream enumeration owns its socket. Preserve cancellation, bounded queues, and UI-thread marshaling. Public market data requires no credentials; never commit secrets. Keep generated `bin/` and `obj/` directories out of version control.

## Knowledge Graph Updates

When `.ua/config.json` enables `autoUpdate`, check `.ua/meta.json` against `git rev-parse HEAD` at task start and after creating commits. If stale, follow `~/.understand-anything-plugin/hooks/auto-update-prompt.md` to update the graph incrementally. Preserve the baseline on failure and report it. Do not create extra commits or push generated updates unless requested.
