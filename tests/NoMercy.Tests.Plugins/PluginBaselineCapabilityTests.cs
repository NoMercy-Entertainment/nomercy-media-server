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

using System.Reflection;
using FluentAssertions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// "Baseline" - the hooks harmless enough to run without the owner being asked
/// - was written out three times and the three lists did not agree. A plugin
/// that only runs a scheduled task was in none of them, so it installed
/// Disabled waiting for a consent prompt that described nothing dangerous.
/// </summary>
public class PluginBaselineCapabilityTests
{
    private static readonly PluginConsentService Consent = new(new InMemoryConsentStore());

    private static IEnumerable<string> EveryDeclaredHook() =>
        typeof(PluginHookCapability)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    [Fact]
    public void A_plugin_that_only_runs_a_scheduled_task_needs_no_consent_prompt()
    {
        // Running on a schedule is not a permission - what the task then does
        // is, and that is whatever other hook it declares beside this one.
        Consent
            .IsBaseline(new() { Hooks = [PluginHookCapability.ScheduledTask] })
            .Should()
            .BeTrue();
    }

    [Fact]
    public void A_scheduled_task_beside_an_elevated_hook_is_still_not_baseline()
    {
        Consent
            .IsBaseline(
                new()
                {
                    Hooks = [PluginHookCapability.ScheduledTask, PluginHookCapability.LibraryWrite],
                }
            )
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// The two places that answer "is this hook baseline" read the same set, so
    /// they cannot drift apart again.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeclaredHooks))]
    public void Both_consumers_answer_from_the_one_baseline_set(string hook)
    {
        bool baseline = PluginHookCapability.Baseline.Contains(hook);

        Consent.IsBaseline(new() { Hooks = [hook] }).Should().Be(baseline);
        PluginCapabilityGuard.DeclaresHook(null, hook).Should().Be(baseline);
    }

    /// <summary>
    /// A hook in neither set is one nobody classified, and it would take the
    /// silent answer of whichever list was checked first.
    /// </summary>
    [Theory]
    [MemberData(nameof(DeclaredHooks))]
    public void Every_hook_is_baseline_or_elevated_and_never_both(string hook)
    {
        bool baseline = PluginHookCapability.Baseline.Contains(hook);
        bool elevated = PluginHookCapability.Elevated.Contains(hook);

        (baseline ^ elevated).Should().BeTrue($"{hook} must be classified exactly once");
    }

    public static TheoryData<string> DeclaredHooks()
    {
        TheoryData<string> data = [];

        foreach (string hook in EveryDeclaredHook())
            data.Add(hook);

        return data;
    }
}
