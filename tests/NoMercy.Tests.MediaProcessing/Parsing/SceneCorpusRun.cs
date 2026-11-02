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
using MovieFileLibrary;
using NoMercy.MediaProcessing.Files.Parsing;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// The whole corpus through the production resolver, once per run, in both
/// modes. Set NM_CORPUS_DUMP to a file path to get every mismatch as JSON.
/// </summary>
internal static class SceneCorpusRun
{
    private static readonly Lazy<IReadOnlyList<CorpusMismatch>> Lazily = new(Run);

    public static IReadOnlyList<CorpusMismatch> Mismatches => Lazily.Value;

    private static IReadOnlyList<CorpusMismatch> Run()
    {
        FilenameResolver resolver = new(ProductionFilenameParser.Pipeline());
        List<CorpusMismatch> mismatches = [];

        foreach (SceneCorpusEntry entry in SceneCorpus.Entries)
        {
            foreach (CorpusMode mode in Enum.GetValues<CorpusMode>())
            {
                CorpusInput input = CorpusInput.For(entry, mode);
                MovieFile parsed = resolver
                    .Resolve(input.FileName, input.Directory, input.Path, input.LibraryType)
                    .Parsed;
                mismatches.AddRange(SceneCorpusComparer.Compare(entry, mode, parsed));
            }
        }

        string? dump = Environment.GetEnvironmentVariable("NM_CORPUS_DUMP");
        if (!string.IsNullOrEmpty(dump))
        {
            File.WriteAllText(
                dump,
                JsonSerializer.Serialize(
                    mismatches.Select(m => new
                    {
                        m.Entry.Name,
                        m.Entry.Kind,
                        Source = m.Entry.SourceRepo,
                        m.Entry.Folder,
                        Mode = m.Mode.ToString(),
                        m.Field,
                        m.Ours,
                        m.Theirs,
                    })
                )
            );
        }

        return mismatches;
    }
}
