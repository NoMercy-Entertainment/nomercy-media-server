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
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Subtitles;

namespace NoMercy.Encoder.Pipeline.Stages;

/// <summary>
/// SubtitleOutputPlan[] assembly extracted from PlanStage.BuildOutputPlanAsync.
/// Pure projection over the profile + probed media; no encoder state.
/// </summary>
public static class SubtitlePlanBuilder
{
    public static SubtitleOutputPlan[] Build(EncodingProfile profile, MediaInfo media)
    {
        // Build one SubtitleOutputPlan per matching source stream.
        // Deduplicate by source stream index — first matching profile wins.
        // Variants are resolved across ALL source streams once so the per-
        // language "full vs alt" promotion sees every track, not just the
        // ones a given profile happens to allow.
        IReadOnlyList<string> subtitleVariants = SubtitleClassifier.ResolveVariants(
            media.SubtitleStreams
        );
        List<SubtitleOutputPlan> subtitlePlans = [];
        HashSet<int> claimedStreams = [];
        foreach (SubtitleOutput subProfile in profile.Subtitles)
        {
            HashSet<string> allowed =
                subProfile.AllowedLanguages.Length > 0
                    ? new HashSet<string>(
                        subProfile.AllowedLanguages,
                        StringComparer.OrdinalIgnoreCase
                    )
                    : [];

            for (int si = 0; si < media.SubtitleStreams.Count; si++)
            {
                if (claimedStreams.Contains(si))
                    continue;

                SubtitleStreamInfo stream = media.SubtitleStreams[si];
                string streamLang = stream.Language ?? "und";

                if (allowed.Count > 0 && !allowed.Contains(streamLang))
                    continue;

                claimedStreams.Add(si);
                subtitlePlans.Add(
                    new(
                        OutputCodec: subProfile.Codec,
                        Action: subProfile.Policy == SubtitlePolicy.BurnIn
                            ? StreamAction.Transcode
                            : StreamAction.Extract,
                        Language: streamLang,
                        SourceIndex: si,
                        MapLabel: $"0:s:{si}",
                        PlaylistNameTemplate: subProfile.PlaylistNameTemplate,
                        Policy: subProfile.Policy,
                        Variant: subtitleVariants[si],
                        ExtraFlags: subProfile.CustomArguments is not null
                            ? CustomArgumentGuard.Sanitize(
                                subProfile.CustomArguments,
                                true,
                                "subtitle custom arguments"
                            )
                            : null
                    )
                );
            }
        }

        return subtitlePlans.ToArray();
    }
}
