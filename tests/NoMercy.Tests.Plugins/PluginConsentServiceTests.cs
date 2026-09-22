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

using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginConsentServiceTests
{
    private static PluginCapabilities Caps(params string[] hooks) => new() { Hooks = [.. hooks] };

    [Fact]
    public void IsBaseline_NullOrMediaUiOnly_IsBaseline()
    {
        PluginConsentService service = new(new InMemoryConsentStore());
        Assert.True(service.IsBaseline(null));
        Assert.True(service.IsBaseline(Caps(["mediaSource", "ui"])));
        Assert.True(service.IsBaseline(Caps("metadata")));
    }

    [Fact]
    public void IsBaseline_AuthOrNetworkOrRest_IsElevated()
    {
        PluginConsentService service = new(new InMemoryConsentStore());
        Assert.False(service.IsBaseline(Caps("auth")));
        Assert.False(service.IsBaseline(new() { Hooks = ["ui"], Rest = true }));
        Assert.False(
            service.IsBaseline(
                new()
                {
                    Hooks = ["ui"],
                    Network = new() { Hosts = ["x"] },
                }
            )
        );
    }

    [Fact]
    public void Consent_RoundTrips()
    {
        PluginConsentService service = new(new InMemoryConsentStore());
        Ulid id = Ulid.NewUlid();
        Assert.False(service.HasConsent(id));
        service.GrantConsent(id, Caps("mediaSource"), new Version(1, 0));
        Assert.True(service.HasConsent(id));
    }

    [Fact]
    public void RevokeConsent_RemovesGrantedConsent()
    {
        PluginConsentService service = new(new InMemoryConsentStore());
        Ulid id = Ulid.NewUlid();
        service.GrantConsent(id, Caps("mediaSource"), new Version(1, 0));
        Assert.True(service.HasConsent(id));

        service.RevokeConsent(id);

        Assert.False(service.HasConsent(id));
    }

    [Fact]
    public void ConsentCoversCapabilities_NoConsent_IsFalse()
    {
        PluginConsentService service = new(new InMemoryConsentStore());
        Ulid id = Ulid.NewUlid();

        Assert.False(service.ConsentCoversCapabilities(id, Caps("auth"), new Version(1, 0)));
    }

    [Fact]
    public void ConsentCoversCapabilities_SameCapabilities_IsTrue()
    {
        PluginConsentService service = new(new InMemoryConsentStore());
        Ulid id = Ulid.NewUlid();
        PluginCapabilities caps = Caps("auth");

        service.GrantConsent(id, caps, new Version(1, 0));

        Assert.True(service.ConsentCoversCapabilities(id, caps, new Version(1, 0)));
    }

    [Fact]
    public void ConsentCoversCapabilities_WidenedCapabilities_IsFalse()
    {
        PluginConsentService service = new(new InMemoryConsentStore());
        Ulid id = Ulid.NewUlid();

        service.GrantConsent(id, Caps("auth"), new Version(1, 0));

        Assert.False(
            service.ConsentCoversCapabilities(id, Caps("auth", "encoder"), new Version(1, 0))
        );
    }
}
