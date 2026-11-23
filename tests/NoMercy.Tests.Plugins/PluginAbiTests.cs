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
using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginAbiTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("not-a-version", false)]
    public void IsCompatible_HandlesTheNonVersionInputsRegardlessOfWhereCurrentIs(
        string? targetAbi,
        bool expected
    )
    {
        Assert.Equal(expected, PluginAbi.IsCompatible(targetAbi));
    }

    /// <summary>
    /// Every case below is derived from <see cref="PluginAbi.Current"/> and
    /// <see cref="PluginAbi.Oldest"/> rather than a literal version, so a
    /// routine minor/major bump never requires touching this test — only an
    /// actual change to the compatibility rule itself would fail it.
    /// </summary>
    [Fact]
    public void IsCompatible_AcceptsNothingOlderThanTheOldestLoadableMajor()
    {
        Version current = PluginAbi.Current;
        Version oldest = PluginAbi.Oldest;

        // The oldest loadable major, and everything up to and including current, load.
        Assert.True(PluginAbi.IsCompatible($"{oldest.Major}.0"));
        Assert.True(PluginAbi.IsCompatible($"{current.Major}.{current.Minor}"));

        // A minor newer than current does not load yet.
        Assert.False(PluginAbi.IsCompatible($"{current.Major}.{current.Minor + 1}"));

        // A major newer than current, and anything older than the oldest
        // loadable major, do not load.
        Assert.False(PluginAbi.IsCompatible($"{current.Major + 1}.0"));
        if (oldest.Major > 0)
            Assert.False(PluginAbi.IsCompatible($"{oldest.Major - 1}.0"));
    }

    /// <summary>
    /// Eleven is refused rather than given the usual one-major grace. That
    /// grace exists because a major normally only removes members, so a plugin
    /// that never called them keeps working. Twelve renamed the SDK assembly
    /// and every namespace in it, so an eleven plugin resolves no type at all;
    /// accepting it would install something that fails at load with a
    /// missing-type error naming nothing its author recognises.
    /// </summary>
    [Fact]
    public void ElevenIsRefusedBecauseItsTypesNoLongerExist()
    {
        PluginAbi.IsCompatible("11.0").Should().BeFalse();
        PluginAbi.Oldest.Should().Be(new Version(12, 0));
    }

    /// <summary>
    /// Thirteen removed members and renamed nothing, so it gives the usual
    /// grace: a plugin built against any twelve loads, and only one that
    /// called a removed member has to be rebuilt.
    /// </summary>
    [Theory]
    [InlineData("12.0")]
    [InlineData("12.10")]
    [InlineData("12.15")]
    [InlineData("13.0")]
    public void APluginBuiltAgainstTwelveStillLoadsOnThirteen(string targetAbi)
    {
        PluginAbi.Current.Major.Should().Be(13);
        PluginAbi.IsCompatible(targetAbi).Should().BeTrue();
    }

    [Fact]
    public void Current_IsNeverOlderThanOldest()
    {
        Assert.True(
            PluginAbi.Current.Major > PluginAbi.Oldest.Major
                || (
                    PluginAbi.Current.Major == PluginAbi.Oldest.Major
                    && PluginAbi.Current.Minor >= PluginAbi.Oldest.Minor
                )
        );
    }

    /// <summary>
    /// A plugin pins the major, so a member that existed in 11.0 is still
    /// called by binaries nobody is going to rebuild. This one went away and
    /// took both installed plugins off every screen in the ecosystem, and the
    /// only word anywhere was a MissingMethodException in the server log.
    /// </summary>
    [Theory]
    [InlineData(typeof(PluginScheduledJob), new[] { typeof(string), typeof(string), typeof(bool) })]
    [InlineData(typeof(PluginLibrary), new[] { typeof(string), typeof(string), typeof(string) })]
    [InlineData(
        typeof(PluginLibraryShow),
        new[]
        {
            typeof(int),
            typeof(string),
            typeof(int?),
            typeof(string),
            typeof(string),
            typeof(int),
            typeof(int),
        }
    )]
    [InlineData(
        typeof(PluginTrack),
        new[]
        {
            typeof(Guid),
            typeof(string),
            typeof(string),
            typeof(string),
            typeof(int?),
            typeof(int?),
            typeof(double?),
            typeof(string),
        }
    )]
    public void ConstructorsElevenZeroPluginsCall_AreStillThere(Type type, Type[] parameters)
    {
        ConstructorInfo? found = type.GetConstructor(parameters);

        Assert.True(
            found is not null,
            $"{type.Name} no longer has .ctor({string.Join(", ", parameters.Select(one => one.Name))}). "
                + "Every plugin built against 11.0 fails to load with MissingMethodException."
        );
    }

    /// <summary>
    /// A property that keeps its name and changes its type is a break with no
    /// compile error anywhere: the plugin's IL still asks for
    /// <c>get_UserId</c> returning String, and the load fails at the first call.
    /// </summary>
    [Theory]
    [InlineData(typeof(PluginViewRequest), "UserId", typeof(string))]
    [InlineData(typeof(PluginViewRequest), "Route", typeof(string))]
    [InlineData(typeof(PluginViewRequest), "Query", typeof(Dictionary<string, string>))]
    [InlineData(typeof(PluginLibrary), "Id", typeof(string))]
    [InlineData(typeof(PluginLibrary), "Title", typeof(string))]
    [InlineData(typeof(PluginLibrary), "Type", typeof(string))]
    public void PropertiesElevenZeroPluginsRead_KeepTheirType(
        Type type,
        string property,
        Type expected
    )
    {
        PropertyInfo? found = type.GetProperty(property);

        Assert.True(found is not null, $"{type.Name}.{property} is gone.");
        Assert.True(
            found!.PropertyType == expected,
            $"{type.Name}.{property} returns {found.PropertyType.Name}, and every plugin built "
                + $"against 11.0 reads it as {expected.Name}. The call fails with MissingMethodException."
        );
    }

    /// <summary>
    /// 12.11 added <see cref="PluginLibrary.EncodingPresets"/> as an init-only
    /// member, so a plugin that builds a library the way it always did (a test
    /// double, a fake host) still compiles, still reads the three values it
    /// knows, and sees no presets rather than a null.
    /// </summary>
    [Fact]
    public void Library_BuiltTheWayAPluginBefore12_11BuiltIt_CarriesItsValuesAndNoPresets()
    {
        PluginLibrary library = new("films", "Films", "movie");

        library.Id.Should().Be("films");
        library.Title.Should().Be("Films");
        library.Type.Should().Be("movie");
        library.EncodingPresets.Should().NotBeNull().And.BeEmpty();
    }

    /// <summary>
    /// A plugin built against 12.10 spots a changed library with <c>==</c>, a
    /// set or a dictionary key. The presets list must compare by its items, or
    /// every read of an unchanged library looks like a change.
    /// </summary>
    [Fact]
    public void Libraries_with_equal_presets_are_equal_and_a_changed_preset_is_not()
    {
        PluginLibrary first = Shows(new PluginEncodingPreset("p1", "Streaming", true, 2160));
        PluginLibrary second = Shows(new PluginEncodingPreset("p1", "Streaming", true, 2160));
        PluginLibrary changed = Shows(new PluginEncodingPreset("p1", "Streaming", true, 1080));
        PluginLibrary fewer = Shows();

        first.Should().Be(second);
        (first == second).Should().BeTrue();
        first.GetHashCode().Should().Be(second.GetHashCode());
        new HashSet<PluginLibrary> { first }
            .Contains(second)
            .Should()
            .BeTrue();

        first.Should().NotBe(changed);
        first.Should().NotBe(fewer);
        first.Should().NotBe(first with { Title = "Other" });
    }

    /// <summary>A server or a plugin that sets the list to null still reads as no presets.</summary>
    [Fact]
    public void A_null_preset_list_reads_as_empty()
    {
        PluginLibrary library = new("shows", "Shows", "tv") { EncodingPresets = null! };

        library.EncodingPresets.Should().NotBeNull().And.BeEmpty();
        library.Should().Be(new PluginLibrary("shows", "Shows", "tv"));
    }

    /// <summary>
    /// 12.15 added genres, year, album id, albums and artists to
    /// <see cref="PluginTrack"/> as init-only members, so a plugin that builds
    /// a track the way it always did still compiles, still reads the eight
    /// values it knows, and sees empty lists rather than nulls.
    /// </summary>
    [Fact]
    public void Track_BuiltTheWayAPluginBefore12_15BuiltIt_CarriesItsValuesAndNoDetails()
    {
        Guid id = Guid.NewGuid();
        PluginTrack track = new(id, "Song", "Album", "Artist", 3, 1, 225.0, "library");

        track.Id.Should().Be(id);
        track.Album.Should().Be("Album");
        track.Artist.Should().Be("Artist");
        track.DurationSeconds.Should().Be(225.0);
        track.Genres.Should().NotBeNull().And.BeEmpty();
        track.Albums.Should().NotBeNull().And.BeEmpty();
        track.Artists.Should().NotBeNull().And.BeEmpty();
        track.Year.Should().BeNull();
        track.AlbumId.Should().BeNull();
    }

    /// <summary>
    /// A plugin spots a changed track with <c>==</c>, a set or a dictionary
    /// key. The lists must compare by their items, or every read of an
    /// unchanged track looks like a change.
    /// </summary>
    [Fact]
    public void Tracks_with_equal_details_are_equal_and_a_changed_detail_is_not()
    {
        Guid id = Guid.NewGuid();
        Guid album = Guid.NewGuid();
        Guid artist = Guid.NewGuid();
        PluginTrack Track(params string[] genres) =>
            new(id, "Song", "Album", "Artist", 3, 1, 225.0, "library")
            {
                Genres = [.. genres],
                Year = 1999,
                AlbumId = album,
                Albums = [new(album, "Album")],
                Artists = [new(artist, "Artist")],
            };

        PluginTrack first = Track("Pop", "Rock");
        PluginTrack second = Track("Pop", "Rock");

        first.Should().Be(second);
        (first == second).Should().BeTrue();
        first.GetHashCode().Should().Be(second.GetHashCode());
        new HashSet<PluginTrack> { first }
            .Contains(second)
            .Should()
            .BeTrue();

        first.Should().NotBe(Track("Pop"));
        first.Should().NotBe(first with { Year = 2000 });
        first.Should().NotBe(first with { AlbumId = Guid.NewGuid() });
        first.Should().NotBe(first with { Albums = [] });
        first.Should().NotBe(first with { Artists = [new(artist, "Another name")] });
        first.Should().NotBe(first with { Title = "Other" });
    }

    /// <summary>A server or a plugin that sets a list to null still reads as an empty one.</summary>
    [Fact]
    public void A_null_detail_list_on_a_track_reads_as_empty()
    {
        PluginTrack track = new(Guid.NewGuid(), "Song", null, null, null, null, null, "library")
        {
            Genres = null!,
            Albums = null!,
            Artists = null!,
        };

        track.Genres.Should().NotBeNull().And.BeEmpty();
        track.Albums.Should().NotBeNull().And.BeEmpty();
        track.Artists.Should().NotBeNull().And.BeEmpty();
        track.Should().Be(track with { Genres = [] });
    }

    private static PluginLibrary Shows(params PluginEncodingPreset[] presets) =>
        new("shows", "Shows", "tv") { EncodingPresets = [.. presets] };

    /// <summary>
    /// 12.11 added the paging contract and the shelf builders. Every action
    /// word and builder a 12.10 plugin was compiled against still means what
    /// it meant, and the new word is one more, not a renamed one.
    /// </summary>
    [Fact]
    public void The_12_11_paging_additions_leave_every_older_action_word_alone()
    {
        PluginActionType.PlayMedia.Should().Be("playMedia");
        PluginActionType.Navigate.Should().Be("navigate");
        PluginActionType.RefreshView.Should().Be("refreshView");
        PluginActionType.SubmitForm.Should().Be("submitForm");
        PluginActionType.PickFolder.Should().Be("pickFolder");
        PluginActionType.LoadPage.Should().Be("loadPage");
        PluginActionType.All.Should().HaveCount(10);

        typeof(PluginViews)
            .GetMethod(nameof(PluginViews.Grid), [typeof(string), typeof(PluginComponent[])])
            .Should()
            .NotBeNull("PluginViews.Grid keeps its 12.0 shape; the app grid is PluginShelves.Grid");
    }

    [Fact]
    public void ScheduledJob_BuiltTheWayElevenZeroBuiltIt_CarriesItsValues()
    {
        PluginScheduledJob job = new("refresh", "0 */6 * * *", true);

        Assert.Equal("refresh", job.Name);
        Assert.Equal("0 */6 * * *", job.CronExpression);
        Assert.True(job.AllowConcurrent);
    }
}
