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

namespace NoMercy.Tests.NmSystem.Extensions;

[Trait("Category", "Unit")]
public class GenericHttpClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendAsync_WhenFirstAttemptTimesOut_RetriesAndSucceeds(bool withQuery)
    {
        StallOnceHandler handler = new();
        using HttpClient httpClient = new(handler)
        {
            BaseAddress = new("https://example.invalid/"),
        };
        GenericHttpClient client = new(timeoutSeconds: 1, retryCount: 1);
        FieldInfo clientField = typeof(GenericHttpClient).GetField(
            "_client",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        clientField.SetValue(client, httpClient);

        using HttpResponseMessage response = withQuery
            ? await client.SendAsync(
                HttpMethod.Get,
                "test",
                new Dictionary<string, string> { ["q"] = "1" }
            )
            : await client.SendAsync(HttpMethod.Get, "test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Attempts);
    }

    private sealed class StallOnceHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Attempts++;
            if (Attempts == 1)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new(HttpStatusCode.OK);
        }
    }
}
