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
/// One response per platform, declared rather than branched.
///
/// A plugin that answers three screens with a switch statement rewrites the same
/// switch on every route, and the surface it forgot falls through to whichever
/// branch happened to be last. Declaring the responses puts the platforms beside
/// each other where a missing one is visible, and makes the fallback a decision
/// rather than an accident.
///
/// The components themselves are untouched by this. A television gets a
/// different tree, not a different component vocabulary.
/// </summary>
public class PluginSurfaceViews
{
    /// <summary>A pointer and a keyboard.</summary>
    public PluginView? Web { get; init; }

    /// <summary>A thumb on a small screen.</summary>
    public PluginView? Mobile { get; init; }

    /// <summary>A remote at four metres.</summary>
    public PluginView? Tv { get; init; }

    /// <summary>
    /// What a surface with nothing of its own gets.
    ///
    /// Required, because the alternative is a platform that renders nothing at
    /// all, and a blank page reads as a broken plugin rather than as one that
    /// was never adapted.
    /// </summary>
    public required PluginView Fallback { get; init; }

    /// <summary>The response for one surface.</summary>
    public PluginView For(string surface)
    {
        return surface switch
        {
            PluginSurface.Web => Web ?? Fallback,
            PluginSurface.Mobile => Mobile ?? Fallback,
            PluginSurface.Tv => Tv ?? Fallback,
            _ => Fallback,
        };
    }

    /// <summary>Which surfaces this plugin actually adapted, for its author.</summary>
    public IEnumerable<string> Adapted()
    {
        if (Web is not null)
            yield return PluginSurface.Web;
        if (Mobile is not null)
            yield return PluginSurface.Mobile;
        if (Tv is not null)
            yield return PluginSurface.Tv;
    }
}
