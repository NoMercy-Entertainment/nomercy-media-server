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
using NoMercy.Data.Plugins;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The tokens a plugin writes in its filter text: model files by bare name,
/// a plugin's own file by an escaped absolute path that never leaves the
/// plugin's data folder (#620).
/// </summary>
[Trait("Category", "Unit")]
public class PluginGraphTokensTests
{
    private static readonly string Folder = OperatingSystem.IsWindows()
        ? @"C:\NoMercy\plugins\lyrics creator"
        : "/srv/nomercy/plugins/lyrics creator";

    [Fact]
    public void Model_tokens_become_the_bare_file_names()
    {
        ResolvedGraphText resolved = PluginGraphTokens.Resolve(
            "whisper=model={whisperModel}:vad_model={vadModel},stemsplit=model={stemsModel}",
            Folder
        );

        resolved.Refusal.Should().BeNull();
        resolved
            .Text.Should()
            .Be(
                $"whisper=model={AppFiles.WhisperModel}.bin:vad_model={AppFiles.VadModel}.bin,stemsplit=model={AppFiles.StemsplitModel}.gguf"
            );
    }

    [Fact]
    public void A_plugin_file_becomes_an_absolute_path_escaped_for_both_filtergraph_levels()
    {
        ResolvedGraphText resolved = PluginGraphTokens.Resolve(
            "whisper=prompt_file=" + PluginFilterGraphTokens.PluginFile("prompts/song one.txt"),
            Folder
        );

        resolved.Refusal.Should().BeNull();
        string expected = OperatingSystem.IsWindows()
            ? @"C\\:/NoMercy/plugins/lyrics creator/prompts/song one.txt"
            : "/srv/nomercy/plugins/lyrics creator/prompts/song one.txt";
        resolved.Text.Should().Be("whisper=prompt_file=" + expected);
    }

    [Theory]
    [InlineData("../other-plugin/secret.txt")]
    [InlineData("prompts/../../secret.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void A_plugin_file_outside_the_data_folder_is_refused(string relativePath)
    {
        ResolvedGraphText resolved = PluginGraphTokens.Resolve(
            PluginFilterGraphTokens.PluginFile(relativePath),
            Folder
        );

        resolved.Refusal.Should().NotBeNull();
    }

    [Fact]
    public void A_windows_absolute_path_is_refused()
    {
        ResolvedGraphText resolved = PluginGraphTokens.Resolve(
            PluginFilterGraphTokens.PluginFile(@"C:\Windows\win.ini"),
            Folder
        );

        resolved.Refusal.Should().NotBeNull();
    }

    [Fact]
    public void A_plugin_file_without_a_known_data_folder_is_refused()
    {
        ResolvedGraphText resolved = PluginGraphTokens.Resolve(
            PluginFilterGraphTokens.PluginFile("prompts/a.txt"),
            dataFolderPath: null
        );

        resolved.Refusal.Should().Contain("data folder");
    }

    [Fact]
    public void Text_without_tokens_is_left_alone()
    {
        PluginGraphTokens
            .Resolve("volume=1", null)
            .Should()
            .Be(new ResolvedGraphText("volume=1", null));
    }
}
