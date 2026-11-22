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
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Api.Controllers.V1;
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Libraries;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.Storage.Validation;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// A plugin names its navigation entries with keys into its own string bundle.
/// The client only knows its own catalogue, so the sidebar showed
/// <c>stations.title</c> until the server started translating them.
/// </summary>
[Trait("Category", "Unit")]
public class SetupNavigationPluginLabelTests
{
    private static readonly Ulid PluginId = Ulid.Parse("01J9ZK5V8Y000000000000000D");
    private static readonly Guid Viewer = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static PluginNavEntry Entry(string label, string route) =>
        new()
        {
            Section = PluginKind.Music,
            Label = label,
            Icon = "radio",
            Route = route,
        };

    private static Mock<IPluginManager> Manager(params PluginNavEntry[] entries)
    {
        Mock<IUiPlugin> plugin = new();
        plugin.SetupGet(one => one.NavEntries).Returns(entries);

        Mock<IPluginManager> manager = new();
        manager
            .Setup(one => one.GetInstalledPlugins())
            .Returns([
                new PluginInfo
                {
                    Id = PluginId,
                    Name = "Radio",
                    Description = "Internet radio",
                    Version = new System.Version(1, 0, 0),
                    Status = PluginStatus.Active,
                },
            ]);
        manager.Setup(one => one.GetPluginInstance(PluginId)).Returns(plugin.Object);
        return manager;
    }

    private static async Task<List<LibraryNavigationEntryDto>> Navigation(
        Mock<IPluginManager> manager,
        string acceptLanguage,
        IPluginAccessResolver? resolver = null
    )
    {
        Mock<ILibraryRepository> libraries = new();
        libraries
            .Setup(one => one.GetLibraries(Viewer, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Library>());

        DefaultHttpContext context = new()
        {
            User = new(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, Viewer.ToString())],
                    "test"
                )
            ),
        };
        context.Request.Headers.AcceptLanguage = acceptLanguage;

        SetupController controller = new(
            Mock.Of<IAnimeThemeRepository>(),
            Mock.Of<IServerConfigurationRepository>(),
            Mock.Of<IMusicRepository>(),
            null!,
            libraries.Object,
            manager.Object,
            resolver ?? Mock.Of<IPluginAccessResolver>(),
            NullLogger<SetupController>.Instance
        )
        {
            ControllerContext = new() { HttpContext = context },
        };

        IActionResult result = await controller.Navigation();

        return result
            .Should()
            .BeOfType<OkObjectResult>()
            .Which.Value.Should()
            .BeOfType<DataResponseDto<List<LibraryNavigationEntryDto>>>()
            .Which.Data!.Where(entry => entry.Origin == LibraryNavigationOrigin.Plugin)
            .ToList();
    }

    [Fact]
    public async Task ALabelKeyComesBackInTheCallersLanguage()
    {
        Mock<IPluginManager> manager = Manager(Entry("stations.title", "/"));
        manager
            .Setup(one => one.ReadTranslationsAsync(PluginId, "nl", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["stations.title"] = "Zenders" });

        List<LibraryNavigationEntryDto> entries = await Navigation(manager, "nl-NL");

        entries.Should().ContainSingle().Which.Label.Should().Be("Zenders");
    }

    [Fact]
    public async Task ALabelWithNoStringComesBackUnchanged()
    {
        Mock<IPluginManager> manager = Manager(
            Entry("stations.title", "/"),
            Entry("Favourites", "/favourites")
        );
        manager
            .Setup(one => one.ReadTranslationsAsync(PluginId, "en", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { ["stations.title"] = "Stations" });

        List<LibraryNavigationEntryDto> entries = await Navigation(manager, "en");

        entries.Select(entry => entry.Label).Should().BeEquivalentTo("Stations", "Favourites");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnOwnerOnlyEntryIsListedOnlyForTheOwner(bool owner)
    {
        PluginNavEntry hidden = new()
        {
            Section = PluginKind.Music,
            Label = "hidden-entry",
            Route = "/admin",
            Access = PluginRouteAccess.Owner,
        };
        Mock<IPluginManager> manager = Manager(Entry("open-entry", "/"), hidden);
        Mock<IPluginAccessResolver> resolver = new();
        resolver.Setup(one => one.IsOwner(Viewer)).Returns(owner);

        List<LibraryNavigationEntryDto> entries = await Navigation(manager, "en", resolver.Object);

        entries.Select(entry => entry.Label).Should().Contain("open-entry");
        entries.Select(entry => entry.Label).Contains("hidden-entry").Should().Be(owner);
    }

    // A bundle the server cannot read costs the plugin its translations, never
    // the whole navigation. path_not_allowed is how the storage layer refuses
    // a path, and it is not an IOException.
    [Theory]
    [InlineData("io")]
    [InlineData("access")]
    [InlineData("path_not_allowed")]
    [InlineData("json")]
    public async Task AnUnreadableBundleLeavesTheKeysInPlace(string failure)
    {
        Exception exception = failure switch
        {
            "io" => new IOException("locked"),
            "access" => new UnauthorizedAccessException("denied"),
            "path_not_allowed" => new StoragePathNotAllowedException("lang", "outside the roots"),
            _ => new JsonException("broken bundle"),
        };
        Mock<IPluginManager> manager = Manager(Entry("stations.title", "/"));
        manager
            .Setup(one =>
                one.ReadTranslationsAsync(
                    PluginId,
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ThrowsAsync(exception);

        List<LibraryNavigationEntryDto> entries = await Navigation(manager, "nl");

        entries.Should().ContainSingle().Which.Label.Should().Be("stations.title");
    }
}
