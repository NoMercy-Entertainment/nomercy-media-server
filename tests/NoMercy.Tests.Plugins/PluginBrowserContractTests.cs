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
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The headless browser the platform ships, and the shape that keeps it from
/// becoming process spawn under another name.
/// </summary>
public class PluginBrowserContractTests
{
    [Fact]
    public void The_context_offers_a_browser()
    {
        typeof(IPluginContext)
            .GetProperty("Browser")!
            .PropertyType.Should()
            .Be(typeof(IPluginBrowser));
    }

    [Fact]
    public void The_capability_is_medium_and_reversible()
    {
        PluginCapabilityDescriptor browser = PluginCapabilityVocabulary.ByName(
            PluginCapabilityNames.BrowserHeadless
        )!;

        browser.Trust.Should().Be(PluginTrust.Medium);
        browser.Reversible.Should().BeTrue();
        browser.Facade.Should().Be("IPluginContext.Browser");
    }

    [Fact]
    public void A_page_is_disposable_so_a_solver_cannot_leak_one()
    {
        typeof(IPluginBrowserPage).Should().Implement<IAsyncDisposable>();
    }

    [Fact]
    public void Navigating_off_the_consented_hosts_refuses()
    {
        PluginRefusal refusal = PluginRefusalMessages.BrowserNavigationBlocked(
            "Torrent Downloader 1.0.0",
            "https://tracker.example.org/"
        );

        refusal.Code.Should().Be(PluginRefusalCodes.BrowserNavigationBlocked);
        refusal.Why.Should().Contain("network.fetch");
        refusal.Fix.Should().Contain("/nomercy-plugins/capabilities/browser-headless");
    }

    [Fact]
    public void Options_carry_a_timeout_and_a_user_agent_rather_than_a_command_line()
    {
        PropertyInfo[] options = typeof(PluginBrowserOptions).GetProperties();

        options
            .Select(option => option.Name)
            .Should()
            .BeEquivalentTo(["Timeout", "UserAgent", "Referer", "Locale"]);
    }
}
