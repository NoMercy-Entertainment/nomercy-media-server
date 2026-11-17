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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// Every threshold of the intro / credits / end-of-content analysis (#598).
/// Each value was measured on 13 seasons (93 episodes); the comment says which
/// episode broke the simpler rule. Change one only with the corpus tests.
/// </summary>
public static class SegmentRules
{
    // ---- Stage 2: fingerprint matching

    /// <summary>Two hashes match when at most this many of their 32 bits differ.</summary>
    public const int MaxDifferingBits = 10;

    /// <summary>Shortest shared run worth keeping.</summary>
    public const double MinRunSeconds = 8.0;

    /// <summary>
    /// Matching hashes at most this many apart stay in one run (up to 7
    /// non-matching hashes, 0.87 s, bridged between them).
    /// </summary>
    public const int MaxGapHashes = 8;

    /// <summary>
    /// Partners each episode is matched against at most: its nearest episodes by
    /// order. Exhaustive matching grows with the square of the season.
    /// </summary>
    public const int MaxPartners = 12;

    // ---- Stage 3: themes by position

    /// <summary>Head zone: the first 40 % (Silo E05 opens at 14:31 of 50:50).</summary>
    public const double HeadZone = 0.40;

    /// <summary>Tail zone: the last 40 %.</summary>
    public const double TailZone = 0.60;

    /// <summary>Share of partners that must hold the audio in the same zone.</summary>
    public const double NeedFraction = 0.40;

    /// <summary>
    /// Region edges are trimmed to where same-zone support is at least this share
    /// of the region's peak (Silo E02 otherwise started 15 s early, inside story).
    /// </summary>
    public const double CoreFraction = 0.60;

    /// <summary>Theme counting resolution.</summary>
    public const double BinSeconds = 0.5;

    /// <summary>Bins of no support bridged inside one theme stretch.</summary>
    public const int StretchGapBins = 4;

    /// <summary>
    /// Pass 2 also counts a partner's hit when at least this share of it lies inside
    /// one of the partner's own pass-1 themes or credits.
    /// </summary>
    public const double PartnerThemeOverlap = 0.5;

    // ---- Stage 4: edges

    /// <summary>A silence within this distance of an edge was absorbed by the fingerprint.</summary>
    public const double EdgeSnapSeconds = 4.0;

    /// <summary>A cut or black within this distance confirms an edge.</summary>
    public const double EdgeToleranceSeconds = 2.0;

    /// <summary>Music leads the picture into a theme by up to this much (Rick and Morty 2.5–4 s).</summary>
    public const double LeadSeconds = 5.0;

    /// <summary>Half-width of the picture probe around an edge.</summary>
    public const double EdgeProbeSeconds = 6.0;

    /// <summary>A recorded probe counts for an edge when it was taken this close to it.</summary>
    public const double EdgeProbeMatchSeconds = 0.6;

    /// <summary>Edges this close to the file start or end are hard by definition.</summary>
    public const double FileEdgeSeconds = 2.0;

    // ---- Stage 5: picture

    /// <summary>Thumbnails per second.</summary>
    public const int ThumbnailFps = 2;

    public const int ThumbnailWidth = 32;

    public const int ThumbnailHeight = 18;

    public const int ThumbnailPixels = ThumbnailWidth * ThumbnailHeight;

    /// <summary>Only candidates at least this long get thumbnails.</summary>
    public const double ThumbnailMinRegionSeconds = 12.0;

    /// <summary>Thumbnails are grabbed this far beyond each candidate edge.</summary>
    public const double ThumbnailPadSeconds = 2.0;

    /// <summary>Two frames match when their pixels correlate at least this much.</summary>
    public const double FrameCorrelation = 0.85;

    /// <summary>A frame this flat (pixel standard deviation) is compared by brightness.</summary>
    public const double FlatFrameDeviation = 4.0;

    /// <summary>Two flat frames match when their mean brightness differs less than this.</summary>
    public const double FlatFrameBrightness = 12.0;

    /// <summary>A partner's frame is used when it sits this close to the aligned time.</summary>
    public const double FrameAlignSeconds = 0.3;

    /// <summary>A partner matches when this share of its aligned frames match.</summary>
    public const double PartnerFrameFraction = 0.6;

    /// <summary>A partner is compared only with at least this many aligned frames.</summary>
    public const int MinComparedFrames = 6;

    /// <summary>A partner is compared only over at least this much shared audio.</summary>
    public const double MinComparedSeconds = 4.0;

    // ---- Stage 6: keep rule

    /// <summary>Shortest intro or ending reported (title cards of 5–6 s are not).</summary>
    public const double MinSegmentSeconds = 15.0;

    /// <summary>Strong audio: share of partners holding the audio at the same place.</summary>
    public const double StrongSameFraction = 0.6;

    /// <summary>Strong audio: median share of the stretch those partners cover.</summary>
    public const double StrongCoverage = 0.95;

    /// <summary>Strong audio: minimum length (Krapopolis opening is 25.0 s).</summary>
    public const double StrongMinSeconds = 25.0;

    /// <summary>Strong audio: start within the first quarter (Squid Game reused score sat at 33 %).</summary>
    public const double StrongMaxPosition = 0.25;

    // ---- Stage 7: credits

    /// <summary>Credits must end within max(this, <see cref="CreditsTailFraction"/> × duration) of the end.</summary>
    public const double CreditsTailSeconds = 180.0;

    public const double CreditsTailFraction = 0.12;

    /// <summary>Shortest credits block from dark frames.</summary>
    public const double CreditsMinSeconds = 20.0;

    /// <summary>Dark runs this close together form one block.</summary>
    public const double CreditsGapSeconds = 6.0;

    /// <summary>blackdetect is run over this much of the end (and around every silence).</summary>
    public const double VideoTailSeconds = 480.0;

    /// <summary>Padding of the blackdetect window around each silence.</summary>
    public const double SilenceWindowPadSeconds = 1.5;

    /// <summary>A frame with this many 3+ letter words is a text frame.</summary>
    public const int OcrWords = 4;

    /// <summary>Shortest run of text frames that counts as credits.</summary>
    public const double OcrMinSeconds = 15.0;

    /// <summary>Text frames this close together form one run.</summary>
    public const double OcrGapSeconds = 4.0;

    /// <summary>OCR covers at most the last this-many seconds (the prototype's tail).</summary>
    public const double OcrTailSeconds = 420.0;

    /// <summary>OCR starts this far before the earliest dark credits candidate.</summary>
    public const double OcrLeadSeconds = 60.0;

    /// <summary>Credits blocks this close together are one block.</summary>
    public const double CreditsMergeSeconds = 3.0;

    /// <summary>A block longer than this × the season's median credits length starts at end − median.</summary>
    public const double CreditsOutlierFactor = 1.5;

    /// <summary>The season median needs at least this many credits blocks.</summary>
    public const int CreditsMedianMinBlocks = 3;

    // ---- Stage 8: combine

    /// <summary>An audio ending touches credits when within this distance.</summary>
    public const double TouchSeconds = 3.0;

    /// <summary>Less than this after the credits is logos and end cards: part of the credits.</summary>
    public const double LogoTailSeconds = 8.0;

    /// <summary>Silence counts as reaching the end when it ends this close to it.</summary>
    public const double TailSilenceSeconds = 0.5;

    /// <summary>Black counts as reaching the end when it ends this close to it.</summary>
    public const double TailBlackSeconds = 0.6;

    /// <summary>Seasons with fewer episodes get only credits and end of content (stages 7–8).</summary>
    public const int MinEpisodesForThemes = 3;
}
