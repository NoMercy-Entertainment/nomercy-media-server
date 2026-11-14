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

using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Dashboard.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Dependencies;
using NoMercy.Storage;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// AUD-1046: the catalog entry name becomes the staged file name, so it must
/// be a plain file name. Anything else is refused before a byte is written.
/// </summary>
public class PluginInstallStagedNameTests
{
    private static readonly Ulid EntryId = Ulid.Parse("01JCCCCCCCCCCCCCCCCCCCCCCC");

    private sealed class OneEntryCatalog(string name) : IPluginRepository
    {
        private readonly PluginRepositoryEntry _entry = new()
        {
            Id = EntryId,
            Name = name,
            Description = "A made-up entry",
            Versions = [new() { Version = "1.0.0", DownloadUrl = "https://example.com/a.dll" }],
        };

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

        public IReadOnlyList<PluginRepositoryEntry> GetAvailablePlugins() => [_entry];

        public PluginRepositoryEntry? FindPlugin(Ulid pluginId) =>
            _entry.Id == pluginId ? _entry : null;

        public PluginVersionEntry? FindVersion(Ulid pluginId, string version) =>
            FindPlugin(pluginId)?.Versions.FirstOrDefault(entry => entry.Version == version);
    }

    private sealed class FewBytesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3]),
                }
            );
    }

    private sealed class Harness
    {
        public List<string> OpenedForWrite { get; } = [];
        public Mock<IPluginManager> Manager { get; } = new();
        public PluginRepositoryController Controller { get; }

        public Harness(string entryName)
        {
            // CallBase keeps the interface's own CombinePath, the join under test.
            Mock<IStorageDriver> storage = new() { CallBase = true };
            storage
                .Setup(driver => driver.OpenWrite(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(
                    (string path, bool _) =>
                    {
                        OpenedForWrite.Add(path);
                        return new MemoryStream();
                    }
                );

            Mock<IHttpClientFactory> http = new();
            http.Setup(factory => factory.CreateClient(It.IsAny<string>()))
                .Returns(() => new(new FewBytesHandler()));

            OneEntryCatalog repository = new(entryName);

            Controller = new(
                repository,
                Manager.Object,
                storage.Object,
                http.Object,
                new(new PluginRepositoryCatalogue(repository), Mock.Of<IPluginManifestSource>())
            )
            {
                ControllerContext = new() { HttpContext = new DefaultHttpContext() },
            };
        }
    }

    [Theory]
    [InlineData("../../probe")]
    [InlineData("..\\..\\probe")]
    [InlineData("sub/probe")]
    [InlineData("..")]
    public async Task Installing_an_entry_whose_name_leaves_the_staging_folder_is_refused_Aud1046(
        string entryName
    )
    {
        Harness harness = new(entryName);

        IActionResult result = await harness.Controller.Install(
            EntryId,
            version: null,
            CancellationToken.None
        );

        harness.OpenedForWrite.Should().BeEmpty();
        ObjectResult problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(422);
        ((ProblemDetails)problem.Value!).Detail.Should().Contain("not a plain file name");
        harness.Manager.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task Installing_an_entry_with_a_plain_name_still_installs_Aud1046()
    {
        Harness harness = new("Example.Plugin");

        IActionResult result = await harness.Controller.Install(
            EntryId,
            version: null,
            CancellationToken.None
        );

        result.Should().BeOfType<OkObjectResult>();
        string staged = harness.OpenedForWrite.Should().ContainSingle().Subject;
        staged.Should().EndWith("/Example.Plugin.dll");
        harness.Manager.Verify(
            manager => manager.InstallPluginAsync(staged, null, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }
}
