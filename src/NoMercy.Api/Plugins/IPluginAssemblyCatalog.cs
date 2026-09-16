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

using System.Reflection;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Which controllers came from a plugin, and which plugin.
/// <para>
/// The route convention runs over every controller in the application, so it
/// needs to tell a plugin's from the server's own. Keyed on the assembly rather
/// than a naming convention or an attribute: the assembly is what the loader
/// actually knows, and a plugin cannot fake being the server by naming a class
/// well.
/// </para>
/// </summary>
public interface IPluginAssemblyCatalog
{
    /// <summary>The plugin an assembly belongs to, or null when it is not one.</summary>
    Ulid? OwnerOf(Assembly assembly);

    /// <summary>
    /// Whether this plugin's manifest asked for a REST surface that answers
    /// without a token.
    /// <para>
    /// False by default, and false for a catalogue that cannot answer: an
    /// endpoint whose intent the host is unsure of is shut, never open.
    /// </para>
    /// </summary>
    bool AllowsAnonymousRest(Assembly assembly) => false;
}
