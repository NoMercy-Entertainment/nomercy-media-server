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

using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Provenance;
using NoMercy.PluginSdk.Sideload;
using NoMercy.PluginSdk.Storage;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// The plugin's info says where it came from, whether that source is trusted
/// now, and which grants it holds, and one grant can be taken back on its own.
/// </summary>
public class PluginInfoSourceAndGrantsTests
{
    private const string IndexUrl = "https://example.com/index.json";
    private static readonly Ulid PluginId = Ulid.Parse("01JS0VRCE00000000000000000");

    private readonly Mock<IPluginManager> _pluginManager = new();
    private readonly Mock<IPluginGrantStore> _grantStore = new();
    private readonly Mock<IPluginProvenanceReader> _provenance = new();
    private readonly Mock<IPluginConsentService> _consent = new();

    public PluginInfoSourceAndGrantsTests()
    {
        _pluginManager
            .Setup(manager => manager.GetInstalledPlugins())
            .Returns([
                new()
                {
                    Id = PluginId,
                    Name = "Radio",
                    Description = "Stations",
                    Version = new(1, 2, 0),
                    Status = PluginStatus.Active,
                },
            ]);
        _grantStore.Setup(store => store.HeldBy(It.IsAny<Ulid>())).Returns([]);
        _provenance
            .Setup(reader => reader.Read(It.IsAny<Ulid>()))
            .Returns(PluginProvenance.Unknown);
    }

    private readonly Mock<IPluginGrantedLocations> _locations = new();

    private PluginController BuildController(
        bool withProvenance = true,
        IPluginGrantStore? grantStore = null,
        IPluginGrantedLocations? locations = null
    ) =>
        new(
            _pluginManager.Object,
            _consent.Object,
            grantStore ?? _grantStore.Object,
            Mock.Of<IPluginRestartAdvisor>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IPluginDeveloperModeSource>(),
            provenance: withProvenance ? _provenance.Object : null,
            grantedLocations: locations ?? _locations.Object
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

    private static PluginInfoDto Payload(IActionResult result) =>
        ((DataResponseDto<PluginInfoDto>)((OkObjectResult)result).Value!).Data!;

    private static JObject Json(PluginInfoDto dto) =>
        JObject.Parse(JsonConvert.SerializeObject(dto));

    private static PluginProvenance FromIndex(bool trustedNow) =>
        new()
        {
            Kind = PluginInstallSourceKind.Repository,
            RepositoryName = "Example Index",
            RepositoryUrl = IndexUrl,
            RepositoryTrusted = trustedNow,
            RepositoryRemoved = false,
            FromOfficialMarketplace = false,
            ChecksumVerified = true,
            Version = "1.2.0",
            RecordedAt = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
        };

    private static PluginGrantEntry Held(string kind, string value) =>
        new()
        {
            PluginId = PluginId,
            Kind = kind,
            Value = value,
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Show_reports_the_recorded_source_and_the_trust_read_now(bool trustedNow)
    {
        _provenance.Setup(reader => reader.Read(PluginId)).Returns(FromIndex(trustedNow));

        PluginInfoDto info = Payload(BuildController().Show(PluginId));

        info.Source.Kind.Should().Be("repository");
        info.Source.Repository!.Name.Should().Be("Example Index");
        info.Source.Repository.Url.Should().Be(IndexUrl);
        info.Source.Repository.Trusted.Should().Be(trustedNow);
        info.Source.Repository.Removed.Should().BeFalse();
        info.Source.OfficialMarketplace.Should().BeFalse();
        info.Source.ChecksumVerified.Should().BeTrue();
    }

    [Fact]
    public void An_upload_is_reported_as_a_file_with_no_repository()
    {
        _provenance
            .Setup(reader => reader.Read(PluginId))
            .Returns(
                new PluginProvenance
                {
                    Kind = PluginInstallSourceKind.File,
                    FromOfficialMarketplace = false,
                    ChecksumVerified = false,
                    RecordedAt = DateTime.UtcNow,
                }
            );

        PluginInfoDto info = Payload(BuildController().Show(PluginId));

        info.Source.Kind.Should().Be("file");
        info.Source.Repository.Should().BeNull();
        info.Source.ChecksumVerified.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_plugin_with_no_recorded_source_is_unknown_never_a_guess(bool withProvenance)
    {
        JObject json = Json(Payload(BuildController(withProvenance).Show(PluginId)));

        JObject source = (JObject)json["source"]!;
        source["kind"]!.Value<string>().Should().Be("unknown");
        source["repository"]!.Type.Should().Be(JTokenType.Null);
        source["official_marketplace"]!.Type.Should().Be(JTokenType.Null);
        source["checksum_verified"]!.Type.Should().Be(JTokenType.Null);
        source["version"]!.Type.Should().Be(JTokenType.Null);
        source["recorded_at"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void Show_and_the_list_return_the_grants_the_plugin_holds()
    {
        _grantStore
            .Setup(store => store.HeldBy(PluginId))
            .Returns([
                Held("capability.media.proxy", PluginGrant.Everything),
                Held(PluginGrantKind.NetworkHost, PluginGrant.Everything),
            ]);
        PluginController controller = BuildController();

        PluginInfoDto shown = Payload(controller.Show(PluginId));
        PluginInfoDto listed = (
            (DataResponseDto<IEnumerable<PluginInfoDto>>)((OkObjectResult)controller.Index()).Value!
        ).Data!.Single();

        foreach (PluginInfoDto info in new[] { shown, listed })
            info.Grants.Select(grant => (grant.Kind, grant.Value))
                .Should()
                .Equal(
                    ("capability.media.proxy", PluginGrant.Everything),
                    (PluginGrantKind.NetworkHost, PluginGrant.Everything)
                );
    }

    [Fact]
    public void The_new_fields_have_the_names_the_client_reads()
    {
        _provenance.Setup(reader => reader.Read(PluginId)).Returns(FromIndex(trustedNow: true));
        _grantStore
            .Setup(store => store.HeldBy(PluginId))
            .Returns([Held(PluginGrantKind.NetworkHost, PluginGrant.Everything)]);

        JObject json = Json(Payload(BuildController().Show(PluginId)));

        JToken
            .DeepEquals(
                json["source"],
                JObject.Parse(
                    """
                    {
                      "kind": "repository",
                      "repository": {
                        "name": "Example Index",
                        "url": "https://example.com/index.json",
                        "trusted": true,
                        "removed": false
                      },
                      "official_marketplace": false,
                      "checksum_verified": true,
                      "version": "1.2.0",
                      "recorded_at": "2026-11-01T12:00:00Z"
                    }
                    """
                )
            )
            .Should()
            .BeTrue(json["source"]!.ToString());
        JToken
            .DeepEquals(
                json["grants"],
                JArray.Parse("""[{ "kind": "network.host", "value": "*" }]""")
            )
            .Should()
            .BeTrue(json["grants"]!.ToString());
    }

    [Fact]
    public void Every_field_the_info_had_before_is_still_sent_under_its_name()
    {
        JObject json = Json(Payload(BuildController().Show(PluginId)));

        json.Properties()
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
                "refusal",
            ]);
        json["version"]!.Value<string>().Should().Be("1.2.0");
        json["status"]!.Value<string>().Should().Be("active");
    }

    [Fact]
    public async Task Revoking_one_grant_takes_back_only_that_grant()
    {
        _grantStore
            .Setup(store => store.HeldBy(PluginId))
            .Returns([
                Held(PluginGrantKind.NetworkHost, PluginGrant.Everything),
                Held(PluginGrantKind.LibraryWrite, "library-1"),
            ]);

        IActionResult result = await BuildController()
            .RevokeGrant(PluginId, PluginGrantKind.NetworkHost, PluginGrant.Everything);

        result.Should().BeOfType<OkObjectResult>();
        _grantStore.Verify(
            store => store.Revoke(PluginId, PluginGrantKind.NetworkHost, PluginGrant.Everything),
            Times.Once
        );
        _grantStore.Verify(store => store.RevokeAll(It.IsAny<Ulid>()), Times.Never);
        _consent.Verify(consent => consent.RevokeConsent(It.IsAny<Ulid>()), Times.Never);
        _pluginManager.Verify(
            manager => manager.DisablePluginAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "taking back one grant leaves the plugin running"
        );
    }

    [Fact]
    public async Task Revoking_a_grant_the_plugin_does_not_hold_is_not_found()
    {
        _grantStore
            .Setup(store => store.HeldBy(PluginId))
            .Returns([Held(PluginGrantKind.LibraryWrite, "library-1")]);

        IActionResult result = await BuildController()
            .RevokeGrant(PluginId, PluginGrantKind.NetworkHost, PluginGrant.Everything);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
        _grantStore.Verify(
            store => store.Revoke(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Theory]
    [InlineData(null, "*")]
    [InlineData("network.host", null)]
    [InlineData(" ", "*")]
    public async Task Revoking_without_a_kind_and_a_value_is_refused(string? kind, string? value)
    {
        IActionResult result = await BuildController().RevokeGrant(PluginId, kind, value);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(422);
        _grantStore.Verify(
            store => store.Revoke(It.IsAny<Ulid>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never
        );
    }

    [Theory]
    [InlineData("process.spawn", true)]
    [InlineData("network.host", false)]
    [InlineData("capability.storage.path", false)]
    public async Task A_kind_read_only_at_launch_says_the_revoke_waits_for_a_restart(
        string kind,
        bool restartRequired
    )
    {
        _grantStore.Setup(store => store.HeldBy(PluginId)).Returns([Held(kind, "*")]);

        IActionResult result = await BuildController().RevokeGrant(PluginId, kind, "*");

        PluginGrantRevokedDto revoked = (PluginGrantRevokedDto)((OkObjectResult)result).Value!;
        revoked.RestartRequired.Should().Be(restartRequired);
        JObject.Parse(JsonConvert.SerializeObject(revoked))["restart_required"]!
            .Value<bool>()
            .Should()
            .Be(restartRequired);
        _pluginManager.Verify(
            manager => manager.RestartPluginAsync(It.IsAny<Ulid>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "the owner restarts the plugin, the revoke does not"
        );
    }

    [Fact]
    public void The_launch_time_list_is_the_one_the_revoke_reads()
    {
        PluginLaunchTimeGrants.All.Should().Contain(PluginCapabilityNames.ProcessSpawn);
        PluginLaunchTimeGrants.AppliesFromNextStart(PluginGrantKind.NetworkHost).Should().BeFalse();
    }

    /// <summary>
    /// The folders a plugin reads from its server info are cached. A revoked
    /// folder has to leave that list on the revoke, not at the plugin's next start.
    /// </summary>
    [Fact]
    public async Task A_revoked_storage_folder_leaves_the_plugin_s_granted_paths_at_once()
    {
        string kind = PluginGrantKind.ForCapability(PluginCapabilityNames.StoragePath);
        InMemoryGrants grants = new();
        grants.Grant(PluginId, kind, "movies");
        Mock<IPluginFolderCatalog> catalog = new();
        catalog
            .Setup(folders => folders.LocationsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new PluginStorageLocation("movies", "Movies", "library", true)]);
        PluginGrantedLocations locations = new(catalog.Object, grants);
        await locations.RefreshAsync(PluginId);
        PluginServerInfo info = new(
            PluginId,
            new(1, 0),
            locations,
            Mock.Of<IPluginFreeSpaceProbe>(),
            grants
        );
        info.GrantedPaths.Should().ContainSingle();

        await BuildController(grantStore: grants, locations: locations)
            .RevokeGrant(PluginId, kind, "movies");

        info.GrantedPaths.Should().BeEmpty();
    }

    /// <summary>Grants as the store keeps them, without the file.</summary>
    private sealed class InMemoryGrants : IPluginGrantStore
    {
        private readonly List<PluginGrantEntry> _grants = [];

        public IReadOnlyList<string> Granted(Ulid pluginId, string kind) =>
            [.. HeldBy(pluginId).Where(grant => grant.Kind == kind).Select(grant => grant.Value)];

        public IReadOnlyList<PluginGrantEntry> HeldBy(Ulid pluginId) =>
            [.. _grants.Where(grant => grant.PluginId == pluginId)];

        public bool Holds(Ulid pluginId, string kind, string value) =>
            Granted(pluginId, kind).Contains(value);

        public void Grant(Ulid pluginId, string kind, string value) =>
            _grants.Add(
                new()
                {
                    PluginId = pluginId,
                    Kind = kind,
                    Value = value,
                }
            );

        public void Revoke(Ulid pluginId, string kind, string value) =>
            _grants.RemoveAll(grant =>
                grant.PluginId == pluginId && grant.Kind == kind && grant.Value == value
            );

        public void RevokeAll(Ulid pluginId) =>
            _grants.RemoveAll(grant => grant.PluginId == pluginId);

        public void Request(Ulid pluginId, string kind, string value, string reason) { }

        public IReadOnlyList<PluginGrantRequest> PendingRequests() => [];

        public void ClearRequest(Ulid pluginId, string kind, string value) { }
    }

    [Fact]
    public void The_info_and_the_revoke_are_owner_only()
    {
        typeof(PluginController)
            .GetCustomAttribute<AuthorizeAttribute>()!
            .Policy.Should()
            .Be("Owner");

        foreach (
            string action in new[]
            {
                nameof(PluginController.Index),
                nameof(PluginController.Show),
                nameof(PluginController.RevokeGrant),
            }
        )
        {
            MethodInfo method = typeof(PluginController).GetMethod(action)!;

            method
                .GetCustomAttributes<AllowAnonymousAttribute>()
                .Should()
                .BeEmpty($"{action} must not open owner data to anyone");
            method
                .GetCustomAttributes<AuthorizeAttribute>()
                .Should()
                .BeEmpty($"{action} must keep the controller's owner policy");
        }
    }

    [Fact]
    public void The_revoke_is_a_delete_on_the_plugin_s_grants()
    {
        HttpDeleteAttribute route = typeof(PluginController)
            .GetMethod(nameof(PluginController.RevokeGrant))!
            .GetCustomAttribute<HttpDeleteAttribute>()!;

        route.Template.Should().Be("{id:ulid}/grants");
    }
}
