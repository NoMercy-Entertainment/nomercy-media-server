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

using System.Security.Cryptography;
using System.Text;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Lan;

/// <summary>
/// A credential for one named device on the owner's network.
/// <para>
/// A television tuner or an HDHomeRun client cannot hold a bearer token, so
/// this is the one plugin route that does not carry one. The server mints the
/// credential, binds it to one device row, and the owner revokes it from the
/// plugin's page. A plugin never issues one and never sees one.
/// </para>
/// </summary>
public class PluginLanDeviceMinter(IPluginLanDeviceStore store, TimeProvider clock)
{
    /// <summary>
    /// 32 bytes. Long enough that guessing it is not a way in, which matters
    /// more here than anywhere else because nothing else guards this route.
    /// </summary>
    public const int CredentialBytes = 32;

    public PluginLanDevice Mint(Ulid pluginId, string name)
    {
        PluginLanDevice device = new(
            pluginId,
            Guid.NewGuid().ToString("N"),
            name,
            Convert
                .ToBase64String(RandomNumberGenerator.GetBytes(CredentialBytes))
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_'),
            clock.GetUtcNow(),
            false
        );

        store.Add(device);

        return device;
    }

    public PluginLanDevice? Resolve(Ulid pluginId, string deviceId, string credential)
    {
        if (store.Find(pluginId, deviceId) is not { Revoked: false } device)
            return null;

        // Compared in fixed time, because this one is compared against
        // whatever somebody on the network sends, as often as they like.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(device.Credential),
            Encoding.UTF8.GetBytes(credential)
        )
            ? device
            : null;
    }

    public PluginRefusal? Refuse(Ulid pluginId, string deviceId, string credential)
    {
        if (Resolve(pluginId, deviceId, credential) is not null)
            return null;

        return new(
            PluginRefusalCodes.LanCredentialInvalid,
            pluginId.ToString(),
            "The device on your network could not reach the plugin.",
            "The credential in the address is one this server does not know, or one that was revoked.",
            "Add the device again on the plugin's page and copy the new address into it.",
            PluginRefusalSeverity.Blocked
        );
    }
}
