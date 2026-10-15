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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Everything a plugin can do with a stream it did not author.
/// </summary>
public interface IPluginMedia
{
    IPluginMediaProxy Proxy { get; }
    IPluginMediaTranscode Transcode { get; }
    IPluginMediaRemux Remux { get; }
    IPluginMediaLive Live { get; }
    IPluginRecorder Record { get; }
}

/// <summary>
/// Puts the host between a client and an upstream.
/// <para>
/// A plugin never builds a playable URL itself. The radio plugin did, by
/// keeping the viewer's bearer token in a mutable static and appending it to
/// every stream URL, which put a live credential in every log, history entry
/// and shared link. The host mints the URL instead, bound to one user and one
/// session, and the upstream's own credentials stay on the server.
/// </para>
/// </summary>
public interface IPluginMediaProxy
{
    Task<PluginMediaUrl> MintAsync(PluginProxyRequest request, CancellationToken ct = default);

    /// <summary>
    /// The same for a still image, so a cover behind provider auth reaches a
    /// client without that client holding the provider's credentials.
    /// </summary>
    Task<PluginMediaUrl> MintImageAsync(Uri source, CancellationToken ct = default);

    /// <summary>
    /// A link to a file the plugin wrote into its own folder, such as a
    /// recording: <c>context.Storage.Private</c>, which is also
    /// <c>DataFolderPath</c>. The path is relative to that folder, with
    /// forward slashes. An absolute path, a <c>..</c> segment, and a link
    /// that leads out of the folder are refused with
    /// <see cref="PluginRefusalCodes.FileOutsideGrant" />.
    /// <para>
    /// The server serves the file itself, with seeking, to the one account
    /// that asked, under the same ticket rules as <see cref="MintAsync" />.
    /// A file that is still being written can already be played. Added in
    /// ABI 12.3.
    /// </para>
    /// </summary>
    Task<PluginMediaUrl> MintFileAsync(string path, CancellationToken ct = default) =>
        Task.FromException<PluginMediaUrl>(
            new PluginRefusedException(
                PluginRefusalMessages.FacadeNotOnThisHost(
                    string.Empty,
                    "IPluginContext.Media.Proxy.MintFileAsync"
                )
            )
        );
}

/// <summary>
/// Re-encodes, when the source cannot be repackaged as it stands.
/// </summary>
public interface IPluginMediaTranscode
{
    Task<PluginMediaUrl> StartAsync(PluginRemuxRequest request, CancellationToken ct = default);
}

/// <summary>
/// Repackages without re-encoding.
/// <para>
/// Refuses with <see cref="PluginRefusalCodes.RemuxUnsupportedCodec"/> at
/// degraded severity when the codecs cannot survive a repackage, so the caller
/// can fall back to <see cref="IPluginMediaTranscode"/> rather than serving a
/// file no client will play.
/// </para>
/// </summary>
public interface IPluginMediaRemux
{
    Task<PluginMediaUrl> StartAsync(PluginRemuxRequest request, CancellationToken ct = default);
}
