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
namespace NoMercy.MediaProcessing.Jobs.PaletteJobs;

public static class ColorPaletteDispatch
{
    /// <summary>
    /// Queues a color palette job without waiting: enqueueing takes the queue's global
    /// write lock, which encoder workers can hold for seconds.
    /// </summary>
    public static void QueueColorPaletteInBackground(
        this IJobDispatcher jobDispatcher,
        string type,
        string id
    ) => _ = Task.Run(() => jobDispatcher.Dispatch(new ColorPaletteJob(type, id), "palette", 1));
}
