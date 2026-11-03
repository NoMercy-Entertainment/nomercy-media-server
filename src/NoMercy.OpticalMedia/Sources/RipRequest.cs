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

using NoMercy.Encoder.Profiles;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Metadata;

namespace NoMercy.OpticalMedia.Sources;

public enum RipMode
{
    RipAndEncode,
    RipToRaw,
}

public record RipRequest(
    string DrivePath,
    int[] SelectedTitleIndices,
    string? MetadataId,
    CustomMetadata? Custom,
    Ulid LibraryId,
    Ulid FolderId,
    string? EncodingProfileId,
    AudioTrackSelection[] AudioTracks,
    SubtitleSelection[] Subtitles,
    RipMode Mode = RipMode.RipAndEncode,
    /// <summary>
    /// Volume UUID of the disc's filesystem. When the OS can provide this it
    /// is used as the lock key so the same physical disc is recognised even if
    /// the device path changes. Falls back to <see cref="DrivePath"/> when
    /// <c>null</c> or empty.
    /// </summary>
    string? VolumeUuid = null,
    /// <summary>
    /// Resolved disc type. Controllers populate this from the drive monitor
    /// before dispatching the rip so DiscRipper can pick the right ffmpeg
    /// input shape (bluray:?playlist=N vs -f dvdvideo -title N vs cdda:).
    /// Defaults to <see cref="OpticalDiscType.None"/> for raw API calls
    /// where the caller didn't bother — DiscRipper falls back to detecting
    /// from the drive-path prefix in that case.
    /// </summary>
    OpticalDiscType DiscType = OpticalDiscType.None,
    /// <summary>
    /// Volume label of the disc in the drive (e.g. <c>THE_MATRIX</c>), filled
    /// in by the server from the probe before dispatching the rip. Used as
    /// the TMDB query when the caller supplied no <see cref="Custom"/>
    /// metadata. Null when the drive reports no label.
    /// </summary>
    string? DiscLabel = null,
    /// <summary>
    /// Human-readable title read from disc-embedded metadata (Blu-ray
    /// <c>bdmt_*.xml</c>), filled in by the server from the probe. Preferred
    /// over <see cref="DiscLabel"/> for identification. Null when the disc
    /// carries no embedded title.
    /// </summary>
    string? DiscTitle = null
);

public record CustomMetadata(
    string Title,
    int? Year,
    MediaType Type,
    string? PosterUrl,
    /// <summary>
    /// For TV-show rips: season number to file the ripped titles under.
    /// Null for movies (or when the user is OK with the rip going into a
    /// "specials" / Season 00 bucket).
    /// </summary>
    int? SeasonNumber = null,
    /// <summary>
    /// For TV-show rips with multiple selected titles: the episode number
    /// of the first title in <see cref="RipRequest.SelectedTitleIndices"/>.
    /// Subsequent titles get +1, +2 etc. Disc rips usually map title order
    /// → episode order (especially anime / season-discs), so the user just
    /// supplies the start.
    /// </summary>
    int? EpisodeStartNumber = null
);

public record AudioTrackSelection(int StreamIndex, bool Include);

public record SubtitleSelection(int StreamIndex, bool Include, SubtitlePolicy Policy);
