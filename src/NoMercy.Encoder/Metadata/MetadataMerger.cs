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

namespace NoMercy.Encoder.Metadata;

/// <summary>
/// Merges source-file stream metadata with DB-supplied metadata for copy-mode
/// encodes. Each field applies a specific precedence rule documented below.
///
/// Precedence rules:
///   Title    — source preserved unless DB has an explicit non-empty value.
///   Language — source always wins; DB never overwrites (language is intrinsic
///              to the stream content).
///   Comment  — source preserved; DB has no comment field so there is nothing
///              to merge. The SourceTrackMetadata.Comment is passed through to
///              the caller as supplemental data; MetadataInjector does not
///              emit a comment flag today.
///   IsDefault / IsForced — DB wins. Booleans are non-nullable so the DB row
///              is always considered intentional; MetadataInjector writes a
///              false/false pair as "-disposition:... 0", so a DB "not default"
///              clears a source default flag in copy mode.
///
/// Track matching:
///   Source and DB tracks are correlated by OutputIndex.
///   Source-only track (no DB row) — emitted as-is using source values.
///   DB-only track (no source row, e.g. acquired subtitle) — emitted unchanged.
/// </summary>
public class MetadataMerger : IMetadataMerger
{
    public IReadOnlyList<TrackMetadata> Merge(
        IReadOnlyList<SourceTrackMetadata> source,
        IReadOnlyList<TrackMetadata> dbTracks
    )
    {
        Dictionary<int, SourceTrackMetadata> sourceByIndex = [];
        foreach (SourceTrackMetadata s in source)
            sourceByIndex[s.OutputIndex] = s;

        Dictionary<int, TrackMetadata> dbByIndex = [];
        foreach (TrackMetadata d in dbTracks)
            dbByIndex[d.OutputIndex] = d;

        HashSet<int> allIndexes = [];
        foreach (int idx in sourceByIndex.Keys)
            allIndexes.Add(idx);
        foreach (int idx in dbByIndex.Keys)
            allIndexes.Add(idx);

        List<TrackMetadata> result = [];

        foreach (int idx in allIndexes.OrderBy(i => i))
        {
            bool hasSource = sourceByIndex.TryGetValue(idx, out SourceTrackMetadata? src);
            bool hasDb = dbByIndex.TryGetValue(idx, out TrackMetadata? db);

            if (hasSource && !hasDb)
            {
                // Source-only: no DB row — emit source values, dispositions included.
                result.Add(
                    new(
                        OutputIndex: src!.OutputIndex,
                        Kind: src.Kind,
                        Language: src.Language,
                        Title: src.Title,
                        IsDefault: src.IsDefault,
                        IsForced: src.IsForced
                    )
                );
                continue;
            }

            if (!hasSource && hasDb)
            {
                // DB-only: e.g. acquired subtitle — emit unchanged.
                result.Add(db!);
                continue;
            }

            // Both present — apply field-level precedence rules.
            string? mergedLanguage = src!.Language; // rule: source always wins

            string? mergedTitle = !string.IsNullOrEmpty(db!.Title)
                ? db.Title // rule: DB wins if non-empty
                : src.Title; // fallback to source

            bool mergedIsDefault = db.IsDefault; // rule: DB wins
            bool mergedIsForced = db.IsForced; // rule: DB wins

            result.Add(
                new(
                    OutputIndex: idx,
                    Kind: db.Kind,
                    Language: mergedLanguage,
                    Title: mergedTitle,
                    IsDefault: mergedIsDefault,
                    IsForced: mergedIsForced
                )
            );
        }

        return result;
    }
}
