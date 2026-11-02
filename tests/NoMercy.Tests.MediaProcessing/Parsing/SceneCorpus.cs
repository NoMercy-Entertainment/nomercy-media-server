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

using System.Text.Json;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// The release names of the public parsers (see scene-corpus.json) and the
/// differences we accept. Both files are data: nothing in them is retyped here.
/// </summary>
internal static class SceneCorpus
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<SceneCorpusEntry> Entries { get; } =
        Load<SceneCorpusEntry>("scene-corpus.json");

    public static IReadOnlyList<KnownDifference> KnownDifferences { get; } =
    [
        .. Load<KnownDifference>("scene-corpus.known-differences.json"),
        .. Load<KnownDifference>("scene-corpus.known-differences.identification.json"),
    ];

    public static IReadOnlyList<string> Sources { get; } =
    [.. Entries.Select(entry => entry.SourceRepo).Distinct().Order()];

    public static bool IsKnown(CorpusMismatch mismatch) =>
        KnownDifferences.Any(known =>
            known.Name == mismatch.Entry.Name
            && known.Field == mismatch.Field
            && known.Ours == mismatch.Ours
        );

    private static List<T> Load<T>(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Parsing", "Corpus", file);
        return JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"{file} holds no list.");
    }
}
