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
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Lan;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Plugins;

/// <summary>
/// A plugin's LAN routes through the real pipeline: a device on the owner's
/// network with its credential reaches the routes the manifest names, as a
/// device and never as a user, and every other request gets one and the same
/// refusal, whichever part of it was wrong.
/// </summary>
[Trait("Category", "Authorization")]
public class PluginLanRouteTests
{
    private const string Lan = "192.168.1.20";
    private const string PeerHeader = "X-Test-Peer";

    private static readonly Ulid LiveTv = Ulid.Parse("01J9ZK5V8Y00000000000000A7");
    private static readonly Ulid OtherPlugin = Ulid.Parse("01J9ZK5V8Y00000000000000A8");

    private static readonly PluginCapabilities WithLanRoutes = new()
    {
        Rest = true,
        Lan = new()
        {
            Routes =
            [
                "hdhr/discover.json",
                "hdhr/lineup.json",
                "hdhr/auto/{channel}",
                "hdhr/stream",
            ],
        },
    };

    [Fact]
    public async Task A_device_on_the_lan_reaches_a_route_the_manifest_names()
    {
        await using LanFactory factory = new(WithLanRoutes);
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();

        HttpResponseMessage response = await Get(client, Address(device, "hdhr/lineup.json"), Lan);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("lineup");
    }

    [Fact]
    public async Task A_named_value_reaches_the_plugin()
    {
        await using LanFactory factory = new(WithLanRoutes);
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();

        HttpResponseMessage response = await Get(client, Address(device, "hdhr/auto/v5.1"), Lan);

        (await response.Content.ReadAsStringAsync()).Should().Be("channel=v5.1");
    }

    [Fact]
    public async Task The_plugin_sees_the_device_and_its_own_route_never_the_credential_in_the_path()
    {
        await using LanFactory factory = new(WithLanRoutes);
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();

        string seen = await (
            await Get(client, Address(device, "hdhr/discover.json?x=1"), Lan)
        ).Content.ReadAsStringAsync();

        seen.Should().Contain($"device={device.DeviceId}");
        seen.Should()
            .Contain($"minting-for={device.DeviceId}", "a link minted now is the device's");
        seen.Should().Contain("name=Living room TV");
        seen.Should().Contain($"path=/api/v1/plugins/{LiveTv}/hdhr/discover.json");
        seen.Should().Contain("query=?x=1");
        Line(seen, "path").Should().NotContain(device.Credential);
        Line(seen, "raw").Should().NotContain(device.Credential);
        Line(seen, "base")
            .Should()
            .EndWith($"/api/v1/plugins/{LiveTv}/lan/{device.DeviceId}/{device.Credential}/");
    }

    /// <summary>
    /// The default fixture signs in every request it sends. Through a LAN
    /// route the plugin still sees nobody: not the principal, not the result
    /// of authenticating, not the header, the cookie or the token in the query.
    /// </summary>
    [Fact]
    public async Task The_plugin_cannot_see_a_user_or_a_token_through_a_lan_route()
    {
        await using LanFactory factory = new(WithLanRoutes, signsEveryoneIn: true);
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();

        HttpRequestMessage request = new(
            HttpMethod.Get,
            Address(device, "hdhr/discover.json?access_token=abc&token=def&keep=1")
        );
        request.Headers.Add(PeerHeader, Lan);
        request.Headers.Add("Authorization", "Bearer user-token");
        request.Headers.Add("Cookie", "session=user-cookie");

        HttpResponseMessage response = await client.SendAsync(request);
        string seen = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        seen.Should().Contain("authenticated=False");
        seen.Should().Contain("claims=0");
        seen.Should().Contain("result=False");
        seen.Should().Contain("authorization=False");
        seen.Should().Contain("cookie=False");
        seen.Should().Contain("query=?keep=1");
        seen.Should().Contain($"accessor={Guid.Empty}");
        seen.Should().Contain("caller=refused");
        seen.Should().NotContain("user-token").And.NotContain("abc").And.NotContain("def");
    }

    [Fact]
    public async Task Every_wrong_request_gets_the_refusal_an_outside_caller_gets()
    {
        await using LanFactory factory = new(WithLanRoutes);
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();
        PluginLanDeviceMinted revoked = factory.Mint("Old box");
        factory.Devices.Revoke(LiveTv, revoked.DeviceId);

        (HttpStatusCode Status, string Body) outside = await Read(
            await Get(client, Address(device, "hdhr/lineup.json"), "203.0.113.9")
        );

        outside.Status.Should().Be(HttpStatusCode.Forbidden);
        outside.Body.Should().Contain(PluginRefusalCodes.LanCredentialInvalid);

        List<(string Case, HttpResponseMessage Response)> attempts =
        [
            ("loopback", await Get(client, Address(device, "hdhr/lineup.json"), "127.0.0.1")),
            ("no address", await Get(client, Address(device, "hdhr/lineup.json"), null)),
            (
                "a forwarded LAN address",
                await Get(
                    client,
                    Address(device, "hdhr/lineup.json"),
                    Lan,
                    ("X-Forwarded-For", "192.168.1.30")
                )
            ),
            (
                "a relay's own header",
                await Get(
                    client,
                    Address(device, "hdhr/lineup.json"),
                    Lan,
                    ("CF-Connecting-IP", "192.168.1.30")
                )
            ),
            (
                "a wrong credential",
                await Get(
                    client,
                    $"/api/v1/plugins/{LiveTv}/lan/{device.DeviceId}/not-the-credential/hdhr/lineup.json",
                    Lan
                )
            ),
            (
                "an unknown device",
                await Get(
                    client,
                    $"/api/v1/plugins/{LiveTv}/lan/never-added/{device.Credential}/hdhr/lineup.json",
                    Lan
                )
            ),
            ("a revoked device", await Get(client, Address(revoked, "hdhr/lineup.json"), Lan)),
            (
                "another plugin's id",
                await Get(
                    client,
                    $"/api/v1/plugins/{OtherPlugin}/lan/{device.DeviceId}/{device.Credential}/hdhr/lineup.json",
                    Lan
                )
            ),
            (
                "a GET action the manifest does not name",
                await Get(client, Address(device, "hdhr/secret.json"), Lan)
            ),
            ("a route that is not there", await Get(client, Address(device, "hdhr/nothing"), Lan)),
            ("POST", await Send(client, HttpMethod.Post, Address(device, "hdhr/lineup.json"), Lan)),
            ("PUT", await Send(client, HttpMethod.Put, Address(device, "hdhr/lineup.json"), Lan)),
            (
                "DELETE",
                await Send(client, HttpMethod.Delete, Address(device, "hdhr/lineup.json"), Lan)
            ),
            ("HEAD", await Send(client, HttpMethod.Head, Address(device, "hdhr/lineup.json"), Lan)),
            ("an encoded slash", await Get(client, Address(device, "hdhr%2Flineup.json"), Lan)),
            ("an encoded dot", await Get(client, Address(device, "hdhr/auto/%2e%2e"), Lan)),
            ("a trailing slash", await Get(client, Address(device, "hdhr/lineup.json/"), Lan)),
        ];

        foreach ((string attempt, HttpResponseMessage response) in attempts)
        {
            (HttpStatusCode status, string body) = await Read(response);

            status.Should().Be(outside.Status, attempt);

            // HEAD carries no body by definition; everything else carries the same
            // one. The refusal names the plugin id the address named, which the
            // caller wrote itself, so that is the one part read past.
            if (attempt != "HEAD")
                body.Replace(OtherPlugin.ToString(), LiveTv.ToString())
                    .Should()
                    .Be(outside.Body, attempt);
        }
    }

    /// <summary>
    /// The host folds <c>..</c> and <c>%2e%2e</c> out of a path before routing
    /// sees it, so an address that steps out and back in would land on a route
    /// the manifest names. The bytes the device sent are checked too, and a
    /// path written to step anywhere is refused like everything else.
    /// </summary>
    [Theory]
    [InlineData("hdhr/../hdhr/lineup.json", "hdhr/lineup.json")]
    [InlineData("hdhr/%2e%2e/hdhr/lineup.json", "hdhr/lineup.json")]
    [InlineData("hdhr/%2E%2E/hdhr/lineup.json", "hdhr/lineup.json")]
    [InlineData("hdhr//lineup.json", "hdhr//lineup.json")]
    [InlineData("hdhr/./lineup.json", "hdhr/lineup.json")]
    [InlineData("hdhr\\lineup.json", "hdhr\\lineup.json")]
    [InlineData("../../settings", "settings")]
    public async Task An_address_that_steps_anywhere_is_refused(string sent, string routed)
    {
        await using LanFactory factory = new(WithLanRoutes);
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();
        (HttpStatusCode Status, string Body) outside = await Read(
            await Get(client, Address(device, "hdhr/lineup.json"), "203.0.113.10")
        );

        HttpContext context = await factory.Server.SendAsync(http =>
        {
            http.Request.Method = HttpMethods.Get;
            http.Request.Path = Address(device, routed);
            http.Features.Get<IHttpRequestFeature>()!.RawTarget = Address(device, sent);
            http.Connection.RemoteIpAddress = IPAddress.Parse(Lan);
        });

        context.Response.StatusCode.Should().Be((int)outside.Status);
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).Should().Be(outside.Body);
    }

    /// <summary>
    /// The control for the test below: the server's response cache is live in
    /// this host, and keeps an answer a plugin marks public on an open route.
    /// </summary>
    [Fact]
    public async Task The_servers_response_cache_keeps_a_public_answer_on_an_open_route()
    {
        await using LanFactory factory = new(
            new()
            {
                Rest = true,
                RestAnonymous = true,
                Lan = new() { Routes = ["hdhr/lineup_status.json"] },
            }
        );
        (HttpClient client, _) = factory.Start();
        string open = $"/api/v1/plugins/{LiveTv}/hdhr/lineup_status.json";

        string first = await (await Get(client, open, Lan)).Content.ReadAsStringAsync();
        string second = await (await Get(client, open, Lan)).Content.ReadAsStringAsync();

        second.Should().Be(first, "the cache answered the second request");
    }

    /// <summary>
    /// The server's response cache runs before routing and keys by the
    /// address, which holds the credential here. A LAN answer it kept would
    /// be served again with no network check, no credential check and no
    /// revocation, so no LAN answer is ever kept, whatever the plugin says.
    /// </summary>
    [Fact]
    public async Task A_lan_answer_is_never_kept_by_a_cache_whatever_the_plugin_says()
    {
        await using LanFactory factory = new(
            new()
            {
                Rest = true,
                RestAnonymous = true,
                Lan = new() { Routes = ["hdhr/lineup_status.json"] },
            }
        );
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();
        string address = Address(device, "hdhr/lineup_status.json");

        HttpResponseMessage first = await Get(client, address, Lan);
        HttpResponseMessage second = await Get(client, address, Lan);

        first.Headers.CacheControl!.NoStore.Should().BeTrue();
        first.Headers.CacheControl.Public.Should().BeFalse();
        (await second.Content.ReadAsStringAsync())
            .Should()
            .NotBe(await first.Content.ReadAsStringAsync(), "each answer came from the plugin");

        factory.Devices.Revoke(LiveTv, device.DeviceId);

        (await Get(client, address, Lan))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden, "nothing kept answers for a revoked device");
    }

    [Fact]
    public async Task A_plugin_that_names_no_lan_routes_serves_none_and_keeps_its_token()
    {
        await using LanFactory factory = new(new() { Rest = true });
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();

        (await Get(client, Address(device, "hdhr/lineup.json"), Lan))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);

        (await Get(client, $"/api/v1/plugins/{LiveTv}/hdhr/lineup.json", Lan))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized, "its own routes still ask for a token");
    }

    [Fact]
    public async Task A_lan_route_does_not_open_the_plugin_route_itself()
    {
        await using LanFactory factory = new(WithLanRoutes);
        (HttpClient client, _) = factory.Start();

        (await Get(client, $"/api/v1/plugins/{LiveTv}/hdhr/lineup.json", Lan))
            .StatusCode.Should()
            .Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoking_a_device_cuts_the_stream_it_has_open()
    {
        await using LanFactory factory = new(WithLanRoutes);
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();

        HttpRequestMessage request = new(HttpMethod.Get, Address(device, "hdhr/stream"));
        request.Headers.Add(PeerHeader, Lan);

        HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead
        );
        Stream body = await response.Content.ReadAsStreamAsync();
        byte[] buffer = new byte[16];
        (await body.ReadAsync(buffer)).Should().Be(1, "the stream is playing");

        factory.Devices.Revoke(LiveTv, device.DeviceId);

        Task reading = ReadToEndOrFailAsync(body);
        Task finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(reading, "revoking the device ends what it is reading");
    }

    [Fact]
    public async Task The_device_route_answers_only_a_direct_lan_caller_too()
    {
        await using LanFactory factory = new(WithLanRoutes);
        (HttpClient client, PluginLanDeviceMinted device) = factory.Start();
        string address = $"/api/v1/plugins/{LiveTv}/lan/{device.DeviceId}/{device.Credential}";

        (await Get(client, address, Lan)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get(client, address, "127.0.0.1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Get(client, address, Lan, ("X-Forwarded-For", "192.168.1.30")))
            .StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
    }

    private static async Task ReadToEndOrFailAsync(Stream body)
    {
        byte[] buffer = new byte[16];

        try
        {
            while (await body.ReadAsync(buffer) > 0) { }
        }
        catch (Exception exception)
            when (exception is IOException or HttpRequestException or OperationCanceledException)
        {
            // An aborted request surfaces as a failed read, which is the point.
        }
    }

    private static string Address(PluginLanDeviceMinted device, string route) =>
        $"/api/v1/plugins/{LiveTv}/lan/{device.DeviceId}/{device.Credential}/{route}";

    private static string Line(string seen, string key) =>
        seen.Split('\n').First(line => line.StartsWith(key + "=")).Trim();

    private static Task<HttpResponseMessage> Get(
        HttpClient client,
        string address,
        string? peer,
        params (string Name, string Value)[] headers
    ) => Send(client, HttpMethod.Get, address, peer, headers);

    private static Task<HttpResponseMessage> Send(
        HttpClient client,
        HttpMethod method,
        string address,
        string? peer,
        params (string Name, string Value)[] headers
    )
    {
        HttpRequestMessage request = new(method, address);

        if (peer is not null)
            request.Headers.Add(PeerHeader, peer);

        foreach ((string name, string value) in headers)
            request.Headers.Add(name, value);

        return client.SendAsync(request);
    }

    private static async Task<(HttpStatusCode Status, string Body)> Read(
        HttpResponseMessage response
    ) => (response.StatusCode, await response.Content.ReadAsStringAsync());

    private sealed class LanFactory(PluginCapabilities capabilities, bool signsEveryoneIn = false)
        : NoMercyApiFactory
    {
        public PluginLanDeviceMinter Devices =>
            Services.GetRequiredService<PluginLanDeviceMinter>();

        protected override bool UseAnonymousTestAuthentication => !signsEveryoneIn;

        protected override IPluginManager CreateTestPluginManager() =>
            new LanPluginManager(
                new()
                {
                    Id = LiveTv,
                    Name = "Live TV",
                    Description = "Channels.",
                    Version = new(1, 0),
                    Status = PluginStatus.Active,
                    Capabilities = capabilities,
                }
            );

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPluginLanDeviceStore>();
                services.AddSingleton<IPluginLanDeviceStore>(new InMemoryLanDeviceStore());
                services.AddSingleton<IStartupFilter>(new PeerFromHeader());
            });
        }

        public (HttpClient Client, PluginLanDeviceMinted Device) Start()
        {
            HttpClient client = CreateClient();
            IPluginManager manager = Services.GetRequiredService<IPluginManager>();

            Services
                .GetRequiredService<PluginApplicationPartRegistrar>()
                .Attach(manager.GetInstalledPlugins()[0], manager);

            return (client, Mint("Living room TV"));
        }

        public PluginLanDeviceMinted Mint(string name) => Devices.Mint(LiveTv, name);
    }

    /// <summary>
    /// The test host has no network, so the transport address a request came
    /// from is set from a header the test writes, and that header is taken off
    /// before the server sees the request.
    /// </summary>
    private sealed class PeerFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(
                    (context, nextMiddleware) =>
                    {
                        if (context.Request.Headers.TryGetValue(PeerHeader, out StringValues peer))
                        {
                            context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
                            context.Request.Headers.Remove(PeerHeader);
                        }

                        return nextMiddleware(context);
                    }
                );

                next(app);
            };
    }

    private sealed class LanPluginManager(PluginInfo info) : IPluginManager
    {
        public IReadOnlyList<PluginInfo> GetInstalledPlugins() => [info];

        public IPlugin? GetPluginInstance(Ulid pluginId) =>
            pluginId == info.Id ? new LanPlugin(info.Id) : null;

        public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);

        public IEnumerable<T> GetPluginsOfType<T>()
            where T : IPlugin => [];
    }

    private sealed class LanPlugin(Ulid id) : IPlugin
    {
        public string Name => "Live TV";
        public string Description => "Channels.";
        public Ulid Id => id;
        public Version Version => new(1, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }
    }
}
