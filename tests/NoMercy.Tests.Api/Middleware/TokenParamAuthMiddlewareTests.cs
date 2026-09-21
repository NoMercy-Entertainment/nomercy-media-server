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

using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using NoMercy.Api.Middleware;
using Xunit;

namespace NoMercy.Tests.Api.Middleware;

/// <summary>
/// TokenParamAuthMiddleware only promotes a ?token=/?access_token= query
/// parameter to a bearer Authorization header and always continues. It never
/// decides access: that belongs to FolderAccessMiddleware, which runs after
/// authentication and can see the validated principal.
/// </summary>
[Trait("Category", "Unit")]
public class TokenParamAuthMiddlewareTests
{
    private static TokenParamAuthMiddleware CreateMiddleware(out StrongBox<bool> nextCalled)
    {
        StrongBox<bool> called = new(false);
        TokenParamAuthMiddleware middleware = new(_ =>
        {
            called.Value = true;
            return Task.CompletedTask;
        });
        nextCalled = called;
        return middleware;
    }

    private static DefaultHttpContext MakeContext(string path)
    {
        DefaultHttpContext context = new() { RequestServices = null! };
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task AccessTokenQueryParam_PromotedToBearerAuthorizationHeader()
    {
        TokenParamAuthMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);

        DefaultHttpContext context = MakeContext("/random-non-folder-path");
        context.Request.QueryString = new("?access_token=my-jwt-value");

        await middleware.InvokeAsync(context);

        context.Request.Headers.Authorization.ToString().Should().Be("Bearer my-jwt-value");
        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task TokenQueryParam_PromotedToBearerAuthorizationHeader()
    {
        TokenParamAuthMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);

        DefaultHttpContext context = MakeContext("/random-non-folder-path");
        context.Request.QueryString = new("?token=my-jwt-value");

        await middleware.InvokeAsync(context);

        context.Request.Headers.Authorization.ToString().Should().Be("Bearer my-jwt-value");
        nextCalled.Value.Should().BeTrue();
    }

    [Fact]
    public async Task ExistingBearerHeader_IsNeverOverwrittenByQueryParam()
    {
        TokenParamAuthMiddleware middleware = CreateMiddleware(out _);

        DefaultHttpContext context = MakeContext("/random-non-folder-path");
        context.Request.Headers.Authorization = "Bearer already-set-token";
        context.Request.QueryString = new("?access_token=should-be-ignored");

        await middleware.InvokeAsync(context);

        context.Request.Headers.Authorization.ToString().Should().Be("Bearer already-set-token");
    }

    [Fact]
    public async Task NoTokenQueryParamAndNoBearer_LeavesAuthorizationHeaderEmpty()
    {
        TokenParamAuthMiddleware middleware = CreateMiddleware(out _);

        DefaultHttpContext context = MakeContext("/random-non-folder-path");

        await middleware.InvokeAsync(context);

        context.Request.Headers.Authorization.ToString().Should().BeEmpty();
    }

    [Fact]
    public async Task FolderScopedPath_WithoutAnyToken_StillCallsNext()
    {
        // The gate lives downstream. This middleware must not short-circuit a
        // folder path on its own: before authentication has run it cannot know
        // who the caller is.
        TokenParamAuthMiddleware middleware = CreateMiddleware(out StrongBox<bool> nextCalled);

        DefaultHttpContext context = MakeContext($"/{Ulid.NewUlid()}/movie.m3u8");

        await middleware.InvokeAsync(context);

        nextCalled.Value.Should().BeTrue();
        context.Response.StatusCode.Should().Be(200);
    }
}
