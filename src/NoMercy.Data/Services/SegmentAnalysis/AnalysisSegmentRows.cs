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

using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;
using NoMercy.Encoder.ContentAnalysis.Segments;

namespace NoMercy.Data.Services.SegmentAnalysis;

/// <summary>
/// Maps one episode's (or movie's) analysis answer onto <see cref="ContentSegment"/>
/// rows with <c>Source = "analysis"</c>: the intro as <see cref="ContentSegmentType.Intro"/>
/// and the outro block as <see cref="ContentSegmentType.Credits"/>
/// (<c>StartSeconds</c> is <c>credits_start</c>, <c>ContentEndSeconds</c> is
/// <c>content_end</c>). No recap rows: recaps are not
/// detectable within a season. Content after the credits gets no row: it is
/// content and must never be skipped.
/// </summary>
public static class AnalysisSegmentRows
{
    public const string Source = ContentSegmentSource.Analysis;

    /// <summary>Intro whose picture repeats in at least two partners.</summary>
    public const double PictureConfirmedIntro = 0.9;

    /// <summary>Intro on strong audio alone (an opening that shows different clips each episode).</summary>
    public const double StrongAudioIntro = 0.75;

    /// <summary>Credits in the picture joined by the ending theme.</summary>
    public const double CreditsAndTheme = 0.9;

    /// <summary>Credits in the picture (dark frames, with or without text).</summary>
    public const double PictureCredits = 0.8;

    /// <summary>An ending theme without credits in the picture.</summary>
    public const double ThemeOnlyCredits = 0.6;

    /// <summary>
    /// Credits found only by OCR, over the last scene (Krapopolis): show the
    /// "next episode" button, never skip automatically. Clients treat a credits
    /// row below 0.5 as button-only.
    /// </summary>
    public const double ButtonOnlyCredits = 0.4;

    public static List<ContentSegment> For(int? episodeId, int? movieId, EpisodeSegments answer)
    {
        List<ContentSegment> rows = [];
        if (answer.Intro is { } intro)
        {
            rows.Add(
                Row(
                    episodeId,
                    movieId,
                    ContentSegmentType.Intro,
                    intro.Start,
                    intro.End,
                    intro.PictureConfirmed == true ? PictureConfirmedIntro : StrongAudioIntro
                )
            );
        }

        if (
            answer is { Outro: { } outro, CreditsStart: { } creditsStart }
            && outro.End > creditsStart
        )
        {
            ContentSegment credits = Row(
                episodeId,
                movieId,
                ContentSegmentType.Credits,
                creditsStart,
                outro.End,
                CreditsConfidence(outro)
            );
            credits.ContentEndSeconds = answer.ContentEnd.Seconds;
            rows.Add(credits);
        }

        return rows;
    }

    public static double CreditsConfidence(OutroBlock outro)
    {
        if (outro.ButtonOnly)
            return ButtonOnlyCredits;
        bool credits = outro.Sources.HasFlag(OutroSources.Credits);
        bool theme = outro.Sources.HasFlag(OutroSources.Audio);
        if (credits && theme)
            return CreditsAndTheme;
        return credits ? PictureCredits : ThemeOnlyCredits;
    }

    private static ContentSegment Row(
        int? episodeId,
        int? movieId,
        ContentSegmentType type,
        double start,
        double end,
        double confidence
    ) =>
        new()
        {
            EpisodeId = episodeId,
            MovieId = movieId,
            SegmentType = type,
            StartSeconds = start,
            EndSeconds = end,
            Confidence = confidence,
            Source = Source,
        };
}
