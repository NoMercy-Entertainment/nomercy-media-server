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

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NoMercy.Database;
using NoMercy.Database.Maintenance;
using NoMercy.Database.Models.Common;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Security;

namespace NoMercy.Tests.Database;

public class DeviceIdentityResolverTests : IDisposable
{
    private readonly AppDbContext _context;

    public DeviceIdentityResolverTests()
    {
        ServiceCollection services = new();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        ServiceProvider provider = services.BuildServiceProvider();
        TokenStore.Initialize(provider);

        _context = CreateFreshContext();
    }

    public void Dispose()
    {
        _context.Database.CloseConnection();
        _context.Dispose();
    }

    [Fact]
    public void ResolveAndPersist_Container_PersistedId_IsStableAcrossCalls()
    {
        Guid first = DeviceIdentityResolver.ResolveAndPersist(_context, inContainer: true);
        Guid second = DeviceIdentityResolver.ResolveAndPersist(_context, inContainer: true);

        Assert.Equal(first, second);

        Configuration? stored = _context.Configuration.FirstOrDefault(c =>
            c.Key == DeviceIdentityResolver.ConfigKey
        );
        Assert.NotNull(stored);
        Assert.Equal(first.ToString(), stored.Value);
    }

    [Fact]
    public void ResolveAndPersist_Container_NoPriorRegistration_GetsUniqueNonCollidingId()
    {
        using AppDbContext otherInstall = CreateFreshContext();

        Guid firstInstallId = DeviceIdentityResolver.ResolveAndPersist(_context, inContainer: true);
        Guid secondInstallId = DeviceIdentityResolver.ResolveAndPersist(
            otherInstall,
            inContainer: true
        );

        Assert.NotEqual(Guid.Empty, firstInstallId);
        Assert.NotEqual(Guid.Empty, secondInstallId);
        Assert.NotEqual(firstInstallId, secondInstallId);
    }

    [Fact]
    public void ResolveAndPersist_Container_EvidenceOfPriorRegistration_KeepsHardwareDerivedId()
    {
        // The fingerprint is supplied rather than read from this machine. Left
        // to Info.DeviceId the case asserts a property of whatever host runs
        // it, and a CI runner in a container reads the empty DMI that hashes to
        // a known-degenerate id - which the resolver is supposed to migrate, so
        // the case failed for doing its job.
        Guid hardwareId = Guid.Parse("0f9a4c31-7c2e-4a55-9a6d-5f2b1e3d7c48");
        Assert.False(KnownDegenerateDeviceIds.IsDegenerate(hardwareId));

        _context.Configuration.Add(
            new() { Key = "ssl_certificate", SecureValue = "existing-cert" }
        );
        _context.Configuration.Add(new() { Key = "ssl_private_key", SecureValue = "existing-key" });
        _context.SaveChanges();

        Guid resolvedId = DeviceIdentityResolver.ResolveAndPersist(
            _context,
            hardwareId,
            inContainer: true
        );

        Assert.Equal(hardwareId, resolvedId);

        // Regression guard: the non-degenerate path must never touch the cert rows.
        Assert.NotNull(_context.Configuration.FirstOrDefault(c => c.Key == "ssl_certificate"));
        Assert.NotNull(_context.Configuration.FirstOrDefault(c => c.Key == "ssl_private_key"));
    }

    [Fact]
    public void ResolveAndPersist_BareMetal_DegenerateHardwareId_IsKeptAndCertPreserved()
    {
        // Stoney's regression: a bare-metal box whose DMI reads empty hashes to a
        // known-degenerate value, but its DNS subdomain + certificate were issued
        // for exactly that id. It must NOT be migrated off the machine — the id is
        // returned untouched and the certificate rows are preserved.
        Guid knownDegenerateId = KnownDegenerateDeviceIds.Values.First();

        _context.Configuration.Add(
            new() { Key = "ssl_certificate", SecureValue = "existing-cert" }
        );
        _context.Configuration.Add(new() { Key = "ssl_private_key", SecureValue = "existing-key" });
        _context.SaveChanges();

        Guid resolvedId = DeviceIdentityResolver.ResolveAndPersist(
            _context,
            knownDegenerateId,
            inContainer: false
        );

        Assert.Equal(knownDegenerateId, resolvedId);
        Assert.NotNull(_context.Configuration.FirstOrDefault(c => c.Key == "ssl_certificate"));
        Assert.NotNull(_context.Configuration.FirstOrDefault(c => c.Key == "ssl_private_key"));
    }

    [Fact]
    public void ResolveAndPersist_BareMetal_DegenerateHardwareId_NoCertificate_GetsPersistedUniqueId()
    {
        Guid knownDegenerateId = KnownDegenerateDeviceIds.Values.First();

        Guid resolvedId = DeviceIdentityResolver.ResolveAndPersist(
            _context,
            knownDegenerateId,
            inContainer: false
        );

        Assert.NotEqual(Guid.Empty, resolvedId);
        Assert.False(KnownDegenerateDeviceIds.IsDegenerate(resolvedId));

        Configuration? stored = _context.Configuration.FirstOrDefault(c =>
            c.Key == DeviceIdentityResolver.ConfigKey
        );
        Assert.NotNull(stored);
        Assert.Equal(resolvedId.ToString(), stored.Value);

        Guid second = DeviceIdentityResolver.ResolveAndPersist(
            _context,
            knownDegenerateId,
            inContainer: false
        );
        Assert.Equal(resolvedId, second);
    }

    [Fact]
    public void ResolveAndPersist_BareMetal_DegenerateHardwareId_NoCertificate_SecondInstallGetsDifferentId()
    {
        using AppDbContext otherInstall = CreateFreshContext();
        Guid knownDegenerateId = KnownDegenerateDeviceIds.Values.First();

        Guid first = DeviceIdentityResolver.ResolveAndPersist(
            _context,
            knownDegenerateId,
            inContainer: false
        );
        Guid second = DeviceIdentityResolver.ResolveAndPersist(
            otherInstall,
            knownDegenerateId,
            inContainer: false
        );

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ResolveAndPersist_BareMetal_UniqueHardwareId_NoCertificate_IsKeptAndNotPersisted()
    {
        Guid hardwareId = Guid.Parse("0f9a4c31-7c2e-4a55-9a6d-5f2b1e3d7c48");
        Assert.False(KnownDegenerateDeviceIds.IsDegenerate(hardwareId));

        Guid resolvedId = DeviceIdentityResolver.ResolveAndPersist(
            _context,
            hardwareId,
            inContainer: false
        );

        Assert.Equal(hardwareId, resolvedId);
        Assert.Null(
            _context.Configuration.FirstOrDefault(c => c.Key == DeviceIdentityResolver.ConfigKey)
        );
    }

    [Fact]
    public void ResolveAndPersist_Container_DegenerateHardwareId_MigratesEvenWithPriorRegistrationEvidence()
    {
        Guid knownDegenerateId = KnownDegenerateDeviceIds.Values.First();

        _context.Configuration.Add(
            new() { Key = "ssl_certificate", SecureValue = "existing-cert" }
        );
        _context.Configuration.Add(new() { Key = "ssl_private_key", SecureValue = "existing-key" });
        _context.Configuration.Add(
            new() { Key = "auth_access_token", SecureValue = "existing-access-token" }
        );
        _context.Configuration.Add(
            new() { Key = "auth_refresh_token", SecureValue = "existing-refresh-token" }
        );
        _context.SaveChanges();

        Guid resolvedId = DeviceIdentityResolver.ResolveAndPersist(
            _context,
            knownDegenerateId,
            inContainer: true
        );

        Assert.NotEqual(knownDegenerateId, resolvedId);
        Assert.False(KnownDegenerateDeviceIds.IsDegenerate(resolvedId));

        // The stale cert must be gone — otherwise HasValidCertificate() keeps
        // reporting "registered" under a certificate that doesn't cover the new id.
        Assert.Null(_context.Configuration.FirstOrDefault(c => c.Key == "ssl_certificate"));
        Assert.Null(_context.Configuration.FirstOrDefault(c => c.Key == "ssl_private_key"));

        // Auth tokens must survive so the re-registration that follows is non-interactive.
        Configuration? accessToken = _context.Configuration.FirstOrDefault(c =>
            c.Key == "auth_access_token"
        );
        Configuration? refreshToken = _context.Configuration.FirstOrDefault(c =>
            c.Key == "auth_refresh_token"
        );
        Assert.NotNull(accessToken);
        Assert.Equal("existing-access-token", accessToken.SecureValue);
        Assert.NotNull(refreshToken);
        Assert.Equal("existing-refresh-token", refreshToken.SecureValue);

        Configuration? deviceIdRow = _context.Configuration.FirstOrDefault(c =>
            c.Key == DeviceIdentityResolver.ConfigKey
        );
        Assert.NotNull(deviceIdRow);
        Assert.Equal(resolvedId.ToString(), deviceIdRow.Value);
    }

    [Fact]
    public void ResolveAndPersist_Container_PersistedDegenerateId_IsReplaced()
    {
        Guid knownDegenerateId = KnownDegenerateDeviceIds.Values.First();

        _context.Configuration.Add(
            new() { Key = DeviceIdentityResolver.ConfigKey, Value = knownDegenerateId.ToString() }
        );
        _context.SaveChanges();

        Guid resolvedId = DeviceIdentityResolver.ResolveAndPersist(
            _context,
            knownDegenerateId,
            inContainer: true
        );

        Assert.NotEqual(knownDegenerateId, resolvedId);
        Assert.False(KnownDegenerateDeviceIds.IsDegenerate(resolvedId));

        Configuration? stored = _context.Configuration.FirstOrDefault(c =>
            c.Key == DeviceIdentityResolver.ConfigKey
        );
        Assert.NotNull(stored);
        Assert.Equal(resolvedId.ToString(), stored.Value);
    }

    private static AppDbContext CreateFreshContext()
    {
        DbContextOptionsBuilder<AppDbContext> optionsBuilder = new();
        optionsBuilder.UseSqlite("Data Source=:memory:");

        AppDbContext context = new(optionsBuilder.Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();

        return context;
    }
}
