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

namespace NoMercy.PluginSdk.Watchdog;

/// <summary>
/// What this stage can honestly measure from inside one process.
/// <para>
/// A plugin runs in the server's own process, so there is no per-plugin
/// counter to read: the runtime knows what the process allocated, not which
/// plugin allocated it. Rather than invent a number, this answers null, and a
/// null sample is a plugin the watchdog leaves alone.
/// </para>
/// <para>
/// This is what stage two changes. Real per-plugin measurement needs the
/// plugin somewhere the operating system can count separately, which is the
/// whole point of that stage; a sampler that guessed here would produce
/// restarts nobody could explain.
/// </para>
/// </summary>
public class PluginAssemblyResourceSampler : IPluginResourceSampler
{
    public PluginResourceSample? Sample(Ulid pluginId) => null;
}
