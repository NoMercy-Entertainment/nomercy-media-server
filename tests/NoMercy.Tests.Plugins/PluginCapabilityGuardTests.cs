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
        Assert.True(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.Ui));

        // Running on a schedule is baseline: it is not a permission, and what
        // the task then does needs whatever hook that work declares.
        Assert.True(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.ScheduledTask));

        Assert.False(PluginCapabilityGuard.DeclaresHook(null, PluginHookCapability.Auth));
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

    [Fact]
    public void HasWidened_SameCapabilities_IsFalse()
    {
        PluginCapabilities caps = new() { Hooks = ["mediaSource"] };
        Assert.False(PluginCapabilityGuard.HasWidened(caps, caps));
    }

    [Fact]
    public void HasWidened_NarrowerCapabilities_IsFalse()
    {
        PluginCapabilities consented = new() { Hooks = ["mediaSource", "metadata"] };
        PluginCapabilities current = new() { Hooks = ["mediaSource"] };
        Assert.False(PluginCapabilityGuard.HasWidened(consented, current));
    }

    [Fact]
    public void HasWidened_NewHook_IsTrue()
    {
        PluginCapabilities consented = new() { Hooks = ["mediaSource"] };
        PluginCapabilities current = new() { Hooks = ["mediaSource", "auth"] };
        Assert.True(PluginCapabilityGuard.HasWidened(consented, current));
    }

    [Fact]
    public void HasWidened_RestTurnedOn_IsTrue()
    {
        PluginCapabilities consented = new() { Hooks = ["ui"] };
        PluginCapabilities current = new() { Hooks = ["ui"], Rest = true };
        Assert.True(PluginCapabilityGuard.HasWidened(consented, current));
    }

    /// <summary>
    /// Opening an endpoint to callers with no token is the largest thing a
    /// plugin can ask for and it was not compared at all, so a plugin could
    /// update from "everyone needs a token" to "nobody does" on a consent the
    /// owner gave to the first of those.
    /// </summary>
    [Fact]
    public void HasWidened_RestOpenedToAnonymousCallers_IsTrue()
    {
        PluginCapabilities consented = new() { Hooks = ["ui"], Rest = true };
        PluginCapabilities current = new()
        {
            Hooks = ["ui"],
            Rest = true,
            RestAnonymous = true,
        };
        Assert.True(PluginCapabilityGuard.HasWidened(consented, current));
    }

    [Fact]
    public void HasWidened_RestClosedToAnonymousCallers_IsFalse()
    {
        PluginCapabilities consented = new()
        {
            Hooks = ["ui"],
            Rest = true,
            RestAnonymous = true,
        };
        PluginCapabilities current = new() { Hooks = ["ui"], Rest = true };
        Assert.False(PluginCapabilityGuard.HasWidened(consented, current));
    }

    [Fact]
    public void HasWidened_NullConsented_AnonymousRest_IsTrue()
    {
        PluginCapabilities current = new() { Hooks = ["ui"], RestAnonymous = true };
        Assert.True(PluginCapabilityGuard.HasWidened(null, current));
    }

    [Fact]
    public void HasWidened_NewNetworkHost_IsTrue()
    {
        PluginCapabilities consented = new()
        {
            Hooks = ["ui"],
            Network = new() { Hosts = ["a.example.com"] },
        };
        PluginCapabilities current = new()
        {
            Hooks = ["ui"],
            Network = new() { Hosts = ["a.example.com", "b.example.com"] },
        };
        Assert.True(PluginCapabilityGuard.HasWidened(consented, current));
    }

    [Fact]
    public void HasWidened_NullConsented_ElevatedCurrent_IsTrue()
    {
        PluginCapabilities current = new() { Hooks = ["auth"] };
        Assert.True(PluginCapabilityGuard.HasWidened(null, current));
    }

    [Fact]
    public void HasWidened_NullConsented_BaselineCurrent_IsFalse()
    {
        PluginCapabilities current = new() { Hooks = ["mediaSource", "ui"] };
        Assert.False(PluginCapabilityGuard.HasWidened(null, current));
    }
}
