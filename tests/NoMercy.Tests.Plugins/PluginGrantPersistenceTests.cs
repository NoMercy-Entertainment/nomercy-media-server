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
using Microsoft.AspNetCore.DataProtection;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The grant store's own cases run over an in-memory configuration, so the file
/// the owner actually inspects was never part of a test. That is the surface
/// the report was about: Allow cleared the request and left
/// <c>plugins/data/platform/config.json</c> holding <c>"Grants": []</c>, which
/// is indistinguishable from the site refusing the plugin.
/// </summary>
public class PluginGrantPersistenceTests : IDisposable
{
    private static readonly Ulid PluginId = Ulid.Parse("0H248H248H248H248H248H248H");
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"nm-grants-{Guid.NewGuid():N}"
    );

    private IPluginConfiguration Configuration() =>
        new PluginConfiguration(_tempDir, TestStorageHelper.CreateStorage(_tempDir));

    private IPluginGrantStore Store() => new ConfigPluginGrantStore(Configuration());

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void A_granted_host_survives_the_file_it_is_written_to()
    {
        Store().Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example");

        // A second store over the same file is the next request the plugin
        // makes: the process that asks is never the one that granted.
        IPluginGrantStore reader = Store();

        reader
            .Granted(PluginId, PluginGrantKind.NetworkHost)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("tracker.example");
        reader.Holds(PluginId, PluginGrantKind.NetworkHost, "tracker.example").Should().BeTrue();
    }

    /// <summary>
    /// Which host was granted, not merely that one was: seventeen hosts asked
    /// for and one stored reads as success from a count alone.
    /// </summary>
    [Fact]
    public void Every_granted_host_is_kept_apart_from_the_others()
    {
        IPluginGrantStore writer = Store();
        writer.Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example");
        writer.Grant(PluginId, PluginGrantKind.NetworkHost, "cdn.example");
        writer.Grant(PluginId, PluginGrantKind.LibraryWrite, "library-1");

        IPluginGrantStore reader = Store();

        reader
            .Granted(PluginId, PluginGrantKind.NetworkHost)
            .Should()
            .BeEquivalentTo(["tracker.example", "cdn.example"]);
        reader.Holds(PluginId, PluginGrantKind.NetworkHost, "library-1").Should().BeFalse();
        reader.Holds(PluginId, PluginGrantKind.LibraryWrite, "library-1").Should().BeTrue();
    }

    /// <summary>
    /// The reported loop: the request comes straight back on the plugin's next
    /// cadence, so the owner presses Allow for ever and nothing changes.
    /// </summary>
    [Fact]
    public void A_host_already_granted_is_not_asked_for_again()
    {
        Store().Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example");

        IPluginGrantStore next = Store();
        next.Request(PluginId, PluginGrantKind.NetworkHost, "tracker.example", "fetch");

        next.PendingRequests().Should().BeEmpty();
    }

    [Fact]
    public void Granting_takes_the_request_off_the_owners_screen()
    {
        IPluginGrantStore store = Store();
        store.Request(PluginId, PluginGrantKind.NetworkHost, "tracker.example", "fetch");
        store.Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example");

        IPluginGrantStore reader = Store();

        reader.PendingRequests().Should().BeEmpty();
        reader.Holds(PluginId, PluginGrantKind.NetworkHost, "tracker.example").Should().BeTrue();
    }

    /// <summary>
    /// Consent, grants and secrets share one platform file, each written by its
    /// own record type. Serializing one over the whole file dropped the others:
    /// consenting to a plugin wiped the grants it had just been given, which is
    /// exactly the reported "Allow, and nothing is stored".
    /// </summary>
    [Fact]
    public void Consenting_does_not_wipe_the_grants_the_same_owner_gave()
    {
        IPluginConfiguration configuration = Configuration();
        IPluginGrantStore grants = new ConfigPluginGrantStore(configuration);
        IPluginConsentStore consent = new ConfigPluginConsentStore(configuration);

        grants.Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example");
        consent.Add(PluginId, null, new Version(1, 0));

        // Both answers, from stores rebuilt over the file rather than the ones
        // that wrote it: what the next request and the next boot actually read.
        IPluginConfiguration reread = Configuration();

        new ConfigPluginGrantStore(reread)
            .Holds(PluginId, PluginGrantKind.NetworkHost, "tracker.example")
            .Should()
            .BeTrue();
        new ConfigPluginConsentStore(reread).Contains(PluginId).Should().BeTrue();
    }

    /// <summary>The same collision the other way round, which wiped the consent.</summary>
    [Fact]
    public void Granting_does_not_wipe_the_consent_that_came_first()
    {
        IPluginConfiguration configuration = Configuration();

        new ConfigPluginConsentStore(configuration).Add(PluginId, null, new Version(1, 0));
        new ConfigPluginGrantStore(configuration).Grant(
            PluginId,
            PluginGrantKind.NetworkHost,
            "tracker.example"
        );

        IPluginConfiguration reread = Configuration();

        new ConfigPluginConsentStore(reread).Contains(PluginId).Should().BeTrue();
        new ConfigPluginGrantStore(reread)
            .Holds(PluginId, PluginGrantKind.NetworkHost, "tracker.example")
            .Should()
            .BeTrue();
    }

    /// <summary>
    /// The third record in the same file. A plugin storing a token after the
    /// owner granted it a host would have taken the grant with it.
    /// </summary>
    [Fact]
    public async Task A_stored_secret_does_not_wipe_consent_or_grants()
    {
        IPluginConfiguration configuration = Configuration();

        new ConfigPluginConsentStore(configuration).Add(PluginId, null, new Version(1, 0));
        new ConfigPluginGrantStore(configuration).Grant(
            PluginId,
            PluginGrantKind.NetworkHost,
            "tracker.example"
        );

        await new PluginSecretStore(
            PluginId,
            new EphemeralDataProtectionProvider(),
            configuration
        ).SetAsync("api-key", "s3cret");

        IPluginConfiguration reread = Configuration();

        new ConfigPluginConsentStore(reread).Contains(PluginId).Should().BeTrue();
        new ConfigPluginGrantStore(reread)
            .Holds(PluginId, PluginGrantKind.NetworkHost, "tracker.example")
            .Should()
            .BeTrue();

        // And the secret itself round-trips, so the merge is not simply
        // dropping the newest writer's keys.
        (
            await new PluginSecretStore(
                PluginId,
                new EphemeralDataProtectionProvider(),
                reread
            ).KeysAsync()
        )
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("api-key");
    }

    [Fact]
    public void A_revoke_reaches_the_file_too()
    {
        Store().Grant(PluginId, PluginGrantKind.NetworkHost, "tracker.example");
        Store().Revoke(PluginId, PluginGrantKind.NetworkHost, "tracker.example");

        Store().Holds(PluginId, PluginGrantKind.NetworkHost, "tracker.example").Should().BeFalse();
    }
}
