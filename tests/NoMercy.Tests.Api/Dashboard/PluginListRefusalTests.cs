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
using NoMercy.Api.DTOs.Common;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Sideload;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>The plugin list says why a run check refused a plugin; null when none did (AUD-1134).</summary>
public class PluginListRefusalTests
{
    private static readonly Ulid Revoked = Ulid.Parse("01J9ZK5V8Y0000000000000001");
    private static readonly Ulid Healthy = Ulid.Parse("01J9ZK5V8Y0000000000000002");

    private static readonly PluginRefusal Refusal = new(
        PluginRefusalCodes.Revoked,
        Revoked.ToString(),
        "The plugin stopped because this build was revoked.",
        "NoMercy revoked this build: plugins.revoked.security.",
        "Install the newest version.",
        PluginRefusalSeverity.Blocked
    );

    private static PluginInfo Plugin(Ulid id) =>
        new()
        {
            Id = id,
            Name = "Sample",
            Description = "d",
            Version = new(1, 0, 0),
            Status = PluginStatus.Disabled,
        };

    private sealed class RefusesTheRevoked : IPluginRunCheck
    {
        public PluginRefusal? Check(PluginInfo plugin) => plugin.Id == Revoked ? Refusal : null;
    }

    private static List<PluginInfoDto> List()
    {
        PluginInfo[] installed = [Plugin(Revoked), Plugin(Healthy)];

        Mock<IPluginManager> manager = new();
        manager.Setup(m => m.GetInstalledPlugins()).Returns(installed);

        Mock<IPluginConsentService> consent = new();
        consent.Setup(c => c.IsBaseline(It.IsAny<PluginCapabilities>())).Returns(true);

        // The real gate: the list reads the refusal where the run checks keep it.
        PluginRunGate runGate = new([new RefusesTheRevoked()]);
        foreach (PluginInfo plugin in installed)
            runGate.MayRun(plugin);

        PluginController controller = new(
            manager.Object,
            consent.Object,
            Mock.Of<IPluginGrantStore>(),
            Mock.Of<IPluginRestartAdvisor>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IPluginDeveloperModeSource>(),
            runGate
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

        OkObjectResult result = (OkObjectResult)controller.Index();

        return [.. ((DataResponseDto<IEnumerable<PluginInfoDto>>)result.Value!).Data!];
    }

    [Fact]
    public void A_revoked_plugin_carries_its_refusal_in_the_list()
    {
        JObject row = JObject.Parse(
            JsonConvert.SerializeObject(List().Single(plugin => plugin.Id == Revoked))
        );

        row["refusal"]!["code"]!.Value<string>().Should().Be(PluginRefusalCodes.Revoked);
        row["refusal"]!["why"]!.Value<string>().Should().Be(Refusal.Why);
        row["refusal"]!["fix"]!.Value<string>().Should().Be(Refusal.Fix);
    }

    [Fact]
    public void A_healthy_plugin_carries_a_null_refusal_in_the_list()
    {
        JObject row = JObject.Parse(
            JsonConvert.SerializeObject(List().Single(plugin => plugin.Id == Healthy))
        );

        row.ContainsKey("refusal").Should().BeTrue("the field is always there");
        row["refusal"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void The_list_keeps_every_field_it_had()
    {
        JObject row = JObject.Parse(
            JsonConvert.SerializeObject(List().Single(plugin => plugin.Id == Healthy))
        );

        row.Properties()
            .Select(property => property.Name)
            .Should()
            .Contain([
                "id",
                "name",
                "description",
                "version",
                "status",
                "author",
                "project_url",
                "capabilities",
                "awaiting_consent",
                "awaiting_approval",
                "consented_capabilities",
                "restart_required",
                "restart_reasons",
            ]);
    }
}
