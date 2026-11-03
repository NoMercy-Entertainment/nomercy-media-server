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

using NoMercy.Resources;
using Xunit;

namespace NoMercy.Tests.Resources;

/// <summary>
/// REQUIREMENT (see the XML doc on <see cref="ResourceBudgetOptions"/>): any
/// threshold at or below zero (and <c>MinFreeMemoryMb</c> at exactly zero) is
/// the documented contract for "this check is disabled". <see cref="ResourceBudgetOptions.Disabled"/>
/// must satisfy that contract, and the ordinary constructor defaults must NOT
/// satisfy it — the two states must stay distinguishable, or a caller that
/// mistakenly used a bare <c>new ResourceBudgetOptions()</c> where it meant
/// <c>Disabled</c> would silently start throttling live encode dispatch.
/// </summary>
public sealed class ResourceBudgetOptionsTests
{
    [Fact]
    public void Disabled_CpuHeadroomPercent_SatisfiesDisabledContract()
    {
        ResourceBudgetOptions.Disabled.CpuHeadroomPercent.Should().BeLessThanOrEqualTo(0);
    }

    [Fact]
    public void Disabled_GpuHeadroomPercent_SatisfiesDisabledContract()
    {
        ResourceBudgetOptions.Disabled.GpuHeadroomPercent.Should().BeLessThanOrEqualTo(0);
    }

    [Fact]
    public void Disabled_MinFreeMemoryMb_SatisfiesDisabledContract()
    {
        ResourceBudgetOptions.Disabled.MinFreeMemoryMb.Should().Be(0);
    }

    [Fact]
    public void Disabled_GpuEncodePercentPerLease_SatisfiesDisabledContract()
    {
        // A non-zero per-lease estimate would still hold back GPU leases in the
        // "every live check off" configuration.
        ResourceBudgetOptions.Disabled.GpuEncodePercentPerLease.Should().Be(0);
    }

    [Fact]
    public void Disabled_IsASingleSharedInstance()
    {
        // ResourceBudget's legacy constructor null-coalesces onto this property
        // on every call — if it allocated a new record each time that would
        // still be value-equal, but the "single shared instance" shape is what
        // the doc comment promises ("Used by ... tests that don't want to
        // model live host load"), so pin object identity, not just equality.
        ResourceBudgetOptions.Disabled.Should().BeSameAs(ResourceBudgetOptions.Disabled);
    }

    [Fact]
    public void DefaultConstructor_DoesNotSatisfyDisabledContract()
    {
        // The non-Disabled defaults must leave real headroom active — otherwise
        // every caller that forgets to pass options gets the "disabled" behavior
        // by accident instead of the documented "leave headroom" behavior.
        ResourceBudgetOptions defaults = new();

        defaults.CpuHeadroomPercent.Should().BeGreaterThan(0);
        defaults.GpuHeadroomPercent.Should().BeGreaterThan(0);
        defaults.MinFreeMemoryMb.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DefaultConstructor_HeadroomPercentagesAreValidPercentages()
    {
        // A headroom expressed as "percent of the box to leave free" that fell
        // outside 0-100 would be a meaningless threshold the live-dispatch gate
        // could never actually cross (or would always be crossed).
        ResourceBudgetOptions defaults = new();

        defaults.CpuHeadroomPercent.Should().BeInRange(0, 100);
        defaults.GpuHeadroomPercent.Should().BeInRange(0, 100);
    }

    [Fact]
    public void Constructor_ExplicitValues_AreAssignedToMatchingProperties()
    {
        ResourceBudgetOptions options = new(
            CpuHeadroomPercent: 42.5,
            GpuHeadroomPercent: 33.3,
            MinFreeMemoryMb: 2048
        );

        options.CpuHeadroomPercent.Should().Be(42.5);
        options.GpuHeadroomPercent.Should().Be(33.3);
        options.MinFreeMemoryMb.Should().Be(2048);
    }
}
