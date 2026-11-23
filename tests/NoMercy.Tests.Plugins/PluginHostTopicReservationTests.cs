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

using System.Reflection;
using FluentAssertions;
using NoMercy.Events;
using NoMercy.Events.Plugins;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public sealed class PluginHostTopicReservationTests : IDisposable
{
    private static readonly Ulid PublisherId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV");
    private static readonly Ulid SubscriberId = Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAW");

    private readonly string _dataFolder = Path.Combine(
        Path.GetTempPath(),
        $"nomercy-host-topics-{Guid.NewGuid():N}"
    );

    private readonly InMemoryEventBus _bus = new();
    private readonly List<TrackPayload> _received = [];

    public static TheoryData<string> HostTopics()
    {
        TheoryData<string> topics = [];
        foreach (string topic in SdkHostTopics())
            topics.Add(topic);

        return topics;
    }

    [Fact]
    public void The_sdk_names_the_host_topics_this_suite_covers()
    {
        SdkHostTopics()
            .Should()
            .Contain([PluginTopics.MusicPlaybackStarted, PluginTopics.LyricsRequested]);
    }

    [Theory]
    [MemberData(nameof(HostTopics))]
    public async Task A_plugin_publish_of_a_host_topic_is_refused_and_reaches_no_subscriber(
        string topic
    )
    {
        using PluginEvents subscriber = Subscribed(topic);
        using PluginEvents publisher = new(PublisherId, _bus);

        Func<Task> publish = () => publisher.PublishAsync(topic, new TrackPayload("forged"));

        await publish.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{topic}*");
        _received.Should().BeEmpty();
    }

    [Fact]
    public async Task A_refused_publish_is_logged_with_the_plugin_and_the_topic()
    {
        ListLogger logger = new();
        using PluginEvents publisher = new(PublisherId, _bus, logger);

        Func<Task> publish = () =>
            publisher.PublishAsync(PluginTopics.LyricsRequested, new TrackPayload("forged"));

        await publish.Should().ThrowAsync<InvalidOperationException>();
        logger
            .Entries.Should()
            .ContainSingle()
            .Which.Message.Should()
            .Contain(PublisherId.ToString())
            .And.Contain(PluginTopics.LyricsRequested);
    }

    [Fact]
    public async Task The_context_publish_shortcut_refuses_a_host_topic_too()
    {
        using PluginEvents subscriber = Subscribed(PluginTopics.LyricsRequested);
        Directory.CreateDirectory(_dataFolder);
        PluginContext context = TestPluginPlatform.Context(
            _bus,
            _dataFolder,
            TestStorageHelper.CreateStorage(_dataFolder),
            PublisherId
        );

        Func<Task> publish = () =>
            context.PublishAsync(PluginTopics.LyricsRequested, new TrackPayload("forged"));

        await publish.Should().ThrowAsync<InvalidOperationException>();
        _received.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_topic_sent_under_a_plugin_id_is_not_delivered()
    {
        using PluginEvents subscriber = Subscribed(PluginTopics.LyricsRequested);

        await _bus.PublishAsync(
            PluginMessageEvent.From(
                PublisherId,
                PluginTopics.LyricsRequested,
                new TrackPayload("forged")
            )
        );

        _received.Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_publish_of_a_host_topic_still_reaches_a_subscriber()
    {
        using PluginEvents subscriber = Subscribed(PluginTopics.LyricsRequested);

        await _bus.PublishAsync(
            PluginMessageEvent.From(
                Ulid.Empty,
                PluginTopics.LyricsRequested,
                new TrackPayload("real")
            )
        );

        _received.Should().ContainSingle().Which.TrackId.Should().Be("real");
    }

    [Fact]
    public async Task A_plugin_topic_of_its_own_still_reaches_another_plugin()
    {
        using PluginEvents subscriber = Subscribed("download.completed");
        using PluginEvents publisher = new(PublisherId, _bus);

        await publisher.PublishAsync("download.completed", new TrackPayload("own"));

        _received.Should().ContainSingle().Which.TrackId.Should().Be("own");
    }

    /// <summary>
    /// The server published <c>music.analysis.completed</c> until ABI 13.0. A
    /// plugin built against 12 may still listen for it, so the name stays the
    /// server's: nobody can publish it and pass for the server.
    /// </summary>
    [Fact]
    public async Task The_retired_analysis_topic_cannot_be_published_or_forged()
    {
        const string retired = "music.analysis.completed";
        using PluginEvents subscriber = Subscribed(retired);
        using PluginEvents publisher = new(PublisherId, _bus);

        Func<Task> publish = () => publisher.PublishAsync(retired, new TrackPayload("forged"));

        await publish.Should().ThrowAsync<InvalidOperationException>();
        await _bus.PublishAsync(
            PluginMessageEvent.From(PublisherId, retired, new TrackPayload("forged"))
        );
        _received.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
            Directory.Delete(_dataFolder, recursive: true);
    }

    private static List<string> SdkHostTopics() =>
        typeof(PluginTopics)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => field.GetRawConstantValue())
            .OfType<string>()
            .ToList();

    private PluginEvents Subscribed(string topic)
    {
        PluginEvents subscriber = new(SubscriberId, _bus);
        subscriber.Subscribe<TrackPayload>(
            topic,
            (payload, _) =>
            {
                _received.Add(payload);
                return Task.CompletedTask;
            }
        );
        return subscriber;
    }

    private sealed record TrackPayload(string TrackId);
}
