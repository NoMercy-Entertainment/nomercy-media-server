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

using NoMercy.PluginSdk.Verification;

namespace NoMercy.PluginSdk.Approval;

/// <summary>
/// Decides, before any assembly is mapped, whether a plugin folder found at
/// boot may load or waits for the owner.
/// <para>
/// A marketplace install or a dashboard upload is recorded by the server as
/// it lands, so a folder the server never recorded was put there by hand:
/// no repository checksum, no signature, nobody's yes. It is held. The owner
/// approves it per plugin, and the approval names the digest of the files it
/// covered, so a folder that changes afterwards is held again rather than
/// riding the old yes.
/// </para>
/// </summary>
internal sealed class PluginDropGate(IPluginApprovalStore approvals)
{
    public const string NeverApproved =
        "This plugin was put in the plugins folder by hand, not installed by the server. "
        + "It is not loaded until you approve it.";

    public const string ChangedSinceApproval =
        "This plugin's files changed after you approved them. "
        + "It is not loaded until you approve it again.";

    /// <summary>
    /// Why the folder is held, or null when it may load.
    /// <paramref name="recordedByServer"/> is whether the folder is in the
    /// recorded set the server keeps outside every plugin folder.
    /// </summary>
    public async Task<string?> WhyHeldAsync(
        string folder,
        bool recordedByServer,
        CancellationToken ct = default
    )
    {
        PluginApproval? approval = approvals.Find(Path.GetFileName(folder));

        if (approval is null)
            return recordedByServer ? null : NeverApproved;

        string digest = await PluginFileManifest.DigestAsync(folder, ct);

        return string.Equals(digest, approval.Digest, StringComparison.Ordinal)
            ? null
            : ChangedSinceApproval;
    }
}
