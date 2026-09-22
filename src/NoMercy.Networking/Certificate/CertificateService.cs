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

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Networking.Discovery;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Networking.Certificate;

public class CertificateService : ICertificateService
{
    private readonly IHttpClientFactory _httpClientFactory;

    private readonly ILogger<CertificateService> _logger;

    // Optional: absent in the throwaway pre-DI presence-check instance built by
    // ServerBootstrapper before the real DI container exists. Self-signed SANs
    // degrade gracefully (localhost/loopback/LAN-IP only) when this is null.
    private readonly INetworkDiscovery? _networkDiscovery;

    public CertificateService(
        ILogger<CertificateService> logger,
        IHttpClientFactory httpClientFactory,
        INetworkDiscovery? networkDiscovery = null
    )
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _networkDiscovery = networkDiscovery;
    }

    // LOCAL-ONLY: Certificate runs before StorageProvider is initialized (startup phase 1-3).
    // DI is not available here; replacing this would require threading IStorageDriver
    // through every startup call site including Program.cs and BootOrchestrator.
    private readonly IStorageDriver _driver = new LocalStorageDriver();
    private X509Certificate2? _cachedCertificate;
    private X509Certificate2? _selfSignedCertificate;
    private readonly object _certLock = new();

    public void LoadFromDb()
    {
        using AppDbContext db = new();
        string? certPem = db
            .Configuration.Where(c => c.Key == "ssl_certificate")
            .Select(c => c.SecureValue)
            .FirstOrDefault();
        string? keyPem = db
            .Configuration.Where(c => c.Key == "ssl_private_key")
            .Select(c => c.SecureValue)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(certPem) || string.IsNullOrEmpty(keyPem))
        {
            // Fallback: load from legacy PEM files (pre-DB-storage installs)
#pragma warning disable CS0618
            if (_driver.FileExists(AppFiles.CertFile) && _driver.FileExists(AppFiles.KeyFile))
            {
                using (StreamReader certReader = new(_driver.OpenRead(AppFiles.CertFile)))
                    certPem = certReader.ReadToEnd();
                using (StreamReader keyReader = new(_driver.OpenRead(AppFiles.KeyFile)))
                    keyPem = keyReader.ReadToEnd();
                _logger.LogInformation("Loading SSL certificate from legacy PEM files");
            }
            else
            {
                return;
            }
#pragma warning restore CS0618
        }

        lock (_certLock)
        {
            using X509Certificate2 tempCert = X509Certificate2.CreateFromPem(certPem, keyPem);
            byte[] pkcs12Data = tempCert.Export(X509ContentType.Pkcs12);
            _cachedCertificate = X509CertificateLoader.LoadPkcs12(pkcs12Data, null);
        }

        _logger.LogInformation("Loaded SSL certificate into memory cache");
    }

    private void UpsertConfig(AppDbContext db, string key, string value)
    {
        Configuration? existing = db.Configuration.FirstOrDefault(c => c.Key == key);
        if (existing != null)
        {
            existing.SecureValue = value;
        }
        else
        {
            db.Configuration.Add(new() { Key = key, SecureValue = value });
        }
    }

    public bool HasValidCertificate()
    {
        lock (_certLock)
        {
            if (_cachedCertificate is not null)
                return _cachedCertificate.NotAfter > DateTime.Now;
        }

        // Check DB — must load and verify NotAfter, not just row presence.
        // A row with an expired cert must return false so Kestrel does not
        // enable HTTPS with a cert that browsers reject.
        try
        {
            string? certPem = ReadCertificatePemFromDb();
            if (!string.IsNullOrEmpty(certPem))
            {
                using X509Certificate2 dbCert = X509Certificate2.CreateFromPem(certPem);
                return dbCert.NotAfter > DateTime.Now;
            }
        }
        catch
        {
            // DB not ready or cert PEM is malformed — fall through to file check.
        }

        // Legacy fallback: cert files on disk (pre-DB-storage installs). File presence
        // alone is not validity — an expired PEM left on disk from a pre-DB install must
        // return false the same way the DB-backed path above does, or BootOrchestrator
        // treats an expired legacy cert as "already registered" and skips re-registration.
#pragma warning disable CS0618
        if (!_driver.FileExists(AppFiles.CertFile) || !_driver.FileExists(AppFiles.KeyFile))
            return false;

        try
        {
            string legacyCertPem;
            using (StreamReader certReader = new(_driver.OpenRead(AppFiles.CertFile)))
                legacyCertPem = certReader.ReadToEnd();

            using X509Certificate2 legacyCert = X509Certificate2.CreateFromPem(legacyCertPem);
            return legacyCert.NotAfter > DateTime.Now;
        }
        catch
        {
            // Malformed/unreadable legacy PEM — treat as no valid certificate.
            return false;
        }
#pragma warning restore CS0618
    }

    /// <summary>
    /// Returns the raw certificate PEM stored in the DB, or <c>null</c> when no
    /// row exists or the DB is not yet available.  Extracted as a virtual method
    /// so unit tests can override it without a real SQLite connection.
    /// </summary>
    protected virtual string? ReadCertificatePemFromDb()
    {
        using AppDbContext db = new();
        return db
            .Configuration.Where(c => c.Key == "ssl_certificate")
            .Select(c => c.SecureValue)
            .FirstOrDefault();
    }

    /// <summary>
    /// Waits between certificate-fetch retries. Extracted as a virtual method so
    /// unit tests can override it to a no-op instead of blocking for the real
    /// 10-second production delay on every 202/timeout retry.
    /// </summary>
    protected virtual Task DelayBetweenAttemptsAsync(TimeSpan delay, CancellationToken ct)
    {
        return Task.Delay(delay, ct);
    }

    public void KestrelConfig(KestrelServerOptions options)
    {
        options.AddServerHeader = false;
        options.Limits.MaxRequestBodySize = 100L * 1024 * 1024 * 1024; // 100GB — 4K remux support
        options.Limits.MaxRequestBufferSize = null; // Kestrel manages adaptively
        options.Limits.MaxConcurrentConnections = 1000; // Many streaming clients
        options.Limits.MaxConcurrentUpgradedConnections = 500; // WebSocket/SignalR
    }

    /// <summary>
    /// Selection order: a valid Let's Encrypt cert (unchanged happy path), else the
    /// cached self-signed fallback (generated/persisted on demand), else — only when
    /// self-signed generation itself fails — plaintext HTTP/1.1. Keeping the origin on
    /// HTTPS (even with a self-signed cert) is what lets an HTTPS dashboard reach a
    /// LAN-only or pre-LE server at all; plaintext is a last resort, not a normal state.
    /// </summary>
    public void ConfigureHttpsListener(ListenOptions listenOptions)
    {
        if (EnsureHttpsCertificate())
        {
            PlaintextToHttpsRedirector.Install(
                listenOptions,
                RuntimeServerSettings.Current.InternalServerPort
            );
            listenOptions.UseHttps(HttpsConnectionAdapterOptions());
        }
        else
        {
            // Without TLS, HTTP/2 requires prior-knowledge (h2c) and HTTP/3 requires QUIC/TLS.
            // Fall back to HTTP/1.1 only to avoid empty-reply issues from protocol mismatches.
            listenOptions.Protocols = HttpProtocols.Http1;
        }
    }

    /// <summary>
    /// Returns true when a certificate is ready for TLS — either the real Let's Encrypt
    /// cert, or the self-signed fallback (loaded from cache/DB, generating a new one only
    /// if neither exists). This is deliberately broader than <see cref="HasValidCertificate"/>,
    /// which callers (BootOrchestrator's "is this server registered" check, PortManager)
    /// rely on meaning "a real LE cert exists" — that meaning is unchanged.
    /// </summary>
    public bool EnsureHttpsCertificate()
    {
        // Kestrel's certificate selector serves ONLY the in-memory cache, and every
        // new DI container starts with that cache empty even when the real cert sits
        // in the DB. Answering "HTTPS is ready" from DB presence alone made a freshly
        // rebuilt host (the setup→HTTPS restart) bind TLS and then throw
        // "No SSL certificate loaded" on every handshake. Load before answering.
        bool cacheEmpty;
        lock (_certLock)
        {
            cacheEmpty = _cachedCertificate is null;
        }

        if (cacheEmpty)
        {
            try
            {
                LoadFromDb();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Certificate cache preload failed, falling through to the self-signed fallback: {Message}",
                    [ex.Message]
                );
            }
        }

        // Answer from the same caches the selector reads — never from DB/file
        // presence — so "true" always means the first TLS handshake can be served.
        lock (_certLock)
        {
            if (_cachedCertificate is not null && _cachedCertificate.NotAfter > DateTime.Now)
                return true;
        }

        return TryEnsureSelfSignedCertificate();
    }

    private HttpsConnectionAdapterOptions HttpsConnectionAdapterOptions()
    {
        return new()
        {
            SslProtocols = SslProtocols.Tls12,
            ServerCertificateSelector = (_, _) =>
            {
                lock (_certLock)
                {
                    if (
                        _cachedCertificate is not null
                        && _cachedCertificate.NotAfter > DateTime.Now
                    )
                        return _cachedCertificate;

                    if (
                        _selfSignedCertificate is not null
                        && _selfSignedCertificate.NotAfter > DateTime.Now
                    )
                        return _selfSignedCertificate;
                }

                throw new InvalidOperationException("No SSL certificate loaded");
            },
        };
    }

    private const string SelfSignedCertConfigKey = "ssl_selfsigned_certificate";
    private const string SelfSignedKeyConfigKey = "ssl_selfsigned_private_key";

    // Self-signed certs carry no CA trust, so the 398-day CA/Browser Forum leaf-validity
    // cap doesn't legally apply — but browsers increasingly enforce it for ANY TLS leaf
    // cert regardless of trust chain, and this is meant to double as the base for a future
    // OS-trust-store install. Stay inside that ceiling so it doesn't need special-casing later.
    private const int SelfSignedValidityDays = 397;

    /// <summary>
    /// Ensures a self-signed fallback certificate is cached in memory, loading the
    /// persisted one from the DB or generating (and persisting) a new one if needed.
    /// Never mints a new certificate when a cached/persisted one is still valid — the
    /// fallback must be stable across restarts, not a new identity every boot.
    /// </summary>
    private bool TryEnsureSelfSignedCertificate()
    {
        lock (_certLock)
        {
            if (
                _selfSignedCertificate is not null
                && _selfSignedCertificate.NotAfter > DateTime.Now
            )
                return true;
        }

        try
        {
            if (LoadSelfSignedFromDb())
                return true;

            GenerateAndPersistSelfSignedCertificate();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to generate self-signed fallback certificate — falling back to plaintext HTTP: {Message}",
                ex.Message
            );
            return false;
        }
    }

    /// <summary>
    /// Loads a previously-persisted self-signed cert from the DB. Returns false (not an
    /// error) when none exists yet, or the persisted one has expired — the caller
    /// regenerates in that case.
    /// </summary>
    private bool LoadSelfSignedFromDb()
    {
        using AppDbContext db = new();
        string? certPem = db
            .Configuration.Where(c => c.Key == SelfSignedCertConfigKey)
            .Select(c => c.SecureValue)
            .FirstOrDefault();
        string? keyPem = db
            .Configuration.Where(c => c.Key == SelfSignedKeyConfigKey)
            .Select(c => c.SecureValue)
            .FirstOrDefault();

        if (string.IsNullOrEmpty(certPem) || string.IsNullOrEmpty(keyPem))
            return false;

        using X509Certificate2 tempCert = X509Certificate2.CreateFromPem(certPem, keyPem);
        if (tempCert.NotAfter <= DateTime.Now)
            return false;

        lock (_certLock)
        {
            byte[] pkcs12Data = tempCert.Export(X509ContentType.Pkcs12);
            _selfSignedCertificate = X509CertificateLoader.LoadPkcs12(pkcs12Data, null);
        }

        _logger.LogInformation("Loaded self-signed fallback certificate from cache");
        return true;
    }

    /// <summary>
    /// Generates a fresh self-signed certificate, persists it as DB SecureValue rows
    /// (same protection mechanism as the real LE cert/key — never plaintext, never a
    /// separate on-disk store), and loads it into the in-memory cache.
    /// </summary>
    private void GenerateAndPersistSelfSignedCertificate()
    {
        using X509Certificate2 generated = BuildSelfSignedCertificate();

        string certPem = generated.ExportCertificatePem();
        using RSA? rsa = generated.GetRSAPrivateKey();
        string keyPem =
            rsa?.ExportRSAPrivateKeyPem()
            ?? throw new InvalidOperationException(
                "Generated self-signed certificate has no exportable RSA private key"
            );

        using (AppDbContext db = new())
        {
            UpsertConfig(db, SelfSignedCertConfigKey, certPem);
            UpsertConfig(db, SelfSignedKeyConfigKey, keyPem);
            db.SaveChanges();
        }

        lock (_certLock)
        {
            byte[] pkcs12Data = generated.Export(X509ContentType.Pkcs12);
            _selfSignedCertificate = X509CertificateLoader.LoadPkcs12(pkcs12Data, null);
        }

        // Never log PEM/key material — only the non-secret expiry.
        _logger.LogInformation(
            "Generated self-signed fallback certificate (expires {NotAfter:yyyy-MM-dd}) so HTTPS "
                + "stays reachable without a Let's Encrypt cert. Direct browser access will show a "
                + "trust warning until a real certificate is acquired.",
            generated.NotAfter
        );
    }

    /// <summary>
    /// Builds the self-signed certificate covering localhost, loopback, discoverable LAN
    /// IPs, and (when <see cref="INetworkDiscovery"/> is available) the server's
    /// InternalDomain/ExternalDomain. RSA 2048/SHA-256, ~13 months validity.
    /// </summary>
    private X509Certificate2 BuildSelfSignedCertificate()
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            $"CN=NoMercy MediaServer {Info.DeviceId}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(false, false, 0, false)
        );
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                false
            )
        );
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], false)
        );

        SubjectAlternativeNameBuilder sanBuilder = new();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

        foreach (IPAddress address in CollectLocalIpAddresses(_networkDiscovery))
            sanBuilder.AddIpAddress(address);

        if (_networkDiscovery is not null)
        {
            AddDnsNameIfValid(sanBuilder, _networkDiscovery.InternalDomain);

            if (
                !string.IsNullOrEmpty(_networkDiscovery.ExternalIp)
                && _networkDiscovery.ExternalIp != "0.0.0.0"
            )
                AddDnsNameIfValid(sanBuilder, _networkDiscovery.ExternalDomain);
        }

        request.CertificateExtensions.Add(sanBuilder.Build());

        DateTimeOffset notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        DateTimeOffset notAfter = DateTimeOffset.UtcNow.AddDays(SelfSignedValidityDays);

        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private void AddDnsNameIfValid(SubjectAlternativeNameBuilder sanBuilder, string? dnsName)
    {
        if (string.IsNullOrWhiteSpace(dnsName))
            return;

        try
        {
            sanBuilder.AddDnsName(dnsName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Skipping invalid SAN DNS name {DnsName} for self-signed cert: {Message}",
                [dnsName, ex.Message]
            );
        }
    }

    /// <summary>
    /// Best-effort LAN IP enumeration for self-signed SANs. Duplicates
    /// <c>NetworkDiscovery.GetInternalIp</c>'s NIC-walk rather than depending on it —
    /// this must degrade to "just localhost/loopback" instead of throwing when no NIC
    /// is up (e.g. inside a locked-down container).
    /// </summary>
    private static IEnumerable<IPAddress> CollectLocalIpAddresses(
        INetworkDiscovery? networkDiscovery
    )
    {
        HashSet<IPAddress> addresses = [];

        if (
            networkDiscovery is not null
            && IPAddress.TryParse(networkDiscovery.InternalIp, out IPAddress? discovered)
            && !IPAddress.IsLoopback(discovered)
        )
            addresses.Add(discovered);

        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (
                    nic.NetworkInterfaceType
                    is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel
                )
                    continue;

                foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(addr.Address))
                        continue;

                    addresses.Add(addr.Address);
                }
            }
        }
        catch
        {
            // Best effort only — the fallback cert still works with localhost/loopback SANs.
        }

        return addresses;
    }

    // Kept as fallback — Task 17 will remove this once DB-only path is stable.
    private X509Certificate2 CombinePublicAndPrivateCerts()
    {
#pragma warning disable CS0618 // Obsolete
        if (!_driver.FileExists(AppFiles.CertFile))
            throw new FileNotFoundException($"Certificate file not found: {AppFiles.CertFile}");

        if (!_driver.FileExists(AppFiles.KeyFile))
            throw new FileNotFoundException($"Private key file not found: {AppFiles.KeyFile}");

        string certPem;
        string keyPem;
        using (StreamReader certReader = new(_driver.OpenRead(AppFiles.CertFile)))
            certPem = certReader.ReadToEnd();
        using (StreamReader keyReader = new(_driver.OpenRead(AppFiles.KeyFile)))
            keyPem = keyReader.ReadToEnd();
#pragma warning restore CS0618

        using X509Certificate2 tempCert = X509Certificate2.CreateFromPem(certPem, keyPem);

        byte[] pkcs12Data = tempCert.Export(X509ContentType.Pkcs12);
        return X509CertificateLoader.LoadPkcs12(pkcs12Data, null);
    }

    // Must stay strictly below the API's renewal window (currently 14 days at
    // nomercy-tv's ServerCertificateController::renew). When the client trips
    // the renewal threshold while the cert is still outside the API window,
    // the API rejects with 400 "Certificate is not due for renewal yet" and
    // we hit a 30-attempt retry storm. 13 days gives a 1-day clock-drift safety
    // margin while keeping the renewal proactive (still ~1 month before
    // browsers start showing warnings for Let's Encrypt's 90-day certs).
    private const int RenewalThresholdDays = 13;

    private bool ValidateSslCertificate()
    {
        lock (_certLock)
        {
            if (_cachedCertificate is null)
                return false;

            // Don't call certificate.Verify() — it may trigger OCSP/CRL network calls
            // which fail when Cloudflare or the internet is down. Just check the expiry date.
            if (_cachedCertificate.NotAfter <= DateTime.Now)
                return false; // Actually expired

            if (_cachedCertificate.NotAfter < DateTime.Now.AddDays(RenewalThresholdDays))
            {
                _logger.LogInformation(
                    "SSL cert expires {NotAfter:yyyy-MM-dd} — will attempt renewal",
                    _cachedCertificate.NotAfter
                );
                return false; // Expiring soon — trigger renewal
            }

            return true;
        }
    }

    private const int CertRetryDelaySeconds = 10;

    // Per-request, not per-operation. The loop already retries maxRetries times with
    // CertRetryDelaySeconds between attempts, and the API answers 202 immediately while
    // issuance is still running, so a single request has no reason to take minutes. The old
    // 10-minute value made one unresponsive attempt cost longer than the whole retry budget.
    private static readonly TimeSpan CertRequestTimeout = TimeSpan.FromSeconds(30);

    public async Task RenewSslCertificate(string? accessToken, int maxRetries = 30)
    {
        if (ValidateSslCertificate())
        {
            _logger.LogInformation("SSL Certificate is valid");
            return;
        }

        bool hasExistingCert = HasValidCertificate();

        _logger.LogInformation(
            !hasExistingCert ? "Generating SSL Certificate..." : "Renewing SSL Certificate..."
        );

        try
        {
            string? token = accessToken;
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Skipping certificate renewal — no auth token available");
                return;
            }

            HttpClient client = _httpClientFactory.CreateClient("cert-renewal");
            client.BaseAddress = new(ExternalServicesConfig.Current.ApiServerBaseUrl);
            client.Timeout = CertRequestTimeout;
            client.DefaultRequestHeaders.Accept.Add(new("application/json"));
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);

            string serverUrl = $"certificate?id={Info.DeviceId}";
            if (hasExistingCert)
                serverUrl = $"renew-certificate?id={Info.DeviceId}";

            for (int attempt = 1; attempt <= maxRetries; attempt++)
                try
                {
                    CertificateDto? result = await FetchCertificate(
                        client,
                        serverUrl,
                        hasExistingCert
                    );
                    if (result != null)
                        return;

                    // null means 202 — cert not ready yet, wait and retry
                    _logger.LogInformation(
                        "Certificate not ready, waiting {CertRetryDelaySeconds}s (attempt {Attempt}/{MaxRetries})",
                        [CertRetryDelaySeconds, attempt, maxRetries]
                    );
                    await DelayBetweenAttemptsAsync(
                        TimeSpan.FromSeconds(CertRetryDelaySeconds),
                        CancellationToken.None
                    );
                }
                catch (CertificateNotDueException ex)
                {
                    // The API's answer means our locally cached NotAfter disagrees
                    // with what it actually has on file — the API is the source of
                    // truth, so pull the current record rather than keep serving a
                    // cert whose real expiry we're guessing at. This is the same
                    // "certificate?id=" fetch a first boot uses, just re-run to
                    // resync instead of to create. A stale local cache otherwise
                    // repeats this exact "will attempt renewal" / "not due yet"
                    // round trip on every boot with no way to correct itself.
                    _logger.LogInformation(
                        "Renewal not due, pulling the current certificate to resync: {Message}",
                        ex.Message
                    );
                    try
                    {
                        await FetchCertificate(client, $"certificate?id={Info.DeviceId}", false);
                    }
                    catch (Exception fetchEx)
                    {
                        _logger.LogWarning(
                            "Could not resync certificate after a not-due renewal: {Message}. "
                                + "Using existing certificate.",
                            fetchEx.Message
                        );
                    }
                    return;
                }
                catch (CertificateUnauthorizedException ex)
                {
                    _logger.LogError("Certificate request aborted: {Message}", ex.Message);
                    return;
                }
                catch (Exception ex)
                    when (attempt < maxRetries
                        // An HttpClient timeout surfaces as TaskCanceledException wrapping a
                        // TimeoutException, which this filter did not match — so the single
                        // most likely transient failure was the one thing that escaped the
                        // retry loop, and on a first boot there is no existing certificate for
                        // the outer handler to swallow it against.
                        && ex
                            is HttpRequestException
                                or InvalidOperationException
                                or TaskCanceledException
                                or TimeoutException
                    )
                {
                    _logger.LogInformation(
                        "Certificate attempt failed: {Message}, retrying in {CertRetryDelaySeconds}s (attempt {Attempt}/{MaxRetries})",
                        [ex.Message, CertRetryDelaySeconds, attempt, maxRetries]
                    );
                    await DelayBetweenAttemptsAsync(
                        TimeSpan.FromSeconds(CertRetryDelaySeconds),
                        CancellationToken.None
                    );
                }
        }
        catch (Exception ex) when (hasExistingCert)
        {
            // Cert exists in DB but renewal failed (Cloudflare down, network issue, etc.)
            // The existing cert is usable — don't block boot
            _logger.LogWarning(
                "Certificate renewal failed: {Message}. Using existing certificate.",
                ex.Message
            );
        }
    }

    private async Task<CertificateDto?> FetchCertificate(
        HttpClient client,
        string serverUrl,
        bool hasExistingCert
    )
    {
        using HttpResponseMessage response = await client.GetAsync(serverUrl);

        if (response.StatusCode == HttpStatusCode.Accepted) // 202 — cert not ready yet
        {
            _logger.LogInformation("Certificate not ready yet (202 Accepted), will retry");
            return null;
        }

        if (response.StatusCode == HttpStatusCode.GatewayTimeout)
            throw new HttpRequestException("Gateway timeout waiting for certificate");

        // 401/403 is terminal for this token — retrying cannot heal an invalid or
        // expired token, and hammering the endpoint 30 times just delays boot by
        // 5 minutes before failing anyway. Bail out; the daily cron / degraded-mode
        // recovery retries later with a refreshed token.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            string unauthorizedBody = await response.Content.ReadAsStringAsync();
            string unauthorizedMessage = ExtractApiMessage(unauthorizedBody);
            throw new CertificateUnauthorizedException(
                $"Certificate API rejected the request with {(int)response.StatusCode} — "
                    + "the auth token is invalid, expired, or this server is not registered to the "
                    + "authenticated account."
                    + (
                        string.IsNullOrEmpty(unauthorizedMessage)
                            ? string.Empty
                            : $" API said: {unauthorizedMessage}"
                    )
            );
        }

        // 400 from the renewal endpoint means the API doesn't think the cert is
        // due yet (it gates renewal at 14 days from expiry). Surface the body
        // as a typed exception so the outer retry loop can bail instead of
        // hammering 30 times with the same predictable rejection.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            string body = await response.Content.ReadAsStringAsync();
            string apiMessage = ExtractApiMessage(body);
            throw new CertificateNotDueException(
                string.IsNullOrEmpty(apiMessage)
                    ? "API rejected renewal with 400 Bad Request (no body)"
                    : apiMessage
            );
        }

        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync();
        ApiResponse<CertificateResponse> data =
            content.FromJson<ApiResponse<CertificateResponse>>()
            ?? throw new("Failed to deserialize certificate JSON");

        if (data.Data is null)
            throw new InvalidOperationException(
                $"Certificate API returned no data (status: {data.Status ?? "unknown"}, message: {data.Message ?? "none"})"
            );

        if (
            string.IsNullOrEmpty(data.Data.PrivateKey)
            || string.IsNullOrEmpty(data.Data.Certificate)
        )
            throw new InvalidOperationException(
                $"Certificate API returned incomplete data (status: {data.Status ?? "unknown"})"
            );

        string certPem = $"{data.Data.Certificate}\n{data.Data.IssuerCertificate}";
        string keyPem = data.Data.PrivateKey;
        string? caPem = string.IsNullOrEmpty(data.Data.CertificateAuthority)
            ? null
            : data.Data.CertificateAuthority;

        // Write to DB
        await using AppDbContext db = new();
        UpsertConfig(db, "ssl_certificate", certPem);
        UpsertConfig(db, "ssl_private_key", keyPem);
        if (!string.IsNullOrEmpty(caPem))
            UpsertConfig(db, "ssl_ca", caPem);
        await db.SaveChangesAsync();

        // Update in-memory cache
        lock (_certLock)
        {
            using X509Certificate2 tempCert = X509Certificate2.CreateFromPem(certPem, keyPem);
            byte[] pkcs12Data = tempCert.Export(X509ContentType.Pkcs12);
            _cachedCertificate = X509CertificateLoader.LoadPkcs12(pkcs12Data, null);
        }

        // Keep file writes alongside DB writes for backwards compat (Task 17 removes these)
#pragma warning disable CS0618 // Obsolete
        if (_driver.FileExists(AppFiles.KeyFile))
            _driver.DeleteFile(AppFiles.KeyFile);
        if (_driver.FileExists(AppFiles.CaFile))
            _driver.DeleteFile(AppFiles.CaFile);
        if (_driver.FileExists(AppFiles.CertFile))
            _driver.DeleteFile(AppFiles.CertFile);

        await WriteTextAsync(AppFiles.KeyFile, keyPem);
        await WriteTextAsync(AppFiles.CaFile, data.Data.CertificateAuthority);
        await WriteTextAsync(AppFiles.CertFile, certPem);
#pragma warning restore CS0618

        _logger.LogInformation(
            !hasExistingCert ? "SSL Certificate created" : "SSL Certificate renewed"
        );
        return new();
    }

    private async Task WriteTextAsync(string path, string content)
    {
        await using Stream stream = _driver.OpenWrite(path, true);
        await using StreamWriter writer = new(stream, encoding: Encoding.UTF8, leaveOpen: true);
        await writer.WriteAsync(content);
        await writer.FlushAsync();
    }

    /// <summary>Sentinel returned by FetchCertificate to indicate a successfully written certificate.</summary>
    private sealed class CertificateDto { }

    /// <summary>
    /// API responded with 400 because the cert is not yet within its renewal
    /// window. Distinct from generic HttpRequestException so the retry loop
    /// can short-circuit instead of hammering the endpoint.
    /// </summary>
    private sealed class CertificateNotDueException : Exception
    {
        public CertificateNotDueException(string message)
            : base(message) { }
    }

    /// <summary>
    /// API responded 401/403 — the token is invalid/expired or the server is not
    /// registered to this account. Terminal for the current attempt window: the
    /// retry loop must bail instead of hammering with the same dead token.
    /// </summary>
    private sealed class CertificateUnauthorizedException : Exception
    {
        public CertificateUnauthorizedException(string message)
            : base(message) { }
    }

    /// <summary>
    /// Pulls the human-readable message out of nomercy-tv's standard error
    /// envelope (<c>{ "status": "...", "message": "..." }</c>). Falls back to
    /// the raw body when the JSON doesn't parse, and to the empty string when
    /// the body is missing — the caller decides how to phrase the no-message
    /// case.
    /// </summary>
    private string ExtractApiMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        try
        {
            ApiResponse<object>? parsed = body.FromJson<ApiResponse<object>>();
            if (!string.IsNullOrEmpty(parsed?.Message))
                return parsed.Message;
        }
        catch
        {
            // Body isn't our standard envelope — fall through to raw return.
        }

        return body.Trim();
    }

    public class ApiResponse<T>
    {
        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("message")]
        public string? Message { get; set; }

        [JsonProperty("data")]
        public T Data { get; set; } = default!;
    }

    public class CertificateResponse
    {
        [JsonProperty("status")]
        public string Status { get; set; } = null!;

        [JsonProperty("certificate")]
        public string Certificate { get; set; } = null!;

        [JsonProperty("private_key")]
        public string PrivateKey { get; set; } = null!;

        [JsonProperty("issuer_certificate")]
        public string IssuerCertificate { get; set; } = null!;

        [JsonProperty("certificate_authority")]
        public string CertificateAuthority { get; set; } = null!;
    }
}
