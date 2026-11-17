// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.OutOfProcess;
using NoMercy.PluginSdk.Sideload;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The runtime-mode answer names the plugins that need the server's own
/// process, and why (issue #68).
/// </summary>
[Trait("Category", "Unit")]
public class PluginRuntimeModeNeedsTests
{
    private static readonly Ulid Analyser = Ulid.Parse("01J9ZK5V8Y0000000000000011");
    private static readonly Ulid Fits = Ulid.Parse("01J9ZK5V8Y0000000000000012");

    private static PluginInfo Plugin(Ulid id, PluginCapabilities capabilities) =>
        new()
        {
            Id = id,
            Name = "Sample",
            Description = "d",
            Version = new(1, 0, 0),
            Status = PluginStatus.Active,
            Capabilities = capabilities,
        };

    private static JObject RuntimeMode(IPluginIsolationFit? fit)
    {
        PluginInfo[] installed =
        [
            Plugin(
                Analyser,
                new PluginCapabilities
                {
                    Hooks = [PluginHookCapability.MusicAnalysisWrite],
                    Rest = true,
                }
            ),
            Plugin(Fits, new PluginCapabilities { Hooks = [PluginHookCapability.LibraryWrite] }),
        ];

        Mock<IPluginManager> manager = new();
        manager.Setup(m => m.GetInstalledPlugins()).Returns(installed);

        PluginController controller = new(
            manager.Object,
            Mock.Of<IPluginConsentService>(),
            Mock.Of<IPluginGrantStore>(),
            Mock.Of<IPluginRestartAdvisor>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IPluginDeveloperModeSource>(),
            null,
            fit
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

        OkObjectResult result = (OkObjectResult)controller.RuntimeMode();

        return JObject.Parse(JsonConvert.SerializeObject(result.Value));
    }

    [Fact]
    public void A_plugin_that_needs_the_server_process_is_named_with_what_it_uses()
    {
        JObject need = (JObject)
            RuntimeMode(new PluginIsolationFit())["needs_in_process"]![Analyser.ToString()]!;

        need["facades"]!
            .Values<string>()
            .Should()
            .BeEquivalentTo(nameof(IPluginContext.MusicAnalysisWriter));
        need["hooks"]!.Values<string>().Should().BeEmpty();
        need["notice"]!.Value<string>().Should().Be(PluginIsolationLevel.NeedsInProcessKey);
        need["controllers"]!.Value<bool>().Should().BeTrue();
    }

    [Fact]
    public void A_plugin_that_fits_is_not_in_the_map()
    {
        JObject needs = (JObject)RuntimeMode(new PluginIsolationFit())["needs_in_process"]!;

        needs.ContainsKey(Fits.ToString()).Should().BeFalse();
    }

    [Fact]
    public void The_answer_keeps_every_field_it_had()
    {
        JObject answer = RuntimeMode(new PluginIsolationFit());

        answer.Properties().Select(p => p.Name).Should().Contain(["isolation", "per_plugin"]);
    }

    [Fact]
    public void A_host_with_no_fit_check_leaves_the_field_out_rather_than_claiming_every_plugin_fits()
    {
        RuntimeMode(null).ContainsKey("needs_in_process").Should().BeFalse();
    }
}
