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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Access;
using NoMercy.Plugins.Search;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// The search box, answered by the plugins as well as by the library.
///
/// <para>
/// One slow provider was one slow search box for everyone, so the deadline is
/// the behaviour worth pinning: a plugin that is late is absent, and the ones
/// that answered are still returned.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginSearchTests
{
    private sealed class FakePlugin(
        IReadOnlyList<PluginSearchResult> results,
        TimeSpan? delay = null,
        Exception? throws = null
    ) : ISearchablePlugin
    {
        public string Query { get; private set; } = string.Empty;

        public Ulid Id { get; } = Ulid.NewUlid();
        public string Name => "Fake";
        public string Description => "A plugin that answers a search.";
        public Version Version { get; } = new(1, 0, 0);

        public void Initialize(IPluginContext context) { }

        public void Dispose() { }

        public async Task<IReadOnlyList<PluginSearchResult>> SearchAsync(
            string query,
            PluginCaller caller,
            CancellationToken ct
        )
        {
            Query = query;

            if (delay is not null)
            {
                await Task.Delay(delay.Value, ct);
            }

            if (throws is not null)
            {
                throw throws;
            }

            return results;
        }
    }

    private static PluginSearchResult Result(string id, string title)
    {
        return new PluginSearchResult
        {
            Id = id,
            Title = title,
            Route = "/stations/:id",
            Params = new Dictionary<string, string> { ["id"] = id },
        };
    }

    private static PluginInfo Info(Ulid id, string name, PluginStatus status = PluginStatus.Active)
    {
        return new PluginInfo
        {
            Id = id,
            Name = name,
            Description = "A plugin that answers a search.",
            Version = new Version(1, 0, 0),
            Status = status,
        };
    }

    private static PluginCaller Caller(Guid userId)
    {
        return new PluginCaller(
            new UserId(new Ulid(userId)),
            "Stoney",
            PluginRole.Owner,
            PluginAccess.Owned,
            "en",
            PluginSurface.Web
        );
    }

    private static PluginSearchService Service(
        IReadOnlyList<(PluginInfo info, IPlugin? plugin)> installed,
        PluginAccess access = PluginAccess.Owned
    )
    {
        Mock<IPluginManager> manager = new();
        manager
            .Setup(one => one.GetInstalledPlugins())
            .Returns([.. installed.Select(pair => pair.info)]);

        foreach ((PluginInfo info, IPlugin? plugin) in installed)
        {
            manager.Setup(one => one.GetPluginInstance(info.Id)).Returns(plugin);
        }

        Mock<IPluginAccessResolver> resolver = new();
        resolver.Setup(one => one.Resolve(It.IsAny<Ulid>(), It.IsAny<Guid>())).Returns(access);

        return new PluginSearchService(
            manager.Object,
            resolver.Object,
            NullLogger<PluginSearchService>.Instance
        );
    }

    [Fact]
    public async Task EveryPluginTheCallerMayUse_IsAsked()
    {
        FakePlugin radio = new([Result("r1", "NPO 3FM")]);
        FakePlugin tv = new([Result("t1", "NPO 1")]);
        Ulid first = Ulid.NewUlid();
        Ulid second = Ulid.NewUlid();

        IReadOnlyList<PluginSearchGroup> groups = await Service([
                (Info(first, "Internet Radio"), radio),
                (Info(second, "Live TV"), tv),
            ])
            .SearchAsync("3fm", Caller(Guid.NewGuid()), TimeSpan.FromSeconds(2), default);

        groups.Should().HaveCount(2);
        radio.Query.Should().Be("3fm");
        tv.Query.Should().Be("3fm");
    }

    [Fact]
    public async Task APluginTheCallerHasNoAccessTo_IsNotAskedAtAll()
    {
        FakePlugin radio = new([Result("r1", "NPO 3FM")]);

        IReadOnlyList<PluginSearchGroup> groups = await Service(
                [(Info(Ulid.NewUlid(), "Internet Radio"), radio)],
                PluginAccess.None
            )
            .SearchAsync("3fm", Caller(Guid.NewGuid()), TimeSpan.FromSeconds(2), default);

        groups.Should().BeEmpty();
        radio.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task APluginThatIsNotActive_IsNotAsked()
    {
        FakePlugin radio = new([Result("r1", "NPO 3FM")]);

        IReadOnlyList<PluginSearchGroup> groups = await Service([
                (Info(Ulid.NewUlid(), "Internet Radio", PluginStatus.Disabled), radio),
            ])
            .SearchAsync("3fm", Caller(Guid.NewGuid()), TimeSpan.FromSeconds(2), default);

        groups.Should().BeEmpty();
        radio.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task APluginThatCannotSearch_IsSkippedRatherThanFailingTheRequest()
    {
        IReadOnlyList<PluginSearchGroup> groups = await Service([
                (Info(Ulid.NewUlid(), "Encoder"), null),
            ])
            .SearchAsync("3fm", Caller(Guid.NewGuid()), TimeSpan.FromSeconds(2), default);

        groups.Should().BeEmpty();
    }

    [Fact]
    public async Task ASlowPlugin_IsAbsentAndTheOthersStillAnswer()
    {
        FakePlugin slow = new([Result("s1", "Slow")], delay: TimeSpan.FromSeconds(30));
        FakePlugin quick = new([Result("q1", "Quick")]);

        IReadOnlyList<PluginSearchGroup> groups = await Service([
                (Info(Ulid.NewUlid(), "Slow"), slow),
                (Info(Ulid.NewUlid(), "Quick"), quick),
            ])
            .SearchAsync("3fm", Caller(Guid.NewGuid()), TimeSpan.FromMilliseconds(120), default);

        groups.Should().ContainSingle();
        groups[0].PluginName.Should().Be("Quick");
    }

    [Fact]
    public async Task APluginThatThrows_IsAbsentAndTheOthersStillAnswer()
    {
        FakePlugin broken = new([], throws: new InvalidOperationException("provider down"));
        FakePlugin quick = new([Result("q1", "Quick")]);

        IReadOnlyList<PluginSearchGroup> groups = await Service([
                (Info(Ulid.NewUlid(), "Broken"), broken),
                (Info(Ulid.NewUlid(), "Quick"), quick),
            ])
            .SearchAsync("3fm", Caller(Guid.NewGuid()), TimeSpan.FromSeconds(2), default);

        groups.Should().ContainSingle();
        groups[0].PluginName.Should().Be("Quick");
    }

    [Fact]
    public async Task APluginThatFoundNothing_IsNotAGroupAtAll()
    {
        FakePlugin empty = new([]);

        IReadOnlyList<PluginSearchGroup> groups = await Service([
                (Info(Ulid.NewUlid(), "Internet Radio"), empty),
            ])
            .SearchAsync("3fm", Caller(Guid.NewGuid()), TimeSpan.FromSeconds(2), default);

        groups.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyQuery_WakesNobody()
    {
        FakePlugin radio = new([Result("r1", "NPO 3FM")]);

        IReadOnlyList<PluginSearchGroup> groups = await Service([
                (Info(Ulid.NewUlid(), "Internet Radio"), radio),
            ])
            .SearchAsync("   ", Caller(Guid.NewGuid()), TimeSpan.FromSeconds(2), default);

        groups.Should().BeEmpty();
        radio.Query.Should().BeEmpty();
    }

    [Fact]
    public async Task AGroup_NamesThePluginItCameFrom()
    {
        Ulid id = Ulid.NewUlid();
        FakePlugin radio = new([Result("r1", "NPO 3FM")]);

        IReadOnlyList<PluginSearchGroup> groups = await Service([
                (Info(id, "Internet Radio"), radio),
            ])
            .SearchAsync("3fm", Caller(Guid.NewGuid()), TimeSpan.FromSeconds(2), default);

        groups[0].PluginId.Should().Be(id.ToString());
        groups[0].PluginName.Should().Be("Internet Radio");
        groups[0].Results[0].Route.Should().Be("/stations/:id");
        groups[0].Results[0].Params["id"].Should().Be("r1");
    }
}
