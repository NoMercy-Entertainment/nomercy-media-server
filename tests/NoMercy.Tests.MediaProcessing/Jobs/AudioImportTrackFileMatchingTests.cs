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

using NoMercy.MediaProcessing.Jobs.Dto;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.MusicBrainz.Models;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// Live data on the dev instance showed one physical file (a soundtrack cue) with
/// up to 12 different <c>Tracks</c> rows attached to it, each under a different
/// title. The cause: <c>AddSingleOrRelease</c> matched each MusicBrainz track to a
/// file with a loose OR (title contains / duration within 5s / track number) and
/// never removed a matched file from the pool, so several short, similarly-timed
/// tracks on the same release could all claim the identical file. Playback then
/// showed metadata for whichever row happened to be read, unrelated to what the
/// file actually contains — reported as "playing music that has mismatching info
/// tied to the audio file."
/// </summary>
[Trait("Category", "Unit")]
public sealed class AudioImportTrackFileMatchingTests
{
    private static MediaFile File(string path, int trackNumber, string? title = null) =>
        new()
        {
            Path = path,
            Name = Path.GetFileName(path),
            Parsed = new() { Title = title, TrackNumber = trackNumber },
        };

    private static MusicBrainzTrack Track(string title, int position, int durationSeconds) =>
        new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Position = position,
            Length = durationSeconds * 1000,
            Recording = new() { Id = Guid.NewGuid() },
        };

    private static AudioTagModel UntaggedAudio(double durationSeconds) =>
        new()
        {
            MusicBrainz = null,
            Tags = null,
            Duration = durationSeconds,
        };

    /// <summary>
    /// The exact reproduced shape: several short cues on a soundtrack all sit
    /// within 5 seconds of each other and none carry MusicBrainz tags, so every one
    /// of them satisfies the fuzzy fallback against the one file that happens to
    /// come first. Only one may claim it.
    /// </summary>
    [Fact]
    public void ResolveFilesForTracks_SeveralLooselyMatchingTracks_OneSharedFile_OnlyOneClaimsIt()
    {
        MediaFile onlyFile = File("/006 Space Lasers.mp3", trackNumber: 6);
        List<(MediaFile, AudioTagModel)> audioFiles = [(onlyFile, UntaggedAudio(31))];

        List<MusicBrainzTrack> tracks =
        [
            Track("Space Lasers", position: 6, durationSeconds: 30),
            Track("Cirsi", position: 7, durationSeconds: 32),
            Track("Office Post Office", position: 8, durationSeconds: 29),
        ];

        Dictionary<Guid, MediaFile> resolved = AudioImportJob.ResolveFilesForTracks(
            tracks,
            audioFiles
        );

        Assert.Single(resolved);
        Assert.Same(onlyFile, resolved.Values.Single());
    }

    /// <summary>
    /// With one file per track, every track should still resolve to its own file —
    /// the fix must not under-match a release whose tracks legitimately have
    /// distinct, unclaimed files available.
    /// </summary>
    [Fact]
    public void ResolveFilesForTracks_OneFilePerTrack_EachTrackGetsItsOwnFile()
    {
        MediaFile fileOne = File("/01 Intro.mp3", trackNumber: 1);
        MediaFile fileTwo = File("/02 Main Theme.mp3", trackNumber: 2);
        List<(MediaFile, AudioTagModel)> audioFiles =
        [
            (fileOne, UntaggedAudio(30)),
            (fileTwo, UntaggedAudio(180)),
        ];

        List<MusicBrainzTrack> tracks =
        [
            Track("Intro", position: 1, durationSeconds: 30),
            Track("Main Theme", position: 2, durationSeconds: 180),
        ];

        Dictionary<Guid, MediaFile> resolved = AudioImportJob.ResolveFilesForTracks(
            tracks,
            audioFiles
        );

        Assert.Equal(2, resolved.Count);
        Assert.Same(fileOne, resolved[tracks[0].Id]);
        Assert.Same(fileTwo, resolved[tracks[1].Id]);
    }

    /// <summary>
    /// An exact embedded MusicBrainz tag id must win its file even when a fuzzy
    /// signal (duration) would have pointed a different, earlier-processed track at
    /// the same file.
    /// </summary>
    [Fact]
    public void ResolveFilesForTracks_ExactTagMatch_WinsOverAnEarlierFuzzyCandidate()
    {
        MusicBrainzTrack trackA = Track("Overture", position: 1, durationSeconds: 60);
        MusicBrainzTrack trackB = Track("Reprise", position: 2, durationSeconds: 61);

        MediaFile taggedFile = File("/02 - Reprise.mp3", trackNumber: 2);
        AudioTagModel taggedAudio = new()
        {
            MusicBrainz = new() { RecordingId = trackB.Recording.Id },
            Tags = null,
            Duration = 61,
        };

        MediaFile fuzzyFile = File("/01 - Overture.mp3", trackNumber: 1);

        // trackA is processed first and would fuzzy-match taggedFile on duration
        // (within 5s of 60) if pass 1 did not reserve it for trackB's exact id match.
        List<(MediaFile, AudioTagModel)> audioFiles =
        [
            (taggedFile, taggedAudio),
            (fuzzyFile, UntaggedAudio(60)),
        ];

        Dictionary<Guid, MediaFile> resolved = AudioImportJob.ResolveFilesForTracks(
            [trackA, trackB],
            audioFiles
        );

        Assert.Same(taggedFile, resolved[trackB.Id]);
        Assert.Same(fuzzyFile, resolved[trackA.Id]);
    }
}
