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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// Where a plugin came from, and what that is allowed to decide.
/// <para>
/// Trust rests on provenance and nothing else. The manifest's author line is
/// free text any file can copy, so these pin that it decides nothing, and that a
/// server which could not read an index refuses rather than assuming.
/// </para>
/// </summary>
public class TrustedRepositoryTests
{
    private static readonly Ulid Listed = Ulid.NewUlid();
    private static readonly Ulid Unlisted = Ulid.NewUlid();

    private static PluginVerificationContext Context(Ulid id, string author) =>
        new()
        {
            Manifest = new()
            {
                Id = new PluginId(id),
                Name = "Internet Radio",
                Description = "Stations",
                Version = "1.0.2",
                Assembly = "NoMercy.Plugin.InternetRadio.dll",
                Author = author,
            },
            AssemblyPath = "NoMercy.Plugin.InternetRadio.dll",
        };

    [Fact]
    public void APluginATrustedIndexLists_IsTrusted()
    {
        TrustedRepositoryVerificationStage stage = new(() => new FakeRepository(Listed));

        (PluginStageOutcome outcome, string? message) = stage.Evaluate(
            Context(Listed, "NoMercy Community")
        );

        outcome.Should().Be(PluginStageOutcome.Trust);
        message.Should().Contain("trusts");
    }

    [Fact]
    public void APluginNoTrustedIndexLists_EarnsNoTrust()
    {
        TrustedRepositoryVerificationStage stage = new(() => new FakeRepository(Listed));

        (PluginStageOutcome outcome, string? _) = stage.Evaluate(
            Context(Unlisted, "NoMercy Community")
        );

        outcome.Should().Be(PluginStageOutcome.Pass);
    }

    /// <summary>
    /// The attack this shape exists to refuse: a manifest claiming to be ours.
    /// </summary>
    [Fact]
    public void AManifestClaimingOurName_EarnsNothing()
    {
        TrustedRepositoryVerificationStage stage = new(() => new FakeRepository(Listed));

        (PluginStageOutcome outcome, string? _) = stage.Evaluate(
            Context(Unlisted, "NoMercy Entertainment")
        );

        outcome.Should().Be(PluginStageOutcome.Pass);
    }

    /// <summary>
    /// A server that could not reach the index has not learned that a plugin is
    /// untrusted — it has learned nothing, and the safe answer is the one that
    /// asks the owner.
    /// </summary>
    [Fact]
    public void WithNoIndexRead_NothingIsTrusted()
    {
        TrustedRepositoryVerificationStage stage = new(() => new FakeRepository());

        (PluginStageOutcome outcome, string? _) = stage.Evaluate(
            Context(Listed, "NoMercy Community")
        );

        outcome.Should().Be(PluginStageOutcome.Pass);
    }

    /// <summary>
    /// Granting trust is all this stage does. Enforcing would make an unreadable
    /// index a reason a plugin fails to load at all.
    /// </summary>
    [Fact]
    public void TheStageNeverWithholds()
    {
        new TrustedRepositoryVerificationStage(() => new FakeRepository())
            .Enforced.Should()
            .BeFalse();
    }

    private sealed class FakeRepository(params Ulid[] trusted) : IPluginRepository
    {
        public IReadOnlyList<PluginRepositoryInfo> GetRepositories() => [];

        public Task AddRepositoryAsync(string name, string url, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveRepositoryAsync(string name, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RefreshAsync(CancellationToken ct = default) => Task.CompletedTask;

        public IReadOnlyList<PluginRepositoryEntry> GetAvailablePlugins() => [];

        public PluginRepositoryEntry? FindPlugin(Ulid pluginId) => null;

        public PluginVersionEntry? FindVersion(Ulid pluginId, string version) => null;

        public bool IsFromTrustedRepository(Ulid pluginId) => trusted.Contains(pluginId);
    }
}
