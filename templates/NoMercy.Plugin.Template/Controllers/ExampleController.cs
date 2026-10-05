using Microsoft.AspNetCore.Mvc;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;

namespace NoMercy.Plugin.Template.Controllers;

/// <summary>
/// One endpoint, to show the shape.
/// <para>
/// The caller is read from the request rather than from a token the plugin
/// parsed itself, and the access is declared on the action so the host
/// refuses before the body runs rather than after it has already done work.
/// Shared means any signed-in user; Owner narrows it to the server owner.
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
    [PluginRequires(PluginRouteAccess.Shared)]
    public IActionResult Get()
    {
        return Data(new { greetingKey = "template.home.heading", caller = Caller.DisplayName });
    }
}
