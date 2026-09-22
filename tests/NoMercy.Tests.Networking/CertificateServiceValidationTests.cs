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
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Information;
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class CertificateServiceValidationTests : IDisposable
{
    private readonly string _certDir;
    private bool _disposed;

    public CertificateServiceValidationTests()
    {
        _certDir = Path.Combine(AppFiles.AppPath, "security", "certs");
        Directory.CreateDirectory(_certDir);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (Directory.Exists(_certDir))
        {
            foreach (string f in Directory.GetFiles(_certDir))
                File.Delete(f);
        }
    }

    private static CertificateService BuildService(IHttpClientFactory? factory = null)
    {
        return new(NullLogger<CertificateService>.Instance, factory ?? new NullHttpClientFactory());
    }

    /// <summary>
    /// Builds a service whose inter-attempt delay is a no-op, so retry tests
    /// exercise the loop's control flow without paying the real 10s production
    /// wait per retry.
    /// </summary>
    private static CertificateService BuildFastRetryService(IHttpClientFactory factory)
    {
        return new NoDelayCertificateService(factory);
    }

    private sealed class NoDelayCertificateService(IHttpClientFactory factory)
        : CertificateService(NullLogger<CertificateService>.Instance, factory)
    {
        protected override Task DelayBetweenAttemptsAsync(TimeSpan delay, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static X509Certificate2 CreateSelfSignedCert(DateTimeOffset notAfter)
    {
        using RSA rsa = RSA.Create(2048);
        CertificateRequest req = new(
            "CN=test.nomercy.tv",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), notAfter);
    }

    private static void InjectCachedCertificate(CertificateService service, X509Certificate2? cert)
    {
        FieldInfo field = typeof(CertificateService).GetField(
            "_cachedCertificate",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        field.SetValue(service, cert);
    }

    private static void WritePemFiles(string certDir, X509Certificate2 cert)
    {
#pragma warning disable CS0618
        string certPath = AppFiles.CertFile;
        string keyPath = AppFiles.KeyFile;
#pragma warning restore CS0618

        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);
        File.WriteAllText(certPath, cert.ExportCertificatePem());

        using RSA? rsa = cert.GetRSAPrivateKey();
        File.WriteAllText(keyPath, rsa is null ? string.Empty : rsa.ExportRSAPrivateKeyPem());
    }

    /// <summary>
    /// Subclass that overrides the DB read seam so tests never need a real SQLite connection.
    /// </summary>
    private sealed class StubCertificateService(string? certPem, IHttpClientFactory? factory = null)
        : CertificateService(
            NullLogger<CertificateService>.Instance,
            factory ?? new NullHttpClientFactory()
        )
    {
        protected override string? ReadCertificatePemFromDb() => certPem;
    }

    private sealed class NullHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException(
                "HttpClientFactory must not be called in this test"
            );
    }

    private sealed class StubHttpClientFactory(
        Func<HttpRequestMessage, HttpResponseMessage> handler
    ) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new DelegatingFakeHandler(handler))
            {
                BaseAddress = new("https://test.nomercy.tv/v1/server/"),
            };

        private sealed class DelegatingFakeHandler(
            Func<HttpRequestMessage, HttpResponseMessage> handler
        ) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken
            ) => Task.FromResult(handler(request));
        }
    }

    [Fact]
    public void HasValidCertificate_ReturnsFalse_WhenCachedCertIsExpired()
    {
        CertificateService service = BuildService();
        using X509Certificate2 expired = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddSeconds(-1));
        InjectCachedCertificate(service, expired);

        bool result = service.HasValidCertificate();

        Assert.False(result);
    }

    [Fact]
    public void HasValidCertificate_ReturnsTrue_WhenCachedCertIsValid()
    {
        CertificateService service = BuildService();
        using X509Certificate2 valid = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddDays(30));
        InjectCachedCertificate(service, valid);

        bool result = service.HasValidCertificate();

        Assert.True(result);
    }

    [Fact]
    public void HasValidCertificate_ReturnsFalse_WhenOnlyCertFileExists_KeyFileMissing()
    {
        CertificateService service = BuildService();
        using X509Certificate2 cert = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddDays(30));
#pragma warning disable CS0618
        File.WriteAllText(AppFiles.CertFile, cert.ExportCertificatePem());
#pragma warning restore CS0618

        bool result = service.HasValidCertificate();

        Assert.False(result);
    }

    [Fact]
    public void HasValidCertificate_ReturnsTrue_WhenBothLegacyPemFilesExist()
    {
        CertificateService service = BuildService();
        using X509Certificate2 cert = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddDays(30));
        WritePemFiles(_certDir, cert);

        bool result = service.HasValidCertificate();

        Assert.True(result);
    }

    [Fact]
    public void HasValidCertificate_ReturnsFalse_WhenNoCertAnywhere()
    {
        CertificateService service = BuildService();

        bool result = service.HasValidCertificate();

        Assert.False(result);
    }

    // Regression: legacy PEM fallback must parse and check NotAfter like the DB path,
    // not just check file presence. Without the fix, an expired PEM left on disk from a
    // pre-DB-storage install made HasValidCertificate() return true, so BootOrchestrator
    // skipped re-registration and the recovery loop marked Registered permanently.

    [Fact]
    public void HasValidCertificate_ReturnsFalse_WhenLegacyPemFilesAreExpired()
    {
        CertificateService service = BuildService();
        using X509Certificate2 expired = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddSeconds(-1));
        WritePemFiles(_certDir, expired);

        bool result = service.HasValidCertificate();

        Assert.False(result);
    }

    [Fact]
    public void HasValidCertificate_ReturnsFalse_WhenLegacyCertFileIsMalformed()
    {
        CertificateService service = BuildService();
#pragma warning disable CS0618
        File.WriteAllText(AppFiles.CertFile, "not a real certificate");
        File.WriteAllText(AppFiles.KeyFile, "not a real key");
#pragma warning restore CS0618

        bool result = service.HasValidCertificate();

        Assert.False(result);
    }

    // Regression: DB-presence branch must load + check NotAfter, not just check row existence.
    // Without the fix, HasValidCertificate() returned true for any row regardless of expiry.

    [Fact]
    public void HasValidCertificate_ReturnsFalse_WhenDbCertIsExpired()
    {
        using X509Certificate2 expired = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddSeconds(-1));
        string certPem = expired.ExportCertificatePem();
        StubCertificateService service = new(certPem);

        bool result = service.HasValidCertificate();

        Assert.False(result);
    }

    [Fact]
    public void HasValidCertificate_ReturnsTrue_WhenDbCertIsValid()
    {
        using X509Certificate2 valid = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddDays(30));
        string certPem = valid.ExportCertificatePem();
        StubCertificateService service = new(certPem);

        bool result = service.HasValidCertificate();

        Assert.True(result);
    }

    [Fact]
    public async Task RenewSslCertificate_CallsFactory_WhenCachedCertIsNull()
    {
        bool factoryCalled = false;
        StubHttpClientFactory factory = new(_ =>
        {
            factoryCalled = true;
            return new(HttpStatusCode.Accepted);
        });
        CertificateService service = BuildFastRetryService(factory);

        await service.RenewSslCertificate("test-token", maxRetries: 1);

        Assert.True(factoryCalled);
    }

    [Fact]
    public async Task RenewSslCertificate_DoesNotCallFactory_WhenCertValidBeyondRenewalThreshold()
    {
        bool factoryCalled = false;
        StubHttpClientFactory factory = new(_ =>
        {
            factoryCalled = true;
            return new(HttpStatusCode.OK);
        });
        CertificateService service = BuildService(factory);
        using X509Certificate2 valid = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddDays(14));
        InjectCachedCertificate(service, valid);

        await service.RenewSslCertificate("test-token", maxRetries: 1);

        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task RenewSslCertificate_CallsFactory_WhenCertExpiresWithinRenewalThreshold()
    {
        bool factoryCalled = false;
        StubHttpClientFactory factory = new(_ =>
        {
            factoryCalled = true;
            return new(HttpStatusCode.Accepted);
        });
        CertificateService service = BuildFastRetryService(factory);
        using X509Certificate2 nearExpiry = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddDays(12));
        InjectCachedCertificate(service, nearExpiry);

        await service.RenewSslCertificate("test-token", maxRetries: 1);

        Assert.True(factoryCalled);
    }

    [Fact]
    public async Task RenewSslCertificate_CallsFactory_WhenCertIsActuallyExpired()
    {
        bool factoryCalled = false;
        StubHttpClientFactory factory = new(_ =>
        {
            factoryCalled = true;
            return new(HttpStatusCode.Accepted);
        });
        CertificateService service = BuildFastRetryService(factory);
        using X509Certificate2 expired = CreateSelfSignedCert(DateTimeOffset.UtcNow.AddSeconds(-1));
        InjectCachedCertificate(service, expired);

        await service.RenewSslCertificate("test-token", maxRetries: 1);

        Assert.True(factoryCalled);
    }

    [Fact]
    public async Task RenewSslCertificate_SkipsHttpCall_WhenTokenIsNull()
    {
        bool factoryCalled = false;
        StubHttpClientFactory factory = new(_ =>
        {
            factoryCalled = true;
            return new(HttpStatusCode.OK);
        });
        CertificateService service = BuildService(factory);

        await service.RenewSslCertificate(accessToken: null, maxRetries: 1);

        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task RenewSslCertificate_SkipsHttpCall_WhenTokenIsEmpty()
    {
        bool factoryCalled = false;
        StubHttpClientFactory factory = new(_ =>
        {
            factoryCalled = true;
            return new(HttpStatusCode.OK);
        });
        CertificateService service = BuildService(factory);

        await service.RenewSslCertificate(accessToken: string.Empty, maxRetries: 1);

        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task RenewSslCertificate_DoesNotRetry_WhenApiReturns400()
    {
        int callCount = 0;
        StubHttpClientFactory factory = new(_ =>
        {
            callCount++;
            return new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"status\":\"error\",\"message\":\"Certificate is not due for renewal yet\"}"
                ),
            };
        });
        CertificateService service = BuildService(factory);

        await service.RenewSslCertificate("test-token", maxRetries: 5);

        // 1 renewal attempt + 1 resync fetch of the current certificate — the
        // API's "not due" answer means our cached expiry disagrees with its
        // record, so the service pulls the real one instead of trusting the
        // stale local cache.
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RenewSslCertificate_ShortCircuits_On400WithEmptyBody()
    {
        int callCount = 0;
        StubHttpClientFactory factory = new(_ =>
        {
            callCount++;
            return new(HttpStatusCode.BadRequest) { Content = new StringContent(string.Empty) };
        });
        CertificateService service = BuildService(factory);

        await service.RenewSslCertificate("test-token", maxRetries: 5);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RenewSslCertificate_FallsBackToRawBody_When400BodyIsNotJson()
    {
        int callCount = 0;
        StubHttpClientFactory factory = new(_ =>
        {
            callCount++;
            return new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("plain text error message"),
            };
        });
        CertificateService service = BuildService(factory);

        await service.RenewSslCertificate("test-token", maxRetries: 3);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RenewSslCertificate_Retries_On202_UpToMaxRetries()
    {
        int callCount = 0;
        StubHttpClientFactory factory = new(_ =>
        {
            callCount++;
            return new(HttpStatusCode.Accepted);
        });
        CertificateService service = BuildFastRetryService(factory);

        await service.RenewSslCertificate("test-token", maxRetries: 2);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RenewSslCertificate_NotDue_ResyncsTheCurrentCertificateInstead()
    {
        List<string?> requestedPaths = [];
        StubHttpClientFactory factory = new(request =>
        {
            requestedPaths.Add(request.RequestUri?.AbsolutePath);
            return new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"status\":\"error\",\"message\":\"Certificate is not due for renewal yet\"}"
                ),
            };
        });
        CertificateService service = BuildService(factory);
        // Not expired (HasValidCertificate: true) but within the renewal
        // threshold (ValidateSslCertificate: false), so the first call really
        // is a renewal attempt rather than the first-boot "create" path.
        InjectCachedCertificate(service, CreateSelfSignedCert(DateTimeOffset.UtcNow.AddDays(5)));

        await service.RenewSslCertificate("test-token", maxRetries: 5);

        Assert.Equal(2, requestedPaths.Count);
        Assert.Contains("renew-certificate", requestedPaths[0]);
        Assert.DoesNotContain("renew-certificate", requestedPaths[1]);
        Assert.Contains("certificate", requestedPaths[1]);
    }

    [Fact]
    public async Task RenewSslCertificate_NotDue_DoesNotThrow_WhenTheResyncAlsoFails()
    {
        int callCount = 0;
        StubHttpClientFactory factory = new(_ =>
        {
            callCount++;
            // Both the renewal attempt and the resync fetch fail the same way —
            // a not-due renewal must never leave the caller (BootOrchestrator)
            // with an unhandled exception just because the API is also having a
            // bad day on the plain fetch.
            return new(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(
                    "{\"status\":\"error\",\"message\":\"Certificate is not due for renewal yet\"}"
                ),
            };
        });
        CertificateService service = BuildService(factory);

        Exception? thrown = await Record.ExceptionAsync(() =>
            service.RenewSslCertificate("test-token", maxRetries: 5)
        );

        Assert.Null(thrown);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task RenewSslCertificate_Retries_OnGatewayTimeout()
    {
        int callCount = 0;
        StubHttpClientFactory factory = new(_ =>
        {
            callCount++;
            return new(HttpStatusCode.GatewayTimeout);
        });
        CertificateService service = BuildFastRetryService(factory);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.RenewSslCertificate("test-token", maxRetries: 2)
        );

        Assert.Equal(2, callCount);
    }

    [Fact]
    public void CatalogueCompleteness_AllGuardRulesAreCoveredOrDocumented()
    {
        string sourceFile = FindSourceFile(
            "src/NoMercy.Networking/Certificate/CertificateService.cs"
        );
        string source = File.ReadAllText(sourceFile);

        Assert.Contains("_cachedCertificate.NotAfter > DateTime.Now", source);

        // DB branch must load + check NotAfter, not just assert row presence.
        Assert.Contains("ReadCertificatePemFromDb", source);
        Assert.Contains("dbCert.NotAfter > DateTime.Now", source);

        Assert.Contains(
            "!_driver.FileExists(AppFiles.CertFile) || !_driver.FileExists(AppFiles.KeyFile)",
            source
        );

        // Legacy fallback must parse NotAfter, not just check presence.
        Assert.Contains("legacyCert.NotAfter > DateTime.Now", source);

        Assert.Contains("_cachedCertificate is null", source);

        Assert.Contains("_cachedCertificate.NotAfter <= DateTime.Now", source);

        Assert.Contains("RenewalThresholdDays", source);
        Assert.Contains("private const int RenewalThresholdDays = 13", source);

        Assert.Contains("string.IsNullOrEmpty(token)", source);

        Assert.Contains("HttpStatusCode.Accepted", source);

        Assert.Contains("HttpStatusCode.BadRequest", source);

        Assert.Contains("CertificateNotDueException", source);

        Assert.Contains("string.IsNullOrWhiteSpace(body)", source);
        Assert.Contains("parsed?.Message", source);

        Assert.Contains("HttpProtocols.Http1", source);
    }

    private static string FindSourceFile(string relativePath)
    {
        return RepoPaths.At(relativePath);
    }
}
