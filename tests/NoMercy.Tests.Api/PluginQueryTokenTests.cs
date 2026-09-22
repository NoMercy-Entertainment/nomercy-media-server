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

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Plugins;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// Design section 10 item 18: v3 ships the replacement capabilities before v2
/// code outside the contract is blocked. Internet Radio's audio proxy passes
/// its bearer in the query string, so refusing it the moment plugin routes
/// gained authorization would stop radio playback on a box that works today.
/// <para>
/// A plugin on ABI 10.x keeps being served and the server says once, in the
/// log, what to change. From ABI 11 the same request is refused with the
/// teaching message of section 3.9, never a bare 401.
/// </para>
/// </summary>
[Trait("Category", "Authorization")]
public class PluginQueryTokenTests
{
    private static readonly Ulid PluginId = Ulid.NewUlid();

    private class SampleController : PluginControllerBase;

    private class StubPluginManager(string? targetAbi) : IPluginManager
    {
        public IReadOnlyList<PluginInfo> GetInstalledPlugins() =>
            [
                new()
                {
                    Id = PluginId,
                    Name = "Internet Radio",
                    Description = "d",
                    Version = new(1, 5, 0),
                    Status = PluginStatus.Active,
                    TargetAbi = targetAbi,
                    Capabilities = new() { Rest = true },
                },
            ];

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

    /// <summary>Counts warnings, because "said once" is the behavior under test.</summary>
    private class CountingLogger : ILogger<PluginQueryTokenFilter>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private static ActionExecutingContext Request(string query)
    {
        DefaultHttpContext http = new();
        http.Request.Path = $"/api/v1/plugins/{PluginId}/stream";
        http.Request.QueryString = new(query);

        RouteData routeData = new();
        routeData.Values["pluginId"] = PluginId.ToString();

        ActionContext actionContext = new(http, routeData, new ActionDescriptor());

        return new(actionContext, [], new Dictionary<string, object?>(), new SampleController());
    }

    private static async Task<(ActionExecutingContext Context, bool Ran)> Run(
        string? targetAbi,
        string query,
        ILogger<PluginQueryTokenFilter>? logger = null
    )
    {
        PluginQueryTokenFilter filter = new(
            new StubPluginManager(targetAbi),
            logger ?? NullLogger<PluginQueryTokenFilter>.Instance
        );
        ActionExecutingContext context = Request(query);
        bool ran = false;

        await filter.OnActionExecutionAsync(
            context,
            () =>
            {
                ran = true;
                return Task.FromResult<ActionExecutedContext>(new(context, [], context.Controller));
            }
        );

        return (context, ran);
    }

    [Fact]
    public async Task A_plugin_on_abi_ten_keeps_being_served()
    {
        (ActionExecutingContext context, bool ran) = await Run("10.2", "?access_token=abc");

        ran.Should().BeTrue("radio playback works on this box today");
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task A_plugin_on_abi_ten_is_told_once_what_to_change()
    {
        CountingLogger logger = new();
        PluginQueryTokenFilter filter = new(new StubPluginManager("10.2"), logger);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            ActionExecutingContext context = Request("?access_token=abc");
            await filter.OnActionExecutionAsync(
                context,
                () => Task.FromResult<ActionExecutedContext>(new(context, [], context.Controller))
            );
        }

        logger.Warnings.Should().ContainSingle();
        logger.Warnings[0].Should().Contain("Internet Radio");
        logger.Warnings[0].Should().Contain("Authorization header");
    }

    [Fact]
    public async Task A_plugin_on_abi_eleven_is_refused()
    {
        (ActionExecutingContext context, bool ran) = await Run("11.0", "?access_token=abc");

        ran.Should().BeFalse();
        context.Result.Should().BeOfType<ObjectResult>();

        ObjectResult refusal = (ObjectResult)context.Result!;
        refusal.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);

        PluginRefusalDto body = (PluginRefusalDto)refusal.Value!;
        body.Code.Should().Be(PluginRefusalCode.TokenInUrl);
        body.Plugin.Should().Contain("Internet Radio");
        body.What.Should().NotBeNullOrWhiteSpace();
        body.Why.Should().NotBeNullOrWhiteSpace();
        body.Fix.Should().Contain("media.proxy");
        body.Severity.Should().Be("blocked");
    }

    [Fact]
    public async Task A_request_that_carries_its_token_in_the_header_is_never_touched()
    {
        (ActionExecutingContext context, bool ran) = await Run("11.0", "?station=jazz");

        ran.Should().BeTrue();
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task A_manifest_that_names_no_abi_is_read_as_the_v2_era_it_came_from()
    {
        (_, bool ran) = await Run(null, "?token=abc");

        ran.Should().BeTrue();
    }
}
