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

using Newtonsoft.Json.Linq;

namespace NoMercy.Encoder.Analysis;

public record DolbyVisionInfo(
    int Profile,
    int Level,
    bool HasRpu,
    bool HasEl,
    DvBlCompatibility BlCompat
);

public enum DvBlCompatibility
{
    None,
    Hdr10,
    Sdr,
}

public record CropArea(int W, int H, int X, int Y);

public record AttachmentInfo(int Index, string Codec, string? Filename, string? MimeType);

public record MediaInfo(
    string FilePath,
    string Format,
    TimeSpan Duration,
    long OverallBitRateKbps,
    long FileSizeBytes,
    IReadOnlyList<VideoStreamInfo> VideoStreams,
    IReadOnlyList<AudioStreamInfo> AudioStreams,
    IReadOnlyList<SubtitleStreamInfo> SubtitleStreams,
    IReadOnlyList<ChapterInfo> Chapters,
    IReadOnlyList<AttachmentInfo> Attachments = null!,
    DolbyVisionInfo? DolbyVision = null,
    bool HasHdr10Plus = false,
    CropArea? DetectedCrop = null,
    double CropAspectRatio = 0,
    string? StereoMode = null,
    string? SphericalProjection = null,
    // Raw ffprobe JSON, retained verbatim for the reconstruction blueprint
    // (see .claude/specs/reconstruction-blueprint/SPEC.md). Null when the
    // caller constructs MediaInfo without going through the analyzer (unit
    // tests using minimal stubs).
    JObject? Ffprobe = null,
    // Length of the video stream itself. Duration is the container's, which ends
    // with its longest stream (audio or subtitles can run past the video, #647).
    TimeSpan? VideoDuration = null
)
{
    public TimeSpan EncodedVideoDuration => VideoDuration ?? Duration;
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = Attachments ?? [];
    public bool HasVideo => VideoStreams.Count > 0;
    public bool HasAudio => AudioStreams.Count > 0;
    public bool HasSubtitles => SubtitleStreams.Count > 0;
    public bool HasAttachments => Attachments.Count > 0;

    public bool IsHdr => VideoStreams.Any(v => v.IsHdr);
    public int PrimaryBitDepth => VideoStreams.FirstOrDefault()?.BitDepth ?? 8;
    public bool IsVariableFrameRate => VideoStreams.Any(v => v.IsVariableFrameRate);
    public double PrimaryFrameRate => VideoStreams.FirstOrDefault()?.AverageFrameRate ?? 0;
    public int PrimaryRotation => VideoStreams.FirstOrDefault()?.Rotation ?? 0;
}
