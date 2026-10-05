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

using NoMercy.MediaProcessing.Files;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// Every release id below came from a live AcoustID lookup of three tracks in
/// "Blondie - Greatest Hits (2002)" on 2026-08-02. Those three tracks alone named 324
/// distinct releases between them: fetching each one costs a rate-limited MusicBrainz
/// call and then an ffprobe of the whole folder to score it, so the folder has to agree
/// on a shortlist before any of that happens.
/// </summary>
[Trait("Category", "Unit")]
public class FileRepositoryRankCandidatesTests
{
    // Three pressings of "Atomic: The Very Best of Blondie" — the releases all three
    // sampled tracks named, all 19 tracks long.
    private static readonly Guid AtomicFr = Guid.Parse("3fa7841a-81d5-4681-a1e4-838445c3a01c");
    private static readonly Guid AtomicAu = Guid.Parse("224f40e5-a693-482c-aac3-8041adad5760");
    private static readonly Guid AtomicJp = Guid.Parse("3b0c4ec5-a065-31ac-a006-50e8072f3de5");

    // A single-track release carrying one of the same recordings: named by one track,
    // and the wrong length to be this folder.
    private static readonly Guid UnrelatedSingle = Guid.Parse(
        "7047d8d5-e91c-4d48-90cc-eba5d6dc96ea"
    );

    private const int FolderTrackCount = 19;

    private static FileRepository.ReleaseCandidate Candidate(
        Guid id,
        int? trackCount = FolderTrackCount,
        string? title = null,
        string? artist = null,
        int? year = null
    ) => new(id, trackCount, title, artist, year);

    [Fact]
    public void The_release_matching_the_file_count_outranks_one_that_cannot_be_this_folder()
    {
        List<FileRepository.ReleaseCandidate> candidates =
        [
            Candidate(UnrelatedSingle, trackCount: 2),
            Candidate(UnrelatedSingle, trackCount: 2),
            Candidate(UnrelatedSingle, trackCount: 2),
            Candidate(AtomicFr),
        ];

        FileRepository.RankCandidates(candidates, FolderTrackCount).Should().StartWith(AtomicFr);
    }

    [Fact]
    public void The_release_most_of_the_folder_agrees_on_comes_first()
    {
        List<FileRepository.ReleaseCandidate> candidates =
        [
            Candidate(AtomicAu),
            Candidate(AtomicFr),
            Candidate(AtomicFr),
            Candidate(AtomicFr),
            Candidate(AtomicAu),
            Candidate(AtomicJp),
        ];

        FileRepository
            .RankCandidates(candidates, FolderTrackCount)
            .Should()
            .Equal(AtomicFr, AtomicAu, AtomicJp);
    }

    /// <summary>
    /// The case the tag signal exists for. A greatest-hits folder's songs appear on far
    /// more compilations than on the album itself, so raw agreement puts a compilation
    /// first — and the folder has been saying which album it is the whole time.
    /// </summary>
    [Fact]
    public void The_album_the_tags_name_beats_a_compilation_more_tracks_appear_on()
    {
        List<FileRepository.ReleaseCandidate> candidates =
        [
            Candidate(UnrelatedSingle, title: "D.I.Y.: Blank Generation"),
            Candidate(UnrelatedSingle, title: "D.I.Y.: Blank Generation"),
            Candidate(UnrelatedSingle, title: "D.I.Y.: Blank Generation"),
            Candidate(AtomicFr, title: "Greatest Hits"),
        ];

        FileRepository
            .RankCandidates(candidates, FolderTrackCount, new("Greatest Hits", "Blondie", 2002))
            .Should()
            .StartWith(AtomicFr);
    }

    [Fact]
    public void A_matching_year_breaks_a_tie_between_pressings()
    {
        List<FileRepository.ReleaseCandidate> candidates =
        [
            Candidate(AtomicAu, title: "Greatest Hits", year: 1998),
            Candidate(AtomicJp, title: "Greatest Hits", year: 2002),
        ];

        FileRepository
            .RankCandidates(candidates, FolderTrackCount, new("Greatest Hits", "Blondie", 2002))
            .Should()
            .StartWith(AtomicJp);
    }

    /// <summary>
    /// A folder missing a track, or one whose releases carry no track count, still has to
    /// return something to triage — "no results" is the failure this whole path exists to
    /// stop, so nothing here is a filter.
    /// </summary>
    [Fact]
    public void No_release_matching_the_file_count_still_returns_candidates()
    {
        List<FileRepository.ReleaseCandidate> candidates =
        [
            Candidate(AtomicFr, trackCount: 18),
            Candidate(AtomicFr, trackCount: 18),
            Candidate(UnrelatedSingle, trackCount: null),
        ];

        FileRepository
            .RankCandidates(candidates, FolderTrackCount)
            .Should()
            .Equal(AtomicFr, UnrelatedSingle);
    }

    [Fact]
    public void The_shortlist_stays_short_enough_to_fetch()
    {
        List<FileRepository.ReleaseCandidate> candidates = Enumerable
            .Range(0, 324)
            .Select(index => Candidate(Guid.Parse($"00000000-0000-0000-0000-{index:D12}")))
            .ToList();

        FileRepository.RankCandidates(candidates, FolderTrackCount).Should().HaveCount(10);
    }

    [Fact]
    public void No_candidates_yields_no_lookups()
    {
        FileRepository.RankCandidates([], FolderTrackCount).Should().BeEmpty();
    }

    [Fact]
    public void Untagged_folders_still_rank_on_agreement_and_length()
    {
        List<FileRepository.ReleaseCandidate> candidates =
        [
            Candidate(AtomicFr),
            Candidate(AtomicFr),
            Candidate(UnrelatedSingle, trackCount: 2),
        ];

        FileRepository
            .RankCandidates(candidates, FolderTrackCount, default)
            .Should()
            .StartWith(AtomicFr);
    }
}
