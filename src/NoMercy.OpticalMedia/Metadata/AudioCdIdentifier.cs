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

using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Sources;
using NoMercy.Providers.CoverArt.Client;
using NoMercy.Providers.CoverArt.Models;
using NoMercy.Providers.MusicBrainz.Client;
using NoMercy.Providers.MusicBrainz.Models;

namespace NoMercy.OpticalMedia.Metadata;

/// <summary>
/// Identifies Audio CDs using MusicBrainz Disc ID (exact TOC hash lookup),
/// falling back to fuzzy TOC lookup when the disc id is not found.
///
/// Flow:
///   1. Read TOC via <see cref="ITocReader"/>.
///   2. If TOC is null → NeedsManualAssignment.
///   3. Compute MusicBrainz Disc ID via <see cref="MusicBrainzDiscId.Compute"/>.
///   4. Exact lookup: <c>/ws/2/discid/{id}</c> → releases.
///   5. If exact miss → fuzzy lookup: <c>/ws/2/discid/-?toc=…</c>.
///   6. Build ranked candidates; fetch cover art via Cover Art Archive.
///   7. AcoustID per-track fallback is not yet wired (follow-up: wire
///      ChromaprintFingerprinter through AcoustIdFingerprintClient).
/// </summary>
public sealed class AudioCdIdentifier(
    ITocReader tocReader,
    MusicBrainzDiscClient discClient,
    ILogger<AudioCdIdentifier> logger
) : IDiscIdentifier
{
    private const double ExactMatchConfidence = 0.97;
    private const double FuzzyMatchBaseConfidence = 0.70;
    private const double MultiPressingsConfidencePenalty = 0.05;
    private const double AutoApplyThreshold = 0.90;

    public bool CanHandle(OpticalDiscType type) => type == OpticalDiscType.Cd;

    public async Task<DiscIdentification> IdentifyAsync(DiscInfo disc, CancellationToken ct)
    {
        string drivePath = disc.DrivePath ?? disc.DiscLabel ?? string.Empty;

        DiscToc? toc = await tocReader.ReadTocAsync(drivePath, ct);
        if (toc is null)
        {
            logger.LogInformation(
                "AudioCdIdentifier: TOC unavailable for drive '{Drive}' — returning NeedsManualAssignment",
                drivePath
            );
            return NeedsManual();
        }

        string discId = MusicBrainzDiscId.Compute(toc);
        logger.LogInformation(
            "AudioCdIdentifier: computed disc id {DiscId} for drive '{Drive}'",
            discId,
            drivePath
        );

        DiscIdLookupResponse? exactResult = null;
        try
        {
            exactResult = await discClient.LookupByDiscId(discId, priority: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex,
                "AudioCdIdentifier: exact disc-id lookup failed ({Message}); falling back to fuzzy",
                ex.Message
            );
        }

        if (exactResult?.Releases is { Length: > 0 })
        {
            logger.LogInformation(
                "AudioCdIdentifier: exact match — {Count} release(s)",
                exactResult.Releases.Length
            );
            return await BuildIdentification(exactResult.Releases, toc, isExactMatch: true, ct);
        }

        // Fuzzy TOC lookup fallback.
        logger.LogInformation(
            "AudioCdIdentifier: no exact match for disc id {DiscId}, trying fuzzy TOC lookup",
            discId
        );

        DiscIdLookupResponse? fuzzyResult = null;
        try
        {
            string tocString = BuildTocString(toc);
            fuzzyResult = await discClient.LookupByTocString(tocString, priority: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogInformation(
                ex,
                "AudioCdIdentifier: fuzzy TOC lookup failed ({Message})",
                ex.Message
            );
        }

        if (fuzzyResult?.Releases is { Length: > 0 })
        {
            logger.LogInformation(
                "AudioCdIdentifier: fuzzy match — {Count} release(s)",
                fuzzyResult.Releases.Length
            );
            return await BuildIdentification(fuzzyResult.Releases, toc, isExactMatch: false, ct);
        }

        // TODO follow-up: AcoustID per-track fingerprint fallback (wire
        // ChromaprintFingerprinter through AcoustIdFingerprintClient when
        // the V3 encoder exposes fingerprinting).
        logger.LogInformation(
            "AudioCdIdentifier: no MusicBrainz match found — returning NeedsManualAssignment"
        );
        return NeedsManual();
    }

    private async Task<DiscIdentification> BuildIdentification(
        MusicBrainzReleaseAppends[] releases,
        DiscToc toc,
        bool isExactMatch,
        CancellationToken ct
    )
    {
        int releaseCount = releases.Length;
        List<DiscCandidate> candidates = [];

        for (int releaseIndex = 0; releaseIndex < releaseCount; releaseIndex++)
        {
            ct.ThrowIfCancellationRequested();
            MusicBrainzReleaseAppends release = releases[releaseIndex];

            double confidence = isExactMatch
                ? ExactMatchConfidence - (releaseIndex * MultiPressingsConfidencePenalty)
                : FuzzyMatchBaseConfidence - (releaseIndex * MultiPressingsConfidencePenalty);

            confidence = Math.Clamp(confidence, 0.0, 1.0);

            string? posterUrl = await FetchCoverUrlAsync(release.Id, ct);

            TrackMapping[] trackMappings = BuildTrackMappings(release, toc);

            string artistCredit = FormatArtistCredit(release.ArtistCredit);
            string fullTitle = string.IsNullOrWhiteSpace(artistCredit)
                ? release.Title
                : $"{artistCredit} — {release.Title}";

            candidates.Add(
                new(
                    Source: "musicbrainz",
                    StableId: release.Id.ToString(),
                    Title: fullTitle,
                    Year: release.DateTime?.Year,
                    PosterUrl: posterUrl,
                    BackdropUrl: null,
                    Confidence: Math.Round(confidence, 4),
                    TrackMapping: trackMappings
                )
            );
        }

        DiscCandidate[] ranked = candidates.OrderByDescending(c => c.Confidence).ToArray();

        double topConfidence = ranked.Length > 0 ? ranked[0].Confidence : 0;
        bool autoApply = topConfidence >= AutoApplyThreshold && releaseCount == 1;

        return new(
            Kind: MediaKind.Music,
            Candidates: ranked,
            TopConfidence: topConfidence,
            AutoApply: autoApply,
            NeedsManualAssignment: false
        );
    }

    /// <summary>
    /// Maps disc tracks (1-based) to MusicBrainz recording MBIDs.
    /// Walks the first CD medium in the release whose track count matches
    /// the TOC track count.
    /// </summary>
    private static TrackMapping[] BuildTrackMappings(MusicBrainzReleaseAppends release, DiscToc toc)
    {
        int tocTrackCount = toc.LastTrack - toc.FirstTrack + 1;

        MusicBrainzMedia? medium = release.Media.FirstOrDefault(m =>
            m.TrackCount == tocTrackCount || m.Tracks.Length == tocTrackCount
        );

        if (medium is null)
            return [];

        List<TrackMapping> mappings = [];
        foreach (MusicBrainzTrack track in medium.Tracks)
        {
            string artistCredit = FormatArtistCredit(track.ArtistCredit);
            mappings.Add(
                new(
                    TrackIndex: track.Position,
                    RecordingMbid: track.Recording.Id,
                    Title: track.Title,
                    ArtistCredit: artistCredit,
                    DurationMs: track.Length
                )
            );
        }

        return mappings.ToArray();
    }

    private static async Task<string?> FetchCoverUrlAsync(Guid releaseId, CancellationToken ct)
    {
        try
        {
            CoverArtCoverArtClient coverClient = new(releaseId);
            CoverArtCovers? covers = await coverClient.Cover();
            CoverArtImage? front = covers?.Images.FirstOrDefault(i =>
                i.Front || i.Types.Contains("Front")
            );
            return front?.Image?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatArtistCredit(ReleaseArtistCredit[] credits)
    {
        return string.Concat(credits.Select(c => (c.Name ?? string.Empty) + c.Joinphrase));
    }

    /// <summary>
    /// Builds the <c>toc=</c> query string for the MusicBrainz fuzzy TOC lookup:
    /// <c>firstTrack+lastTrack+leadOut+t1+t2+…</c>. The offsets are the absolute
    /// ones the TOC reader delivers, which already include the 150-frame pregap.
    /// </summary>
    internal static string BuildTocString(DiscToc toc)
    {
        List<string> parts =
        [
            toc.FirstTrack.ToString(System.Globalization.CultureInfo.InvariantCulture),
            toc.LastTrack.ToString(System.Globalization.CultureInfo.InvariantCulture),
            toc.LeadOutOffsetSectors.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];

        foreach (int offset in toc.TrackOffsetsSectors)
        {
            parts.Add(offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join("+", parts);
    }

    private static DiscIdentification NeedsManual() =>
        new(
            Kind: MediaKind.Music,
            Candidates: [],
            TopConfidence: 0,
            AutoApply: false,
            NeedsManualAssignment: true
        );
}
