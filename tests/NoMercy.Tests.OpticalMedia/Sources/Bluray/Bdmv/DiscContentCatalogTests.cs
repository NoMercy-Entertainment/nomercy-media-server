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

using NoMercy.DiscFormat.Disc.Bdmv;

namespace NoMercy.DiscFormat.Tests;

/// The content catalog must recover a disc's playable titles (feature + bonus videos) from the parsed
/// playlists alone, with the disc's non-content machinery excluded: scene/chapter index playlists
/// (hundreds of playitems, no audio) and transition/loader stubs (a few silent seconds). This mirrors
/// the real Frostbite layout, where the longest playlist is a 3.7 h scene index, not the feature.
public sealed class DiscContentCatalogTests
{
    [Fact]
    public void Picks_the_feature_and_bonus_videos_and_drops_index_and_stub_playlists()
    {
        Dictionary<int, MplsPlaylist> playlists = new()
        {
            // The feature: long, one clip, real audio + many chapter marks.
            [800] = Title(durationSeconds: 7452, playItems: 1, marks: 18, audio: 7, subs: 16),
            // Two bonus videos: minutes long, real audio.
            [250] = Title(durationSeconds: 243, playItems: 1, marks: 2, audio: 1, subs: 10),
            [251] = Title(durationSeconds: 652, playItems: 1, marks: 2, audio: 1, subs: 10),
            // A scene/chapter INDEX: longer than the feature but hundreds of items and NO audio.
            [1000] = Title(durationSeconds: 13558, playItems: 301, marks: 301, audio: 0, subs: 0),
            // A studio logo / transition stub: a few seconds, no audio.
            [11] = Title(durationSeconds: 14.9, playItems: 1, marks: 1, audio: 0, subs: 0),
        };

        IReadOnlyList<DiscContentTitle> catalog = DiscContentCatalog.Build(playlists);

        // Exactly the feature + the two bonus videos survive; the index and the stub are dropped.
        Assert.Equal(3, catalog.Count);
        Assert.DoesNotContain(catalog, title => title.Playlist == 1000);
        Assert.DoesNotContain(catalog, title => title.Playlist == 11);

        // The feature is the single longest playable title — NOT the longer index playlist.
        DiscContentTitle feature = Assert.Single(
            catalog,
            title => title.Kind == DiscContentKind.Feature
        );
        Assert.Equal(800, feature.Playlist);
        Assert.Equal(18, feature.ChapterCount);
        Assert.Equal(7, feature.AudioCount);

        // The rest are bonus, ordered longest-first.
        Assert.Equal(
            [251, 250],
            catalog
                .Where(title => title.Kind == DiscContentKind.Bonus)
                .Select(title => title.Playlist)
        );
    }

    [Fact]
    public void Empty_disc_yields_an_empty_catalog()
    {
        Assert.Empty(DiscContentCatalog.Build(new Dictionary<int, MplsPlaylist>()));
    }

    private static MplsPlaylist Title(
        double durationSeconds,
        int playItems,
        int marks,
        int audio,
        int subs
    )
    {
        return new MplsPlaylist
        {
            PlayItems =
            [
                .. Enumerable
                    .Range(0, playItems)
                    .Select(index => new MplsPlayItem
                    {
                        Index = index,
                        ClipName = $"{index:D5}",
                        InTime = 0,
                        OutTime = 0,
                        IsMultiAngle = false,
                        AngleCount = 1,
                        AngleClips = [],
                    }),
            ],
            Marks =
            [
                .. Enumerable
                    .Range(0, marks)
                    .Select(index => new MplsMark
                    {
                        Index = index,
                        MarkType = 1,
                        PlayItemRef = 0,
                        Seconds = index,
                    }),
            ],
            AudioStreams =
            [
                .. Enumerable.Range(0, audio).Select(_ => new MplsStream { CodingType = 0x80 }),
            ],
            SubtitleStreams =
            [
                .. Enumerable.Range(0, subs).Select(_ => new MplsStream { CodingType = 0x90 }),
            ],
            InteractiveGraphicsStreams = [],
            TotalSeconds = durationSeconds,
        };
    }
}
