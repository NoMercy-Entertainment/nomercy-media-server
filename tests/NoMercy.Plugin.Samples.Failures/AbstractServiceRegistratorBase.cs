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

using Microsoft.Extensions.DependencyInjection;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Samples.Failures;

// Never instantiated. Present purely so the assembly contains a TYPE that is
// assignable to IPluginServiceRegistrator but abstract — exercising the
// IsAbstract branch of RegisterPluginServicesFromManifests' reflection filter
// (a type it must find, but must never try to Activator.CreateInstance).
public abstract class AbstractServiceRegistratorBase : IPluginServiceRegistrator
{
    public abstract void RegisterServices(IServiceCollection services);
}
