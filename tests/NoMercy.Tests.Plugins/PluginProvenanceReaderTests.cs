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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Provenance;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The recorded install joined with the repository list as it is now: trust
/// follows the owner's current choice, and what was never recorded stays unknown.
/// </summary>
public class PluginProvenanceReaderTests
{
    private const string IndexUrl = "https://example.com/index.json";
    private static readonly Ulid PluginId = Ulid.Parse("01JPR0VENANCE0000000000000");

    private sealed class OneSource(PluginInstallSource? source) : IPluginInstallSourceStore
    {
        public PluginInstallSource? Find(Ulid pluginId) => pluginId == PluginId ? source : null;

        public void Record(Ulid pluginId, PluginInstallSource recorded) { }

        public void Forget(Ulid pluginId) { }
    }

    private static PluginInstallSource FromIndex() =>
        new()
        {
            Kind = PluginInstallSourceKind.Repository,
            RepositoryName = "Example Index",
            RepositoryUrl = IndexUrl,
            FromOfficialMarketplace = false,
            ChecksumVerified = true,
            Version = "1.0.0",
            RecordedAt = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
        };

    private static PluginProvenanceReader Reader(
        PluginInstallSource? source,
        params PluginRepositoryInfo[] repositories
    )
    {
        Mock<IPluginRepository> list = new();
        list.Setup(repository => repository.GetRepositories()).Returns(repositories);

        return new(new OneSource(source), list.Object);
    }

    [Fact]
    public void A_plugin_installed_before_sources_were_recorded_is_unknown_in_every_field()
    {
        PluginProvenance provenance = Reader(source: null).Read(PluginId);

        provenance.Kind.Should().Be(PluginInstallSourceKind.Unknown);
        provenance.RepositoryUrl.Should().BeNull();
        provenance.RepositoryTrusted.Should().BeNull();
        provenance.FromOfficialMarketplace.Should().BeNull();
        provenance.ChecksumVerified.Should().BeNull();
        provenance.RecordedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Trust_is_the_repository_s_trust_now(bool trustedNow)
    {
        PluginRepositoryInfo index = new()
        {
            Name = "Example Index",
            Url = IndexUrl,
            Trusted = trustedNow,
        };

        PluginProvenance provenance = Reader(FromIndex(), index).Read(PluginId);

        provenance.RepositoryTrusted.Should().Be(trustedNow);
        provenance.RepositoryRemoved.Should().BeFalse();
        provenance.ChecksumVerified.Should().BeTrue();
    }

    [Fact]
    public void Taking_trust_back_after_the_install_shows_on_the_next_read()
    {
        PluginRepositoryInfo index = new()
        {
            Name = "Example Index",
            Url = IndexUrl,
            Trusted = true,
        };
        PluginProvenanceReader reader = Reader(FromIndex(), index);
        reader.Read(PluginId).RepositoryTrusted.Should().BeTrue();

        index.Trusted = false;

        reader.Read(PluginId).RepositoryTrusted.Should().BeFalse();
    }

    [Fact]
    public void The_repository_is_matched_by_its_index_url_not_its_name()
    {
        PluginRepositoryInfo renamed = new()
        {
            Name = "Renamed",
            Url = IndexUrl,
            Trusted = true,
        };
        PluginRepositoryInfo sameName = new()
        {
            Name = "Example Index",
            Url = "https://elsewhere.example/index.json",
            Trusted = false,
        };

        PluginProvenance provenance = Reader(FromIndex(), sameName, renamed).Read(PluginId);

        provenance.RepositoryTrusted.Should().BeTrue();
        provenance.RepositoryName.Should().Be("Example Index", "the name it had at install");
    }

    [Fact]
    public void A_removed_repository_is_reported_removed_and_not_trusted()
    {
        PluginProvenance provenance = Reader(FromIndex()).Read(PluginId);

        provenance.RepositoryRemoved.Should().BeTrue();
        provenance.RepositoryTrusted.Should().BeFalse();
    }

    [Fact]
    public void A_disabled_repository_is_not_trusted()
    {
        PluginRepositoryInfo index = new()
        {
            Name = "Example Index",
            Url = IndexUrl,
            Trusted = true,
            Enabled = false,
        };

        Reader(FromIndex(), index).Read(PluginId).RepositoryTrusted.Should().BeFalse();
    }

    [Fact]
    public void An_upload_has_no_repository_to_trust()
    {
        PluginInstallSource upload = new()
        {
            Kind = PluginInstallSourceKind.File,
            RecordedAt = DateTime.UtcNow,
        };

        PluginProvenance provenance = Reader(upload).Read(PluginId);

        provenance.Kind.Should().Be(PluginInstallSourceKind.File);
        provenance.RepositoryTrusted.Should().BeNull();
        provenance.RepositoryRemoved.Should().BeNull();
        provenance.FromOfficialMarketplace.Should().BeFalse();
        provenance.ChecksumVerified.Should().BeFalse();
    }
}
