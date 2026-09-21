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

using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Plugins;
using NoMercy.Authorization;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Access;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Quotas;
using NoMercy.Plugins.Telemetry;
using NoMercy.Plugins.Watchdog;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// The pages the server owns. A member is turned away from the pages that
/// change the server, and told exactly what somebody without access is told:
/// a refusal that distinguished them would list this server's plugins.
/// </summary>
public class PluginBasePathEndpointTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01J9ZK5V8Y000000000000000B");
    private static readonly Guid Owner = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Member = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly Mock<IPluginManager> _plugins = new();
    private readonly Mock<IPluginConsentService> _consent = new();
    private readonly Mock<IMediaAuthorizationPolicy> _policy = new();
    private readonly PluginRefusalCounter _refusals = new();
    private readonly PluginCrashCounter _crashes = new();

    private PluginBasePathController Build(Guid asking, bool owner, PluginAccess granted)
    {
        _plugins
            .Setup(manager => manager.GetPluginInfo(PluginId))
            .Returns(
                new PluginInfo
                {
                    Id = PluginId,
                    Name = "Internet Radio",
                    Description = "Stations",
                    Version = new(2, 0, 0),
                    Status = PluginStatus.Active,
                    Capabilities = new() { Hooks = ["network.fetch"] },
                    Settings =
                    [
                        new()
                        {
                            Key = "catalogue.url",
                            LabelKey = "radio.settings.catalogue",
                            Type = PluginFormFieldType.Text,
                        },
                        new()
                        {
                            Key = "favorites.sort",
                            LabelKey = "radio.settings.sort",
                            Type = PluginFormFieldType.Select,
                            Scope = PluginSettingsScope.User,
                        },
                        new()
                        {
                            Key = "provider.password",
                            LabelKey = "radio.settings.password",
                            Type = PluginFormFieldType.Password,
                            Default = "hunter2",
                        },
                    ],
                }
            );

        _policy.Setup(check => check.IsOwner(It.IsAny<ClaimsPrincipal>())).Returns(owner);

        PluginQuotaStore quotas = new(
            PluginQuota.DefaultFor(4, 8L * 1024 * 1024 * 1024, 1_000_000)
        );

        PluginBasePathController controller = new(
            _plugins.Object,
            new StubAccess(granted),
            _policy.Object,
            new(_consent.Object),
            _refusals,
            _crashes,
            new(quotas, new StubLifecycle(), TimeProvider.System),
            quotas,
            new(quotas, TimeProvider.System)
        );

        controller.ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new(
                    new ClaimsIdentity([new(ClaimTypes.NameIdentifier, asking.ToString())], "test")
                ),
            },
        };

        return controller;
    }

    [Fact]
    public void Permissions_are_owner_only()
    {
        PluginBasePathController controller = Build(Member, owner: false, PluginAccess.Shared);

        controller.Permissions(PluginId).Should().BeOfType<ObjectResult>();
        ((ObjectResult)controller.Permissions(PluginId)).StatusCode.Should().Be(403);
    }

    [Fact]
    public void Health_is_owner_only()
    {
        PluginBasePathController controller = Build(Member, owner: false, PluginAccess.Shared);

        ((ObjectResult)controller.Health(PluginId)).StatusCode.Should().Be(403);
    }

    [Fact]
    public void Update_is_owner_only()
    {
        PluginBasePathController controller = Build(Member, owner: false, PluginAccess.Shared);

        ((ObjectResult)controller.Update(PluginId)).StatusCode.Should().Be(403);
    }

    [Fact]
    public void Info_is_open_to_anyone_the_plugin_is_shared_with()
    {
        PluginBasePathController controller = Build(Member, owner: false, PluginAccess.Shared);

        controller.Info(PluginId).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public void Someone_without_access_sees_nothing_at_all()
    {
        PluginBasePathController controller = Build(Member, owner: false, PluginAccess.None);

        ((ObjectResult)controller.Info(PluginId)).StatusCode.Should().Be(403);
    }

    [Fact]
    public void Permissions_say_that_stage_one_is_not_isolated()
    {
        PluginBasePathController controller = Build(Owner, owner: true, PluginAccess.Owned);

        PluginPermissionsPageDto page = Page<PluginPermissionsPageDto>(
            controller.Permissions(PluginId)
        );

        page.Isolation.Should().Be("in-process");
        page.NoticeKey.Should().Be("plugins.permissions.in_process_notice");
    }

    [Fact]
    public void Permissions_carry_what_the_plugin_asked_for_and_what_it_was_refused()
    {
        _refusals.Count(
            PluginId,
            new(
                PluginRefusalCodes.CapabilityNotDeclared,
                PluginId.ToString(),
                "w",
                "y",
                "f",
                PluginRefusalSeverity.Blocked
            )
        );

        PluginBasePathController controller = Build(Owner, owner: true, PluginAccess.Owned);

        PluginPermissionsPageDto page = Page<PluginPermissionsPageDto>(
            controller.Permissions(PluginId)
        );

        page.Capabilities.Should().ContainSingle().Which.Name.Should().Be("network.fetch");
        page.Refusals.Should()
            .ContainSingle()
            .Which.Code.Should()
            .Be(PluginRefusalCodes.CapabilityNotDeclared);
    }

    [Fact]
    public void Health_reports_counts_and_what_the_plugin_is_allowed()
    {
        _crashes.RecordCrash(PluginId);
        _crashes.RecordCeilingHit(PluginId);

        PluginBasePathController controller = Build(Owner, owner: true, PluginAccess.Owned);

        PluginHealthPageDto page = Page<PluginHealthPageDto>(controller.Health(PluginId));

        page.Crashes.Should().Be(1);
        page.CeilingHits.Should().Be(1);
        page.Quota.DiskBytes.Should().Be(5L * 1024 * 1024 * 1024);
    }

    [Fact]
    public void An_unknown_plugin_is_not_found_rather_than_an_empty_page()
    {
        PluginBasePathController controller = Build(Owner, owner: true, PluginAccess.Owned);

        ((ObjectResult)controller.Info(Ulid.NewUlid())).StatusCode.Should().Be(404);
    }

    [Fact]
    public void The_owner_sees_every_settings_field()
    {
        PluginBasePathController controller = Build(Owner, owner: true, PluginAccess.Owned);

        PluginSettingsPageDto page = Page<PluginSettingsPageDto>(controller.Settings(PluginId));

        page.Fields.Should().HaveCount(3);
        page.Scope.Should().Be("server");
    }

    [Fact]
    public void A_member_sees_only_the_fields_that_are_theirs()
    {
        PluginBasePathController controller = Build(Member, owner: false, PluginAccess.Shared);

        PluginSettingsPageDto page = Page<PluginSettingsPageDto>(controller.Settings(PluginId));

        page.Fields.Should().ContainSingle().Which.Key.Should().Be("favorites.sort");
    }

    [Fact]
    public void A_password_is_never_sent_back_with_the_page()
    {
        PluginBasePathController controller = Build(Owner, owner: true, PluginAccess.Owned);

        PluginSettingsPageDto page = Page<PluginSettingsPageDto>(controller.Settings(PluginId));

        page.Fields.Single(field => field.Key == "provider.password")
            .Default.Should()
            .BeNull("a password lives in the secret store, not in a page every proxy can log");
    }

    private static T Page<T>(IActionResult result) =>
        ((DataResponseDto<T>)((OkObjectResult)result).Value!).Data!;

    private sealed class StubAccess(PluginAccess granted) : IPluginAccessResolver
    {
        public PluginAccess Resolve(Ulid pluginId, Guid userId) => granted;
    }

    private sealed class StubLifecycle : IPluginWatchdogLifecycle
    {
        public void Throttle(Ulid pluginId) { }

        public void Restart(Ulid pluginId) { }

        public void Disable(Ulid pluginId) { }
    }
}
