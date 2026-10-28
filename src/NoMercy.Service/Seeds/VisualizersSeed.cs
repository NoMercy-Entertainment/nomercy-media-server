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

using NoMercy.Data.Visualizers;
using NoMercy.Database;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Service.Seeds;

/// <summary>Seeds the built-in visualizers from the pinned bundle on every startup.</summary>
public static class VisualizersSeed
{
    public static async Task Init(MediaContext context)
    {
        Logger.Setup("Adding Visualizers", LogEventLevel.Verbose);

        try
        {
            await new VisualizerBundleSeeder(context).SeedAsync();
        }
        catch (Exception e)
        {
            Logger.Setup(e.Message, LogEventLevel.Fatal);
        }
    }
}
