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

using FluentAssertions;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.OutOfProcess;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What a plugin would lose in a process of its own, read from its manifest
/// (issue #68).
/// </summary>
[Trait("Category", "Unit")]
public class PluginIsolationFitTests
{
    private readonly PluginIsolationFit _fit = new();

    [Fact]
    public void APluginThatOnlyUsesCarriedFacades_CanRunOutOfProcess()
    {
        PluginIsolationVerdict verdict = _fit.For(
            new PluginCapabilities
            {
                Hooks = [PluginHookCapability.LibraryWrite, PluginHookCapability.Storage],
            }
        );

        verdict.CanRunOutOfProcess.Should().BeTrue();
        verdict.Facades.Should().BeEmpty();
        verdict.Hooks.Should().BeEmpty();
        verdict.Controllers.Should().BeFalse();
    }

    [Fact]
    public void AMusicAnalysisPluginWithRestButtons_NamesEveryFacadeAndItsControllers()
    {
        PluginIsolationVerdict verdict = _fit.For(
            new PluginCapabilities
            {
                Hooks =
                [
                    PluginHookCapability.AudioTools,
                    PluginHookCapability.DerivedAudio,
                    PluginHookCapability.MusicAnalysisWrite,
                ],
                Rest = true,
            }
        );

        verdict.CanRunOutOfProcess.Should().BeFalse();
        verdict
            .Facades.Should()
            .BeEquivalentTo(
                nameof(IPluginContext.AudioTools),
                nameof(IPluginContext.DerivedAudio),
                nameof(IPluginContext.MusicAnalysisWriter)
            );
        verdict.Controllers.Should().BeTrue();
    }

    [Fact]
    public void TheEncoderHook_NamesTheJobsFacadeThatTravelsWithIt()
    {
        PluginIsolationVerdict verdict = _fit.For(
            new PluginCapabilities { Hooks = [PluginHookCapability.Encoder] }
        );

        verdict
            .Facades.Should()
            .BeEquivalentTo(nameof(IPluginContext.Encoder), nameof(IPluginContext.Jobs));
    }

    [Fact]
    public void AnEntryPointHook_IsNamedBecauseTheServerNeverCallsItElsewhere()
    {
        PluginIsolationVerdict verdict = _fit.For(
            new PluginCapabilities { Hooks = ["UI", PluginHookCapability.Auth] }
        );

        verdict.Hooks.Should().BeEquivalentTo(PluginHookCapability.Auth, PluginHookCapability.Ui);
        verdict.Facades.Should().BeEmpty();
    }

    [Fact]
    public void AManifestWithNoCapabilities_CountsAsDeclaringTheBaselineHooks()
    {
        PluginIsolationVerdict verdict = _fit.For(null);

        verdict
            .Hooks.Should()
            .BeEquivalentTo(
                PluginHookCapability.MediaSource,
                PluginHookCapability.Metadata,
                PluginHookCapability.ScheduledTask,
                PluginHookCapability.Ui
            );
        verdict.Controllers.Should().BeFalse();
    }
}
