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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Pipeline;
using NoMercy.PluginSdk.Abstractions;
using EncoderMediaInfo = NoMercy.Encoder.Analysis.MediaInfo;
using EncoderVideoOutput = NoMercy.Encoder.Profiles.VideoOutput;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using PluginProfile = NoMercy.PluginSdk.Abstractions.EncodingProfile;

namespace NoMercy.PluginSdk;

/// <summary>
/// Wires <see cref="IEncoderPlugin.GetProfile"/> into the encoder's
/// <see cref="IProfileOverride"/> seam: the first encoder plugin that returns a
/// non-null profile for the analyzed source replaces the configured profile.
/// No plugins, or all returning null, keeps the configured profile unchanged.
///
/// The plugin surface (<see cref="PluginProfile"/>) is a flat public DTO kept
/// deliberately decoupled from the encoder's rich internal profile, so this class
/// owns the bridge in both directions: encoder MediaInfo → plugin MediaInfo to
/// call the hook, and the returned flat profile → a full encoder profile.
/// </summary>
public class PluginProfileOverride(
    IPluginManager pluginManager,
    ILogger<PluginProfileOverride>? logger = null
) : IProfileOverride
{
    private readonly ILogger _log = logger ?? NullLogger<PluginProfileOverride>.Instance;

    public EncodingProfile Apply(EncodingProfile configured, EncoderMediaInfo media)
    {
        MediaInfo pluginMedia = ToPluginMediaInfo(media);

        foreach (IEncoderPlugin plugin in pluginManager.GetPluginsOfType<IEncoderPlugin>())
        {
            PluginProfile? pluginProfile;

            try
            {
                pluginProfile = plugin.GetProfile(pluginMedia);
            }
            catch (Exception exception)
            {
                // An encode in flight is worth more than one plugin's opinion
                // of the profile, so a plugin that cannot answer is skipped.
                if (
                    !PluginStaleMemberLog.Explain(
                        _log,
                        plugin.Id,
                        exception,
                        "choosing an encoding profile"
                    )
                )
                {
                    _log.LogError(
                        exception,
                        "Plugin {Plugin} threw choosing an encoding profile; the configured one is used.",
                        plugin.Id
                    );
                }

                continue;
            }

            if (pluginProfile is not null)
                return ToEncodingProfile(pluginProfile);
        }

        return configured;
    }

    private static MediaInfo ToPluginMediaInfo(EncoderMediaInfo media)
    {
        Encoder.Analysis.VideoStreamInfo? video =
            media.VideoStreams.Count > 0 ? media.VideoStreams[0] : null;
        Encoder.Analysis.AudioStreamInfo? audio =
            media.AudioStreams.Count > 0 ? media.AudioStreams[0] : null;

        return new()
        {
            FilePath = media.FilePath,
            VideoCodec = video?.Codec,
            AudioCodec = audio?.Codec,
            Width = video?.Width,
            Height = video?.Height,
            Bitrate = media.OverallBitRateKbps > 0 ? media.OverallBitRateKbps * 1000L : null,
            Duration = media.Duration,
            IsHdr = video?.IsHdr ?? false,
        };
    }

    private static EncodingProfile ToEncodingProfile(PluginProfile profile)
    {
        VideoCodecType videoCodec =
            CodecFamilyClassifier.ClassifyVideo(profile.VideoCodec) ?? VideoCodecType.H264;
        AudioCodecType audioCodec =
            CodecFamilyClassifier.ClassifyAudio(profile.AudioCodec) ?? AudioCodecType.Aac;

        return new(
            Id: Ulid.NewUlid(),
            Name: profile.Name,
            Container: ParseContainer(profile.Container),
            Video: new(
                Policy: Encoder.Profiles.StreamPolicy.Transcode,
                Codec: videoCodec,
                Width: profile.Width,
                Height: profile.Height,
                RateControl: profile.VideoBitrate is > 0
                    ? Encoder.Profiles.RateControlMode.Vbr
                    : Encoder.Profiles.RateControlMode.Crf,
                Crf: profile.VideoBitrate is > 0 ? 0 : 23,
                BitrateKbps: profile.VideoBitrate ?? 0,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "medium",
                CodecProfile: Encoder.Profiles.CodecProfile.Auto,
                Level: null,
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                CustomArguments: profile.ExtraParameters.Count > 0
                    ? new Dictionary<string, string>(profile.ExtraParameters)
                    : null
            ),
            Audio:
            [
                new(
                    Policy: Encoder.Profiles.StreamPolicy.Transcode,
                    Codec: audioCodec,
                    BitrateKbps: profile.AudioBitrate ?? 0,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: [],
                    DefaultLanguage: null,
                    Loudness: null,
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    CustomArguments: null
                ),
            ],
            Subtitles: []
        );
    }

    private static Encoder.Profiles.Container ParseContainer(string container) =>
        container.ToLowerInvariant() switch
        {
            "ts" or "mpegts" or "hls_ts" => Encoder.Profiles.Container.HlsTs,
            "m3u8" or "hls" or "fmp4" or "hls_fmp4" => Encoder.Profiles.Container.HlsFmp4,
            "mkv" or "matroska" => Encoder.Profiles.Container.Mkv,
            "dash" or "mpd" => Encoder.Profiles.Container.Dash,
            "mp3" => Encoder.Profiles.Container.Mp3,
            "flac" => Encoder.Profiles.Container.Flac,
            "ogg" or "oga" or "opus" => Encoder.Profiles.Container.Ogg,
            _ => Encoder.Profiles.Container.Mp4,
        };
}
