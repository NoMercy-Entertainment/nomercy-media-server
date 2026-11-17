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

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// stderr lines of the real stemsplit filter, copied from runs of
/// stemsplit 2.0.1 in nomercy-ffmpeg v1.0.47 (Windows x64, RTX 3070,
/// 2026-11-16), not written from the plan. How each was reached:
/// <list type="bullet">
/// <item><see cref="NoGpuRefusal" />: <c>preset=vocal_full:cpu_fallback=0</c>
/// with <c>GGML_VK_VISIBLE_DEVICES=99</c>, so Vulkan offers no device.</item>
/// <item><see cref="MemoryRefusal" />: <c>preset=vocal_full:cpu_fallback=0:debug_dev_mem_limit=1</c>;
/// <c>debug_cpu_models=2</c> gives the same line with "1 of the run's 2".</item>
/// <item>the rest: <c>preset=vocal_full:cpu_fallback=0</c> and
/// <c>preset=full:cpu_fallback=0</c> on the GPU, and <c>model=spleeter-2stems-f16.gguf</c>.</item>
/// <item><see cref="UnknownOption" />: an older build (no <c>preset</c>);
/// stemsplit 1.2.1 says the same of <c>cpu_fallback</c>.</item>
/// <item><see cref="TwoFilters" />: two filters in one graph,
/// <c>[0:a]asplit[a][b];[a]stemsplit=preset=vocal_full:cpu_fallback=0:stem=vocals[v1];[b]stemsplit=preset=vocal_full:cpu_fallback=0:stem=vocals[v2]</c>
/// on 15 seconds of audio, with <c>-nostats</c>; every line, in the order printed.</item>
/// </list>
/// </summary>
internal static class StemsplitLogFixtures
{
    public const string Banner =
        "ffmpeg version 9.0.2-NoMercy-MediaServer Copyright (c) 2000-2026 the FFmpeg developers";

    public const string NoGpuRefusal =
        "[Parsed_stemsplit_0 @ 0000020a2f515b80] stemsplit: cpu_fallback=0 and no GPU of this machine can run RoFormer; refused rather than run on the CPU.";

    public const string MemoryRefusal =
        "[Parsed_stemsplit_0 @ 000001f7c05717c0] stemsplit: cpu_fallback=0 and 2 of the run's 2 model(s) would run on the CPU; refused rather than run on the CPU.";

    public const string PartialMemoryRefusal =
        "[Parsed_stemsplit_0 @ 0000022d2d352340] stemsplit: cpu_fallback=0 and 1 of the run's 2 model(s) would run on the CPU; refused rather than run on the CPU.";

    /// <summary>An option mistake, not a verdict about the machine.</summary>
    public const string UseGpuMistake =
        "[Parsed_stemsplit_0 @ 0000019cff14cd00] stemsplit: cpu_fallback=0 needs use_gpu=auto or use_gpu=1; with use_gpu=0 RoFormer runs on the CPU.";

    public const string UnknownOption =
        "[fc#0 @ 000001fcff293e80] Error applying option 'preset' to filter 'stemsplit': Option not found";

    public const string RunsOn =
        "[Parsed_stemsplit_0 @ 000001ae15ff8a00] stemsplit: RoFormer runs on 'vulkan' (NVIDIA GeForce RTX 3070).";

    public const string ModelLoaded =
        "[Parsed_stemsplit_0 @ 000001ae15ff8a00] stemsplit: model 1/4 'roformer-bs-vocals-revive3e-unwa-f32.gguf' weights loaded: 'revive3e', bs-roformer-v1, weight type f32, 577 tensors, 609.4 MiB, 4 ggml threads, 4.935 s on vulkan.";

    public const string FirstChunk =
        "[Parsed_stemsplit_0 @ 000001f73aecab40] stemsplit: model 1/2, chunk 1/? at sample 0 (485100 samples, 485376 received), 3.0 s elapsed, rtf 0.00.";

    public const string LastChunkOfFirstModel =
        "[Parsed_stemsplit_0 @ 000001f73aecab40] stemsplit: model 1/2, chunk 9/9 at sample 1719900 (485100 samples, 2205000 received), 38.9 s elapsed, rtf 1.27.";

    public const string LastChunk =
        "[Parsed_stemsplit_0 @ 000001f73aecab40] stemsplit: model 2/2, chunk 9/9 at sample 1719900 (485100 samples, 2205000 received), 40.3 s elapsed, rtf 1.24.";

    /// <summary>
    /// A chunk line behind ffmpeg's own progress report: without
    /// <c>-nostats</c> the report ends in a carriage return, and the filter's
    /// line follows it.
    /// </summary>
    public const string ChunkAfterStats =
        "size=N/A time=00:00:05.45 bitrate=N/A speed=0.21x elapsed=0:00:26.02    [Parsed_stemsplit_0 @ 000001f73aecab40] stemsplit: model 2/2, chunk 2/? at sample 242550 (485100 samples, 728064 received), 8.9 s elapsed, rtf 1.86.";

    public const string SpleeterLoaded =
        "[Parsed_stemsplit_0 @ 0000013f3b554ec0] stemsplit: model 'spleeter-2stems-f16.gguf' weights loaded (100 tensors); instruments: vocals, accompaniment.";

    /// <summary>
    /// The chunk lines of two RoFormer filters in one graph: ffmpeg names
    /// each instance (<c>Parsed_stemsplit_1</c>, <c>Parsed_stemsplit_2</c>;
    /// the asplit is 0), and the first is done well before the second.
    /// </summary>
    public static readonly string[] TwoFilters =
    [
        "[Parsed_stemsplit_1 @ 000001c8ec477ac0] stemsplit: RoFormer runs on 'vulkan' (NVIDIA GeForce RTX 3070).",
        "[Parsed_stemsplit_2 @ 000001c8ec4772c0] stemsplit: RoFormer runs on 'vulkan' (NVIDIA GeForce RTX 3070).",
        "[Parsed_stemsplit_1 @ 000001c8ec477ac0] stemsplit: model 1/2, chunk 1/? at sample 0 (485100 samples, 485376 received), 3.3 s elapsed, rtf 0.00.",
        "[Parsed_stemsplit_1 @ 000001c8ec477ac0] stemsplit: model 2/2, chunk 1/? at sample 0 (485100 samples, 485376 received), 4.7 s elapsed, rtf 2.34.",
        "[Parsed_stemsplit_2 @ 000001c8ec4772c0] stemsplit: model 1/2, chunk 1/? at sample 0 (485100 samples, 485376 received), 8.0 s elapsed, rtf 0.00.",
        "[Parsed_stemsplit_2 @ 000001c8ec4772c0] stemsplit: model 2/2, chunk 1/? at sample 0 (485100 samples, 485376 received), 9.4 s elapsed, rtf 2.34.",
        "[Parsed_stemsplit_1 @ 000001c8ec477ac0] stemsplit: model 1/2, chunk 2/2 at sample 176400 (485100 samples, 661500 received), 12.7 s elapsed, rtf 1.39.",
        "[Parsed_stemsplit_1 @ 000001c8ec477ac0] stemsplit: model 2/2, chunk 2/2 at sample 176400 (485100 samples, 661500 received), 14.1 s elapsed, rtf 1.60.",
        "[Parsed_stemsplit_2 @ 000001c8ec4772c0] stemsplit: model 1/2, chunk 2/2 at sample 176400 (485100 samples, 661500 received), 17.3 s elapsed, rtf 1.39.",
        "[Parsed_stemsplit_2 @ 000001c8ec4772c0] stemsplit: model 2/2, chunk 2/2 at sample 176400 (485100 samples, 661500 received), 18.8 s elapsed, rtf 1.60.",
    ];
}
