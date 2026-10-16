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

namespace NoMercy.NmSystem.FFProbe;

public class FfProbeData
{
    public TimeSpan Duration { get; set; }
    public FfProbeFormat Format { get; set; } = new();
    public FfProbeAudioStream? PrimaryAudioStream { get; set; }
    public FfProbeVideoStream? PrimaryVideoStream { get; set; }
    public FfProbeSubtitleStream? PrimarySubtitleStream { get; set; }
    public FfProbeImageStream? PrimaryImageStream { get; set; }
    public List<FfProbeVideoStream> VideoStreams { get; set; } = [];
    public List<FfProbeAudioStream> AudioStreams { get; set; } = [];
    public List<FfProbeSubtitleStream> SubtitleStreams { get; set; } = [];
    public List<FfProbeImageStream> ImageStreams { get; set; } = [];
    public IReadOnlyList<string> ErrorData { get; set; } = new List<string>();

    /// <summary>
    /// True when the probe did not produce a result: ffprobe timed out, could
    /// not run, or its output could not be read. The rest of the object is
    /// then empty, which on its own looks the same as a file with no streams
    /// and no duration (#320).
    /// </summary>
    public bool Failed => ErrorData.Count > 0;
    public string FilePath { get; set; } = string.Empty;
}

public class FfProbeFormat
{
    public string? Filename { get; set; }
    public string? FormatName { get; set; }
    public string? FormatLongName { get; set; }
    public TimeSpan Duration { get; set; }
    public long BitRate { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

public class FfProbeVideoStream
{
    public int Index { get; set; }
    public string? CodecName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? PixFmt { get; set; }
    public string? ColorSpace { get; set; }
    public string? ColorTransfer { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? Language { get; set; }
}

public class FfProbeAudioStream
{
    public int Index { get; set; }
    public string? CodecName { get; set; }
    public string? Language { get; set; }
    public int Channels { get; set; }
    public long BitRate { get; set; }
    public int SampleRate { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

public class FfProbeSubtitleStream
{
    public int Index { get; set; }
    public string? CodecName { get; set; }
    public string? Language { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
}

public class FfProbeImageStream
{
    public int Index { get; set; }
    public string? CodecName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
