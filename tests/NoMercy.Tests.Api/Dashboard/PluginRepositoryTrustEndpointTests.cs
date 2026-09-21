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

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Dependencies;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// Whether a repository is trusted is the strongest privilege an owner grants,
/// and it was invisible: not in any response, not editable, only changeable by
/// hand-editing repositories.json on the server.
/// </summary>
public class PluginRepositoryTrustEndpointTests
{
    private sealed class RecordingRepository : IPluginRepository
    {
        private readonly List<PluginRepositoryInfo> _repositories =
        [
            new()
            {
                Name = "NoMercy Plugins",
                Url = "https://example.com/index.json",
                Trusted = false,
            },
        ];

        public IReadOnlyList<PluginRepositoryInfo> GetRepositories() => _repositories;

        public Task AddRepositoryAsync(string name, string url, CancellationToken ct = default)
        {
            _repositories.Add(new() { Name = name, Url = url });
            return Task.CompletedTask;
        }

        public Task RemoveRepositoryAsync(string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SetRepositoryTrustAsync(
            string name,
            bool trusted,
            CancellationToken ct = default
        )
        {
            PluginRepositoryInfo? info = _repositories.FirstOrDefault(entry => entry.Name == name);

            if (info is null)
                throw new InvalidOperationException($"Repository '{name}' not found.");

            info.Trusted = trusted;
            return Task.CompletedTask;
        }

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<PluginRepositoryEntry> GetAvailablePlugins() => [];

        public PluginRepositoryEntry? FindPlugin(Ulid pluginId) => null;

        public PluginVersionEntry? FindVersion(Ulid pluginId, string version) => null;
    }

    private static PluginRepositoryController BuildController(IPluginRepository repository) =>
        new(
            repository,
            Mock.Of<IPluginManager>(),
            Mock.Of<IStorageDriver>(),
            Mock.Of<IHttpClientFactory>(),
            new(new PluginRepositoryCatalogue(repository), Mock.Of<IPluginManifestSource>())
        )
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext() },
        };

    private static IEnumerable<PluginRepositoryInfoDto> Listed(IActionResult result) =>
        (
            (DataResponseDto<IEnumerable<PluginRepositoryInfoDto>>)((OkObjectResult)result).Value!
        ).Data!;

    [Fact]
    public void The_list_says_whether_a_repository_is_trusted()
    {
        RecordingRepository repository = new();

        PluginRepositoryInfoDto listed = Listed(BuildController(repository).Index()).Single();

        listed.Trusted.Should().BeFalse();
    }

    [Fact]
    public async Task An_owner_can_trust_a_repository()
    {
        RecordingRepository repository = new();

        IActionResult result = await BuildController(repository)
            .SetTrust("NoMercy Plugins", new() { Trusted = true }, CancellationToken.None);

        result.Should().BeOfType<OkObjectResult>();
        repository.GetRepositories().Single().Trusted.Should().BeTrue();
    }

    [Fact]
    public async Task Trusting_a_repository_the_server_does_not_have_is_a_not_found()
    {
        IActionResult result = await BuildController(new RecordingRepository())
            .SetTrust("nothing-here", new() { Trusted = true }, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(404);
    }
}
