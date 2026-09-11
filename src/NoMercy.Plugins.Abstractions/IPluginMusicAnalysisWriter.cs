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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// Writing the DJ analysis record and the stem register -
/// <see cref="IPluginMusicQuery" />'s write side. Kept a separate contract
/// rather than added to that one, the same way
/// <see cref="IPluginLibraryWriter" /> is separate from
/// <see cref="IPluginLibraryQuery" />: reading a library needs no consent,
/// writing what every other plugin's read will trust does.
/// <para>
/// Elevated - see <see cref="PluginHookCapability.MusicAnalysisWrite" /> -
/// because it is a write to data every other plugin's
/// <see cref="IPluginMusicQuery" /> can read, so a plugin declaring it can
/// shape what the rest of the platform believes about a track.
/// </para>
/// </summary>
public interface IPluginMusicAnalysisWriter
{
    /// <summary>
    /// Replaces the DJ analysis row for a track, or inserts one if none
    /// exists yet.
    /// </summary>
    Task<PluginWriteResult> UpsertDjAnalysisAsync(
        PluginTrackDjAnalysis record,
        CancellationToken ct = default
    );

    /// <summary>Adds or replaces one stem's entry in the register.</summary>
    Task<PluginWriteResult> RegisterStemAsync(PluginTrackStem stem, CancellationToken ct = default);

    /// <summary>
    /// Records that analysis was attempted and did not produce a row, so a
    /// sweep can tell "not analysed yet" from "analysed and failed" instead
    /// of retrying the same broken file for ever.
    /// </summary>
    Task<PluginWriteResult> MarkFailedAsync(
        Guid trackId,
        int djAnalyzerVersion,
        int baseAnalyzerVersion,
        string reason,
        CancellationToken ct = default
    );

    /// <summary>Removes the DJ analysis row for a track, if one exists.</summary>
    Task DeleteDjAnalysisAsync(Guid trackId, CancellationToken ct = default);
}
