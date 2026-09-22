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

namespace NoMercy.Plugin.Samples.Failures;

// Never instantiated. Present purely so the assembly contains a type that IS
// assignable to IPlugin but abstract — exercising the IsAbstract branch of the
// loader's own plugin-type reflection filter (a type it must find via
// GetTypes(), but must never try to PluginInstanceFactory.Create).
public abstract class AbstractPluginBase : IPlugin
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract Ulid Id { get; }
    public abstract Version Version { get; }

    public abstract void Initialize(IPluginContext context);

    public abstract void Dispose();
}
