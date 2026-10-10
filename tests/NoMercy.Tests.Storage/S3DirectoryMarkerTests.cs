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

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using NoMercy.Storage.Drivers.S3;

namespace NoMercy.Tests.Storage;

public class S3DirectoryMarkerTests
{
    [Fact]
    public void Recursive_delete_removes_empty_directory_marker()
    {
        using FakeS3Endpoint endpoint = new();
        using S3StorageDriver driver = endpoint.CreateDriver();

        driver.CreateDirectory("a/b");
        driver.DirectoryExists("a/b").Should().BeTrue();

        driver.DeleteDirectory("a/b", recursive: true);

        driver.DirectoryExists("a/b").Should().BeFalse();
        endpoint.Keys.Should().NotContain("a/b/");
    }

    [Fact]
    public void Move_carries_directory_markers_to_destination()
    {
        using FakeS3Endpoint endpoint = new();
        using S3StorageDriver driver = endpoint.CreateDriver();

        driver.CreateDirectory("old");
        driver.CreateDirectory("old/empty");
        driver
            .EnumerateFileSystemEntries("old", "*", SearchOption.AllDirectories)
            .Should()
            .BeEmpty();

        driver.MoveDirectory("old", "new");

        endpoint.Keys.Should().BeEquivalentTo(["new/", "new/empty/"]);
        driver.DirectoryExists("old").Should().BeFalse();
        driver.DirectoryExists("new").Should().BeTrue();
        driver.DirectoryExists("new/empty").Should().BeTrue();
    }

    private sealed class FakeS3Endpoint : IDisposable
    {
        private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";
        private readonly ConcurrentDictionary<string, byte> _keys = new();
        private readonly HttpListener _listener = new();
        private readonly Task _requests;

        public FakeS3Endpoint()
        {
            TcpListener portProbe = new(IPAddress.Loopback, 0);
            portProbe.Start();
            int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add($"{Url}/");
            _listener.Start();
            _requests = Task.Run(ProcessRequestsAsync);
        }

        public string Url { get; }
        public string[] Keys => [.. _keys.Keys];

        public S3StorageDriver CreateDriver() =>
            new("test-bucket", "us-east-1", endpoint: Url, accessKey: "test", secretKey: "test");

        private async Task ProcessRequestsAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) when (!_listener.IsListening)
                {
                    break;
                }
                catch (ObjectDisposedException) when (!_listener.IsListening)
                {
                    break;
                }

                await HandleAsync(context);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            string path = Uri.UnescapeDataString(request.Url!.AbsolutePath);
            string key = path["/test-bucket/".Length..];

            if (request.HttpMethod == "GET" && request.QueryString["list-type"] == "2")
            {
                string prefix = request.QueryString["prefix"] ?? string.Empty;
                XDocument list = new(
                    new XElement(
                        Ns + "ListBucketResult",
                        _keys
                            .Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                            .OrderBy(k => k)
                            .Select(k => new XElement(
                                Ns + "Contents",
                                new XElement(Ns + "Key", k)
                            )),
                        new XElement(Ns + "IsTruncated", "false")
                    )
                );
                await WriteXmlAsync(response, list);
                return;
            }

            if (request.HttpMethod == "HEAD")
            {
                response.StatusCode = _keys.ContainsKey(key) ? 200 : 404;
            }
            else if (
                request.HttpMethod == "PUT"
                && request.Headers["x-amz-copy-source"] is string source
            )
            {
                string sourceKey = Uri.UnescapeDataString(source).TrimStart('/')[
                    "test-bucket/".Length..
                ];
                if (_keys.ContainsKey(sourceKey))
                    _keys[key] = 0;
                await WriteXmlAsync(
                    response,
                    new XDocument(
                        new XElement(Ns + "CopyObjectResult", new XElement(Ns + "ETag", "\"test\""))
                    )
                );
                return;
            }
            else if (request.HttpMethod == "PUT")
            {
                _keys[key] = 0;
                response.Headers["ETag"] = "\"test\"";
            }
            else if (request.HttpMethod == "POST")
            {
                XDocument deleted = await XDocument.LoadAsync(
                    request.InputStream,
                    LoadOptions.None,
                    CancellationToken.None
                );
                foreach (string deletedKey in deleted.Descendants(Ns + "Key").Select(e => e.Value))
                    _keys.TryRemove(deletedKey, out _);
                await WriteXmlAsync(response, new XDocument(new XElement(Ns + "DeleteResult")));
                return;
            }
            else
            {
                response.StatusCode = 400;
            }

            response.Close();
        }

        private static async Task WriteXmlAsync(HttpListenerResponse response, XDocument document)
        {
            response.ContentType = "application/xml";
            byte[] bytes = Encoding.UTF8.GetBytes(document.ToString());
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
            _requests.GetAwaiter().GetResult();
        }
    }
}
