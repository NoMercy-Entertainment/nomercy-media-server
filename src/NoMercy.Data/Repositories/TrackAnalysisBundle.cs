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

using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Repositories;

/// <summary>
/// The rows behind one track-analysis response: the base measurements, the
/// automix plugin's DJ record, and the stem files — one Ok/absent list each,
/// from three separate tables that are never invalidated by the same write.
/// </summary>
public sealed record TrackAnalysisBundle(
    List<TrackAudioAnalysis> Analysis,
    List<TrackDjAnalysis> Dj,
    List<TrackStem> Stems
);
