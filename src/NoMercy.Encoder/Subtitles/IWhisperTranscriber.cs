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

using NoMercy.Encoder.Progress;

namespace NoMercy.Encoder.Subtitles;

public interface IWhisperTranscriber
{
    Task<SubtitleTrack> TranscribeAsync(
        string inputPath,
        int audioStreamIndex,
        string language,
        WhisperOptions? options,
        IProgressObserver? progress,
        CancellationToken ct
    );
}

public record WhisperOptions(
    /// <summary>
    /// Model file to use instead of <c>EncoderOptions.WhisperModelPath</c>.
    /// Null or blank means no override: the configured model applies.
    /// </summary>
    string? ModelPath = null,
    WhisperModelSize ModelSize = WhisperModelSize.LargeV3,
    bool TranslateToEnglish = false,
    int MaxSegmentLengthMs = 10000
);

public enum WhisperModelSize
{
    Tiny,
    Base,
    Small,
    Medium,

    // LargeV2 retained as an available model option beyond spec
    LargeV2,
    LargeV3,
}
