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

using System.Text;
using Newtonsoft.Json;
using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Segments;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>
/// Real chapter tables read from chapters.vtt files of encoded episodes
/// (Segments/Corpus/*.json). Each episode carries the segments it must get
/// and the story ranges no segment may cover.
/// </summary>
internal static class ChapterCorpus
{
    public static readonly string[] Files =
    [
        "hellsing-2001.json",
        "hellsing-ultimate.json",
        "rick-and-morty-s01-s02.json",
        "american-dad.json",
        "silo.json",
        "hijack.json",
        "western-generic-chapters.json",
    ];

    public static CorpusSeries Load(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Segments", "Corpus", file);
        return JsonConvert.DeserializeObject<CorpusSeries>(File.ReadAllText(path))!;
    }

    public static CorpusEpisode Episode(string file, string episode) =>
        Load(file).Episodes.Single(e => e.Episode == episode);

    /// <summary>
    /// The chapters as the server holds them: the cues written back into a
    /// chapters.vtt and parsed by the scan's own parser, which also adds the
    /// "Start" chapter when the first cue starts after 0.
    /// </summary>
    public static List<ChapterSpan> StoredChapters(CorpusEpisode episode)
    {
        StringBuilder vtt = new();
        vtt.Append("WEBVTT\n\n");
        for (int i = 0; i < episode.Chapters.Count; i++)
        {
            CorpusCue cue = episode.Chapters[i];
            vtt.Append($"Chapter {i + 1}\n{cue.Start} --> {cue.End}\n{cue.Title}\n\n");
        }

        List<IChapter> parsed = FileManager.ParseChaptersVtt(vtt.ToString());
        return [.. parsed.Select(ChapterSpan.FromStored)];
    }
}
