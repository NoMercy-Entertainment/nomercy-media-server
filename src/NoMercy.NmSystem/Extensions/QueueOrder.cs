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
namespace NoMercy.NmSystem.Extensions;

public static class QueueOrder
{
    /// <summary>
    /// The items that play after the current one: everything past it, then everything
    /// before it. The current item itself is left out. When no item matches, the whole
    /// list is returned in its order.
    /// </summary>
    public static List<T> QueueAfter<T>(this List<T> items, Predicate<T> isCurrent)
    {
        int index = items.FindIndex(isCurrent);
        if (index == -1)
            return [.. items];

        return [.. items.Skip(index + 1), .. items.Take(index)];
    }
}
