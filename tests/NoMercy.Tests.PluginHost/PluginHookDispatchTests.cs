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
using NoMercy.PluginHost;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// Every entry point the contract declares has to survive the move out of
/// process.
/// <para>
/// A hook that works in-process and not out of it is worse than one that works
/// nowhere: the plugin behaves differently depending on a server setting its
/// author cannot see, and the bug report says the plugin is broken on some
/// installs.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginHookDispatchTests
{
    [Fact]
    public async Task ASearchablePlugin_IsAskedAndItsResultsComeBack()
    {
        HookPlugin plugin = new();

        string payload = System.Text.Json.JsonSerializer.Serialize(
            new { query = "radio", caller = Caller() },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        );

        string json = await PluginDispatch.InvokeAsync(
            plugin,
            nameof(ISearchablePlugin.SearchAsync),
            payload,
            default
        );

        plugin.Searched.Should().Be("radio");
        json.Should().Contain("Station One");
    }

    /// <summary>
    /// A plugin's results can be scoped to who is asking. Inventing an
    /// identity here would hand one user's results to whoever called without
    /// naming themselves.
    /// </summary>
    [Fact]
    public async Task ASearchWithNobodyAsking_IsRefusedRatherThanServed()
    {
        HookPlugin plugin = new();

        await Assert.ThrowsAsync<PluginRefusedException>(() =>
            PluginDispatch.InvokeAsync(
                plugin,
                nameof(ISearchablePlugin.SearchAsync),
                """{"query":"radio"}""",
                default
            )
        );

        plugin.Searched.Should().BeNull();
    }

    private static PluginCaller Caller() =>
        new(
            new UserId(Ulid.NewUlid()),
            "Stoney",
            PluginRole.Owner,
            PluginAccess.Owned,
            "en",
            "web"
        );

    [Fact]
    public async Task AnAuthPlugin_IsHandedTheTokenAndItsAnswerCrossesBack()
    {
        HookPlugin plugin = new();

        string json = await PluginDispatch.InvokeAsync(
            plugin,
            nameof(IAuthPlugin.AuthenticateAsync),
            """{"token":"a-token"}""",
            default
        );

        plugin.AuthenticatedWith.Should().Be("a-token");
        json.Should().Contain("isAuthenticated");
    }

    [Fact]
    public async Task AMediaSourcePlugin_ScansThePathItWasGiven()
    {
        HookPlugin plugin = new();

        await PluginDispatch.InvokeAsync(
            plugin,
            nameof(IMediaSourcePlugin.ScanAsync),
            """{"path":"/music/radio"}""",
            default
        );

        plugin.ScannedPath.Should().Be("/music/radio");
    }

    /// <summary>
    /// A plugin that does not implement the hook is refused by name rather
    /// than answered with an empty result, which would read to the server as
    /// a plugin that searched and found nothing.
    /// </summary>
    [Fact]
    public async Task AHookThePluginDoesNotImplement_IsStillRefusedByName()
    {
        PluginRefusedException refused = await Assert.ThrowsAsync<PluginRefusedException>(() =>
            PluginDispatch.InvokeAsync(
                new BareHookPlugin(),
                nameof(ISearchablePlugin.SearchAsync),
                "{}",
                default
            )
        );

        refused.Refusal.What.Should().Contain(nameof(ISearchablePlugin.SearchAsync));
    }
}

file sealed class HookPlugin : IUiPlugin, ISearchablePlugin, IAuthPlugin, IMediaSourcePlugin
{
    public string? Searched { get; private set; }
    public string? AuthenticatedWith { get; private set; }
    public string? ScannedPath { get; private set; }

    public string Name => "Hooked";
    public string Description => "Implements several entry points.";
    public Ulid Id { get; } = Ulid.NewUlid();
    public Version Version => new(1, 0);

    public IReadOnlyList<PluginNavEntry> NavEntries => [];

    public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct) =>
        Task.FromResult(new PluginView());

    public Task<IReadOnlyList<PluginSearchResult>> SearchAsync(
        string query,
        PluginCaller caller,
        CancellationToken ct
    )
    {
        Searched = query;

        return Task.FromResult<IReadOnlyList<PluginSearchResult>>([
            new PluginSearchResult
            {
                Id = "one",
                Title = "Station One",
                Route = "/stations/one",
            },
        ]);
    }

    public Task<AuthResult> AuthenticateAsync(string token, CancellationToken ct = default)
    {
        AuthenticatedWith = token;

        return Task.FromResult(new AuthResult { IsAuthenticated = true });
    }

    public Task<IEnumerable<MediaFile>> ScanAsync(string path, CancellationToken ct = default)
    {
        ScannedPath = path;

        return Task.FromResult<IEnumerable<MediaFile>>([]);
    }

    public void Initialize(IPluginContext context) { }

    public void Dispose() { }
}

file sealed class BareHookPlugin : IPlugin
{
    public string Name => "Bare";
    public string Description => "Implements no entry point.";
    public Ulid Id { get; } = Ulid.NewUlid();
    public Version Version => new(1, 0);

    public void Initialize(IPluginContext context) { }

    public void Dispose() { }
}
