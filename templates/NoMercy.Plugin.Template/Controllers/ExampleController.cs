using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Plugin.Template.Controllers;

/// <summary>
/// One endpoint, to show the shape.
/// <para>
/// The caller is read from the request rather than from a token the plugin
/// parsed itself, and the capability is declared on the action so the host
/// refuses before the body runs rather than after it has already done work.
/// </para>
/// <para>
/// It answers a key rather than a sentence. A sentence returned from here is
/// one no translator can reach, and it arrives in English on a client whose
/// owner never chose English.
/// </para>
/// </summary>
[Route("api/example")]
public class ExampleController : PluginControllerBase
{
    [HttpGet]
    [PluginRequires(PluginCapabilityNames.Rest)]
    public IActionResult Get()
    {
        return Data(new { greetingKey = "template.home.heading", caller = Caller.DisplayName });
    }
}
