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
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Revocation;
using NoMercy.PluginSdk.Verification;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The start-up load reads a list asked for just before it, and a server that
/// gets no answer in time still loads with the stored list (AUD-1134).
/// </summary>
[Collection(EchoConstructionMarkerCollection.Name)]
public class PluginRevocationStartupRefreshTests : IDisposable
{
    private const string ConstructionRootEnvVar = "NOMERCY_TEST_ECHO_CONSTRUCTION_ROOT";
    private const string EchoDll = "NoMercy.Plugin.Samples.Echo.dll";
    private const string IssuedAt = "2026-05-20T12:00:00+00:00";

    private static readonly Ulid Echo = Ulid.Parse("01ECH000000000000000000000");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        IssuedAt,
        System.Globalization.CultureInfo.InvariantCulture
    );
    private static readonly PluginTrustAddresses Addresses = new(
        new("https://example.test/keys.json"),
        new("https://example.test/revocations.json"),
        () => new("https://example.test/entitlements")
    );

    private readonly string _pluginsDir = Path.Combine(
        Path.GetTempPath(),
        "nomercy-startup-refresh-" + Ulid.NewUlid()
    );
    private readonly string _echoDir;
    private readonly InMemoryEventBus _eventBus = new();
    private readonly PluginRegistry _registry = new();
    private readonly PluginConsentService _consent = new(new InMemoryConsentStore());
    private readonly PluginRevocationStore _store;
    private readonly PluginRunGate _gate;
    private readonly string _publicKey;
    private readonly Ed25519PrivateKeyParameters _privateKey;

    public PluginRevocationStartupRefreshTests()
    {
        _echoDir = Path.Combine(_pluginsDir, "Echo");
        Directory.CreateDirectory(_echoDir);
        Environment.SetEnvironmentVariable(ConstructionRootEnvVar, _pluginsDir);

        Ed25519KeyPairGenerator generator = new();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();
        _privateKey = (Ed25519PrivateKeyParameters)pair.Private;
        _publicKey = Convert.ToBase64String(((Ed25519PublicKeyParameters)pair.Public).GetEncoded());

        // The list this server stored on its last run: it names nothing.
        _store = new(Path.Combine(_pluginsDir, "config"));
        _store.Save(new(Now, []));
        _gate = new([
            new PluginRevocationRunCheck(
                new(_store, new StubClock(Now), new(false, NullLogger.Instance)),
                _store
            ),
            new PluginConsentRunCheck(_consent),
        ]);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ConstructionRootEnvVar, null);

        GC.Collect();
        GC.WaitForPendingFinalizers();

        try
        {
            if (Directory.Exists(_pluginsDir))
                Directory.Delete(_pluginsDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_load_uses_a_list_fetched_before_it_when_the_fetch_answers_in_time()
    {
        StageEcho();
        string entries = $$"""[{"listing_id":"{{Echo}}","reason":"plugins.revoked.security"}]""";

        await Refresh(new AnsweringHandler(SignedList(entries))).RefreshBeforeLoadAsync();
        await Loader().LoadPluginFromManifestAsync(Path.Combine(_echoDir, "plugin.json"));

        EchoRan().Should().BeFalse("the load reads the list asked for at start-up (AUD-1134)");
        Registered().Info.Status.Should().Be(PluginStatus.Disabled);
        Registered().Instance.Should().BeNull();
        _gate.LastRefusal(Echo)!.Code.Should().Be(PluginRefusalCodes.Revoked);
    }

    [Fact]
    public async Task A_fetch_that_does_not_answer_in_time_lets_the_load_go_on_with_the_stored_list()
    {
        StageEcho();
        PluginRevocationStartupRefresh refresh = Refresh(
            new SilentHandler(),
            TimeSpan.FromMilliseconds(50)
        );

        await refresh.RefreshBeforeLoadAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await Loader().LoadPluginFromManifestAsync(Path.Combine(_echoDir, "plugin.json"));

        _store.Current.Entries.Should().BeEmpty("the stored list is kept");
        EchoRan().Should().BeTrue("an offline server still starts its plugins");
        Registered().Info.Status.Should().Be(PluginStatus.Active);
    }

    private PluginRevocationStartupRefresh Refresh(
        HttpMessageHandler handler,
        TimeSpan? bound = null
    )
    {
        HttpClient http = new(handler);
        PluginTrustedKeys trusted = new(
            new Dictionary<string, string> { ["nomercy-1"] = _publicKey }
        );

        return new(
            new(
                http,
                new(new Dictionary<string, string>()),
                NullLogger<PluginMarketplaceKeyClient>.Instance
            ),
            new(http, _store, trusted, NullLogger<PluginRevocationClient>.Instance),
            Addresses,
            NullLogger<PluginRevocationStartupRefresh>.Instance,
            bound
        );
    }

    private string SignedList(string entries)
    {
        Ed25519Signer signer = new();
        signer.Init(true, _privateKey);
        byte[] bytes = Encoding.UTF8.GetBytes($"{IssuedAt}{entries}");
        signer.BlockUpdate(bytes, 0, bytes.Length);

        return JsonSerializer.Serialize(
            new
            {
                issued_at = IssuedAt,
                entries = JsonDocument.Parse(entries).RootElement,
                signature = new
                {
                    kid = "nomercy-1",
                    alg = "ed25519",
                    value = Convert.ToBase64String(signer.GenerateSignature()),
                },
            }
        );
    }

    private bool EchoRan() =>
        File.Exists(Path.Combine(_pluginsDir, "echo-type-initialized.marker"))
        || File.Exists(Path.Combine(_pluginsDir, "echo-constructed.marker"));

    private LoadedPlugin Registered()
    {
        _registry.TryGetValue(Echo, out LoadedPlugin? loaded).Should().BeTrue();
        return loaded!;
    }

    private PluginLoader Loader() =>
        new(
            _eventBus,
            new GateServiceProvider(_gate),
            NullLogger.Instance,
            _pluginsDir,
            TestStorageHelper.CreateStorage(_pluginsDir),
            _registry,
            new PluginVerifier(),
            _consent,
            TestPluginPlatform.ContextFactory(
                _eventBus,
                TestStorageHelper.CreateStorage(_pluginsDir)
            )
        );

    private void StageEcho()
    {
        string binDir = Path.GetDirectoryName(
            CodeScanVerificationStageTests.SampleDllPath("NoMercy.Plugin.Samples.Echo")
        )!;

        foreach (string file in Directory.EnumerateFiles(binDir, "*.dll"))
            File.Copy(file, Path.Combine(_echoDir, Path.GetFileName(file)), overwrite: true);

        foreach (string file in Directory.EnumerateFiles(binDir, "*.deps.json"))
            File.Copy(file, Path.Combine(_echoDir, Path.GetFileName(file)), overwrite: true);

        string manifestJson = $$"""
            {
              "id": "{{Echo}}",
              "name": "Echo",
              "version": "0.1.0",
              "description": "Sample plugin for the test suite",
              "assembly": "{{EchoDll}}",
              "autoEnabled": true
            }
            """;
        File.WriteAllText(Path.Combine(_echoDir, "plugin.json"), manifestJson);

        InstalledPluginFolder.Record(_pluginsDir, _echoDir);
    }

    private sealed class AnsweringHandler(string revocations) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                request.RequestUri == Addresses.Revocations
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(revocations),
                    }
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
            );
    }

    /// <summary>A server that never answers: the request ends only when it is canceled.</summary>
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new(HttpStatusCode.NotFound);
        }
    }

    private sealed class GateServiceProvider(IPluginRunGate gate) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IPluginRunGate) ? gate : null;
    }

    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
