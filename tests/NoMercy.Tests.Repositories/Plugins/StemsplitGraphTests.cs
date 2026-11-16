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

using FluentAssertions;
using NoMercy.Data.Plugins;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>What a graph's text says about its stemsplit filters, and whether it transcribes.</summary>
public class StemsplitGraphTests
{
    // The lyrics plugin's own graphs (nomercy-lyrics-creator-plugin, Filters/Graphs.cs).
    private const string WhisperOnVocals =
        "[0:a]stemsplit={stemsSource}:stem=vocals,aresample=16000,aformat=channel_layouts=mono,"
        + "whisper=model={whisperModel}:dtw=large.v3:word_timestamps=1:vad_model={vadModel}"
        + ":queue=30:destination=-:format=json";

    private const string Align =
        "[0:a]stemsplit={stemsVocalSource}:stem=all[v][b];[v][b]lyricsalign=model={alignModel}"
        + ":text_file={pluginFile:lyrics/a.txt}:language=en:threads=0:destination=-";

    [Theory]
    [InlineData(WhisperOnVocals)]
    [InlineData("[voc]whisper=model=a.bin[out]")]
    [InlineData("whisper=model=a.bin")]
    [InlineData("[a] whisper [b]")]
    public void AWhisperFilter_IsFound(string graph)
    {
        StemsplitGraph.HasWhisper(graph).Should().BeTrue();
    }

    [Theory]
    [InlineData(Align)]
    [InlineData("[0:a]stemsplit=model={stemsModel}[v][a];[v]lyricsalign=model={whisperModel}")]
    [InlineData("[0:a]amovie=whisper.wav[a]")]
    public void ATokenOrAFileNamedWhisper_IsNoWhisperFilter(string graph)
    {
        StemsplitGraph.HasWhisper(graph).Should().BeFalse();
    }

    [Theory]
    [InlineData("[0:a]stemsplit=preset=full:cpu_fallback=0:stem=all[v][b]")]
    [InlineData("[0:a]stemsplit=preset=vocal_full:cpu_fallback=0[v][b]")]
    [InlineData("[0:a]asplit[x][y];[x]stemsplit=models=a.gguf|b.gguf[v][b]")]
    [InlineData("stemsplit=stem=vocals:vocals_models=a.gguf,aresample=16000")]
    public void APresetOrAModelList_RunsRoformer(string graph)
    {
        StemsplitGraph.RunsRoformer(graph).Should().BeTrue();
    }

    [Theory]
    [InlineData("[0:a]stemsplit=model=spleeter-2stems-f16.gguf:stem=all[v][b]")]
    [InlineData("[0:a]stemsplit=model=spleeter-2stems-f16.gguf[v][b];[v]apad=pad_dur=preset")]
    [InlineData("[0:a]asplit[a][b]")]
    public void SpleeterOrNoStemsplit_RunsNoRoformer(string graph)
    {
        StemsplitGraph.RunsRoformer(graph).Should().BeFalse();
    }

    [Theory]
    [InlineData("[0:a]asplit[a][b]", 0)]
    [InlineData("[0:a]stemsplit=model=spleeter-2stems-f16.gguf[v][b]", 0)]
    [InlineData("[0:a]stemsplit=preset=full:cpu_fallback=0[v][b]", 1)]
    [InlineData(
        "[0:a]asplit[a][b];[a]stemsplit=preset=vocal_full:cpu_fallback=0:stem=vocals[v1];[b]stemsplit=preset=full:cpu_fallback=0:stem=vocals[v2]",
        2
    )]
    [InlineData(
        "[0:a]asplit[a][b];[a]stemsplit=preset=full[v1];[b]stemsplit=model=spleeter-2stems-f16.gguf[v2]",
        1
    )]
    public void TheRoformerFilters_AreCounted(string graph, int filters)
    {
        StemsplitGraph.RoformerFilters(graph).Should().Be(filters);
    }
}
