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

using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Database.Maintenance;

/// <summary>
/// Decides the default DNS scheme (apex vs the synthesized "srv" scheme) exactly
/// once — the first boot after this decision was introduced — and persists it so
/// every later boot short-circuits on the persisted value. Must run before
/// <c>ServerRegistrationService</c> sends <c>dns_scheme</c> during registration
/// (see <c>NoMercy.Service.Seeds.DatabaseSeeder.InitSchema</c>, which calls this
/// right after <see cref="DeviceIdentityResolver"/> resolves the device id, well
/// before <c>BootOrchestrator</c> reaches Phase 3 registration).
///
/// An explicit <see cref="ConfigKey"/> row — written by a user/owner toggle in
/// the dashboard, or by an earlier run of this same resolver — is always honored
/// as-is and never re-evaluated.
///
/// When the key has never been written: a server with no evidence of a prior
/// registration (no cached auth token, no SSL certificate on file) is a fresh
/// install and defaults to "srv". A server that already completed registration
/// before this decision existed is pinned to "apex" — its already-connected
/// clients resolved and cached the apex hostname, and a silent switch to the
/// srv wildcard cert would produce a TLS hostname mismatch for them.
/// </summary>
public static class DnsSchemeResolver
{
    public const string ConfigKey = "UseSynthesizedDns";

    private const string AuthAccessTokenKey = "auth_access_token";
    private const string SslCertificateKey = "ssl_certificate";
    private const string SslPrivateKeyKey = "ssl_private_key";

    public static bool ResolveAndPersist(AppDbContext db)
    {
        Configuration? existing = db.Configuration.FirstOrDefault(c => c.Key == ConfigKey);
        if (existing is not null)
            return existing.Value.ToBoolean();

        bool hasPriorRegistration = HasEvidenceOfPriorRegistration(db);
        bool useSynthesizedDns = !hasPriorRegistration;

        db.Configuration.Add(new() { Key = ConfigKey, Value = useSynthesizedDns.ToString() });
        db.SaveChanges();

        RuntimeServerSettings.Current.UseSynthesizedDns = useSynthesizedDns;

        Logger.Setup(
            $"DNS scheme decided once: {(useSynthesizedDns ? "srv" : "apex")} "
                + (
                    hasPriorRegistration
                        ? "(existing server — pinned to apex to avoid a TLS mismatch for already-connected clients)"
                        : "(fresh install — defaulting to the synthesized srv scheme)"
                ),
            LogEventLevel.Information
        );

        return useSynthesizedDns;
    }

    private static bool HasEvidenceOfPriorRegistration(AppDbContext db)
    {
        return db.Configuration.Any(c => c.Key == SslCertificateKey || c.Key == SslPrivateKeyKey)
            || HasAuthAccessToken(db);
    }

    private static bool HasAuthAccessToken(AppDbContext db)
    {
        Configuration? token = db.Configuration.FirstOrDefault(c => c.Key == AuthAccessTokenKey);
        return !string.IsNullOrEmpty(token?.SecureValue);
    }
}
