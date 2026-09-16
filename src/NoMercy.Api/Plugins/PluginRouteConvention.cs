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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Puts every plugin controller under <c>api/plugins/{pluginId}</c>, and makes
/// it ask for a token.
/// <para>
/// A plugin does not get to choose where its routes land. Left to itself it
/// would eventually claim <c>api/v1/movies</c> — by accident or otherwise — and
/// shadow the server's own endpoint for every client on the network. The prefix
/// is applied here, from the assembly the controller came from, so a plugin
/// cannot opt out of it by writing a different <c>[Route]</c>.
/// </para>
/// <para>
/// Authorization is imposed the same way and for the same reason. No
/// <c>[Authorize]</c> reached a plugin route before, so whether a plugin
/// endpoint asked for a token was the plugin author's decision, on the owner's
/// server, against the owner's users. Every plugin route now carries the
/// server's own policy; a plugin that needs an open endpoint declares
/// <c>restAnonymous</c> in its manifest, where the owner reads it before
/// consenting, and an <c>[AllowAnonymous]</c> written in plugin code is taken
/// back off.
/// </para>
/// </summary>
public class PluginRouteConvention(IPluginAssemblyCatalog catalog) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        foreach (ControllerModel controller in application.Controllers)
        {
            if (!typeof(PluginControllerBase).IsAssignableFrom(controller.ControllerType))
                continue;

            Ulid? owner = catalog.OwnerOf(controller.ControllerType.Assembly);

            if (owner is null)
                continue;

            // The literal id, not a route parameter. With a parameter every
            // plugin controller would share one template, so two plugins each
            // defining "status" would be an ambiguous match, and a client could
            // reach one plugin's controller through another plugin's path.
            // Versioned like every other endpoint the clients call. Left
            // unversioned the plugin surface could never be versioned later
            // without breaking plugins already in the field.
            AttributeRouteModel prefix = new(
                new RouteAttribute($"api/v{{version:apiVersion}}/plugins/{owner}")
            );

            // Carried as a route value so the base class can read it. Sourced
            // from the assembly the controller came from, never from the URL,
            // which is what makes it something a caller cannot lie about.
            controller.RouteValues["pluginId"] = owner.ToString();

            foreach (SelectorModel selector in controller.Selectors)
            {
                selector.AttributeRouteModel = selector.AttributeRouteModel is null
                    ? prefix
                    : AttributeRouteModel.CombineAttributeRouteModel(
                        prefix,
                        selector.AttributeRouteModel
                    );
            }

            Authorize(controller, catalog.AllowsAnonymousRest(controller.ControllerType.Assembly));
        }
    }

    /// <summary>
    /// Writes the host's answer over the plugin's. Applied to the controller
    /// and to every action selector, because either level can carry endpoint
    /// metadata and only both together leave no route undecided.
    /// </summary>
    private static void Authorize(ControllerModel controller, bool anonymous)
    {
        if (!anonymous)
            RemoveAnonymous(controller);

        object decision = anonymous ? new AllowAnonymousAttribute() : new AuthorizeAttribute();

        foreach (SelectorModel selector in controller.Selectors)
            selector.EndpointMetadata.Add(decision);

        foreach (ActionModel action in controller.Actions)
        foreach (SelectorModel selector in action.Selectors)
            selector.EndpointMetadata.Add(decision);
    }

    /// <summary>
    /// Takes back an <c>[AllowAnonymous]</c> the plugin wrote for itself. Which
    /// endpoints are open is the owner's decision, declared in the manifest;
    /// leaving the attribute in place would let a plugin overrule it in code.
    /// </summary>
    private static void RemoveAnonymous(ControllerModel controller)
    {
        RemoveAnonymousFilters(controller.Filters);

        foreach (SelectorModel selector in controller.Selectors)
            RemoveAnonymousMetadata(selector.EndpointMetadata);

        foreach (ActionModel action in controller.Actions)
        {
            RemoveAnonymousFilters(action.Filters);

            foreach (SelectorModel selector in action.Selectors)
                RemoveAnonymousMetadata(selector.EndpointMetadata);
        }
    }

    private static void RemoveAnonymousFilters(IList<IFilterMetadata> filters)
    {
        for (int index = filters.Count - 1; index >= 0; index--)
        {
            if (filters[index] is IAllowAnonymous)
                filters.RemoveAt(index);
        }
    }

    private static void RemoveAnonymousMetadata(IList<object> metadata)
    {
        for (int index = metadata.Count - 1; index >= 0; index--)
        {
            if (metadata[index] is IAllowAnonymous)
                metadata.RemoveAt(index);
        }
    }
}
