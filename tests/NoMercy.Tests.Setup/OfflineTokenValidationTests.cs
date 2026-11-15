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

using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NoMercy.Networking.Certificate;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Auth;
using NoMercy.Setup.Boot;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Tests.Common;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup;

[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public class OfflineTokenValidationTests : IDisposable
{
    private readonly string _testAuthKeysFile;
    private readonly string _originalAuthBaseUrl = ExternalServicesConfig.Current.AuthBaseUrl;

    public OfflineTokenValidationTests()
    {
        _testAuthKeysFile = AppFiles.AuthKeysFile;

        // Ensure config directory exists
        string configDir = AppFiles.ConfigPath;
        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        // Clean up any previous test cache
        if (File.Exists(_testAuthKeysFile))
            File.Delete(_testAuthKeysFile);
        if (File.Exists(AppFiles.JwksCacheFile))
            File.Delete(AppFiles.JwksCacheFile);
    }

    public void Dispose()
    {
        ExternalServicesConfig.Current.AuthBaseUrl = _originalAuthBaseUrl;
        if (File.Exists(_testAuthKeysFile))
            File.Delete(_testAuthKeysFile);
        if (File.Exists(AppFiles.JwksCacheFile))
            File.Delete(AppFiles.JwksCacheFile);
        GC.SuppressFinalize(this);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static string TwoKeyJwks(string kid1, string kid2)
    {
        string Key(string kid)
        {
            using RSA rsa = RSA.Create(2048);
            RSAParameters p = rsa.ExportParameters(false);
            return $$"""
                {"kid":"{{kid}}","kty":"RSA","alg":"RS256","use":"sig","n":"{{Base64UrlEncoder.Encode(p.Modulus!)}}","e":"{{Base64UrlEncoder.Encode(p.Exponent!)}}"}
                """;
        }

        return $$"""{"keys":[{{Key(kid1)}},{{Key(kid2)}}]}""";
    }

    private static HttpResponseMessage Json(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };

    private static BootOrchestrator BuildOrchestrator(HttpMessageHandler handler) =>
        new(
            new SetupState(),
            new AuthManager(new(), new LocalStorageDriver(), new AuthTokenStore()),
            Mock.Of<IApiKeyLoader>(),
            Mock.Of<IDegradedModeRecovery>(),
            Mock.Of<IServerRegistrationService>(),
            new AuthTokenStore(),
            new CertificateService(NullLogger<CertificateService>.Instance, null!),
            new StubHttpClientFactory(handler)
        );

    private static bool HasKey(string kid) =>
        OfflineJwksCache.CachedSigningKeys.Any(k => k.KeyId == kid);

    [Fact]
    public async Task OnlineBootCheck_CachesEveryRealmSigningKey()
    {
        ExternalServicesConfig.Current.AuthBaseUrl = "https://auth.example.test/realms/Test/";
        string jwks = TwoKeyJwks("kid-one", "kid-two");
        StubHandler handler = new(request =>
            Json(request.RequestUri!.AbsolutePath.EndsWith("/certs") ? jwks : "{}")
        );

        MethodInfo check =
            typeof(BootOrchestrator).GetMethod(
                "CheckKeycloakReachabilityAsync",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new MissingMethodException("CheckKeycloakReachabilityAsync");
        await (Task)check.Invoke(BuildOrchestrator(handler), null)!;

        Assert.Contains(
            "https://auth.example.test/realms/Test/protocol/openid-connect/certs",
            handler.Requests
        );
        Assert.True(File.Exists(AppFiles.JwksCacheFile), "the JWKS cache file should exist");
        Assert.True(HasKey("kid-one"));
        Assert.True(HasKey("kid-two"));
    }

    [Fact]
    public void CachedJwks_LoadsEveryKeyByKid_WithoutNetwork()
    {
        ExternalServicesConfig.Current.AuthBaseUrl = "https://auth.example.test/realms/Test/";
        File.WriteAllText(AppFiles.JwksCacheFile, TwoKeyJwks("kid-a", "kid-b"));

        bool loaded = OfflineJwksCache.LoadCachedPublicKey();

        Assert.True(loaded);
        Assert.True(HasKey("kid-a"));
        Assert.True(HasKey("kid-b"));
    }

    [Fact]
    public async Task Refresh_RefusesAPlainHttpRealm_AndFetchesNothing()
    {
        ExternalServicesConfig.Current.AuthBaseUrl = "http://auth.example.test/realms/Test/";
        StubHandler handler = new(_ => Json(TwoKeyJwks("kid-http-1", "kid-http-2")));
        using HttpClient client = new(handler, disposeHandler: false);

        bool refreshed = await OfflineJwksCache.RefreshAsync(client);

        Assert.False(refreshed);
        Assert.Empty(handler.Requests);
        Assert.False(File.Exists(AppFiles.JwksCacheFile));
        Assert.False(HasKey("kid-http-1"));
    }

    [Fact]
    public void CachePublicKey_WritesFileAndSetsKey()
    {
        using RSA rsa = RSA.Create(2048);
        string publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        OfflineJwksCache.CachePublicKey(publicKeyBase64);

        Assert.True(
            File.Exists(_testAuthKeysFile),
            "CachePublicKey should create the auth keys cache file"
        );
        Assert.NotNull(OfflineJwksCache.CachedSigningKey);

        string fileContent = File.ReadAllText(_testAuthKeysFile).Trim();
        Assert.Equal(publicKeyBase64, fileContent);
    }

    [Fact]
    public void LoadCachedPublicKey_ReturnsFalse_WhenNoFile()
    {
        if (File.Exists(_testAuthKeysFile))
            File.Delete(_testAuthKeysFile);

        bool result = OfflineJwksCache.LoadCachedPublicKey();

        Assert.False(result, "LoadCachedPublicKey should return false when no cache file exists");
    }

    [Fact]
    public void LoadCachedPublicKey_ReturnsTrue_WhenValidFile()
    {
        using RSA rsa = RSA.Create(2048);
        string publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        File.WriteAllText(_testAuthKeysFile, publicKeyBase64);

        bool result = OfflineJwksCache.LoadCachedPublicKey();

        Assert.True(result, "LoadCachedPublicKey should return true with a valid cache file");
        Assert.NotNull(OfflineJwksCache.CachedSigningKey);
    }

    [Fact]
    public void LoadCachedPublicKey_ReturnsFalse_WhenEmptyFile()
    {
        File.WriteAllText(_testAuthKeysFile, "");

        bool result = OfflineJwksCache.LoadCachedPublicKey();

        Assert.False(result, "LoadCachedPublicKey should return false for empty cache file");
    }

    [Fact]
    public void LoadCachedPublicKey_ReturnsFalse_WhenCorruptFile()
    {
        File.WriteAllText(_testAuthKeysFile, "not-valid-base64!@#$");

        bool result = OfflineJwksCache.LoadCachedPublicKey();

        Assert.False(result, "LoadCachedPublicKey should return false for corrupt cache file");
    }

    [Fact]
    public void CreateSecurityKeyFromBase64_ProducesValidRsaKey()
    {
        using RSA rsa = RSA.Create(2048);
        string publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        RsaSecurityKey key = OfflineJwksCache.CreateSecurityKeyFromBase64(publicKeyBase64);

        Assert.NotNull(key);
        Assert.NotNull(key.Rsa);
    }

    [Fact]
    public void CachedKey_CanValidateJwtSignature()
    {
        // Generate an RSA keypair
        using RSA rsa = RSA.Create(2048);
        RsaSecurityKey signingKey = new(rsa);
        SigningCredentials signingCredentials = new(signingKey, SecurityAlgorithms.RsaSha256);

        // Create a JWT signed with the private key
        JwtSecurityTokenHandler handler = new();
        SecurityTokenDescriptor descriptor = new()
        {
            Subject = new([
                new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new("scope", "openid profile"),
            ]),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = "https://auth.nomercy.tv/realms/NoMercyTV/",
            Audience = "nomercy-server",
            SigningCredentials = signingCredentials,
        };
        string token = handler.WriteToken(handler.CreateToken(descriptor));

        // Cache the public key (simulating what Auth.AuthKeys does)
        string publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
        OfflineJwksCache.CachePublicKey(publicKeyBase64);

        // Validate the JWT using the cached key (offline validation)
        RsaSecurityKey? cachedKey = OfflineJwksCache.CachedSigningKey;
        Assert.NotNull(cachedKey);

        TokenValidationParameters validationParams = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = cachedKey,
            ValidIssuer = "https://auth.nomercy.tv/realms/NoMercyTV/",
            ValidAudience = "nomercy-server",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
        };

        ClaimsPrincipal principal = handler.ValidateToken(
            token,
            validationParams,
            out SecurityToken validatedToken
        );

        Assert.NotNull(principal);
        Assert.NotNull(validatedToken);
        Assert.NotNull(principal.FindFirst(ClaimTypes.NameIdentifier));
    }

    [Fact]
    public void CachedKey_RejectsTokenSignedWithDifferentKey()
    {
        // Generate two different RSA keypairs
        using RSA signingRsa = RSA.Create(2048);
        using RSA differentRsa = RSA.Create(2048);

        RsaSecurityKey signingKey = new(signingRsa);
        SigningCredentials signingCredentials = new(signingKey, SecurityAlgorithms.RsaSha256);

        // Create a JWT signed with key A
        JwtSecurityTokenHandler handler = new();
        SecurityTokenDescriptor descriptor = new()
        {
            Subject = new([new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())]),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = "https://auth.nomercy.tv/realms/NoMercyTV/",
            Audience = "nomercy-server",
            SigningCredentials = signingCredentials,
        };
        string token = handler.WriteToken(handler.CreateToken(descriptor));

        // Cache key B (different from signing key A)
        string differentKeyBase64 = Convert.ToBase64String(
            differentRsa.ExportSubjectPublicKeyInfo()
        );
        OfflineJwksCache.CachePublicKey(differentKeyBase64);

        RsaSecurityKey? cachedKey = OfflineJwksCache.CachedSigningKey;
        Assert.NotNull(cachedKey);

        TokenValidationParameters validationParams = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = cachedKey,
            ValidIssuer = "https://auth.nomercy.tv/realms/NoMercyTV/",
            ValidAudience = "nomercy-server",
            ValidateLifetime = true,
        };

        // Validation should fail — wrong key
        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(() =>
            handler.ValidateToken(token, validationParams, out _)
        );
    }

    [Fact]
    public void CacheRoundTrip_PreservesKeyFidelity()
    {
        // Generate key, cache it, load it, verify it can still validate
        using RSA rsa = RSA.Create(2048);
        string publicKeyBase64 = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        // Cache to file
        OfflineJwksCache.CachePublicKey(publicKeyBase64);

        // Load from file (simulates a server restart)
        bool loaded = OfflineJwksCache.LoadCachedPublicKey();
        Assert.True(loaded);

        // Sign a token with the private key
        RsaSecurityKey signingKey = new(rsa);
        JwtSecurityTokenHandler handler = new();
        SecurityTokenDescriptor descriptor = new()
        {
            Subject = new([new(ClaimTypes.NameIdentifier, "test-user")]),
            Expires = DateTime.UtcNow.AddHours(1),
            Issuer = "test-issuer",
            Audience = "test-audience",
            SigningCredentials = new(signingKey, SecurityAlgorithms.RsaSha256),
        };
        string token = handler.WriteToken(handler.CreateToken(descriptor));

        // Validate with the loaded cached key
        TokenValidationParameters validationParams = new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = OfflineJwksCache.CachedSigningKey,
            ValidIssuer = "test-issuer",
            ValidAudience = "test-audience",
        };

        ClaimsPrincipal principal = handler.ValidateToken(token, validationParams, out _);
        Assert.NotNull(principal);
        Assert.Equal("test-user", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
    }

    [Fact]
    public void AppFiles_HasAuthKeysFilePath()
    {
        string path = AppFiles.AuthKeysFile;

        Assert.NotNull(path);
        Assert.EndsWith("auth_keys.json", path);
        Assert.Contains("config", path);
    }

    [Fact]
    public void AppFiles_HasJwksCacheFilePath()
    {
        string path = AppFiles.JwksCacheFile;

        Assert.NotNull(path);
        Assert.EndsWith("jwks_cache.json", path);
        Assert.Contains("config", path);
    }
}

[Trait("Category", "Unit")]
public class OfflineTokenValidationIntegrationTests
{
    [Fact]
    public void AuthKeysMethod_CachesPublicKey_WhenSourceHasCode()
    {
        // Auth.cs was replaced by AuthManager.cs — verify OfflineJwksCache.LoadCachedPublicKey
        // is still called during token initialization (AuthManager.InitializeAsync).
        string source = File.ReadAllText(
            RepoPaths.At("src", "NoMercy.Setup", "Auth", "AuthManager.cs")
        );

        Assert.Contains("OfflineJwksCache.LoadCachedPublicKey", source);
    }

    [Fact]
    public void InitWithFallback_LoadsCachedKeys()
    {
        // Auth.cs was replaced by AuthManager.cs — verify OfflineJwksCache.LoadCachedPublicKey
        // is called during token initialization in the new implementation.
        string source = File.ReadAllText(
            RepoPaths.At("src", "NoMercy.Setup", "Auth", "AuthManager.cs")
        );

        Assert.Contains("OfflineJwksCache.LoadCachedPublicKey", source);
    }

    [Fact]
    public void ServiceConfiguration_UsesIssuerSigningKeyResolver()
    {
        // Verify the JWT bearer config includes the offline key resolver
        // ServiceConfiguration is split into partial files (ServiceConfiguration*.cs);
        // the JWT bearer setup lives in ServiceConfiguration.Auth.cs.
        string configDir = RepoPaths.At("src", "NoMercy.Service", "Configuration");

        string source = string.Empty;
        foreach (string file in Directory.GetFiles(configDir, "ServiceConfiguration*.cs"))
            source += File.ReadAllText(file);

        Assert.Contains("IssuerSigningKeyResolver", source);
        Assert.Contains("OfflineJwksCache.CachedSigningKey", source);
    }
}
