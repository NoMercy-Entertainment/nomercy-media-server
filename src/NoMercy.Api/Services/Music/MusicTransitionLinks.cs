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

using NoMercy.Api.Controllers.V1.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Media;

namespace NoMercy.Api.Services.Music;

/// <summary>
/// Makes the link a listener's players fetch a transition's file at.
/// <para>
/// A plugin mints a media link only while it serves a request of the user
/// the link is for. A transition is sent by the player's own clock, with no
/// request around it, so the server mints this one itself: for that user, at
/// that moment, under the same ticket rules as every other plugin media link.
/// </para>
/// </summary>
public sealed class MusicTransitionLinks(
    PluginMediaTicketMinter minter,
    IPluginAccessResolver access
)
{
    /// <summary>
    /// The link for <paramref name="userId" />, relative to this server, or
    /// null when that user cannot see the plugin: the media route would
    /// refuse them, so they are not sent a link that cannot play.
    /// </summary>
    public string? For(MusicTransitionOffer offer, Guid userId)
    {
        if (access.Resolve(offer.PluginId, userId) == PluginAccess.None)
            return null;

        return PluginMediaProxy.UserRoute(
            offer.PluginId,
            minter.MintFile(offer.PluginId, userId, offer.File, PluginMediaProxy.TicketLifetime)
        );
    }

    /// <summary>The media type of a file by its name, as the media route serves it.</summary>
    public static string MimeOf(string file) => PluginMediaContentTypes.Of(file);
}
