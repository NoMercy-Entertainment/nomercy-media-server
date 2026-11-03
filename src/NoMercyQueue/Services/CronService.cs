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

namespace NoMercyQueue.Services;

public class CronService
{
    public static DateTime GetNextOccurrence(string cronExpression, DateTime baseTime) =>
        CronSchedule.Parse(cronExpression).GetNextOccurrence(baseTime);

    public static bool ShouldRun(string cronExpression, DateTime lastRun, DateTime currentTime)
    {
        DateTime nextRun = GetNextOccurrence(cronExpression, lastRun);
        return currentTime >= nextRun;
    }
}
