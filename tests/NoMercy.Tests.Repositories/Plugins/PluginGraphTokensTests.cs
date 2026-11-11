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
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;

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
    public void A_path_inside_the_data_folder_resolves_to_its_full_path_unescaped()
    {
        // The same check a token gets, for a path that is handed to ffmpeg
        // as an argument of its own and so needs no filtergraph escaping.
        ResolvedPluginPath resolved = PluginGraphTokens.ResolvePluginPath(
            "renders/mix.opus",
            Folder,
            "an output path"
        );

        resolved.Refusal.Should().BeNull();
        resolved.FullPath.Should().Be(Path.GetFullPath(Path.Combine(Folder, "renders/mix.opus")));
    }

    [Theory]
    [InlineData("../mix.opus", "plugin file '../mix.opus' leaves the plugin's data folder")]
    [InlineData(
        "/var/mix.opus",
        "plugin file '/var/mix.opus' is an absolute path; only a path inside the plugin's data folder is allowed"
    )]
    [InlineData(".", "plugin file '.' leaves the plugin's data folder")]
    [InlineData(" ", "an output path names no file")]
    [InlineData(null, "an output path names no file")]
    public void A_path_outside_the_data_folder_is_refused_in_the_words_a_token_gets(
        string? relativePath,
        string refusal
    )
    {
        ResolvedPluginPath resolved = PluginGraphTokens.ResolvePluginPath(
            relativePath,
            Folder,
            "an output path"
        );

        resolved.Refusal.Should().Be(refusal);
        resolved.FullPath.Should().BeEmpty();
    }

    [Fact]
    public void A_token_keeps_the_refusals_it_always_gave()
    {
        PluginGraphTokens
            .Resolve(PluginFilterGraphTokens.PluginFile(""), Folder)
            .Refusal.Should()
            .Be("a plugin file token names no file");
        PluginGraphTokens
            .Resolve(PluginFilterGraphTokens.PluginFile("a.txt"), dataFolderPath: null)
            .Refusal.Should()
            .Be(
                "a plugin file token needs the plugin's data folder, which this host did not provide"
            );
    }

    [Fact]
    public void A_link_anywhere_between_the_folder_and_the_file_is_seen()
    {
        string root = Path.GetFullPath(Folder);
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.ResolveLinkTarget(Path.Combine(root, "renders")))
            .Returns("/somewhere/else");

        PluginGraphTokens
            .PassesThroughALink(
                Folder,
                Path.Combine(root, "renders", "deep", "mix.opus"),
                driver.Object
            )
            .Should()
            .BeTrue();
        PluginGraphTokens
            .PassesThroughALink(Folder, Path.Combine(root, "other", "mix.opus"), driver.Object)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void Only_text_that_names_the_servers_stems_model_uses_it()
    {
        PluginGraphTokens.UsesStemsModel("stemsplit=model={stemsModel}").Should().BeTrue();
        PluginGraphTokens
            .UsesStemsModel($"stemsplit=model={PluginGraphTokens.StemsModelFileName}")
            .Should()
            .BeTrue();
        PluginGraphTokens.UsesStemsModel("stemsplit=model=mine.gguf").Should().BeFalse();
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
