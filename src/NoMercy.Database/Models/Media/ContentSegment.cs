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

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace NoMercy.Database.Models.Media;

/// <summary>
/// Kinds of timeline segments the player knows how to handle. The segment
/// analysis writes <see cref="Intro"/> and <see cref="Credits"/>, the chapter
/// classifier any kind; users can add or correct any of them from the
/// dashboard. Old detector rows used <see cref="Outro"/>.
/// </summary>
public enum ContentSegmentType
{
    Intro,
    Outro,
    Recap,
    Credits,
}

/// <summary>
/// Annotated timeline range on a piece of media — the payload the player
/// consumes to show a "Skip Intro" button, auto-advance at credits, etc.
/// Either <see cref="EpisodeId"/> or <see cref="MovieId"/> is set, never
/// both.
/// </summary>
[PrimaryKey(nameof(Id))]
[Index(nameof(EpisodeId))]
[Index(nameof(MovieId))]
[Index(nameof(EpisodeId), nameof(SegmentType))]
public class ContentSegment
{
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [JsonProperty("id")]
    public Ulid Id { get; set; } = Ulid.NewUlid();

    [JsonProperty("episode_id")]
    public int? EpisodeId { get; set; }
    public Episode? Episode { get; set; }

    [JsonProperty("movie_id")]
    public int? MovieId { get; set; }
    public Movie? Movie { get; set; }

    [JsonProperty("segment_type")]
    public ContentSegmentType SegmentType { get; set; }

    /// <summary>Segment start in seconds from the source timeline.</summary>
    [JsonProperty("start_seconds")]
    public double StartSeconds { get; set; }

    /// <summary>Segment end (exclusive) in seconds.</summary>
    [JsonProperty("end_seconds")]
    public double EndSeconds { get; set; }

    /// <summary>
    /// How this segment landed in the database — "analysis" for the segment
    /// analysis, "detector" / "auto" for rows the retired chromaprint detector
    /// stored, "manual" for dashboard overrides, "import" for
    /// community-provided data, "chapters" for the file's chapter list. The
    /// values and which one wins live in <c>NoMercy.Database.Segments</c>
    /// (<c>ContentSegmentSource</c>, <c>ContentSegmentPrecedence</c>).
    /// </summary>
    [JsonProperty("source")]
    [MaxLength(32)]
    public string Source { get; set; } = "detector";

    /// <summary>
    /// Detector confidence in [0, 1]. Always 1.0 for manual entries.
    /// Useful for filtering noisy detections in the player UI.
    /// </summary>
    [JsonProperty("confidence")]
    public double Confidence { get; set; } = 1.0;

    /// <summary>
    /// Where the item's content really ends: black and silence run from here to
    /// the end of the file, so auto-advance belongs here, after any epilogue or
    /// preview that follows the credits (#598). Set only on the analysis Credits
    /// row; null on every other row.
    /// </summary>
    [JsonProperty("content_end_seconds")]
    public double? ContentEndSeconds { get; set; }

    [JsonProperty("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [JsonProperty("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
