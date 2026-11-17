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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Newtonsoft.Json;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Profiles;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.Storage;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// A plugin reads how a library's folders are encoded, so it can search for
/// a release the encode will not scale away (#56). The presets are the set
/// <c>VideoEncodeJob</c> would run, each with the tallest video it can make.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginLibraryEncodingPresetsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Mock<IDbContextFactory<MediaContext>> _factory = new();

    public PluginLibraryEncodingPresetsTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        using SqliteCommand relax = _connection.CreateCommand();
        relax.CommandText = "PRAGMA foreign_keys = OFF;";
        relax.ExecuteNonQuery();

        _factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task The_builtin_default_streaming_preset_reaches_2160_as_the_default()
    {
        await using (MediaContext context = new(_options))
        {
            await new BuiltinPresetSeeder(context).SeedAsync();
        }

        Ulid builtin = Builtin(BuiltinPresets.DefaultStreamingPresetName).Id;
        Ulid library = await LibraryWithFoldersAsync(null, [(builtin, true)]);

        PluginEncodingPreset preset = (await PresetsOfAsync(library))
            .Should()
            .ContainSingle()
            .Subject;

        preset.Id.Should().Be(builtin.ToString());
        preset.Name.Should().Be(BuiltinPresets.DefaultStreamingPresetName);
        preset.IsDefault.Should().BeTrue();
        preset.MaxHeight.Should().Be(2160);
    }

    [Fact]
    public async Task A_manual_ladder_reaches_its_highest_rung()
    {
        Ulid manual = await PresetAsync(
            "Manual 720/1080",
            StreamingProfile() with
            {
                Ladder = new()
                {
                    Mode = LadderMode.Manual,
                    Rungs = [Rung(1280, 720), Rung(1920, 1080)],
                },
            }
        );
        Ulid library = await LibraryWithFoldersAsync(null, [(manual, false)]);

        PluginEncodingPreset preset = (await PresetsOfAsync(library))
            .Should()
            .ContainSingle()
            .Subject;

        preset.MaxHeight.Should().Be(1080);
        preset.IsDefault.Should().BeFalse();
    }

    /// <summary>
    /// An Auto ladder that already carries several rungs is run as Manual by
    /// the planner, so its tiers (here up to 2160) do not count.
    /// </summary>
    [Fact]
    public async Task An_auto_ladder_with_several_rungs_reaches_its_highest_rung()
    {
        EncodingProfile streaming = StreamingProfile();
        Ulid preset = await PresetAsync(
            "Auto with rungs",
            streaming with
            {
                Ladder = streaming.Ladder! with { Rungs = [Rung(1280, 720), Rung(1920, 1080)] },
            }
        );

        (await MaxHeightOfAsync(preset)).Should().Be(1080);
    }

    /// <summary>
    /// The legacy generator behind an Auto ladder with no config climbs to the
    /// source's own height, so there is no ceiling to report; the reference
    /// output's 1080 is not one.
    /// </summary>
    [Fact]
    public async Task An_auto_ladder_without_a_config_reaches_no_fixed_height()
    {
        Ulid preset = await PresetAsync(
            "Auto without config",
            SingleOutputProfile(1080) with
            {
                Ladder = new() { Mode = LadderMode.Auto, AutoConfig = null },
            }
        );

        (await MaxHeightOfAsync(preset)).Should().BeNull();
    }

    /// <summary>With no reference output and no rung, the expander makes no video at all.</summary>
    [Fact]
    public async Task An_auto_ladder_with_no_video_and_no_rung_reaches_no_height()
    {
        Ulid preset = await PresetAsync(
            "Auto without video",
            StreamingProfile() with
            {
                Video = null,
            }
        );

        (await MaxHeightOfAsync(preset)).Should().BeNull();
    }

    /// <summary>
    /// One rung is the reference the tiers are built from, not a ladder of its
    /// own, so the tiers' top (2160) is the ceiling rather than the rung's 720.
    /// </summary>
    [Fact]
    public async Task An_auto_ladder_with_one_rung_reaches_its_highest_tier()
    {
        EncodingProfile streaming = StreamingProfile();
        Ulid preset = await PresetAsync(
            "Auto with one rung",
            streaming with
            {
                Video = null,
                Ladder = streaming.Ladder! with { Rungs = [Rung(1280, 720)] },
            }
        );

        (await MaxHeightOfAsync(preset)).Should().Be(2160);
    }

    /// <summary>A Manual ladder with no rungs runs the single Video output.</summary>
    [Fact]
    public async Task A_manual_ladder_without_rungs_reaches_the_video_height()
    {
        Ulid preset = await PresetAsync(
            "Manual without rungs",
            SingleOutputProfile(1080) with
            {
                Ladder = new() { Mode = LadderMode.Manual, Rungs = [] },
            }
        );

        (await MaxHeightOfAsync(preset)).Should().Be(1080);
    }

    [Fact]
    public async Task A_single_output_reaches_its_own_height()
    {
        Ulid single = await PresetAsync("1080p file", SingleOutputProfile(1080));
        Ulid library = await LibraryWithFoldersAsync(null, [(single, true)]);

        (await PresetsOfAsync(library)).Should().ContainSingle().Which.MaxHeight.Should().Be(1080);
    }

    /// <summary>A preset that keeps the source's size has no ceiling to report.</summary>
    [Fact]
    public async Task A_single_output_at_the_sources_size_reaches_no_fixed_height()
    {
        Ulid source = await PresetAsync("Source size", SingleOutputProfile(null));
        Ulid library = await LibraryWithFoldersAsync(null, [(source, true)]);

        (await PresetsOfAsync(library)).Should().ContainSingle().Which.MaxHeight.Should().BeNull();
    }

    /// <summary>
    /// A thin override preset carries no ladder of its own, and resolving it
    /// through the parent is what tells its real ceiling.
    /// </summary>
    [Fact]
    public async Task An_inherited_ladder_counts()
    {
        Ulid parent = await PresetAsync(
            "Parent ladder",
            StreamingProfile() with
            {
                Ladder = new()
                {
                    Mode = LadderMode.Manual,
                    Rungs = [Rung(1280, 720), Rung(2560, 1440)],
                },
            }
        );
        Ulid child = await PresetAsync("Override", """{"Name":"Override"}""", parent);
        Ulid library = await LibraryWithFoldersAsync(null, [(child, true)]);

        PluginEncodingPreset preset = (await PresetsOfAsync(library))
            .Should()
            .ContainSingle()
            .Subject;

        preset.Name.Should().Be("Override");
        preset.MaxHeight.Should().Be(1440);
    }

    [Fact]
    public async Task A_preset_linked_by_two_folders_is_listed_once_and_default_if_either_says_so()
    {
        Ulid shared = await PresetAsync("Shared", SingleOutputProfile(720));
        Ulid library = await LibraryWithFoldersAsync(null, [(shared, false)], [(shared, true)]);

        PluginEncodingPreset preset = (await PresetsOfAsync(library))
            .Should()
            .ContainSingle()
            .Subject;

        preset.Id.Should().Be(shared.ToString());
        preset.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Every_linked_preset_is_listed_when_the_library_names_none()
    {
        Ulid first = await PresetAsync("First", SingleOutputProfile(720));
        Ulid second = await PresetAsync("Second", SingleOutputProfile(1080));
        Ulid library = await LibraryWithFoldersAsync(null, [(first, true), (second, false)]);

        (await PresetsOfAsync(library))
            .Select(preset => preset.Id)
            .Should()
            .BeEquivalentTo(first.ToString(), second.ToString());
    }

    /// <summary>Auto-encode runs only the library's own preset, so only it is the answer.</summary>
    [Fact]
    public async Task The_librarys_encode_preset_narrows_the_list_to_that_one()
    {
        Ulid first = await PresetAsync("First", SingleOutputProfile(720));
        Ulid second = await PresetAsync("Second", SingleOutputProfile(1080));
        Ulid library = await LibraryWithFoldersAsync(second, [(first, true), (second, false)]);

        PluginEncodingPreset preset = (await PresetsOfAsync(library))
            .Should()
            .ContainSingle()
            .Subject;

        preset.Id.Should().Be(second.ToString());
        preset.MaxHeight.Should().Be(1080);
    }

    /// <summary>
    /// The library's own preset is linked to none of its folders, so an encode
    /// runs nothing, and the answer says so rather than falling back to the
    /// presets that are linked.
    /// </summary>
    [Fact]
    public async Task A_library_preset_linked_to_none_of_its_folders_answers_an_empty_list()
    {
        Ulid linked = await PresetAsync("Linked", SingleOutputProfile(720));
        Ulid unlinked = await PresetAsync("Unlinked", SingleOutputProfile(1080));
        Ulid library = await LibraryWithFoldersAsync(unlinked, [(linked, true)]);

        (await PresetsOfAsync(library)).Should().BeEmpty();
    }

    /// <summary>
    /// Two reads of one unchanged library are equal, so a plugin that spots
    /// changes by comparing what it read last time sees none.
    /// </summary>
    [Fact]
    public async Task Two_reads_of_an_unchanged_library_are_equal()
    {
        Ulid first = await PresetAsync("First", SingleOutputProfile(720));
        Ulid second = await PresetAsync("Second", SingleOutputProfile(1080));
        Ulid library = await LibraryWithFoldersAsync(null, [(first, true), (second, false)]);

        PluginLibrary before = await LibraryAsync(library);
        PluginLibrary after = await LibraryAsync(library);

        after.Should().NotBeSameAs(before);
        after.EncodingPresets.Should().NotBeSameAs(before.EncodingPresets);
        after.Should().Be(before);
        after.GetHashCode().Should().Be(before.GetHashCode());
    }

    /// <summary>A broken preset is skipped by the encoder too, and must not fail the read.</summary>
    [Fact]
    public async Task A_preset_that_does_not_resolve_is_left_out()
    {
        Ulid broken = await PresetAsync("Broken", "{}", parentId: Ulid.NewUlid());
        Ulid good = await PresetAsync("Good", SingleOutputProfile(1080));
        Ulid library = await LibraryWithFoldersAsync(null, [(broken, true), (good, false)]);

        (await PresetsOfAsync(library)).Should().ContainSingle().Which.Name.Should().Be("Good");
    }

    [Fact]
    public async Task A_library_whose_folders_link_no_preset_answers_an_empty_list()
    {
        Ulid library = await LibraryWithFoldersAsync(
            null,
            [
                [],
            ]
        );

        (await PresetsOfAsync(library)).Should().BeEmpty();
    }

    /// <summary>
    /// The library a plugin reads and the library it may write to are the same
    /// row, so both answers carry the same presets.
    /// </summary>
    [Fact]
    public async Task The_writable_libraries_carry_the_same_presets_as_the_read()
    {
        Ulid single = await PresetAsync("1080p file", SingleOutputProfile(1080));
        Ulid library = await LibraryWithFoldersAsync(null, [(single, true)]);
        Ulid pluginId = Ulid.NewUlid();

        Mock<IPluginGrantStore> grants = new();
        grants
            .Setup(x => x.Holds(pluginId, PluginGrantKind.LibraryWrite, library.ToString()))
            .Returns(true);

        PluginLibraryWriter writer = Writer(pluginId, grants.Object);

        PluginLibrary writable = (await writer.GetWritableLibrariesAsync())
            .Should()
            .ContainSingle()
            .Subject;
        PluginLibrary read = (await Query().GetLibrariesAsync()).Single(row =>
            row.Id == library.ToString()
        );

        writable.EncodingPresets.Should().BeEquivalentTo(read.EncodingPresets);
        writable.EncodingPresets.Should().ContainSingle().Which.MaxHeight.Should().Be(1080);
    }

    /// <summary>
    /// The cheap check an import makes agrees with the list: a granted library
    /// that exists, and nothing else.
    /// </summary>
    [Fact]
    public async Task The_single_library_write_check_agrees_with_the_writable_list()
    {
        Ulid granted = await LibraryWithFoldersAsync(
            null,
            [
                [],
            ]
        );
        Ulid ungranted = await LibraryWithFoldersAsync(
            null,
            [
                [],
            ]
        );
        Ulid deleted = Ulid.NewUlid();
        Ulid pluginId = Ulid.NewUlid();

        Mock<IPluginGrantStore> grants = new();
        grants
            .Setup(x => x.Holds(pluginId, PluginGrantKind.LibraryWrite, It.IsAny<string>()))
            .Returns(
                (Ulid _, string _, string value) =>
                    value == granted.ToString() || value == deleted.ToString()
            );

        PluginLibraryWriter writer = Writer(pluginId, grants.Object);

        (await writer.MayWriteToLibraryAsync(granted.ToString())).Should().BeTrue();
        (await writer.MayWriteToLibraryAsync(ungranted.ToString())).Should().BeFalse();
        (await writer.MayWriteToLibraryAsync(deleted.ToString())).Should().BeFalse();
        (await writer.MayWriteToLibraryAsync(granted.ToString().ToLowerInvariant()))
            .Should()
            .BeFalse("the list answers the id in its own spelling only");
        (await writer.MayWriteToLibraryAsync("not-a-library")).Should().BeFalse();

        (await writer.GetWritableLibrariesAsync())
            .Select(library => library.Id)
            .Should()
            .Equal(granted.ToString());
    }

    private PluginLibraryWriter Writer(Ulid pluginId, IPluginGrantStore grants) =>
        new(
            pluginId,
            _factory.Object,
            new PluginLibraryProjection(_factory.Object),
            Mock.Of<IStorageFactory>(),
            grants,
            NullLogger.Instance
        );

    private async Task<int?> MaxHeightOfAsync(Ulid preset)
    {
        Ulid library = await LibraryWithFoldersAsync(null, [(preset, true)]);

        return (await PresetsOfAsync(library)).Should().ContainSingle().Subject.MaxHeight;
    }

    private async Task<PluginLibrary> LibraryAsync(Ulid library) =>
        (await Query().GetLibrariesAsync()).Single(row => row.Id == library.ToString());

    private async Task<IReadOnlyList<PluginEncodingPreset>> PresetsOfAsync(Ulid library)
    {
        IReadOnlyList<PluginLibrary> libraries = await Query().GetLibrariesAsync();

        return libraries.Single(row => row.Id == library.ToString()).EncodingPresets;
    }

    private PluginLibraryQuery Query() =>
        new(_factory.Object, new PluginLibraryProjection(_factory.Object));

    /// <summary>One library, one folder per link set, each folder linking its presets.</summary>
    private async Task<Ulid> LibraryWithFoldersAsync(
        Ulid? encodePresetId,
        params (Ulid PresetId, bool IsDefault)[][] folders
    )
    {
        await using MediaContext context = new(_options);

        Ulid libraryId = Ulid.NewUlid();
        context.Libraries.Add(
            new()
            {
                Id = libraryId,
                Title = "Shows",
                Type = "tv",
                EncodePresetId = encodePresetId,
            }
        );

        foreach ((Ulid PresetId, bool IsDefault)[] links in folders)
        {
            Ulid folderId = Ulid.NewUlid();
            context.Folders.Add(
                new()
                {
                    Id = folderId,
                    DriverId = Ulid.NewUlid(),
                    Path = $"/media/{folderId}",
                    EncodingPresetFolders =
                    [
                        .. links.Select(link => new EncodingPresetFolder
                        {
                            PresetId = link.PresetId,
                            FolderId = folderId,
                            IsDefault = link.IsDefault,
                        }),
                    ],
                }
            );
            context.FolderLibrary.Add(new(folderId, libraryId));
        }

        await context.SaveChangesAsync();
        return libraryId;
    }

    private Task<Ulid> PresetAsync(string name, EncodingProfile profile) =>
        PresetAsync(name, JsonConvert.SerializeObject(profile with { Name = name }), null);

    private async Task<Ulid> PresetAsync(string name, string profileJson, Ulid? parentId)
    {
        await using MediaContext context = new(_options);

        Ulid id = Ulid.NewUlid();
        context.EncodingPresets.Add(
            new()
            {
                Id = id,
                Name = name,
                ProfileJson = profileJson,
                ParentPresetId = parentId,
                Source = "test",
            }
        );

        await context.SaveChangesAsync();
        return id;
    }

    private static EncodingProfile Builtin(string name) =>
        BuiltinPresets.All().Single(profile => profile.Name == name);

    private static EncodingProfile StreamingProfile() =>
        Builtin(BuiltinPresets.DefaultStreamingPresetName);

    private static EncodingProfile SingleOutputProfile(int? height)
    {
        EncodingProfile streaming = StreamingProfile();

        return streaming with
        {
            Ladder = null,
            Video = streaming.Video! with { Width = null, Height = height },
        };
    }

    private static LadderRung Rung(int width, int height) =>
        new(
            Width: width,
            Height: height,
            Codec: VideoCodecType.H264,
            BitrateKbps: height * 4,
            MaxBitrateKbps: height * 6,
            BufferSizeKbps: height * 8,
            Framerate: 0
        );
}
