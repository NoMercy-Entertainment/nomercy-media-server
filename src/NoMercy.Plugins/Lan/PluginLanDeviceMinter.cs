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

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Media;

namespace NoMercy.PluginSdk.Lan;

/// <summary>
/// A credential for one named device on the owner's network.
/// <para>
/// A television tuner or an HDHomeRun client cannot hold a bearer token, so
/// this is the one plugin route that does not carry one. The server mints the
/// credential, binds it to one device row, and the owner revokes it from the
/// plugin's page. A plugin never issues one. It does see the credential of a
/// device it is answering, inside <c>PluginLanCaller.BaseAddress</c>, because
/// an HDHomeRun client wants absolute addresses back; that address is for the
/// device and never for a log.
/// </para>
/// <para>
/// The store keeps a hash of the credential, never the credential: the owner
/// sees it once, when the device is added, from what <see cref="Mint" />
/// returns.
/// </para>
/// </summary>
/// <param name="tickets">
/// The media tickets, so revoking a device forgets every link it was handed.
/// Optional only for a host that serves no plugin media.
/// </param>
public class PluginLanDeviceMinter(
    IPluginLanDeviceStore store,
    TimeProvider clock,
    PluginMediaTicketMinter? tickets = null
)
{
    /// <summary>
    /// 32 bytes. Long enough that guessing it is not a way in, which matters
    /// more here than anywhere else because nothing else guards this route.
    /// </summary>
    public const int CredentialBytes = 32;

    /// <summary>
    /// One signal per device that has a request open, fired when the owner
    /// revokes it. A tuner streaming a channel holds one request for hours, and
    /// a revocation that waited for it to end would not be one.
    /// </summary>
    private readonly ConcurrentDictionary<
        (Ulid PluginId, string DeviceId),
        CancellationTokenSource
    > _revocations = new();

    public PluginLanDeviceMinted Mint(Ulid pluginId, string name)
    {
        PluginLanDeviceMinted minted = new(
            pluginId,
            Guid.NewGuid().ToString("N"),
            name,
            Base64Url(RandomNumberGenerator.GetBytes(CredentialBytes)),
            clock.GetUtcNow()
        );

        store.Add(
            new(
                minted.PluginId,
                minted.DeviceId,
                minted.Name,
                HashOf(minted.Credential),
                minted.CreatedAt,
                false
            )
        );

        return minted;
    }

    /// <summary>
    /// What the store keeps for a credential: SHA-256, base64url. Unsalted is
    /// enough here, because the credential is 32 random bytes rather than
    /// something a person chose, so there is no list of likely ones to try.
    /// </summary>
    public static string HashOf(string credential) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public PluginLanDevice? Resolve(Ulid pluginId, string deviceId, string credential)
    {
        if (store.Find(pluginId, deviceId) is not { Revoked: false } device)
            return null;

        // Compared in fixed time, because this one is compared against
        // whatever somebody on the network sends, as often as they like. Both
        // sides are hashes, so they are always the same length.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(device.CredentialHash),
            Encoding.UTF8.GetBytes(HashOf(credential))
        )
            ? device
            : null;
    }

    /// <summary>
    /// Takes a device off the owner's network: its credential stops working,
    /// and every request it still has open is cut.
    /// </summary>
    public void Revoke(Ulid pluginId, string deviceId)
    {
        store.Revoke(pluginId, deviceId);
        tickets?.ForgetDevice(pluginId, deviceId);

        if (_revocations.TryRemove((pluginId, deviceId), out CancellationTokenSource? revoked))
        {
            revoked.Cancel();
            revoked.Dispose();
        }
    }

    /// <summary>
    /// Fires when the owner revokes this device. Already fired for a device
    /// that is revoked or unknown, so a request that raced the revocation is
    /// cut rather than missed.
    /// </summary>
    public CancellationToken RevocationOf(Ulid pluginId, string deviceId)
    {
        if (!IsActive(pluginId, deviceId))
            return new(canceled: true);

        CancellationToken token = _revocations.GetOrAdd((pluginId, deviceId), _ => new()).Token;

        // Asked again after the signal exists: a revocation that landed between
        // the first look and the GetOrAdd removed nothing, so this one would
        // never fire.
        return IsActive(pluginId, deviceId) ? token : new(canceled: true);
    }

    /// <summary>Whether the device exists on this plugin and has not been revoked.</summary>
    public bool IsActive(Ulid pluginId, string deviceId) => Active(pluginId, deviceId) is not null;

    /// <summary>The device, when it exists on this plugin and has not been revoked.</summary>
    public PluginLanDevice? Active(Ulid pluginId, string deviceId) =>
        store.Find(pluginId, deviceId) is { Revoked: false } device ? device : null;

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
