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
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

public class PluginUserFacadeTests
{
    [Fact]
    public void The_user_facade_answers_only_about_the_caller()
    {
        IEnumerable<Type> parameters = typeof(IPluginUserData)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType);

        parameters
            .Should()
            .NotContain(
                typeof(UserId),
                "a plugin that wants another person's history has to ask that person"
            );
        parameters.Should().NotContain(typeof(UserId?));
    }

    [Fact]
    public void Listing_members_takes_no_user_because_it_is_the_whole_server()
    {
        typeof(IPluginUsers).GetMethod("ListAsync")!.GetParameters().Should().HaveCount(1);
    }

    [Fact]
    public void A_notification_carries_an_i18n_key_not_a_sentence()
    {
        PluginNotification notification = new()
        {
            TitleKey = "radio.notify.title",
            BodyKey = "radio.notify.body",
        };

        typeof(PluginNotification)
            .GetProperty("Title")
            .Should()
            .BeNull("a sentence stored in English is still English for a Dutch reader");
        typeof(PluginNotification).GetProperty("Body").Should().BeNull();
        notification.TitleKey.Should().Be("radio.notify.title");
        notification.BodyKey.Should().Be("radio.notify.body");
    }

    [Fact]
    public void Notifying_nobody_in_particular_means_the_owner()
    {
        ParameterInfo user = typeof(IPluginNotifications).GetMethod("PushAsync")!.GetParameters()[
            0
        ];

        user.ParameterType.Should().Be(typeof(UserId?));
    }

    /// <summary>
    /// The return type, and only that. Nullability is erased at runtime, so
    /// typeof(Task&lt;PluginPlaybackState?&gt;) and typeof(Task&lt;PluginPlaybackState&gt;)
    /// are the same type and no reflection assertion can tell them apart. Found
    /// by mutation: dropping the ? left this green.
    /// </summary>
    [Fact]
    public void Player_state_answers_a_playback_state_rather_than_a_bare_task()
    {
        typeof(IPluginPlayer)
            .GetMethod("GetStateAsync")!
            .ReturnType.Should()
            .Be(typeof(Task<PluginPlaybackState?>));
    }

    [Fact]
    public void A_queue_moves_in_one_call_rather_than_a_remove_and_an_add()
    {
        MethodInfo move = typeof(IPluginPlayerQueue).GetMethod("MoveAsync")!;

        move.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Should()
            .Equal(typeof(int), typeof(int), typeof(CancellationToken));
    }

    [Fact]
    public void Hub_handlers_receive_the_caller()
    {
        MethodInfo handle = typeof(IPluginHubContext).GetMethod("Handle")!;

        handle
            .GetParameters()[1]
            .ParameterType.ToString()
            .Should()
            .Contain(
                "PluginCaller",
                "a hub method reached without a caller cannot tell the owner from a guest"
            );
    }

    [Fact]
    public void An_entitlement_is_asked_of_the_host_not_of_nomercy_tv()
    {
        MethodInfo hasFeature = typeof(IPluginMarketplace).GetMethod("HasFeature")!;

        hasFeature.ReturnType.Should().Be(typeof(bool));
        hasFeature
            .GetParameters()
            .Should()
            .ContainSingle("an offline server working from a signed bundle answers the same way");
    }
}
