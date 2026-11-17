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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk;

/// <summary>
/// A configuration file that can change one record as a single step.
/// <para>
/// Reading a record and saving it back are two calls, each taking the file's
/// lock on its own. Two changes to the same record in between could both read
/// the old copy, and the second save then wrote back a record without the
/// first change: two grants given to one plugin at once kept only one.
/// </para>
/// </summary>
public interface IPluginAtomicConfiguration : IPluginConfiguration
{
    /// <summary>
    /// Under the file's lock: reads the record of type <typeparamref name="T" />
    /// fresh from the file (a new one when it has none), applies
    /// <paramref name="change" /> and writes the record back when it returns
    /// true. The other records in the file are left as they are.
    /// <para>
    /// <paramref name="change" /> must not read or write this configuration
    /// itself; it works on the record it is handed.
    /// </para>
    /// </summary>
    void Update<T>(Func<T, bool> change)
        where T : class, new();
}
