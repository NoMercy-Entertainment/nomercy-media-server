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
using System.Net.Sockets;
using NoMercy.PluginSdk.Network;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginNetworkAllowlistHandlerTests
{
    private static HttpClient Client(params string[] hosts) =>
        new(new PluginNetworkAllowlistHandler(hosts) { InnerHandler = new AlwaysOkHandler() });

    [Fact]
    public async Task Allowed_Host_PassesThrough()
    {
        HttpClient client = Client("*.somafm.com");
        HttpResponseMessage response = await client.GetAsync("https://ice1.somafm.com/groovesalad");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Disallowed_Host_Throws()
    {
        HttpClient client = Client("*.somafm.com");
        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            client.GetAsync("https://evil.example.com/x")
        );
    }

    [Fact]
    public async Task EmptyAllowlist_DeniesEverything()
    {
        HttpClient client = Client();
        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            client.GetAsync("https://anything.com/")
        );
    }

    [Fact]
    public async Task NullRequestUri_TreatedAsEmptyHost_Denied()
    {
        // HttpClient itself refuses to dispatch a request with a null RequestUri
        // and no BaseAddress, so the only way to exercise the handler's own
        // `request.RequestUri?.Host ?? string.Empty` null-conditional is to call
        // SendAsync directly against a message with RequestUri explicitly unset.
        ExposedAllowlistHandler handler = new(["*.somafm.com"])
        {
            InnerHandler = new AlwaysOkHandler(),
        };
        HttpRequestMessage request = new(HttpMethod.Get, (Uri?)null);

        Func<Task> act = () => handler.InvokeSendAsync(request, CancellationToken.None);

        await Assert.ThrowsAsync<PluginNetworkDeniedException>(act);
    }

    [Fact]
    public async Task A_redirect_to_a_host_outside_the_allowlist_is_refused_Aud1055()
    {
        ScriptedHandler inner = new(_ => Redirect(HttpStatusCode.Found, "http://192.0.2.10/x"));
        HttpClient client = RedirectClient(inner, "allowed.example.com");

        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            client.GetAsync("http://allowed.example.com/start")
        );
        Assert.Single(inner.Seen);
    }

    [Fact]
    public async Task A_redirect_to_an_allowed_host_is_followed_Aud1055()
    {
        ScriptedHandler inner = new(request =>
            request.RequestUri!.Host == "a.example.com"
                ? Redirect(HttpStatusCode.Found, "http://b.example.com/next")
                : new(HttpStatusCode.OK)
        );
        HttpClient client = RedirectClient(inner, "a.example.com", "b.example.com");

        HttpResponseMessage response = await client.GetAsync("http://a.example.com/start");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.Seen.Count);
        Assert.Equal("http://b.example.com/next", response.RequestMessage!.RequestUri!.ToString());
    }

    [Fact]
    public async Task The_plugin_client_does_not_follow_a_redirect_below_the_allowlist_Aud1055()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        List<string> paths = [];
        Task serve = Task.Run(async () =>
        {
            while (true)
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync();
                using StreamReader reader = new(peer.GetStream());
                string? line = await reader.ReadLineAsync();
                string path = line?.Split(' ')[1] ?? string.Empty;
                lock (paths)
                    paths.Add(path);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }

                string reply =
                    path == "/first"
                        ? $"HTTP/1.1 302 Found\r\nLocation: http://localhost:{port}/second\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await peer.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(reply));
            }
        });

        using HttpClient client = PluginHttpClientFactory.Create(
            new() { Network = new() { Hosts = ["127.0.0.1"] } }
        );

        await Assert.ThrowsAsync<PluginNetworkDeniedException>(() =>
            client.GetAsync($"http://127.0.0.1:{port}/first")
        );
        listener.Stop();

        lock (paths)
            Assert.Equal(["/first"], paths);
        _ = serve;
    }

    [Fact]
    public async Task A_303_after_a_post_arrives_as_a_get_without_a_body_Aud1055()
    {
        ScriptedHandler inner = new(request =>
            request.RequestUri!.AbsolutePath == "/post"
                ? Redirect(HttpStatusCode.SeeOther, "/result")
                : new(HttpStatusCode.OK)
        );
        HttpClient client = RedirectClient(inner, "a.example.com");

        await client.PostAsync("http://a.example.com/post", new StringContent("payload"));

        Assert.Equal(2, inner.Seen.Count);
        Assert.Equal(HttpMethod.Get, inner.Seen[1].Method);
        Assert.Null(inner.Seen[1].Content);
    }

    [Fact]
    public async Task Authorization_is_dropped_when_a_redirect_changes_host_Aud1055()
    {
        ScriptedHandler inner = new(request =>
            request.RequestUri!.Host == "a.example.com"
                ? Redirect(HttpStatusCode.Found, "http://b.example.com/next")
                : new(HttpStatusCode.OK)
        );
        HttpClient client = RedirectClient(inner, "a.example.com", "b.example.com");
        HttpRequestMessage request = new(HttpMethod.Get, "http://a.example.com/start");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer placeholder");

        await client.SendAsync(request);

        Assert.True(inner.Seen[0].Headers.Contains("Authorization"));
        Assert.False(inner.Seen[1].Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task A_redirect_from_https_to_http_is_not_followed_Aud1055()
    {
        ScriptedHandler inner = new(_ =>
            Redirect(HttpStatusCode.Found, "http://a.example.com/next")
        );
        HttpClient client = RedirectClient(inner, "a.example.com");

        HttpResponseMessage response = await client.GetAsync("https://a.example.com/start");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(inner.Seen);
    }

    [Fact]
    public async Task An_eleventh_redirect_hop_throws_Aud1055()
    {
        ScriptedHandler inner = new(_ =>
            Redirect(HttpStatusCode.Found, "http://a.example.com/loop")
        );
        HttpClient client = RedirectClient(inner, "a.example.com");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetAsync("http://a.example.com/loop")
        );
        Assert.Equal(11, inner.Seen.Count);
    }

    private static HttpClient RedirectClient(HttpMessageHandler inner, params string[] hosts) =>
        new(new PluginNetworkAllowlistHandler(hosts) { InnerHandler = inner });

    private static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        HttpResponseMessage response = new(status);
        response.Headers.Location = new(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Seen.Add(request);
            HttpResponseMessage response = answer(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ExposedAllowlistHandler(IReadOnlyList<string> allowedHosts)
        : PluginNetworkAllowlistHandler(allowedHosts)
    {
        public Task<HttpResponseMessage> InvokeSendAsync(
            HttpRequestMessage request,
            CancellationToken ct
        ) => SendAsync(request, ct);
    }
}
