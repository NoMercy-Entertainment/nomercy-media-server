# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NoMercy MediaServer is a self-hosted media streaming platform built with .NET 10.0, featuring automatic media encoding, comprehensive library management, and remote streaming capabilities. The project is under active development (work-in-progress).

## Build & Run Commands

```bash
# Build
dotnet restore
dotnet build

# Run server (default ports: internal 7626, external 7626)
dotnet run --project src/NoMercy.Service

# Run with custom options
dotnet run --project src/NoMercy.Service --dev --loglevel=Debug
```

### Startup Options
- `--dev` (`-d`): Development mode
- `--loglevel` (`-l`): Set logging level (Verbose, Debug, Information, Warning, Error, Fatal)
- `--internal-port` (`-i`) / `--external-port` (`-x`): Custom port configuration
- `--internal-ip` / `--external-ip`: IP address overrides
- `--pipe-name`: Named pipe (Windows) or Unix socket name for IPC
- `--service`: Run as a platform service (Windows SCM, systemd, launchd)

## Testing

```bash
# Run all tests
dotnet test

# Run specific test project
dotnet test tests/NoMercy.Tests.Database
dotnet test tests/NoMercy.Tests.Queue
dotnet test tests/NoMercy.Tests.Providers
dotnet test tests/NoMercy.Tests.MediaProcessing

# Run by category
dotnet test --filter "Category=Unit"
dotnet test --filter "Category=Integration"

# With coverage
dotnet test --collect:"XPlat Code Coverage" --settings tests/coverletArgs.runsettings
```

## Architecture

### Service-Oriented Modular Design

```
src/
├── NoMercy.Service/        # ASP.NET Core host, entry point, Kestrel
├── NoMercy.Launcher/       # Launcher UI app
├── NoMercy.Api/            # REST controllers (v1, v2), SignalR hubs
├── NoMercy.Database/       # EF Core contexts (MediaContext, QueueContext), SQLite
├── NoMercy.Encoder/        # FFmpeg abstraction, fluent encoding pipeline
├── NoMercy.Queue/          # Background job processing, cron scheduling
├── NoMercy.MediaProcessing/# File analysis, thumbnails, media organization
├── NoMercy.Providers/      # External APIs (TMDB, TVDB, MusicBrainz, etc.)
├── NoMercy.Data/           # Data repositories
├── NoMercy.Networking/     # Network discovery, port forwarding, UPnP
├── NoMercy.NmSystem/       # System utilities, file operations
├── NoMercy.Setup/          # Application initialization
```

### Data Flow
1. **Ingestion**: FileManager scans directories → MediaAnalysis via FFmpeg → metadata extraction
2. **Processing**: JobQueue manages encoding tasks → FfMpeg wrapper → multi-resolution HLS output
3. **Storage**: SQLite databases + file-based media assets
4. **Delivery**: SignalR hubs (VideoHub, MusicHub) + REST APIs for streaming

### Database Contexts
- **MediaContext**: Primary application data (movies, shows, users, metadata)
- **QueueContext**: Job processing and background tasks
- Both use SQLite with connection pooling and query splitting

### SQLite Query Restrictions
**NEVER** use `g.First()`, `g.Last()`, or element-access patterns inside
`GroupBy().Select()` projections in EF Core queries — SQLite has no `APPLY`
operator, and EF Core throws `System.InvalidOperationException:
Translating this query requires the SQL APPLY operation`. Fetch a flat
projection first, then `GroupBy` client-side in memory. Same for a nested
`.ToList()` inside `.Select()` — fetch join-table data separately and
combine client-side.

```csharp
// Bad: triggers APPLY
context.Items.GroupBy(r => r.MediaId).Select(g => new Dto { Title = g.First().Title });
// Good: flat query, group in memory
var rows = await context.Items.Select(r => new { r.MediaId, r.Title }).ToListAsync(ct);
var result = rows.GroupBy(r => r.MediaId).Select(g => new Dto { Title = g.First().Title });
```

### FFmpeg Encoding Pipeline
Every encode runs six staged steps, each a separately injected interface in
`src/NoMercy.Encoder/Pipeline/Stages/`: Analyze → Validate → Plan → Build →
Execute → Finalize. `Pipeline/Encoder.cs` drives the sequence;
`Orchestration/EncodingOrchestrator.cs` picks an `IEncodingStrategy` per
container family (HLS, DASH, MP4, MKV, audio-only). Multi-task runs share
one output directory, so only the coordinator runs Finalize
(`EncodingOptions.FinalizeOnly`); per-stream slices stop after Execute to
avoid racing the master playlist and font manifest.

### Real-time Communication
- `VideoHub`: Video playback control, progress tracking, device sync
- `MusicHub`: Audio playback, playlist management
- Located in: `src/NoMercy.Api/Hubs/`

### Key Configuration Files
- `src/NoMercy.Service/Program.cs`: Entry point, command-line parsing
- `src/NoMercy.Service/Configuration/ServiceConfiguration.cs`: DI container setup
- `src/NoMercy.Service/Configuration/ApplicationConfiguration.cs`: Middleware pipeline
- `src/NoMercy.Setup/Start.cs`: Startup sequence initialization

## Formatting

Formatting and naming conventions are in this repo's own top-level
`CLAUDE.md` ("The loop", "Naming", "Modern C#") — not repeated here.

## Code Style Rules (not already in the top-level CLAUDE.md)

### Class Structure Order
1. Private constants
2. Private readonly fields (with `_` prefix)
3. Properties
4. Constructor
5. Public methods
6. Private helper methods

### LINQ Style
Prefer method syntax. Use query syntax with `let` only when it reads more
clearly for a multi-step projection:
```csharp
return from genre in genreItems
    let name = genre.Translations.FirstOrDefault()?.Name ?? genre.Name
    select new GenreRowDto { Title = name };
```

### Comments
- Minimal comments; code should be self-documenting
- When needed, explain "why" not "what"
- Use XML docs (`///`) for public API methods

## External Provider Integrations

Located in `src/NoMercy.Providers/`:
- **TMDB/TVDB**: Movie/TV metadata
- **MusicBrainz/AcoustID**: Music metadata and fingerprinting
- **FanArt/CoverArt**: Artwork
- **OpenSubtitles**: Subtitles
- **Lrclib/MusixMatch**: Lyrics

All providers implement async patterns with retry logic and rate limiting.

## Dev Container & Security Rules

### Server Access
- The server uses wildcard SSL certs issued per device ID: `*.{device-id}.nomercy.tv`
- DNS records are created by the NoMercy API during server registration
- Device IDs are hardware-derived and differ between host and container
- Always access the server via its registered domain, never via `localhost`
- From inside the container, always use the external URL with proper CA verification:
  ```bash
  curl --cacert ~/.local/share/NoMercy_dev/security/certs/ca.pem https://{external-ip-dashed}.{device-id}.nomercy.tv:7626/...
  ```

### Security - Mandatory
- **NEVER** use `curl -sk`, `--insecure`, or skip certificate verification
- **NEVER** use `localhost` to access the server — always use the proper `*.nomercy.tv` domain
- **NEVER** suggest bypassing SSL warnings or security measures
- **NEVER** override internal server behavior (cert, device ID, DNS, registration) with CLI flags unless explicitly asked
- The cert, device ID, and DNS setup are integral parts of the system — they must not be worked around

### Running in Dev Container
```bash
# Start with host LAN IP so both container and host can access
dotnet run --project src/NoMercy.Service -- --dev --internal-ip <HOST_LAN_IP>
```
- The `--internal-ip` flag is needed because the container's Docker IP (172.17.x.x) isn't routable from the host
- Check the log file for the actual Internal/External addresses after startup
- The external URL (via public IP) is accessible from both host browser and container

## Cross-Platform Deployment

- **Linux**: Systemd services, DEB/RPM/Arch packages
- **Windows**: Registry auto-startup, executable (`NoMercyMediaServer.exe`)
- **macOS**: LaunchAgent plist files

CI/CD via GitHub Actions builds platform-specific executables and packages.
