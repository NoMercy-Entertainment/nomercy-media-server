using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Template;

public class Plugin : IPlugin
{
    public string Name => "NoMercy.Plugin.Template";
    public string Description => "PLUGIN-DESCRIPTION-PLACEHOLDER";
    public Ulid Id { get; } = Ulid.Parse("PLUGIN-ULID-PLACEHOLDER");
    public Version Version { get; } = new(1, 0, 0);

    private IPluginContext? _context;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _context.Logger.LogInformation("{PluginName} v{Version} initialized", Name, Version);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
