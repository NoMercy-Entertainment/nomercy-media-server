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
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;
using NoMercy.Plugins.Media;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin never builds the address a client sees and never hands a client an
/// upstream. It says where to fetch from; the server decides what the client
/// gets.
/// </summary>
public class PluginMediaProxyTests
{
    private static readonly Ulid Radio = Ulid.Parse("01J9ZK5V8Y0000000000000000");
    private static readonly Guid Listener = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static PluginMediaProxy Proxy(
        PluginRefusal? refusal = null,
        Func<HttpRequestMessage, HttpResponseMessage>? upstream = null
    ) =>
        new(
            Radio,
            new StubCaller(Listener),
            new StubBroker(refusal),
            new(TimeProvider.System, "a-server-key-that-is-long-enough-for-hmac"u8.ToArray()),
            new HttpClient(
                new StubHandler(
                    upstream ?? (_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) })
                )
            ),
            NullLogger.Instance
        );

    private static PluginProxyRequest Request(params string[] urls) =>
        new() { Links = [.. urls.Select(url => new PluginProxyLink { Url = new(url) })] };

    [Fact]
    public async Task A_minted_link_points_at_this_server_and_not_at_the_upstream()
    {
        PluginMediaUrl url = await Proxy()
            .MintAsync(Request("https://stream.example.com/radio.aac?token=secret"));

        url.Url.ToString().Should().StartWith($"/api/v1/plugins/{Radio}/media/");
        url.Url.ToString().Should().NotContain("stream.example.com");
        url.Url.ToString().Should().NotContain("secret");
    }

    [Fact]
    public async Task A_minted_link_says_when_it_stops_working()
    {
        PluginMediaUrl url = await Proxy().MintAsync(Request("https://stream.example.com/a.aac"));

        url.ExpiresAt.Should()
            .BeCloseTo(
                DateTimeOffset.UtcNow.Add(PluginMediaProxy.TicketLifetime),
                TimeSpan.FromMinutes(1)
            );
    }

    [Fact]
    public async Task A_host_outside_the_declared_scope_is_refused()
    {
        PluginRefusal refusal = new(
            PluginRefusalCodes.CapabilityScopeRefused,
            Radio.ToString(),
            "what",
            "why",
            "fix",
            PluginRefusalSeverity.Blocked
        );

        Func<Task> act = () =>
            Proxy(refusal).MintAsync(Request("https://elsewhere.example/stream.aac"));

        (await act.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Code.Should()
            .Be(PluginRefusalCodes.CapabilityScopeRefused);
    }

    [Fact]
    public async Task Every_link_is_checked_not_only_the_first()
    {
        CountingBroker broker = new();
        PluginMediaProxy proxy = new(
            Radio,
            new StubCaller(Listener),
            broker,
            new(TimeProvider.System, "a-server-key-that-is-long-enough-for-hmac"u8.ToArray()),
            new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))),
            NullLogger.Instance
        );

        await proxy.MintAsync(
            Request("https://a.example.com/s.aac", "https://b.example.com/s.aac")
        );

        broker
            .Asked.Should()
            .Equal(
                ["a.example.com", "b.example.com"],
                "falling through to a second link must not reach a host the manifest never named"
            );
    }

    [Fact]
    public async Task A_link_minted_with_nobody_asking_is_refused()
    {
        PluginMediaProxy proxy = new(
            Radio,
            new StubCaller(Guid.Empty),
            new StubBroker(null),
            new(TimeProvider.System, "a-server-key-that-is-long-enough-for-hmac"u8.ToArray()),
            new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))),
            NullLogger.Instance
        );

        Func<Task> act = () => proxy.MintAsync(Request("https://stream.example.com/a.aac"));

        (await act.Should().ThrowAsync<PluginRefusedException>())
            .Which.Refusal.Why.Should()
            .Contain(
                "Nothing is asking",
                "a ticket bound to the empty account is a ticket anybody can play"
            );
    }

    [Fact]
    public async Task A_request_naming_nowhere_is_refused_rather_than_minted()
    {
        Func<Task> act = () => Proxy().MintAsync(new() { Links = [] });

        await act.Should().ThrowAsync<PluginRefusedException>();
    }

    [Fact]
    public async Task The_range_a_client_asked_for_is_passed_upstream()
    {
        string? seen = null;

        HttpResponseMessage response = await Proxy(upstream: request =>
            {
                seen = request.Headers.TryGetValues("Range", out IEnumerable<string>? values)
                    ? string.Join(",", values)
                    : null;

                return new(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(new byte[100]),
                };
            })
            .FetchAsync(Request("https://stream.example.com/a.aac"), "bytes=100-199", default);

        seen.Should().Be("bytes=100-199");
        response.StatusCode.Should().Be(HttpStatusCode.PartialContent);
    }

    [Fact]
    public async Task A_request_that_does_not_pass_range_does_not_send_one()
    {
        string? seen = null;

        await Proxy(upstream: request =>
            {
                seen = request.Headers.TryGetValues("Range", out IEnumerable<string>? values)
                    ? string.Join(",", values)
                    : null;

                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            })
            .FetchAsync(
                Request("https://stream.example.com/cover.jpg") with
                {
                    PassRange = false,
                },
                "bytes=0-1",
                default
            );

        seen.Should().BeNull("a still image has nothing to seek in");
    }

    [Fact]
    public async Task The_referer_and_user_agent_the_link_carries_are_sent()
    {
        string? referer = null;
        string? agent = null;

        PluginProxyRequest request = new()
        {
            Links =
            [
                new()
                {
                    Url = new("https://stream.example.com/a.aac"),
                    Referer = "https://portal.example.com/",
                    UserAgent = "NoMercyRadio/1.0",
                },
            ],
        };

        await Proxy(upstream: message =>
            {
                referer = message.Headers.TryGetValues("Referer", out IEnumerable<string>? r)
                    ? string.Join(",", r)
                    : null;
                agent = message.Headers.TryGetValues("User-Agent", out IEnumerable<string>? a)
                    ? string.Join(",", a)
                    : null;

                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            })
            .FetchAsync(request, null, default);

        referer.Should().Be("https://portal.example.com/");
        agent.Should().Be("NoMercyRadio/1.0");
    }

    [Fact]
    public async Task An_address_that_resolves_late_is_resolved_at_the_fetch()
    {
        Uri? asked = null;

        PluginProxyRequest request = new()
        {
            Links =
            [
                new()
                {
                    Url = new("https://stream.example.com/placeholder"),
                    ResolveAsync = _ =>
                        Task.FromResult(new Uri("https://stream.example.com/real.aac?t=late")),
                },
            ],
        };

        await Proxy(upstream: message =>
            {
                asked = message.RequestUri;

                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            })
            .FetchAsync(request, null, default);

        asked!
            .ToString()
            .Should()
            .Be(
                "https://stream.example.com/real.aac?t=late",
                "a credential in an address lives shorter than the ticket does"
            );
    }

    [Fact]
    public async Task Every_child_address_in_a_playlist_becomes_another_ticket_here()
    {
        HttpResponseMessage response = await Proxy(upstream: _ =>
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "#EXTM3U\n#EXTINF:6,\nhttps://cdn.example.com/seg1.ts\n#EXTINF:6,\nseg2.ts\n",
                        Encoding.UTF8,
                        "application/vnd.apple.mpegurl"
                    ),
                }
            )
            .FetchAsync(Request("https://stream.example.com/live.m3u8"), null, default);

        string body = await response.Content.ReadAsStringAsync();

        body.Should().NotContain("cdn.example.com");
        body.Split('\n')
            .Count(line => line.Contains($"/api/v1/plugins/{Radio}/media/"))
            .Should()
            .Be(2, "a child left alone sends the client to the provider for the segments");
    }

    [Fact]
    public async Task The_lines_that_are_not_addresses_are_left_alone()
    {
        HttpResponseMessage response = await Proxy(upstream: _ =>
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "#EXTM3U\n#EXTINF:6,\nseg1.ts\n",
                        Encoding.UTF8,
                        "application/vnd.apple.mpegurl"
                    ),
                }
            )
            .FetchAsync(Request("https://stream.example.com/live.m3u8"), null, default);

        (await response.Content.ReadAsStringAsync()).Should().Contain("#EXTINF:6,");
    }

    [Fact]
    public async Task A_playlist_is_left_alone_when_the_request_said_not_to_rewrite()
    {
        HttpResponseMessage response = await Proxy(upstream: _ =>
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "#EXTM3U\nhttps://cdn.example.com/seg1.ts\n",
                        Encoding.UTF8,
                        "application/vnd.apple.mpegurl"
                    ),
                }
            )
            .FetchAsync(
                Request("https://stream.example.com/live.m3u8") with
                {
                    RewriteHlsPlaylists = false,
                },
                null,
                default
            );

        (await response.Content.ReadAsStringAsync()).Should().Contain("cdn.example.com");
    }

    [Fact]
    public async Task Something_that_is_not_a_playlist_is_passed_through_untouched()
    {
        HttpResponseMessage response = await Proxy(upstream: _ =>
                new(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "https://cdn.example.com/not-a-playlist",
                        Encoding.UTF8,
                        "text/plain"
                    ),
                }
            )
            .FetchAsync(Request("https://stream.example.com/a.txt"), null, default);

        (await response.Content.ReadAsStringAsync()).Should().Contain("cdn.example.com");
    }

    [Fact]
    public async Task A_link_that_fails_falls_through_to_the_next_one()
    {
        int calls = 0;

        HttpResponseMessage response = await Proxy(upstream: _ =>
            {
                calls += 1;

                return calls == 1
                    ? new(HttpStatusCode.ServiceUnavailable)
                    : new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            })
            .FetchAsync(
                Request("https://a.example.com/s.aac", "https://b.example.com/s.aac"),
                null,
                default
            );

        calls.Should().Be(2);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_link_that_works_is_not_followed_by_the_next_one()
    {
        int calls = 0;

        await Proxy(upstream: _ =>
            {
                calls += 1;

                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            })
            .FetchAsync(
                Request("https://a.example.com/s.aac", "https://b.example.com/s.aac"),
                null,
                default
            );

        calls.Should().Be(1, "trying the rest anyway is a request the provider did not need");
    }

    [Fact]
    public async Task A_link_that_answers_a_range_is_not_followed_by_the_next_one()
    {
        int calls = 0;

        await Proxy(upstream: _ =>
            {
                calls += 1;

                return new(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(new byte[10]),
                };
            })
            .FetchAsync(
                Request("https://a.example.com/s.aac", "https://b.example.com/s.aac"),
                "bytes=0-9",
                default
            );

        calls.Should().Be(1, "206 is the answer a seek asks for, not a link that failed");
    }

    [Fact]
    public async Task Every_link_failing_answers_the_last_failure_rather_than_throwing()
    {
        HttpResponseMessage response = await Proxy(upstream: _ =>
                new(HttpStatusCode.ServiceUnavailable)
            )
            .FetchAsync(
                Request("https://a.example.com/s.aac", "https://b.example.com/s.aac"),
                null,
                default
            );

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    private sealed class StubCaller(Guid userId) : IPluginCallerAccessor
    {
        public Guid CurrentUserId => userId;
    }

    private sealed class StubBroker(PluginRefusal? refusal) : IPluginCapabilityBroker
    {
        public PluginRefusal? Check(Ulid pluginId, string capability, string? scope = null) =>
            refusal;
    }

    private sealed class CountingBroker : IPluginCapabilityBroker
    {
        public List<string> Asked { get; } = [];

        public PluginRefusal? Check(Ulid pluginId, string capability, string? scope = null)
        {
            if (scope is not null)
                Asked.Add(scope);

            return null;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            HttpResponseMessage response = answer(request);
            response.RequestMessage ??= request;

            return Task.FromResult(response);
        }
    }
}
