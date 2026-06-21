# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Development build
dotnet build --configuration Release

# Publish (replace placeholders)
dotnet publish -c Release -p:SptVersion=x.x.x -p:SptCommit=abc1234 -p:SptBuildTime=1234567890 -p:SptBuildType=LOCAL

# SptBuildType options: LOCAL | DEBUG | RELEASE | BLEEDING_EDGE | BLEEDING_EDGE_MODS
```

## Tests

```bash
# Run all tests
dotnet test --configuration Release --verbosity normal

# Run a single test class/method (NUnit)
dotnet test Testing/UnitTests/UnitTests.csproj --filter "FullyQualifiedName~ClassName.MethodName"
```

## Formatting

CSharpier is enforced by CI. Run before committing:

```bash
csharpier format .
```

JSON files are formatted with Biome (also enforced by CI).

## Architecture

The solution (`server-csharp.slnx`) has five sections:

- **`SPTarkov.Server/`** — ASP.NET Core entry point; bootstraps Kestrel on HTTPS port 6969, loads mod DLLs, wires DI, starts `SptServerStartupService`
- **`Libraries/SPTarkov.Server.Core/`** — all game logic: controllers, routers, services, helpers, models
- **`Libraries/SPTarkov.Server.Web/`** — Blazor/MudBlazor web UI (integrated into the server)
- **`Libraries/SPTarkov.DI/`** — custom DI container built on top of `Microsoft.Extensions.DependencyInjection`
- **`Libraries/SPTarkov.Common/`** — shared utilities (SemVer, MongoId, etc.)
- **`Libraries/SPTarkov.Reflection/`** — Harmony-based patching helpers
- **`Patches/`** — Mono.Cecil IL transformations run as post-build steps on `Server.Core`
- **`Testing/UnitTests/`** — NUnit 3 test suite

### Core library layout (`Libraries/SPTarkov.Server.Core/`)

| Folder | Role |
|---|---|
| `Controllers/` | One controller per game domain (Bot, Game, Hideout, Inventory, Quest, Ragfair, …) |
| `Routers/Static/` | HTTP route → controller mapping per domain |
| `Routers/Dynamic/` | Dynamic routers (bot, bundle, trader, location, …) |
| `Services/` | Business logic services; `Services/Mod/` exposes services to mod authors |
| `Helpers/` | Stateless helpers (ItemHelper, InventoryHelper, BotGeneratorHelper, …) |
| `Models/Eft/` | EFT game-data models (Bot, Inventory, Health, Hideout, …) |
| `Models/Spt/Config/` | Typed server config classes (HttpConfig, BotConfig, RagfairConfig, …) |
| `Servers/` | ConfigServer, DatabaseServer, HttpServer, SaveServer, RagfairServer, WebSocketServer |

### Dependency injection

All injectable classes are annotated with `[Injectable(InjectionType.Singleton)]` (or `Transient`/`Scoped`). `DependencyInjectionHandler` in `SPTarkov.DI` scans assemblies and registers them automatically. To override a default implementation, use `TypeOverride` and `TypePriority` parameters.

### Mod system

Mods are DLLs placed in `user/mods/`. `ModDllLoader` scans and loads them at startup; `ModValidator` checks metadata and version compatibility. Mods can inject services using the same `[Injectable]` attribute and expose Blazor UI components via `IModBlazorMetadata`.

### Request pipeline

HTTP requests flow: Kestrel → `HttpServer` → `HttpRouter` → Static/Dynamic router → Controller → Service/Helper chain. Item-mutating actions go through `ItemEventRouter` instead. Profile persistence uses `SaveLoadRouter` hooks.

## Key conventions

- **Line endings**: LF; **max line length**: 140; **indentation**: 4 spaces; **namespaces**: file-scoped.
- **Branches**: `main` = stable release; `develop` = active dev target for PRs.
- **LFS**: large files are stored on a custom LFS server (`spt-lfs.sp-tarkov.com`); adding new large files requires a project developer.
- **Versioning**: set in `Build.props` via `SptVersion`; version string format is `{version}-{buildtype}+{commit}.{timestamp}`.
