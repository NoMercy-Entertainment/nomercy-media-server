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
/// One season of the research corpus: the recorded inputs, the prototype's
/// recorded matches and the prototype's own answer (analysis.json).
/// </summary>
public sealed record RecordedSeason(
    string Id,
    IReadOnlyList<RecordedEpisode> Episodes,
    IReadOnlyList<PairMatches> PrototypeMatches,
    JObject PrototypeAnalysis
)
{
    public IReadOnlyList<EpisodeSignals> Signals => [.. Episodes.Select(e => e.Signals)];

    public bool HasFingerprints =>
        Episodes.All(e => e.Signals.Fingerprint.All(p => p.Hashes.Length > 0));

    public SeasonEvidence Evidence()
    {
        SeasonEvidence evidence = new(Episodes.Count);
        for (int i = 0; i < Episodes.Count; i++)
        {
            evidence.EdgeProbes[i] = Episodes[i].EdgeProbes;
            evidence.Thumbnails[i] = Episodes[i].Thumbnails;
        }

        return evidence;
    }
}
