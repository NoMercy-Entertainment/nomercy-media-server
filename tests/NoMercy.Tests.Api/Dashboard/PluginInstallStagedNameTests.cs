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
using NoMercy.PluginSdk;
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

    private sealed class OneEntryCatalog(string name, string downloadUrl) : IPluginRepository
    {
        private readonly PluginRepositoryEntry _entry = new()
        {
            Id = EntryId,
            Name = name,
            Description = "A made-up entry",
            Versions = [new() { Version = "1.0.0", DownloadUrl = downloadUrl }],
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

    private static readonly byte[] ZipPackage = [0x50, 0x4B, 0x03, 0x04, 1, 2, 3];

    private const string SignInPage = "<!DOCTYPE html><html><body>Sign in</body></html>";

    /// <summary>Answers every request with one body, as the URL the client ended on.</summary>
    private sealed class CannedDownload(
        byte[] body,
        string contentType = "application/zip",
        string? endedOn = null
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            ByteArrayContent content = new(body);
            content.Headers.ContentType = new(contentType);

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = content,
                    RequestMessage = endedOn is null ? request : new(HttpMethod.Get, endedOn),
                }
            );
        }
    }

    private sealed class Harness
    {
        public List<string> OpenedForWrite { get; } = [];
        public List<MemoryStream> Staged { get; } = [];
        public Mock<IPluginManager> Manager { get; } = new();
        public Mock<IPluginRepositoryInstaller> Installer { get; } = new();
        public PluginRepositoryController Controller { get; }

        public Harness(
            string entryName,
            string downloadUrl = "https://example.com/a.zip",
            bool listedByTheOfficialMarketplace = false,
            HttpMessageHandler? download = null,
            PluginRepositoryInfo? listedBy = null
        )
        {
            Mock<IPluginRepositoryOrigin> origin = new();
            origin
                .Setup(source =>
                    source.IsFromOfficialMarketplace(It.IsAny<PluginRepositoryEntry>())
                )
                .Returns(listedByTheOfficialMarketplace);
            origin
                .Setup(source => source.ListedBy(It.IsAny<PluginRepositoryEntry>()))
                .Returns(listedBy);

            // CallBase keeps the interface's own CombinePath, the join under test.
            Mock<IStorageDriver> storage = new() { CallBase = true };
            storage
                .Setup(driver => driver.OpenWrite(It.IsAny<string>(), It.IsAny<bool>()))
                .Returns(
                    (string path, bool _) =>
                    {
                        OpenedForWrite.Add(path);
                        MemoryStream staged = new();
                        Staged.Add(staged);
                        return staged;
                    }
                );

            Mock<IHttpClientFactory> http = new();
            http.Setup(factory => factory.CreateClient(It.IsAny<string>()))
                .Returns(() => new(download ?? new CannedDownload(ZipPackage)));

            OneEntryCatalog repository = new(entryName, downloadUrl);

            Controller = new(
                repository,
                Manager.Object,
                storage.Object,
                http.Object,
                new(new PluginRepositoryCatalogue(repository), Mock.Of<IPluginManifestSource>()),
                origin.Object,
                Installer.Object
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
        harness.Installer.Invocations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://example.com/a.zip")]
    [InlineData("https://example.com/a.zip?expires=1&sig=abc")]
    public async Task Installing_an_entry_with_a_plain_name_still_installs_Aud1046(
        string downloadUrl
    )
    {
        Harness harness = new("Example.Plugin", downloadUrl);

        IActionResult result = await harness.Controller.Install(
            EntryId,
            version: null,
            CancellationToken.None
        );

        result.Should().BeOfType<OkObjectResult>();
        string staged = harness.OpenedForWrite.Should().ContainSingle().Subject;
        staged.Should().EndWith("/Example.Plugin.zip");
        harness.Staged.Should().ContainSingle().Which.ToArray().Should().Equal(ZipPackage);
        harness.Installer.Verify(
            installer =>
                installer.InstallFromRepositoryAsync(
                    staged,
                    null,
                    It.IsAny<PluginIndexSignature?>(),
                    false,
                    It.IsAny<PluginRepositoryInfo?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_install_carries_whether_the_official_marketplace_listed_the_entry(
        bool listedByTheOfficialMarketplace
    )
    {
        Harness harness = new(
            "Example.Plugin",
            listedByTheOfficialMarketplace: listedByTheOfficialMarketplace
        );

        await harness.Controller.Install(EntryId, version: null, CancellationToken.None);

        harness.Installer.Verify(
            installer =>
                installer.InstallFromRepositoryAsync(
                    It.IsAny<string>(),
                    null,
                    It.IsAny<PluginIndexSignature?>(),
                    listedByTheOfficialMarketplace,
                    It.IsAny<PluginRepositoryInfo?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    /// <summary>
    /// The repository that listed the entry travels with the install, so the
    /// plugin's info can say afterwards which index it came from.
    /// </summary>
    [Fact]
    public async Task The_install_carries_the_repository_that_listed_the_entry()
    {
        PluginRepositoryInfo index = new()
        {
            Name = "Example Index",
            Url = "https://example.com/index.json",
        };
        Harness harness = new("Example.Plugin", listedBy: index);

        await harness.Controller.Install(EntryId, version: null, CancellationToken.None);

        harness.Installer.Verify(
            installer =>
                installer.InstallFromRepositoryAsync(
                    It.IsAny<string>(),
                    null,
                    It.IsAny<PluginIndexSignature?>(),
                    false,
                    index,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Theory]
    [InlineData("https://example.com/a.dll")]
    [InlineData("https://example.com/download?id=1")]
    public async Task A_repository_download_that_is_not_a_zip_package_is_refused(string downloadUrl)
    {
        Harness harness = new("Example.Plugin", downloadUrl);

        IActionResult result = await harness.Controller.Install(
            EntryId,
            version: null,
            CancellationToken.None
        );

        harness.OpenedForWrite.Should().BeEmpty();
        ObjectResult problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(422);
        ((ProblemDetails)problem.Value!).Detail.Should().Contain(".zip");
        harness.Installer.Invocations.Should().BeEmpty();
    }

    [Fact]
    public async Task A_download_redirected_to_a_sign_in_page_is_refused_before_the_checksum()
    {
        Harness harness = new(
            "Example.Plugin",
            download: new CannedDownload(
                System.Text.Encoding.UTF8.GetBytes(SignInPage),
                "text/html",
                endedOn: "https://example.com/login"
            )
        );

        await ShouldRefuseAsNotAPluginFile(harness);
    }

    [Fact]
    public async Task A_download_that_ends_on_another_host_is_refused()
    {
        Harness harness = new(
            "Example.Plugin",
            download: new CannedDownload(ZipPackage, endedOn: "https://elsewhere.example/a.zip")
        );

        await ShouldRefuseAsNotAPluginFile(harness);
    }

    [Fact]
    public async Task A_web_page_carrying_zip_bytes_is_refused()
    {
        Harness harness = new(
            "Example.Plugin",
            download: new CannedDownload(ZipPackage, "text/html")
        );

        await ShouldRefuseAsNotAPluginFile(harness);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html></html>")]
    [InlineData("MZ\u0090\u0000\u0003")]
    [InlineData("PK")]
    [InlineData("")]
    public async Task A_download_whose_bytes_are_not_a_zip_archive_is_refused(string body)
    {
        Harness harness = new(
            "Example.Plugin",
            download: new CannedDownload(
                System.Text.Encoding.Latin1.GetBytes(body),
                "application/octet-stream"
            )
        );

        await ShouldRefuseAsNotAPluginFile(harness);
    }

    private static async Task ShouldRefuseAsNotAPluginFile(Harness harness)
    {
        IActionResult result = await harness.Controller.Install(
            EntryId,
            version: null,
            CancellationToken.None
        );

        ObjectResult problem = result.Should().BeOfType<ObjectResult>().Subject;
        problem.StatusCode.Should().Be(422);
        ((ProblemDetails)problem.Value!).Detail.Should().Be("The download was not a plugin file");
        harness.OpenedForWrite.Should().BeEmpty();
        harness.Installer.Invocations.Should().BeEmpty();
        harness.Manager.Invocations.Should().BeEmpty();
    }
}
