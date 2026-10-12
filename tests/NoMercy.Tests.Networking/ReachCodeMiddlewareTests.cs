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

using Microsoft.AspNetCore.Http;
using NoMercy.Networking.Connectivity;
using NoMercy.NmSystem.Networking;
using Xunit;

namespace NoMercy.Tests.Networking;

[Trait("Category", "Unit")]
public sealed class ReachCodeMiddlewareTests
{
    private static async Task<(DefaultHttpContext Context, bool ReachedNext)> SendAsync(
        ReachCodes codes,
        string path
    )
    {
        DefaultHttpContext context = new();
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        bool reachedNext = false;

        await ReachCodeMiddleware.HandleAsync(
            context,
            () =>
            {
                reachedNext = true;
                return Task.CompletedTask;
            },
            codes
        );

        return (context, reachedNext);
    }

    [Fact]
    public async Task InHttpMode_ALiveCodeIsAnswered_BeforeSetupModeCanRefuseIt()
    {
        // Setup mode answers 503 on every route. The check runs exactly then, on a server
        // that has no certificate yet, so it must be answered before that middleware.
        ReachCodes codes = new();
        string code = codes.Issue();

        (DefaultHttpContext context, bool reachedNext) = await SendAsync(
            codes,
            ReachCodes.PathPrefix + code
        );

        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.False(reachedNext);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal(code, body);
    }

    [Fact]
    public async Task AnUnknownCode_GoesOnThroughThePipeline()
    {
        ReachCodes codes = new();
        codes.Issue();

        (_, bool reachedNext) = await SendAsync(codes, ReachCodes.PathPrefix + "unknown");

        Assert.True(reachedNext);
    }

    [Fact]
    public async Task OtherPaths_GoOnThroughThePipeline()
    {
        (_, bool reachedNext) = await SendAsync(new(), "/status");

        Assert.True(reachedNext);
    }
}
