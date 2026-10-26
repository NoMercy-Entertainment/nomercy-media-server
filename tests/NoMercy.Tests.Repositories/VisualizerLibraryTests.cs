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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using NoMercy.Data.Visualizers;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using Xunit;

namespace NoMercy.Tests.Repositories;

public class VisualizerLibraryTests : IDisposable
{
    private static readonly Guid UserA = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid UserB = Guid.Parse("00000000-0000-4000-8000-0000000000b2");

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public VisualizerLibraryTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand relax = _connection.CreateCommand();
        relax.CommandText = "PRAGMA foreign_keys = OFF;";
        relax.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static VisualizerBundle Bundle(params string[] slugs) =>
        new() { Entries = slugs.Select(MakeEntry).ToList() };

    private static VisualizerBundleEntry MakeEntry(string slug)
    {
        JObject payload = new()
        {
            ["schema"] = 1,
            ["format"] = slug.StartsWith("milkdrop/", StringComparison.Ordinal)
                ? "milkdrop"
                : "twigl-geek",
            ["note"] = slug,
        };
        string text = VisualizerCanonicalJson.Serialize(payload);
        return new()
        {
            Slug = slug,
            Name = "Made up " + slug,
            Author = "Test Author",
            License = "CC0",
            Format = (string)payload["format"]!,
            Tags = ["test", "made-up"],
            Schema = 1,
            Payload = payload,
            ContentHash = VisualizerCanonicalJson.Hash(text),
            PayloadBytes = text.Length,
        };
    }

    [Fact]
    public async Task Seed_UpsertsEveryEntryAndSecondRunChangesNothing()
    {
        VisualizerBundle bundle = Bundle("shader/one", "milkdrop/two");

        await using (MediaContext context = new(_options))
            await new VisualizerBundleSeeder(context).SeedAsync(bundle);
        await using (MediaContext context = new(_options))
            await new VisualizerBundleSeeder(context).SeedAsync(bundle);

        await using MediaContext check = new(_options);
        List<Visualizer> rows = await check.Visualizers.ToListAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(v => v.IsBuiltIn && v.Source == "builtin" && v.Enabled);
        rows.Select(v => v.Id)
            .Should()
            .BeEquivalentTo([
                VisualizerSlug.ToId("shader/one"),
                VisualizerSlug.ToId("milkdrop/two"),
            ]);
    }

    [Fact]
    public async Task Seed_KeepsAModeratorsEnabledValueAndUserEntries()
    {
        await using (MediaContext context = new(_options))
        {
            await new VisualizerBundleSeeder(context).SeedAsync(Bundle("shader/one"));
            Visualizer one = await context.Visualizers.SingleAsync();
            one.Enabled = false;
            context.Visualizers.Add(UserEntry("shader/mine-1"));
            await context.SaveChangesAsync();
        }

        await using (MediaContext context = new(_options))
            await new VisualizerBundleSeeder(context).SeedAsync(Bundle("shader/one"));

        await using MediaContext check = new(_options);
        (await check.Visualizers.SingleAsync(v => v.Slug == "shader/one"))
            .Enabled.Should()
            .BeFalse();
        (await check.Visualizers.SingleAsync(v => v.Slug == "shader/mine-1"))
            .IsBuiltIn.Should()
            .BeFalse();
    }

    [Fact]
    public async Task Seed_RetiresALinkedBuiltInAndDeletesAnUnlinkedOne()
    {
        VisualizerBundle first = Bundle("shader/linked", "shader/unlinked", "shader/kept");
        await using (MediaContext context = new(_options))
        {
            await new VisualizerBundleSeeder(context).SeedAsync(first);
            context.VisualizerUsers.Add(
                new()
                {
                    VisualizerId = VisualizerSlug.ToId("shader/linked"),
                    UserId = UserA,
                    IsFavorite = true,
                }
            );
            await context.SaveChangesAsync();
        }

        await using (MediaContext context = new(_options))
            await new VisualizerBundleSeeder(context).SeedAsync(Bundle("shader/kept"));

        await using MediaContext check = new(_options);
        Visualizer linked = await check.Visualizers.SingleAsync(v => v.Slug == "shader/linked");
        linked.IsBuiltIn.Should().BeFalse();
        linked.Source.Should().Be("retired-builtin");
        (await check.Visualizers.AnyAsync(v => v.Slug == "shader/unlinked")).Should().BeFalse();
        (await check.Visualizers.AnyAsync(v => v.Slug == "shader/kept")).Should().BeTrue();
    }

    [Fact]
    public async Task Favorites_AreKeptPerUser()
    {
        await SeedOne();
        Ulid id = VisualizerSlug.ToId("shader/one");

        await using MediaContext context = new(_options);
        VisualizerRepository repository = new(context);
        await repository.SetFavoriteAsync(UserA, id, true, false);

        (await repository.ListAsync(Query(UserA, favoritesOnly: true))).Items.Should().HaveCount(1);
        (await repository.ListAsync(Query(UserB, favoritesOnly: true))).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Favorites_RefuseThe501stAndRemovingOneFreesASlot()
    {
        await using MediaContext context = new(_options);
        await new VisualizerBundleSeeder(context).SeedAsync(
            Bundle(Enumerable.Range(0, 501).Select(i => $"shader/entry-{i}").ToArray())
        );
        VisualizerRepository repository = new(context);

        for (int i = 0; i < 500; i++)
            (
                await repository.SetFavoriteAsync(
                    UserA,
                    VisualizerSlug.ToId($"shader/entry-{i}"),
                    true,
                    false
                )
            )
                .Should()
                .Be(VisualizerWriteResult.Ok);

        Ulid extra = VisualizerSlug.ToId("shader/entry-500");
        (await repository.SetFavoriteAsync(UserA, extra, true, false))
            .Should()
            .Be(VisualizerWriteResult.LimitReached);

        await repository.SetFavoriteAsync(
            UserA,
            VisualizerSlug.ToId("shader/entry-0"),
            false,
            false
        );
        (await repository.SetFavoriteAsync(UserA, extra, true, false))
            .Should()
            .Be(VisualizerWriteResult.Ok);
    }

    [Fact]
    public async Task Choice_KeepsOnlyTenRecentsAndNullClearsTheChoice()
    {
        await using MediaContext context = new(_options);
        await new VisualizerBundleSeeder(context).SeedAsync(
            Bundle(Enumerable.Range(0, 12).Select(i => $"shader/entry-{i}").ToArray())
        );
        VisualizerRepository repository = new(context);

        (await repository.GetChoiceAsync(UserA)).VisualizerId.Should().BeNull();
        for (int i = 0; i < 12; i++)
        {
            await repository.SetChoiceAsync(UserA, VisualizerSlug.ToId($"shader/entry-{i}"));
            await Task.Delay(5);
        }

        (await repository.GetChoiceAsync(UserA))
            .VisualizerId.Should()
            .Be(VisualizerSlug.ToId("shader/entry-11"));
        VisualizerPage recents = await repository.ListAsync(Query(UserA, sort: "recent"));
        recents.Items.Should().HaveCount(10);
        recents.Items[0].Slug.Should().Be("shader/entry-11");

        await repository.SetChoiceAsync(UserA, null);
        (await repository.GetChoiceAsync(UserA)).VisualizerId.Should().BeNull();
    }

    [Fact]
    public async Task Choice_ReadsBackNullWhileTheEntryIsDisabledAndRefusesToChooseOne()
    {
        await SeedOne();
        Ulid id = VisualizerSlug.ToId("shader/one");

        await using MediaContext context = new(_options);
        VisualizerRepository repository = new(context);
        await repository.SetChoiceAsync(UserA, id);
        await repository.SetEnabledAsync(id, false);

        (await repository.GetChoiceAsync(UserA)).VisualizerId.Should().BeNull();
        (await repository.SetChoiceAsync(UserB, id)).Should().Be(VisualizerWriteResult.NotFound);
        (await repository.ListAsync(Query(UserA))).Items.Should().BeEmpty();
        (await repository.ListAsync(Query(UserA, includeDisabled: true)))
            .Items.Should()
            .HaveCount(1);
    }

    [Fact]
    public async Task Import_ServerWinsForTheChoiceAndSecondCallAddsNothing()
    {
        await using MediaContext context = new(_options);
        await new VisualizerBundleSeeder(context).SeedAsync(
            Bundle("shader/one", "milkdrop/two", "shader/three")
        );
        VisualizerRepository repository = new(context);
        await repository.SetChoiceAsync(UserA, VisualizerSlug.ToId("shader/three"));

        VisualizerImportResult first = await repository.ImportDeviceStateAsync(
            UserA,
            "one",
            ["milkdrop:two", "bars", "nope"],
            ["one"]
        );

        first.ChoiceApplied.Should().BeFalse();
        first.FavoritesAdded.Should().Be(1);
        first.RecentsAdded.Should().Be(1);
        first.Skipped.Select(s => s.Reason).Should().BeEquivalentTo(["bars", "unknown"]);
        (await repository.GetChoiceAsync(UserA))
            .VisualizerId.Should()
            .Be(VisualizerSlug.ToId("shader/three"));

        VisualizerImportResult second = await repository.ImportDeviceStateAsync(
            UserA,
            "one",
            ["milkdrop:two"],
            ["one"]
        );
        second.FavoritesAdded.Should().Be(0);
        second.RecentsAdded.Should().Be(0);
    }

    [Fact]
    public async Task Import_AppliesTheDeviceChoiceWhenTheServerHasNone()
    {
        await SeedOne();
        await using MediaContext context = new(_options);
        VisualizerRepository repository = new(context);

        VisualizerImportResult result = await repository.ImportDeviceStateAsync(
            UserB,
            "one",
            [],
            []
        );

        result.ChoiceApplied.Should().BeTrue();
        (await repository.GetChoiceAsync(UserB))
            .VisualizerId.Should()
            .Be(VisualizerSlug.ToId("shader/one"));
    }

    [Fact]
    public async Task Delete_RemovesTheEntryAndItsLinks()
    {
        await SeedOne();
        Ulid id = VisualizerSlug.ToId("shader/one");
        await using MediaContext context = new(_options);
        VisualizerRepository repository = new(context);
        await repository.SetFavoriteAsync(UserA, id, true, false);

        (await repository.DeleteAsync(id)).Should().BeTrue();

        (await context.VisualizerUsers.AnyAsync()).Should().BeFalse();
        (await repository.GetAsync(id, true)).Should().BeNull();
    }

    [Fact]
    public void PinnedBundle_MatchesItsRecordedHashAndEveryContentHash()
    {
        VisualizerBundle bundle = VisualizerBundle.LoadEmbedded();

        bundle.Entries.Should().HaveCount(550);
        bundle.BundleHash.Should().Be(VisualizerBundle.PinnedHash());
        bundle.ComputeHash().Should().Be(bundle.BundleHash);
        foreach (VisualizerBundleEntry entry in bundle.Entries)
        {
            string text = VisualizerCanonicalJson.Serialize(entry.Payload);
            VisualizerCanonicalJson.Hash(text).Should().Be(entry.ContentHash, entry.Slug);
        }
    }

    [Fact]
    public void PinnedBundle_ShipsEveryTextureWithItsHash()
    {
        VisualizerBundle bundle = VisualizerBundle.LoadEmbedded();

        bundle.Textures.Should().HaveCount(16);
        foreach (VisualizerBundleTexture texture in bundle.Textures)
        {
            VisualizerTexture? stored = VisualizerTextureStore.Find(
                Path.GetFileNameWithoutExtension(texture.Name)
            );
            stored.Should().NotBeNull(texture.Name);
            stored!.Sha256.Should().Be(texture.Sha256);
            stored.Bytes.Length.Should().Be(texture.Bytes);
        }
        VisualizerTextureStore.Find("not_a_texture").Should().BeNull();
        VisualizerTextureStore.Find("../cells").Should().BeNull();
    }

    // Expected values come from JSON.stringify of the sorted-key value (scratchpad viz-2a-hash-vector.json).
    [Theory]
    [InlineData(
        "{\"b\":1,\"a\":{\"d\":[3,2,1],\"c\":null},\"A\":true}",
        "{\"A\":true,\"a\":{\"c\":null,\"d\":[3,2,1]},\"b\":1}",
        "d2ac2dbe63e525b808f32f519848bb5ea3c64350a73112b37cc1bbaf5f73755b"
    )]
    [InlineData(
        "{\"n\":[1,1.5,0.1,1e21,1e-7,123456789012345680000,0.000001,-0,5e-324,1.7976931348623157e308,100,1.0,2.50,-3.25e2,0.3,1e300]}",
        "{\"n\":[1,1.5,0.1,1e+21,1e-7,123456789012345680000,0.000001,0,5e-324,1.7976931348623157e+308,100,1,2.5,-325,0.3,1e+300]}",
        "1975c0e2c81db471164e1a6ca9b9cf3728347bf3868915f50c29a3abca5934b7"
    )]
    [InlineData(
        "{\"s\":\"caf\\u00e9 \\ud83d\\ude00 \\u0001 \\t \\n \\\"q\\\" \\\\ / <>&\\u007f\"}",
        null,
        "1b641da91e1dc47364a8edb8e709f35841b739c806b3fc0550ec5c2b45e007b6"
    )]
    public void CanonicalJson_MatchesJavaScript(string input, string? canonical, string sha256)
    {
        JToken token = JToken.Parse(input);

        string text = VisualizerCanonicalJson.Serialize(token);

        if (canonical is not null)
            Assert.Equal(canonical, text);
        Assert.Equal(sha256, VisualizerCanonicalJson.Hash(text));
    }

    // Newtonsoft's parser replaces a lone surrogate, so the token is built by hand.
    [Fact]
    public void CanonicalJson_EscapesALoneSurrogateLikeJavaScript()
    {
        JObject token = new()
        {
            ["lone"] = "x\ud800y",
            ["emptyKey"] = new JObject { [""] = "" },
            ["z"] = new JArray(),
        };

        string text = VisualizerCanonicalJson.Serialize(token);

        Assert.Equal("{\"emptyKey\":{\"\":\"\"},\"lone\":\"x\\ud800y\",\"z\":[]}", text);
        Assert.Equal(
            "627d3eb0777025ebd651a13ce142bf49915397dcda3e638df7e544346176ef3c",
            VisualizerCanonicalJson.Hash(text)
        );
    }

    private async Task SeedOne()
    {
        await using MediaContext context = new(_options);
        await new VisualizerBundleSeeder(context).SeedAsync(Bundle("shader/one"));
    }

    private static Visualizer UserEntry(string slug) =>
        new()
        {
            Slug = slug,
            Name = "Mine",
            Author = "Me",
            License = "CC0",
            Format = "twigl-geek",
            ContentHash = new string('a', 64),
            PayloadJson = "{}",
        };

    private static VisualizerListQuery Query(
        Guid user,
        bool favoritesOnly = false,
        string sort = "name",
        bool includeDisabled = false
    ) => new(user, includeDisabled, [], null, null, null, favoritesOnly, sort, 0, 100);
}
