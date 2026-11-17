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
using NoMercy.PluginSdk;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A permissions page that lists capabilities and says nothing about isolation
/// invites the reading that refusing one contains the plugin. In stage one it
/// does not.
/// </summary>
public class PluginIsolationLevelTests
{
    [Fact]
    public void Stage_one_reports_in_process()
    {
        PluginIsolationLevel.Current.Should().Be("in-process");
    }

    [Fact]
    public void The_notice_is_a_key_the_clients_translate()
    {
        PluginIsolationLevel.NoticeKey.Should().Be("plugins.permissions.in_process_notice");
        PluginIsolationLevel
            .NoticeKey.Should()
            .NotContain(" ", "a sentence here is one no translator can reach");
    }

    [Fact]
    public void The_english_source_is_true_wherever_the_plugin_runs()
    {
        PluginIsolationLevel
            .EnglishNotice.Should()
            .Be(
                "Permissions are checked on every call. They decide what this plugin may reach, not how far it is kept apart from the server."
            );

        // The page shows it for a plugin in its own process too (#68).
        PluginIsolationLevel.EnglishNotice.Should().NotContain("inside the server");
    }

    [Fact]
    public void The_notice_says_permissions_are_checked_rather_than_promising_a_sandbox()
    {
        PluginIsolationLevel.EnglishNotice.Should().Contain("checked");
        PluginIsolationLevel
            .EnglishNotice.Should()
            .NotContain(
                "sandbox",
                "a word that promises containment is the wrong picture to give an owner"
            );
    }
}
