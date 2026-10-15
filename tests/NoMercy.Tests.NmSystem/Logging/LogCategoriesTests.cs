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

using System.Text.RegularExpressions;
using NoMercy.NmSystem.Logging;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// Pins <see cref="LogCategories"/>: keys resolve to their display name, every
/// provider gets a distinct legible colour, source contexts map to the right
/// category, and unknown input falls back to the default.
/// </summary>
[Trait("Category", "Unit")]
public class LogCategoriesTests
{
    [Theory]
    [InlineData("moviedb", "TheMovieDB")]
    [InlineData("tvdb", "TheTVDB")]
    [InlineData("musicbrainz", "MusicBrainz")]
    [InlineData("MOVIEDB", "TheMovieDB")]
    public void Resolve_ReturnsDisplayName(string key, string expected)
    {
        LogCategories.Resolve(key).DisplayName.Should().Be(expected);
    }

    [Fact]
    public void Resolve_Unknown_FallsBackToDefault()
    {
        LogCategories.Resolve("does-not-exist").Should().Be(LogCategories.Default);
        LogCategories.Resolve(null).Should().Be(LogCategories.Default);
    }

    [Theory]
    [InlineData("NoMercy.Providers.TMDB.Client.TmdbBaseClient", "moviedb")]
    [InlineData("NoMercy.Providers.TVDB.Client.TvdbBaseClient", "tvdb")]
    [InlineData("NoMercy.Encoder.Jobs.VideoEncodeJob", "encoder")]
    [InlineData("NoMercyQueue.QueueWorker", "queue")]
    [InlineData("NoMercy.Random.Unknown.Thing", "app")]
    public void ResolveSource_MapsNamespaceToCategory(string source, string expectedKey)
    {
        LogCategories.ResolveSource(source).Key.Should().Be(expectedKey);
    }

    [Fact]
    public void Providers_HaveDistinctDarkColours()
    {
        string[] providerKeys =
        [
            "youtube",
            "acoustid",
            "anidb",
            "audiodb",
            "coverart",
            "fanart",
            "fingerprint",
            "lrclib",
            "moviedb",
            "musicbrainz",
            "musixmatch",
            "opensubs",
            "tvdb",
        ];

        IEnumerable<string> colours = providerKeys.Select(k => LogCategories.Resolve(k).DarkHex);
        colours.Distinct().Should().HaveCount(providerKeys.Length);
    }

    [Theory]
    [InlineData("moviedb")]
    [InlineData("info")]
    [InlineData("queue")]
    public void Colours_AreValidHex(string key)
    {
        LogCategory category = LogCategories.Resolve(key);
        Regex hex = new("^#[0-9a-f]{6}$");
        hex.IsMatch(category.DarkHex).Should().BeTrue();
        hex.IsMatch(category.LightHex).Should().BeTrue();
    }

    [Fact]
    public void RegisterPlugin_GivesThePluginItsOwnNameInThePluginGroup()
    {
        Ulid plugin = Ulid.NewUlid();

        LogCategories.RegisterPlugin(plugin, "Torrent Downloader");

        LogCategory category = LogCategories.Resolve(LogCategories.PluginKey(plugin));
        category.Key.Should().Be($"plugin:{plugin}");
        category.DisplayName.Should().Be("Torrent Downloader");
        category.Group.Should().Be(LogCategories.PluginGroup);
    }

    /// <summary>One colour for the group: the name says which plugin it is.</summary>
    [Fact]
    public void EveryPlugin_SharesThePluginColour()
    {
        Ulid first = Ulid.NewUlid();
        Ulid second = Ulid.NewUlid();

        LogCategory one = LogCategories.RegisterPlugin(first, "One");
        LogCategory two = LogCategories.RegisterPlugin(second, "Two");

        one.DarkHex.Should().Be(LogCategories.PluginDarkHex).And.Be(two.DarkHex);
        one.LightHex.Should().Be(LogCategories.PluginLightHex).And.Be(two.LightHex);
    }

    [Theory]
    [InlineData("moviedb")]
    [InlineData("app")]
    [InlineData("http")]
    [InlineData("queue")]
    [InlineData("warning")]
    public void ThePluginColour_IsNotABuiltInCategoryColour(string key)
    {
        LogCategory builtIn = LogCategories.Resolve(key);

        builtIn.DarkHex.Should().NotBe(LogCategories.PluginDarkHex);
        builtIn.LightHex.Should().NotBe(LogCategories.PluginLightHex);
    }

    [Fact]
    public void RegisteringAgain_ReplacesTheName()
    {
        Ulid plugin = Ulid.NewUlid();

        LogCategories.RegisterPlugin(plugin, "Old Name");
        LogCategories.RegisterPlugin(plugin, "New Name");

        LogCategories.Resolve(LogCategories.PluginKey(plugin)).DisplayName.Should().Be("New Name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void APluginWithNoName_IsCalledPlugin(string? name)
    {
        LogCategories.RegisterPlugin(Ulid.NewUlid(), name).DisplayName.Should().Be("Plugin");
    }

    [Fact]
    public void ResolveSource_OfAPluginLogger_IsThatPlugin()
    {
        Ulid plugin = Ulid.NewUlid();
        LogCategories.RegisterPlugin(plugin, "Echo");

        LogCategories
            .ResolveSource(LogCategories.PluginKey(plugin))
            .DisplayName.Should()
            .Be("Echo");
    }

    /// <summary>
    /// A plugin id is random letters and digits. One that happens to spell a
    /// subsystem name must not be logged as that subsystem.
    /// </summary>
    [Fact]
    public void ResolveSource_OfAPluginIdThatSpellsASubsystem_IsStillThePlugin()
    {
        Ulid plugin = Ulid.Parse("01HTTPTVDB0000000000000000");
        LogCategories.RegisterPlugin(plugin, "Lookalike");

        LogCategories
            .ResolveSource(LogCategories.PluginKey(plugin))
            .DisplayName.Should()
            .Be("Lookalike");
    }

    /// <summary>
    /// A log written before a restart, or read by another process, names a
    /// plugin this process never registered. It is still a plugin line, not
    /// an App line.
    /// </summary>
    [Fact]
    public void AnUnregisteredPluginKey_ResolvesToThePluginGroup()
    {
        string key = LogCategories.PluginKey(Ulid.NewUlid());

        LogCategory category = LogCategories.Resolve(key);

        category.Should().NotBe(LogCategories.Default);
        category.Group.Should().Be(LogCategories.PluginGroup);
        category.DisplayName.Should().Be("Plugin");
        LogCategories.ResolveSource(key).Should().Be(category);
    }
}
