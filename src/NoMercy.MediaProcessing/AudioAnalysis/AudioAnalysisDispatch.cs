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

using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.MediaProcessing.AudioAnalysis;

/// <summary>
/// The one line an import runs for every track it stores. A small named type
/// rather than three lines inside the import loop, because the rule it carries
/// — the library's opt-out — has to read the same wherever a track is written.
/// </summary>
public static class AudioAnalysisDispatch
{
    /// <summary>
    /// Queues analysis for a track an import has just written, unless the
    /// library opted out.
    /// <para>
    /// This is the layer that reaches a brand-new library. The scan-completed
    /// hook cannot: a music scan only dispatches the import jobs and then
    /// announces itself, so no <c>Track</c> row exists yet when it runs.
    /// </para>
    /// <para>
    /// Costs nothing on a rescan. The queue drops an identical payload, and
    /// <see cref="MusicAnalysisJob" /> returns immediately for a track that
    /// already carries a verdict from the current analyzer — so a rescan of an
    /// analysed library is one cheap no-op per track, never a re-analysis.
    /// </para>
    /// </summary>
    public static void AfterStore(IJobDispatcher dispatcher, Library library, Guid trackId)
    {
        if (!library.AnalyzeAudio)
        {
            return;
        }

        dispatcher.Dispatch(new MusicAnalysisJob { TrackId = trackId });
    }
}
