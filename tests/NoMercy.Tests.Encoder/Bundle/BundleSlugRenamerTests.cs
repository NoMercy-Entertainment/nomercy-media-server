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

using System.Text;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Database;
using NoMercy.Encoder.Bundle;
using NoMercy.Storage;

namespace NoMercy.Tests.Encoder.Bundle;

// ---------------------------------------------------------------------------
// Fake IStorageFactory that always returns the same storage instance
// ---------------------------------------------------------------------------

internal sealed class FixedStorageFactory(IStorage storage) : IStorageFactory
{
    public IStorage For(Ulid folderId, Ulid driverId, string subPath) => storage;

    public void Invalidate(Ulid folderId) { }

    public void InvalidateAll() { }
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

internal static class RenameTestHelpers
{
    private static readonly Ulid FolderId = Ulid.NewUlid();
    private static readonly Ulid DriverId = Ulid.NewUlid();

    public static MediaContext BuildInMemoryContext(string folderPath = "/fake/library")
    {
        DbContextOptions<MediaContext> opts = new DbContextOptionsBuilder<MediaContext>()
            .UseInMemoryDatabase(databaseName: Ulid.NewUlid().ToString())
            .Options;

        MediaContext ctx = new(opts);

        ctx.Drivers.Add(
            new()
            {
                Id = DriverId,
                Name = "local",
                Type = "local",
                Config = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            }
        );

        ctx.Folders.Add(
            new()
            {
                Id = FolderId,
                Path = folderPath,
                DriverId = DriverId,
            }
        );

        ctx.SaveChanges();
        return ctx;
    }

    /// <summary>Builds a minimal-but-realistic <c>.nomercy.json</c> blueprint
    /// with one <c>encodes[]</c> entry per given preset slug.</summary>
    public static string BuildBlueprintJson(params string[] presetSlugs) =>
        JsonConvert.SerializeObject(
            new JObject
            {
                ["version"] = 1,
                ["identity"] = new JObject { ["type"] = "movie", ["tmdb_id"] = 550 },
                ["source"] = new JObject { ["path"] = "Fight Club.mkv" },
                ["encodes"] = new JArray(
                    presetSlugs.Select(slug => new JObject
                    {
                        ["preset_slug"] = slug,
                        ["preset_id"] = "01J3X8R7K2QM9Y0G1Q4ABCDEFG",
                        ["profile_fingerprint"] = "abc123",
                    })
                ),
            },
            Formatting.Indented
        );

    public static BundleSlugRenamer BuildRenamer(
        Dictionary<string, string> slugMap,
        IStorage storage,
        MediaContext context
    )
    {
        FixedStorageFactory factory = new(storage);
        return new(
            slugMap,
            factory,
            context,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BundleSlugRenamer>.Instance
        );
    }

    public static JObject ReadBlueprint(TestStorage storage, string path)
    {
        string? json = storage.ReadString(path);
        json.Should().NotBeNull();
        return JsonConvert.DeserializeObject<JObject>(json!)!;
    }

    public static IReadOnlyList<string> EncodeSlugs(JObject blueprint) =>
        [.. ((JArray)blueprint["encodes"]!).Select(e => e["preset_slug"]!.Value<string>()!)];
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

public class BundleSlugRenamerTests
{
    [Fact]
    public async Task HappyPath_RewritesPresetSlugInBlueprint()
    {
        TestStorage storage = new();
        MediaContext context = RenameTestHelpers.BuildInMemoryContext();

        const string path = "Show/Show S01E01/.nomercy.json";
        storage.Seed(
            path,
            Encoding.UTF8.GetBytes(RenameTestHelpers.BuildBlueprintJson("old-slug"))
        );

        BundleSlugRenamer renamer = RenameTestHelpers.BuildRenamer(
            new() { ["old-slug"] = "new-slug" },
            storage,
            context
        );

        await renamer.RunAsync();

        // The file stays at the same path — there is no directory to move.
        storage.AllPaths().Should().Contain(path);

        JObject blueprint = RenameTestHelpers.ReadBlueprint(storage, path);
        RenameTestHelpers
            .EncodeSlugs(blueprint)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("new-slug");
    }

    [Fact]
    public async Task Idempotent_SecondRunIsNoOp()
    {
        TestStorage storage = new();
        MediaContext context = RenameTestHelpers.BuildInMemoryContext();

        const string path = "Show/Show S01E01/.nomercy.json";
        storage.Seed(
            path,
            Encoding.UTF8.GetBytes(RenameTestHelpers.BuildBlueprintJson("old-slug"))
        );

        BundleSlugRenamer renamer = RenameTestHelpers.BuildRenamer(
            new() { ["old-slug"] = "new-slug" },
            storage,
            context
        );

        // First run rewrites old-slug → new-slug.
        await renamer.RunAsync();
        string? afterFirst = storage.ReadString(path);

        // Second run: the entry now reads "new-slug", which no longer
        // matches the map's old-slug key, so nothing changes.
        await renamer.RunAsync();
        string? afterSecond = storage.ReadString(path);

        afterSecond.Should().Be(afterFirst);
        RenameTestHelpers
            .EncodeSlugs(RenameTestHelpers.ReadBlueprint(storage, path))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("new-slug");
    }

    [Fact]
    public async Task UnrelatedSlug_IsNotRewritten()
    {
        TestStorage storage = new();
        MediaContext context = RenameTestHelpers.BuildInMemoryContext();

        const string path = "Show/Show S01E01/.nomercy.json";
        storage.Seed(
            path,
            Encoding.UTF8.GetBytes(RenameTestHelpers.BuildBlueprintJson("totally-unrelated"))
        );

        BundleSlugRenamer renamer = RenameTestHelpers.BuildRenamer(
            new() { ["old-slug"] = "new-slug" },
            storage,
            context
        );

        await renamer.RunAsync();

        RenameTestHelpers
            .EncodeSlugs(RenameTestHelpers.ReadBlueprint(storage, path))
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("totally-unrelated");
    }

    [Fact]
    public async Task EmptySlugMap_IsNoOp()
    {
        TestStorage storage = new();
        MediaContext context = RenameTestHelpers.BuildInMemoryContext();

        const string path = "Show/Show S01E01/.nomercy.json";
        string original = RenameTestHelpers.BuildBlueprintJson("some-slug");
        storage.Seed(path, Encoding.UTF8.GetBytes(original));

        BundleSlugRenamer renamer = RenameTestHelpers.BuildRenamer(new(), storage, context);

        await renamer.RunAsync();

        storage.ReadString(path).Should().Be(original);
    }
}
