# Encoder Building-Block Plugins

The NoMercy encoder exposes a set of swappable building-block interfaces.
The host application can replace the default implementation by registering a
new binding **after** calling `AddNoMercyEncoder()`. Microsoft's DI container
returns the last registration for `GetRequiredService<T>`, so no special hook
or decorator is required.

A plugin never writes into the server's container — `IPluginServiceRegistrator`
registers into the plugin's own, isolated one, not this one. A plugin-facing
way to replace a building block would be an SDK capability; none exists yet.

## Replaceable building blocks

- `IFontExtractor` — extracts embedded fonts from mkv/mp4 containers
- `ISubtitleExtractor` — pulls subtitle tracks to disk as srt/ass/vtt
- `IChapterWriter` — writes chapter metadata into the output container
- `IThumbnailGenerator` — generates sprite sheets and poster thumbnails
- `IPlaylistGenerator` — builds HLS master/variant playlists
- `IFilterGraphBuilder` — assembles FFmpeg `-filter_complex` graphs
- `IHlsVariantAnalyzer` — inspects finished HLS variants for quality checks
- `IAbrLadderGenerator` — decides which resolution tiers to encode
- `INotificationDispatcher` — delivers webhook/push notifications on job events
- `IWorkerDispatcher` — routes encode tasks to local or remote workers

## Registering a replacement

```csharp
services.AddNoMercyEncoder(...);

// Override the default FontExtractor with your own implementation.
services.AddTransient<IFontExtractor, MyCustomFontExtractor>();
```

## Lifetime rules

Building blocks are registered as **Transient** by default — a new instance
is created per resolution. Replacement implementations must be safe to
instantiate multiple times concurrently. If your implementation holds shared
state (e.g. a connection pool), register it as **Singleton** explicitly; the
container will honour whichever lifetime you declare on the replacement.
Do not capture scoped services in a Singleton replacement.
