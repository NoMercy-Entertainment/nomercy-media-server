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

using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginCapabilityGuardTests
{
    [Fact]
    public void NullCaps_ImplicitlyDeclaresBaselineHooksOnly()
    {
        Assert.True(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.MediaSource));
        Assert.True(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.Metadata));
        Assert.False(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.Auth));
        Assert.False(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.ScheduledTask));
    }

    [Fact]
    public void ExplicitCaps_OnlyDeclaredHooksAllowed()
    {
        PluginCapabilities caps = new() { Hooks = ["auth"] };
        Assert.True(PluginCapabilityGuard.DeclaresHook(caps, PluginHookCapability.Auth));
        Assert.False(PluginCapabilityGuard.DeclaresHook(caps, PluginHookCapability.MediaSource));
    }

    /// <summary>
    /// The three analysis hooks are elevated, not baseline - a plugin that
    /// never says a word about them must not receive
    /// <see cref="IPluginContext.AudioTools" />, <see cref="IPluginContext.DerivedAudio" />
    /// or <see cref="IPluginContext.MusicAnalysisWriter" /> for free.
    /// </summary>
    [Fact]
    public void NullCaps_DoNotDeclareTheAnalysisHooks()
    {
        Assert.False(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.AudioTools));
        Assert.False(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.DerivedAudio));
        Assert.False(
            PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.MusicAnalysisWrite)
        );
    }

    [Fact]
    public void ExplicitCaps_DeclareTheAnalysisHooks()
    {
        PluginCapabilities caps = new()
        {
            Hooks = ["audioTools", "derivedAudio", "musicAnalysisWrite"],
        };
        Assert.True(PluginCapabilityGuard.DeclaresHook(caps, PluginHookCapability.AudioTools));
        Assert.True(PluginCapabilityGuard.DeclaresHook(caps, PluginHookCapability.DerivedAudio));
        Assert.True(
            PluginCapabilityGuard.DeclaresHook(caps, PluginHookCapability.MusicAnalysisWrite)
        );
    }
}
