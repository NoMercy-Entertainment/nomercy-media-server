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

using System.Text.RegularExpressions;
using I18N.DotNet;
using Microsoft.AspNetCore.Http;
using NoMercy.Api.Middleware;
using NoMercy.NmSystem.Extensions;
using NoMercy.Tests.Common;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public class LocalizationMiddlewareTests
{
    [Theory]
    [InlineData(["en-US,nl;q=0.9", "en-US"])]
    [InlineData(["nl;q=1.0,en;q=0.5", "nl"])]
    [InlineData(["nl-NL,nl;q=0.9,en;q=0.8", "nl-NL"])]
    [InlineData(["en;q=0.8,nl;q=0.9", "nl"])]
    [InlineData(["*,nl;q=1.0", "nl"])]
    [InlineData(["", "en-US"])]
    public void ParseBestLanguage_PicksHighestQualityWeight(string header, string expected)
    {
        Assert.Equal(expected, LocalizationMiddleware.ParseBestLanguage(header));
    }

    [Fact]
    public void ApplicationConfiguration_HasSingleUseRequestLocalizationCall()
    {
        string sourceFile = RepoPaths.At(
            "src",
            "NoMercy.Service",
            "Configuration",
            "ApplicationConfiguration.cs"
        );

        string source = File.ReadAllText(sourceFile);

        int count = Regex.Matches(source, @"UseRequestLocalization\s*\(").Count;

        Assert.Equal(1, count);
    }

    // The localizer is per request: code further down the pipeline reads it while the
    // request runs, and a concurrent request in another language must not replace it.
    private static async Task<T> InsideRequest<T>(string? acceptLanguage, Func<T> read)
    {
        T result = default!;
        LocalizationMiddleware middleware = new(_ =>
        {
            result = read();
            return Task.CompletedTask;
        });
        DefaultHttpContext context = new();
        if (acceptLanguage is not null)
            context.Request.Headers["Accept-Language"] = acceptLanguage;

        await middleware.InvokeAsync(context);

        return result;
    }

    [Fact]
    public async Task InvokeAsync_UsesTheRequestLanguage()
    {
        string language = await InsideRequest(
            "nl-NL",
            () => LocalizationHelper.CurrentLocalizer.TargetLanguage
        );

        Assert.Equal("nl", language);
    }

    [Fact]
    public async Task InvokeAsync_SetsLocalizer_WhenNoAcceptLanguageHeader()
    {
        ILocalizer localizer = await InsideRequest(null, () => LocalizationHelper.CurrentLocalizer);

        Assert.NotNull(localizer);
    }

    [Fact]
    public async Task InvokeAsync_ConcurrentRequestsInDifferentLanguages_EachKeepTheirOwn()
    {
        TaskCompletionSource bothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0;

        async Task<string> Request(string header)
        {
            string seen = string.Empty;
            LocalizationMiddleware middleware = new(async _ =>
            {
                if (Interlocked.Increment(ref entered) == 2)
                    bothEntered.SetResult();
                await bothEntered.Task;
                seen = LocalizationHelper.CurrentLocalizer.TargetLanguage;
            });
            DefaultHttpContext context = new();
            context.Request.Headers["Accept-Language"] = header;
            await middleware.InvokeAsync(context);
            return seen;
        }

        string[] seen = await Task.WhenAll(Request("nl-NL"), Request("de-DE"));

        Assert.Equal(["nl", "de"], seen);
    }

    [Fact]
    public async Task InvokeAsync_ReusesCachedLocalizer_ForSameLanguage()
    {
        ILocalizer first = await InsideRequest("de-DE", () => LocalizationHelper.CurrentLocalizer);
        ILocalizer second = await InsideRequest("de-DE", () => LocalizationHelper.CurrentLocalizer);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task InvokeAsync_CreatesDifferentLocalizer_ForDifferentLanguage()
    {
        ILocalizer french = await InsideRequest("fr-FR", () => LocalizationHelper.CurrentLocalizer);
        ILocalizer spanish = await InsideRequest(
            "es-ES",
            () => LocalizationHelper.CurrentLocalizer
        );

        Assert.NotSame(french, spanish);
    }

    [Fact]
    public async Task InvokeAsync_CallsNextMiddleware()
    {
        bool nextCalled = await InsideRequest("en-US", () => true);

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_SetsAcceptLanguageHeader_WithLanguageParts()
    {
        LocalizationMiddleware middleware = new(_ => Task.CompletedTask);
        DefaultHttpContext context = new();
        context.Request.Headers["Accept-Language"] = "nl-NL,en-US;q=0.9";

        await middleware.InvokeAsync(context);

        string?[] acceptLanguage = context.Request.Headers.AcceptLanguage.ToArray();
        Assert.Contains("nl", acceptLanguage);
        Assert.Contains("NL", acceptLanguage);
    }

    [Fact]
    public async Task InvokeAsync_HandlesLanguageWithoutRegion()
    {
        string language = await InsideRequest(
            "nl",
            () => LocalizationHelper.CurrentLocalizer.TargetLanguage
        );

        Assert.Equal("nl", language);
    }

    // Entered where a Dutch viewer enters: the middleware, on a request carrying
    // Accept-Language, reading the catalogue embedded in the built NoMercy.Api
    // assembly. A build succeeding says nothing about any of that — every one of
    // these strings reached a Dutch user in English, and four of them were
    // appended by the runtime itself as it hit them.
    [Theory]
    [InlineData("Live session not found")]
    [InlineData("Image folder not found")]
    [InlineData("Maximum concurrent live sessions reached")]
    [InlineData("No video found for the given media")]
    [InlineData("Plugin enabled successfully")]
    [InlineData("Plugin disabled successfully")]
    [InlineData("Plugin consent revoked")]
    [InlineData("No repository offers this plugin")]
    [InlineData("Something went wrong moving the item")]
    [InlineData("Failed job has been queued for retry")]
    [InlineData("Unprocessable Entity.")]
    public async Task ADutchRequestGetsDutchBack(string englishKey)
    {
        string dutch = await InsideRequest("nl", () => englishKey.Localize());

        Assert.NotEqual(englishKey, dutch);
        Assert.False(string.IsNullOrWhiteSpace(dutch));
    }

    // The placeholders and the words Dutch spells the same way. Asserted so a
    // later sweep that "fixes" every key equal to its value cannot invent a
    // translation for a format string.
    [Theory]
    [InlineData("{0} {1}")]
    [InlineData("Albums")]
    [InlineData("Genres")]
    [InlineData("Conflict.")]
    public async Task SomeKeysAreTheSameInDutchAndStayThatWay(string englishKey)
    {
        string dutch = await InsideRequest("nl", () => englishKey.Localize());

        Assert.Equal(englishKey, dutch);
    }
}
