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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Being told when a library changes, rather than asking.
/// <para>
/// A plugin that reacts to new media used to poll the library on a timer. On a
/// large library that is a full read every few minutes to notice one row, and
/// the owner sees the disk spin for nothing.
/// </para>
/// </summary>
public interface IPluginLibraryWatch
{
    /// <summary>
    /// Watches one library. Disposing the handle stops the subscription, and
    /// the host disposes whatever a plugin leaves behind when it stops.
    /// </summary>
    IDisposable Subscribe(LibraryId library, Action<PluginLibraryChange> onChange);
}
