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

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Plugins;

namespace NoMercy.Service.Configuration;

public static partial class ServiceConfiguration
{
    /// <summary>
    /// Lets a plugin own REST endpoints without a per-plugin edit anywhere in
    /// the server.
    /// <para>
    /// The part manager is only reachable through the builder <c>AddControllers</c>
    /// returns, and only during service configuration — after the provider is
    /// built there is no way to get one. Plugins load later, so the registrar
    /// captures it here and attaches parts when they arrive.
    /// </para>
    /// </summary>
    private static void ConfigurePluginMvc(IServiceCollection services, IMvcBuilder mvc)
    {
        // Constructed rather than resolved, because the convention below needs
        // it before any provider exists. The same object is registered so the
        // running server's registrar and the one the convention reads are one
        // and the same — two would mean a plugin's controllers were attached to
        // MVC while the convention still considered them the server's own, and
        // its routes would land unprefixed.
        PluginActionDescriptorChangeProvider changeProvider = new();
        PluginApplicationPartRegistrar registrar = new(
            mvc.PartManager,
            changeProvider,
            NullLogger<PluginApplicationPartRegistrar>.Instance
        );

        services.AddSingleton(registrar);
        services.AddSingleton<IPluginAssemblyCatalog>(registrar);
        services.AddSingleton<IActionDescriptorChangeProvider>(changeProvider);

        services.Configure<MvcOptions>(options =>
        {
            options.Conventions.Add(new PluginRouteConvention(registrar));
            options.Filters.Add<PluginControllerCapabilityFilter>();

            // After the capability filter, so a request to a plugin that is not
            // serving REST at all is a 404 rather than a lesson about tokens.
            options.Filters.Add<PluginQueryTokenFilter>();
        });
    }
}
