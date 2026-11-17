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
using NoMercy.Encoder.ContentAnalysis.Segments;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

/// <summary>
/// Loads the #598 research corpus in the prototype's own layout
/// (<c>season/episode/audio.json, fp_*.raw, video.json, edges.json, ocr.json,
/// thumbs.npz, chapters.json</c>, plus <c>season/matches.json</c> and
/// <c>season/analysis.json</c>).
/// <para>
/// Two copies exist: the committed fixtures (nine seasons, exported by
/// <c>tools/segment-corpus/export_fixtures.py</c>, fingerprints for Hellsing S01
/// only) and, when <see cref="ResearchEnvironmentVariable"/> points at the
/// nomercy-segment-research folder, all 13 seasons with every fingerprint.
/// </para>
/// </summary>
public static class ResearchCorpus
{
    /// <summary>Path of the nomercy-segment-research folder, for the full corpus run.</summary>
    public const string ResearchEnvironmentVariable = "NOMERCY_SEGMENT_RESEARCH";

    public static readonly string[] CommittedSeasons =
    [
        "american-dad-s02",
        "chernobyl",
        "hellsing-s01",
        "hellsing-ultimate",
        "krapopolis-s01",
        "rick-and-morty-s01",
        "rick-and-morty-s02",
        "the-last-of-us-s01",
        "tomb-raider-s01",
    ];

    public static readonly string[] AllSeasons =
    [
        .. CommittedSeasons,
        "mr-robot-s01",
        "silo-s01",
        "squid-game-s01",
        "yakuza-s01",
    ];

    private static readonly Dictionary<string, RecordedSeason> Cache = new();
    private static readonly Lock CacheLock = new();

    public static string CommittedRoot =>
        Path.Combine(AppContext.BaseDirectory, "ContentAnalysis", "Segments", "Corpus");

    /// <summary>The research folder's corpus directory, or null when the variable is not set.</summary>
    public static string? ResearchRoot
    {
        get
        {
            string? root = Environment.GetEnvironmentVariable(ResearchEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(root))
                return null;
            string corpus = Path.Combine(root, "runs", "corpus");
            return Directory.Exists(corpus) ? corpus : null;
        }
    }

    public static RecordedSeason Committed(string id) => Load(Path.Combine(CommittedRoot, id));

    public static RecordedSeason Load(string seasonDirectory)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(seasonDirectory, out RecordedSeason? cached))
                return cached;
        }

        RecordedSeason season = Read(seasonDirectory);
        lock (CacheLock)
            Cache[seasonDirectory] = season;
        return season;
    }

    private static RecordedSeason Read(string seasonDirectory)
    {
        List<string> episodeDirectories =
        [
            .. Directory
                .GetDirectories(seasonDirectory)
                .Where(d => File.Exists(Path.Combine(d, "audio.json")))
                .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal),
        ];

        List<RecordedEpisode> episodes = [.. episodeDirectories.Select(ReadEpisode)];
        Dictionary<string, int> index = episodes
            .Select((e, i) => (e.Name, i))
            .ToDictionary(x => x.Name, x => x.i);

        JObject matches = ReadJson(Path.Combine(seasonDirectory, "matches.json"));
        List<PairMatches> pairs = [];
        foreach (JProperty pair in ((JObject)matches["pairs"]!).Properties())
        {
            string[] names = pair.Name.Split('|');
            List<AudioMatch> hits =
            [
                .. ((JArray)pair.Value).Select(h => new AudioMatch(
                    (double)h[0]!,
                    (double)h[1]!,
                    (double)h[2]!,
                    (double)h[3]!
                )),
            ];
            pairs.Add(new(index[names[0]], index[names[1]], hits));
        }

        return new(
            Path.GetFileName(seasonDirectory),
            episodes,
            pairs,
            ReadJson(Path.Combine(seasonDirectory, "analysis.json"))
        );
    }

    private static RecordedEpisode ReadEpisode(string directory)
    {
        JObject audio = ReadJson(Path.Combine(directory, "audio.json"));
        double duration = (double)audio["duration"]!;

        List<FingerprintPart> parts = [];
        List<TimeRange> silences = [];
        foreach (JToken part in (JArray)audio["parts"]!)
        {
            string fp = Path.Combine(directory, (string)part["fp"]!);
            uint[] hashes = File.Exists(fp) ? ChromaprintRaw.Parse(File.ReadAllBytes(fp)) : [];
            parts.Add(new((double)part["offset"]!, hashes));
            foreach (JToken silence in (JArray)part["silences"]!)
            {
                double end = silence[1]!.Type == JTokenType.Null ? duration : (double)silence[1]!;
                silences.Add(new((double)silence[0]!, end));
            }
        }

        BlackRuns? black = null;
        string videoPath = Path.Combine(directory, "video.json");
        if (File.Exists(videoPath))
        {
            JObject video = ReadJson(videoPath);
            black = new(Ranges(video["black"]!["strict"]!), Ranges(video["black"]!["loose"]!));
        }

        OcrScan? ocr = null;
        string ocrPath = Path.Combine(directory, "ocr.json");
        if (File.Exists(ocrPath))
        {
            JObject json = ReadJson(ocrPath);
            ocr = new(
                (double)json["start"]!,
                [
                    .. ((JArray)json["per_second"]!).Select(s => new OcrSample(
                        (double)s[0]!,
                        (int)s[1]!
                    )),
                ]
            );
        }

        EdgeProbeSet? probes = null;
        string edgesPath = Path.Combine(directory, "edges.json");
        if (File.Exists(edgesPath))
        {
            probes = new();
            foreach (JProperty probe in ReadJson(edgesPath).Properties())
            {
                probes.Add(
                    double.Parse(probe.Name, System.Globalization.CultureInfo.InvariantCulture),
                    new(
                        [.. ((JArray)probe.Value["cuts"]!).Select(c => (double)c)],
                        Ranges(probe.Value["black"]!)
                    )
                );
            }
        }

        ThumbnailStrip? thumbnails = null;
        string thumbsPath = Path.Combine(directory, "thumbs.npz");
        if (File.Exists(thumbsPath))
        {
            (double[] times, byte[] pixels) = NpzReader.ReadThumbnails(thumbsPath);
            thumbnails = new(times, pixels);
        }

        List<RecordedChapter> chapters = [];
        string chaptersPath = Path.Combine(directory, "chapters.json");
        if (File.Exists(chaptersPath))
        {
            chapters =
            [
                .. JArray
                    .Parse(File.ReadAllText(chaptersPath))
                    .Select(c => new RecordedChapter(
                        (string)c["title"]!,
                        (double)c["start"]!,
                        (double)c["end"]!
                    )),
            ];
        }

        string name = Path.GetFileName(directory);
        List<TimeRange> sortedSilences = [.. silences.OrderBy(s => s.Start).ThenBy(s => s.End)];
        return new(
            name,
            new(name, duration, parts, sortedSilences, black, ocr),
            probes,
            thumbnails,
            chapters
        );
    }

    private static List<TimeRange> Ranges(JToken runs) =>
        [.. ((JArray)runs).Select(r => new TimeRange((double)r[0]!, (double)r[1]!))];

    private static JObject ReadJson(string path) => JObject.Parse(File.ReadAllText(path));
}
