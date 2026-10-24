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
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Dependencies;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// A listing the marketplace shows before its first release has no version to
/// install. The catalog has to say so, and Install has to explain it.
/// </summary>
public class PluginCatalogNoReleaseTests
{
    private static readonly Ulid UnreleasedId = Ulid.Parse("01JAAAAAAAAAAAAAAAAAAAAAAA");
    private static readonly Ulid ReleasedId = Ulid.Parse("01JBBBBBBBBBBBBBBBBBBBBBBB");

    private sealed class FixedCatalogue : IPluginRepository
    {
        private readonly List<PluginRepositoryEntry> _entries =
        [
            new()
            {
                Id = UnreleasedId,
                Name = "Unreleased",
                Description = "Listed, not released",
                Versions = [],
            },
            new()
            {
                Id = ReleasedId,
                Name = "Released",
                Description = "Has a release",
                Versions =
                [
                    new() { Version = "1.0.0", DownloadUrl = "https://example.test/a.zip" },
                ],
            },
        ];

        public IReadOnlyList<PluginRepositoryInfo> GetRepositories() => [];

        public Task AddRepositoryAsync(string name, string url, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveRepositoryAsync(string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SetRepositoryTrustAsync(
            string name,
            bool trusted,
            CancellationToken ct = default
        ) => Task.CompletedTask;

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<PluginRepositoryEntry> GetAvailablePlugins() => _entries;

        public PluginRepositoryEntry? FindPlugin(Ulid pluginId) =>
            _entries.FirstOrDefault(entry => entry.Id == pluginId);

        public PluginVersionEntry? FindVersion(Ulid pluginId, string version) =>
            FindPlugin(pluginId)?.Versions.FirstOrDefault(entry => entry.Version == version);
    }

    private static PluginRepositoryController BuildController()
    {
        FixedCatalogue repository = new();

        return new(
            repository,
            Mock.Of<IPluginManager>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IHttpClientFactory>(),
            new(new PluginRepositoryCatalogue(repository), Mock.Of<IPluginManifestSource>())
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };
    }

    private static List<PluginCatalogueEntryDto> Listed(IActionResult result) =>
        (
            (DataResponseDto<IEnumerable<PluginCatalogueEntryDto>>)((OkObjectResult)result).Value!
        ).Data!.ToList();

    [Fact]
    public void A_listing_without_a_release_says_it_has_none()
    {
        List<PluginCatalogueEntryDto> listed = Listed(BuildController().Available());

        listed.Single(entry => entry.Id == UnreleasedId).HasRelease.Should().BeFalse();
        listed.Single(entry => entry.Id == UnreleasedId).LatestVersion.Should().BeNull();
    }

    [Fact]
    public void A_listing_with_a_release_says_it_has_one()
    {
        List<PluginCatalogueEntryDto> listed = Listed(BuildController().Available());

        listed.Single(entry => entry.Id == ReleasedId).HasRelease.Should().BeTrue();
    }

    [Fact]
    public async Task Installing_a_listing_without_a_release_explains_there_is_none_yet()
    {
        IActionResult result = await BuildController()
            .Install(UnreleasedId, version: null, CancellationToken.None);

        ObjectResult problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(404);
        ((ProblemDetails)problem.Value!).Detail.Should().Contain("no released version yet");
    }
}
