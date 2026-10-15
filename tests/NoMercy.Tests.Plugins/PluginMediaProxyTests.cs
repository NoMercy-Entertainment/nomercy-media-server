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
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Media;
using NoMercy.PluginSdk.Network;
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

    private static PluginMediaTicketMinter Minter() =>
        new(TimeProvider.System, "a-server-key-that-is-long-enough-for-hmac"u8.ToArray());

    private static PluginMediaProxy Proxy(
        PluginMediaTicketMinter minter,
        HttpMessageHandler handler
    ) =>
        new(
            Radio,
            new StubCaller(Listener),
            new StubBroker(null),
            minter,
            new HttpClient(handler),
            NullLogger.Instance
        );

    private static HttpResponseMessage Answer(byte[] body, string? contentType)
    {
        ByteArrayContent content = new(body);
        if (contentType is not null)
            content.Headers.ContentType = new(contentType);

        return new(HttpStatusCode.OK) { Content = content };
    }

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

    /// <summary>
    /// A playlist under a type that says nothing about it is still a playlist.
    /// IPTV panels and CDNs serve HLS under these as often as under the right
    /// type, and one left alone sends the player to the provider.
    /// </summary>
    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("binary/octet-stream")]
    [InlineData("text/plain")]
    [InlineData("video/mp2t")]
    [InlineData(null)]
    public async Task A_playlist_under_a_type_that_says_nothing_is_still_rewritten(
        string? contentType
    )
    {
        HttpResponseMessage response = await Proxy(upstream: _ =>
                Answer(
                    Encoding.UTF8.GetBytes(
                        "#EXTM3U\n#EXTINF:6,\nhttps://cdn.example.com/seg1.ts\n"
                    ),
                    contentType
                )
            )
            .FetchAsync(Request("https://stream.example.com/live"), null, default);

        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("cdn.example.com");
        body.Should().Contain($"/api/v1/plugins/{Radio}/media/");
        response
            .Content.Headers.ContentType!.MediaType.Should()
            .Be("application/vnd.apple.mpegurl", "the player is told what it now holds");
    }

    [Fact]
    public async Task A_playlist_after_a_byte_order_mark_and_whitespace_is_still_rewritten()
    {
        byte[] body =
        [
            0xEF,
            0xBB,
            0xBF,
            .. Encoding.UTF8.GetBytes("\r\n  #EXTM3U\nhttps://cdn.example.com/seg1.ts\n"),
        ];

        HttpResponseMessage response = await Proxy(upstream: _ =>
                Answer(body, "application/octet-stream")
            )
            .FetchAsync(Request("https://stream.example.com/live"), null, default);

        (await response.Content.ReadAsStringAsync()).Should().NotContain("cdn.example.com");
    }

    [Theory]
    [InlineData("Application/X-MPEGURL")]
    [InlineData("audio/x-mpegurl")]
    public async Task A_playlist_type_is_recognised_in_any_case_and_spelling(string contentType)
    {
        HttpResponseMessage response = await Proxy(upstream: _ =>
                Answer(
                    Encoding.UTF8.GetBytes("#EXTM3U\nhttps://cdn.example.com/seg1.ts\n"),
                    contentType
                )
            )
            .FetchAsync(Request("https://stream.example.com/live.m3u8"), null, default);

        (await response.Content.ReadAsStringAsync()).Should().NotContain("cdn.example.com");
    }

    /// <summary>
    /// Looking at the first bytes must not cost the viewer any of them. A
    /// stream is handed on byte for byte, longer and shorter than the look.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(64)]
    [InlineData(5_000)]
    public async Task A_stream_under_a_vague_type_is_passed_on_byte_for_byte(int length)
    {
        byte[] stream = [.. Enumerable.Range(0, length).Select(i => (byte)(0x47 + i))];

        HttpResponseMessage response = await Proxy(upstream: _ => Answer(stream, "video/mp2t"))
            .FetchAsync(Request("https://stream.example.com/live.ts"), null, default);

        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(stream);
        response.Content.Headers.ContentType!.MediaType.Should().Be("video/mp2t");
    }

    [Fact]
    public async Task An_answer_under_a_specific_media_type_is_not_looked_into()
    {
        byte[] body = Encoding.UTF8.GetBytes("#EXTM3U\nhttps://cdn.example.com/seg1.ts\n");

        HttpResponseMessage response = await Proxy(upstream: _ => Answer(body, "video/mp4"))
            .FetchAsync(Request("https://stream.example.com/a.mp4"), null, default);

        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(body);
    }

    /// <summary>
    /// A channel whose first link is dead plays from its second. Its segments
    /// have to be asked for the way its playlist was, with the second link's
    /// headers, or the provider refuses them.
    /// </summary>
    [Fact]
    public async Task The_children_of_a_playlist_carry_the_headers_of_the_link_that_answered()
    {
        PluginMediaTicketMinter minter = Minter();
        PluginProxyRequest request = new()
        {
            Links =
            [
                new()
                {
                    Url = new("https://dead.example.com/live.m3u8"),
                    Referer = "https://first.example.com/",
                    UserAgent = "First/1.0",
                },
                new()
                {
                    Url = new("https://live.example.com/live.m3u8"),
                    Referer = "https://second.example.com/",
                    UserAgent = "Second/1.0",
                },
            ],
        };

        HttpResponseMessage response = await Proxy(
                minter,
                new StubHandler(message =>
                    message.RequestUri!.Host == "dead.example.com"
                        ? new(HttpStatusCode.NotFound)
                        : Answer(
                            Encoding.UTF8.GetBytes("#EXTM3U\nseg1.ts\n"),
                            "application/vnd.apple.mpegurl"
                        )
                )
            )
            .FetchAsync(request, null, default);

        string child = (await response.Content.ReadAsStringAsync())
            .Split('\n')
            .Single(line => line.StartsWith('/'))
            .Split('/')[^1];
        PluginProxyLink link = minter.Read(child)!.Request.Links.Single();

        link.Url.Should().Be(new Uri("https://live.example.com/seg1.ts"));
        link.Referer.Should().Be("https://second.example.com/");
        link.UserAgent.Should().Be("Second/1.0");
    }

    /// <summary>
    /// The user agent a link names reaches the provider through the same
    /// handler that stamps the server's identity on every other request.
    /// Before, that handler replaced it, so the field did nothing.
    /// </summary>
    [Fact]
    public async Task The_user_agent_a_link_names_survives_the_servers_user_agent_handler()
    {
        string? agent = null;
        string? attribution = null;

        PluginUserAgentHandler handler = new(Radio, "Live TV", new Version(1, 0, 0))
        {
            InnerHandler = new StubHandler(message =>
            {
                agent = message.Headers.TryGetValues("User-Agent", out IEnumerable<string>? a)
                    ? string.Join(" ", a)
                    : null;
                attribution = message.Headers.TryGetValues(
                    PluginUserAgentHandler.AttributionHeader,
                    out IEnumerable<string>? b
                )
                    ? string.Join(" ", b)
                    : null;

                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            }),
        };

        await Proxy(Minter(), handler)
            .FetchAsync(
                new()
                {
                    Links =
                    [
                        new()
                        {
                            Url = new("https://iptv.example.com/1.ts"),
                            UserAgent = "VLC/3.0.18",
                        },
                    ],
                },
                null,
                default
            );

        agent.Should().Be("VLC/3.0.18");
        attribution.Should().Contain("NoMercyPlugin-Live-TV/1.0.0");
    }

    [Fact]
    public async Task A_link_without_a_user_agent_is_fetched_with_the_servers_own()
    {
        HttpRequestMessage? sent = null;

        await Proxy(upstream: message =>
            {
                sent = message;

                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            })
            .FetchAsync(Request("https://iptv.example.com/1.ts"), null, default);

        sent!.Options.TryGetValue(PluginUserAgentHandler.ViewerUserAgent, out bool viewer);
        viewer.Should().BeFalse();
    }

    /// <summary>A user agent with a line break in it is a header injection, not a user agent.</summary>
    [Fact]
    public async Task A_user_agent_with_control_characters_is_not_sent()
    {
        HttpRequestMessage? sent = null;

        await Proxy(upstream: message =>
            {
                sent = message;

                return new(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            })
            .FetchAsync(
                new()
                {
                    Links =
                    [
                        new()
                        {
                            Url = new("https://iptv.example.com/1.ts"),
                            UserAgent = "VLC\r\nX-Injected: yes",
                        },
                    ],
                },
                null,
                default
            );

        sent!.Headers.Contains("User-Agent").Should().BeFalse();
        sent.Options.TryGetValue(PluginUserAgentHandler.ViewerUserAgent, out bool viewer);
        viewer.Should().BeFalse();
    }

    /// <summary>
    /// A key, an init segment and an alternate audio rendition name their
    /// address in a URI attribute of a tag line. Each becomes a ticket here,
    /// like a segment line, or the player fetches it from the provider.
    /// </summary>
    [Fact]
    public async Task The_addresses_inside_tag_lines_become_tickets_here_too()
    {
        PluginMediaTicketMinter minter = Minter();
        string playlist = string.Join(
            '\n',
            "#EXTM3U",
            "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",URI=\"audio/en.m3u8\"",
            "#EXT-X-MAP:URI=\"init.mp4\"",
            "#EXT-X-KEY:METHOD=AES-128,URI=\"https://keys.example.com/k?id=7\",IV=0x1234",
            "#EXTINF:6,",
            "seg1.m4s"
        );

        HttpResponseMessage response = await Proxy(
                minter,
                new StubHandler(_ =>
                    Answer(Encoding.UTF8.GetBytes(playlist), "application/vnd.apple.mpegurl")
                )
            )
            .FetchAsync(
                new()
                {
                    Links =
                    [
                        new()
                        {
                            Url = new("https://cdn.example.com/live/index.m3u8"),
                            Referer = "https://portal.example.com/",
                            UserAgent = "VLC/3.0.18",
                        },
                    ],
                },
                null,
                default
            );

        string body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("example.com");

        string[] lines = body.Split('\n');
        Upstream(minter, lines[1])
            .Should()
            .Be(new Uri("https://cdn.example.com/live/audio/en.m3u8"));
        Upstream(minter, lines[2]).Should().Be(new Uri("https://cdn.example.com/live/init.mp4"));
        Upstream(minter, lines[3]).Should().Be(new Uri("https://keys.example.com/k?id=7"));

        PluginProxyLink key = Link(minter, lines[3]);
        key.Referer.Should().Be("https://portal.example.com/");
        key.UserAgent.Should().Be("VLC/3.0.18");
    }

    [Fact]
    public async Task The_rest_of_a_tag_line_is_left_as_it_was()
    {
        string playlist =
            "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\",IV=0x1234,KEYFORMAT=\"identity\"\n";

        HttpResponseMessage response = await Proxy(upstream: _ =>
                Answer(Encoding.UTF8.GetBytes(playlist), "application/vnd.apple.mpegurl")
            )
            .FetchAsync(Request("https://cdn.example.com/live/index.m3u8"), null, default);

        string key = (await response.Content.ReadAsStringAsync()).Split('\n')[1];
        key.Should().StartWith("#EXT-X-KEY:METHOD=AES-128,URI=\"/api/v1/plugins/");
        key.Should().EndWith("\",IV=0x1234,KEYFORMAT=\"identity\"");
    }

    /// <summary>
    /// A FairPlay key and an inline key are not addresses the server can
    /// fetch. They stay exactly as the provider wrote them.
    /// </summary>
    [Theory]
    [InlineData(
        "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key-id-42\",KEYFORMAT=\"com.apple.streamingkeydelivery\""
    )]
    [InlineData(
        "#EXT-X-KEY:METHOD=AES-128,URI=\"data:text/plain;base64,AAECAwQFBgcICQoLDA0ODw==\""
    )]
    [InlineData("#EXT-X-KEY:METHOD=NONE")]
    public async Task A_uri_the_server_cannot_fetch_is_left_alone(string tag)
    {
        HttpResponseMessage response = await Proxy(upstream: _ =>
                Answer(Encoding.UTF8.GetBytes($"#EXTM3U\n{tag}\n"), "application/vnd.apple.mpegurl")
            )
            .FetchAsync(Request("https://cdn.example.com/live/index.m3u8"), null, default);

        (await response.Content.ReadAsStringAsync()).Split('\n')[1].Should().Be(tag);
    }

    private static string TicketIn(string line)
    {
        int start = line.IndexOf("/media/", StringComparison.Ordinal) + "/media/".Length;
        int end = line.IndexOf('"', start);

        return end < 0 ? line[start..] : line[start..end];
    }

    private static PluginProxyLink Link(PluginMediaTicketMinter minter, string line) =>
        minter.Read(TicketIn(line))!.Request.Links.Single();

    private static Uri Upstream(PluginMediaTicketMinter minter, string line) =>
        Link(minter, line).Url;

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
