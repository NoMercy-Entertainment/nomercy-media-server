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
/// One DJ analysis row a plugin's own run marked failed, as
/// <see cref="IPluginMusicQuery.GetFailedDjAnalysisAsync" /> reports it. The
/// "needs DJ analysis" worklist deliberately leaves these tracks out for as
/// long as the analyzer version stands, so a plugin that wants to show why a
/// track failed, or to try it again after the cause is fixed, has to be able
/// to see them; releasing one is <c>IPluginMusicAnalysisWriter.DeleteDjAnalysisAsync</c>.
/// </summary>
/// <param name="TrackId"><c>Guid</c>, matching <c>PluginTrackDjAnalysis.TrackId</c>.</param>
/// <param name="BaseAnalyzerVersion">The base analysis the failed run worked from.</param>
/// <param name="Reason">
/// The reason the plugin gave <c>MarkFailedAsync</c>, as stored (at most 1024
/// characters). Empty when the row carries none.
/// </param>
/// <param name="FailedAt">When the row was marked, in UTC.</param>
public sealed record PluginTrackDjFailure(
    Guid TrackId,
    int BaseAnalyzerVersion,
    string Reason,
    DateTimeOffset FailedAt
);
