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

using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Codecs.Definitions;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Encoder.Pipeline.Stages;

/// <summary>
/// AudioOutputPlan[] assembly extracted from PlanStage.BuildOutputPlanAsync.
/// Pure projection over the profile + probed media; no encoder state.
/// </summary>
public static class AudioPlanBuilder
{
    public static AudioOutputPlan[] Build(EncodingProfile profile, MediaInfo media)
    {
        List<AudioOutputPlan> audioPlans = [];
        foreach (AudioOutput audioProfile in profile.Audio)
        {
            string encoderName = AudioCodecDefinitions.GetEncoder(audioProfile.Codec).FfmpegName;
            HashSet<string> allowed =
                audioProfile.AllowedLanguages.Length > 0
                    ? new HashSet<string>(
                        audioProfile.AllowedLanguages,
                        StringComparer.OrdinalIgnoreCase
                    )
                    : [];

            for (int si = 0; si < media.AudioStreams.Count; si++)
            {
                AudioStreamInfo stream = media.AudioStreams[si];
                string streamLang = stream.Language ?? "und";

                if (allowed.Count > 0 && !allowed.Contains(streamLang))
                    continue;

                LoudnessMode loudnessMode = audioProfile.Loudness?.Mode ?? LoudnessMode.None;
                DownmixMode downmixMode = audioProfile.Downmix?.Mode ?? DownmixMode.Auto;
                string? customPanMatrix = audioProfile.Downmix?.CustomPanMatrix;

                string? audioFilter = AudioFilterBuilder.BuildAudioFilter(
                    loudnessMode,
                    downmixMode,
                    customPanMatrix
                );

                // Policy drives the action: a Copy-policy output (author-declared,
                // e.g. BuiltinPresets.CopyAudio, or downgraded by
                // PlanStage.ApplySmartCopyDowngradeAudio) must map to
                // StreamAction.Copy so downstream output strategies emit
                // "-c:a copy" and skip bitrate/filter flags that only apply to
                // a real encode.
                StreamAction action =
                    audioProfile.Policy == StreamPolicy.Copy
                        ? StreamAction.Copy
                        : StreamAction.Transcode;

                audioPlans.Add(
                    new(
                        EncoderName: encoderName,
                        BitrateKbps: audioProfile.BitrateKbps,
                        Channels: audioProfile.Channels,
                        SampleRate: audioProfile.SampleRateHz,
                        Action: action,
                        Language: streamLang,
                        MapLabel: $"0:a:{si}",
                        SegmentNameTemplate: audioProfile.SegmentNameTemplate,
                        PlaylistNameTemplate: audioProfile.PlaylistNameTemplate,
                        AudioFilter: audioFilter,
                        ExtraFlags: audioProfile.CustomArguments is not null
                            ? new Dictionary<string, string>(audioProfile.CustomArguments)
                            : null,
                        SourceCodecName: stream.Codec.ToLowerInvariant(),
                        SourceStreamIndex: stream.Index,
                        IsSourceDefault: stream.IsDefault,
                        SourceChannels: stream.Channels
                    )
                );
            }
        }

        // Disambiguate any audio plans whose templates would resolve to the
        // same on-disk path. The default audio template is
        // "audio_{lang}_{codec}/audio_{lang}_{codec}", so three English AAC
        // streams all collapse to the same directory + filename. Append the
        // source stream index to colliding plans only — single-stream-per-
        // language sources keep their stable templates.
        AudioOutputPlan[] disambiguated =
        [
            .. PlanStageDisambiguation.DisambiguateAudio(audioPlans),
        ];
        return AppendAacFallback(disambiguated, profile, media);
    }

    private static readonly HashSet<string> DolbyFamilyTokens = new(
        ["ac3", "eac3", "dts"],
        StringComparer.OrdinalIgnoreCase
    );

    // Dolby/DTS-only titles also get a stereo AAC twin, appended after the
    // originals so the first rendition stays what it was.
    private static AudioOutputPlan[] AppendAacFallback(
        AudioOutputPlan[] plans,
        EncodingProfile profile,
        MediaInfo media
    )
    {
        HashSet<string?> aacLanguages = plans
            .Where(p => p.CodecToken.Equals("aac", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Language)
            .ToHashSet();

        HashSet<int> dolbySources = plans
            .Where(p => DolbyFamilyTokens.Contains(p.CodecToken))
            .Where(p => !aacLanguages.Contains(p.Language))
            .Select(p => p.SourceStreamIndex)
            .ToHashSet();
        if (dolbySources.Count == 0)
            return plans;

        AudioOutput aac = new(
            Policy: StreamPolicy.Transcode,
            Codec: AudioCodecType.Aac,
            BitrateKbps: 192,
            Channels: 2,
            SampleRateHz: 48000,
            AllowedLanguages: AllowedLanguages.All,
            DefaultLanguage: null,
            Loudness: null,
            Downmix: null,
            SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
            PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
        );

        // Built over the full stream list so MapLabel keeps the real 0:a:N ordinal.
        AudioOutputPlan[] twins =
        [
            .. Build(profile with { Audio = [aac] }, media)
                .Where(p => dolbySources.Contains(p.SourceStreamIndex)),
        ];

        return [.. plans, .. twins];
    }
}
