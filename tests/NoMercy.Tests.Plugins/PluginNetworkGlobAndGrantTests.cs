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
using System.Net.Http.Headers;
using FluentAssertions;
using NoMercy.NmSystem.Configuration;
using NoMercy.PluginSdk.Network;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The allowlist is the only thing standing between a plugin's declared hosts
/// and the network, so both directions matter: a host it should reach has to
/// pass, and one it should not has to be refused. The old glob could express
/// neither "any host" nor a multi-level subdomain, which is what pushed a
/// plugin into building its own client and leaving the model entirely.
/// </summary>
public class PluginNetworkGlobAndGrantTests
{
    [Theory]
    // Exact.
    [InlineData("example.com", "example.com", true)]
    [InlineData("example.com", "other.com", false)]
    // One label, the shape that always worked.
    [InlineData("*.example.com", "a.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    // The gap: * never crossed a dot, so a deeper subdomain was refused.
    [InlineData("*.example.com", "a.b.example.com", false)]
    [InlineData("**.example.com", "a.b.example.com", true)]
    [InlineData("**.example.com", "a.example.com", true)]
    // The other gap: there was no way to write "any host" at all.
    [InlineData("**", "anything.example.com", true)]
    [InlineData("**", "localhost", true)]
    [InlineData("*", "localhost", true)]
    [InlineData("*", "a.example.com", false)]
    // A glob still cannot wander off its suffix.
    [InlineData("**.example.com", "example.com.evil.test", false)]
    [InlineData("*.example.com", "a.example.com.evil.test", false)]
    public void The_glob_matches_what_it_says(string pattern, string host, bool expected) =>
        PluginNetworkAllowlistHandler.ToPattern(pattern).IsMatch(host).Should().Be(expected);

    private static HttpClient ClientFor(
        IReadOnlyList<string> manifestHosts,
        Func<IReadOnlyList<string>>? granted = null
    ) =>
        new(
            new PluginNetworkAllowlistHandler(manifestHosts, granted)
            {
                InnerHandler = new AlwaysOkHandler(),
            }
        );

    [Fact]
    public async Task A_host_in_the_manifest_is_allowed()
    {
        HttpClient client = ClientFor(["api.example.com"]);

        HttpResponseMessage response = await client.GetAsync("https://api.example.com/x");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_host_in_neither_list_is_denied()
    {
        HttpClient client = ClientFor(["api.example.com"]);

        Func<Task> act = () => client.GetAsync("https://tracker.example.org/x");

        await act.Should().ThrowAsync<PluginNetworkDeniedException>();
    }

    [Fact]
    public async Task An_empty_manifest_denies_everything()
    {
        HttpClient client = ClientFor([]);

        Func<Task> act = () => client.GetAsync("https://anything.example.com/x");

        await act.Should().ThrowAsync<PluginNetworkDeniedException>();
    }

    [Fact]
    public async Task A_granted_host_is_allowed_even_though_the_manifest_never_named_it()
    {
        // The case the manifest cannot express: the user typed the host in
        // after installing.
        HttpClient client = ClientFor([], () => ["indexer.example.org"]);

        HttpResponseMessage response = await client.GetAsync("https://indexer.example.org/api");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_grant_made_after_the_client_exists_takes_effect_without_a_restart()
    {
        // Read through a delegate for exactly this: the owner grants a host
        // while the plugin is running, and the next request has to work.
        List<string> granted = [];
        HttpClient client = ClientFor([], () => granted);

        Func<Task> before = () => client.GetAsync("https://late.example.org/x");
        await before.Should().ThrowAsync<PluginNetworkDeniedException>();

        granted.Add("late.example.org");

        HttpResponseMessage response = await client.GetAsync("https://late.example.org/x");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoking_a_grant_closes_the_host_again()
    {
        List<string> granted = ["temporary.example.org"];
        HttpClient client = ClientFor([], () => granted);

        (await client.GetAsync("https://temporary.example.org/x"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        granted.Clear();

        Func<Task> act = () => client.GetAsync("https://temporary.example.org/x");
        await act.Should().ThrowAsync<PluginNetworkDeniedException>();
    }
}

/// <summary>
/// The user agent is attribution, not enforcement — a plugin can always build
/// its own client. What it must not do is break a plugin that set its own.
/// </summary>
public class PluginUserAgentHandlerTests
{
    private static readonly Ulid PluginId = Ulid.Parse("1K6CSK6CSK6CSK6CSK6CSK6CSK");

    private static HttpClient ClientFor(string? name, Version? version = null) =>
        new(
            new PluginUserAgentHandler(PluginId, name, version ?? new Version(1, 2, 3))
            {
                InnerHandler = new EchoingUserAgentHandler(),
            }
        );

    [Fact]
    public async Task Egress_carries_the_owners_identity_and_the_plugins_attribution()
    {
        HttpResponseMessage response = await ClientFor("Torrent Downloader")
            .GetAsync("https://example.com/x");

        string sent = await response.Content.ReadAsStringAsync();
        sent.Should().Contain(ExternalServicesConfig.Current.UserAgent);
        sent.Should().Contain("NoMercyPlugin-Torrent-Downloader/1.2.3");
    }

    [Fact]
    public async Task A_plugin_cannot_choose_the_identity_this_server_presents()
    {
        // It could, until this. A host we do not control is reached from the
        // owner's address, and letting a plugin send "Mozilla/5.0" made it the
        // plugin's choice who that traffic came from.
        HttpClient client = ClientFor("Torrent Downloader");
        HttpRequestMessage request = new(HttpMethod.Get, "https://example.com/x");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0");

        HttpResponseMessage response = await client.SendAsync(request);

        string sent = await response.Content.ReadAsStringAsync();
        sent.Should().NotContain("Mozilla");
        sent.Should().Contain(ExternalServicesConfig.Current.UserAgent);
        sent.Should().Contain("NoMercyPlugin-Torrent-Downloader/1.2.3");
    }

    [Theory]
    // A plugin name is author-supplied text, and a product token is a narrow
    // grammar. Anything outside it has to go or the header is malformed.
    [InlineData("My Plugin (Beta)", "NoMercyPlugin-My-Plugin-Beta/1.2.3")]
    [InlineData("Torrent/Downloader", "NoMercyPlugin-Torrent-Downloader/1.2.3")]
    [InlineData("naïve", "NoMercyPlugin-na-ve/1.2.3")]
    public void A_name_is_reduced_to_a_product_token(string name, string expected) =>
        PluginUserAgentHandler
            .BuildProduct(PluginId, name, new Version(1, 2, 3))
            .Should()
            .Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("...")]
    public void A_name_with_nothing_usable_falls_back_to_the_id(string? name)
    {
        string product = PluginUserAgentHandler.BuildProduct(PluginId, name, new Version(1, 0, 0));

        product.Should().Be($"NoMercyPlugin-{PluginId:N}/1.0.0");
    }

    [Fact]
    public async Task A_hostile_name_cannot_inject_a_header()
    {
        // The failure that would matter is a newline in the name splitting the
        // request into two headers. The words survive as part of one token,
        // which is harmless — what must not survive is the CRLF that would make
        // them a header of their own.
        HttpClient client = ClientFor("evil\r\nX-Injected: yes");

        HttpResponseMessage response = await client.GetAsync("https://example.com/x");

        string sent = await response.Content.ReadAsStringAsync();
        sent.Should().NotContain("\r").And.NotContain("\n").And.NotContain(":");
        sent.Should().Contain("NoMercyPlugin-evil-X-Injected-yes/");
    }

    private sealed class EchoingUserAgentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            HttpHeaderValueCollection<ProductInfoHeaderValue> agent = request.Headers.UserAgent;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(agent.ToString()),
                }
            );
        }
    }
}
