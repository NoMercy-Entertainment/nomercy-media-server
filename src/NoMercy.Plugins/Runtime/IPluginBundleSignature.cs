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

namespace NoMercy.Plugins.Runtime;

/// <summary>
/// Whether this plugin's bundle came from the marketplace with a signature
/// the server checked.
/// <para>
/// The one question native code asks. Managed code can be refused as it runs;
/// native code cannot be inspected or stopped once it is in the process, so
/// its gate is who built the bundle, and no capability the owner grants lifts
/// it.
/// </para>
/// </summary>
public interface IPluginBundleSignature
{
    bool IsMarketplaceSigned(Ulid pluginId);
}

/// <summary>
/// What a host that wired no signature stage answers: nothing is signed.
/// <para>
/// Refusing is the safe default here and the only honest one. A host that
/// cannot check a signature does not know the bundle is safe; it knows it
/// cannot tell.
/// </para>
/// </summary>
public sealed class NothingIsSigned : IPluginBundleSignature
{
    public bool IsMarketplaceSigned(Ulid pluginId) => false;
}
