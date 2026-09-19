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
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Api.Plugins;
using Xunit;

namespace NoMercy.Tests.Api;

/// <summary>
/// What an owner and an author see when a plugin calls a member contract v3
/// took away.
/// <para>
/// Fillz's Live TV plugin reads the host container at
/// <c>IptvPlugin.cs:292</c>. Contract v3 removed that, and the runtime answers
/// with a bare missing-member exception the first time the method runs. The
/// break is allowed; a crash that names nothing is not.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginRemovedMemberTests
{
    private static ExceptionContext ContextFor(Exception exception, bool onAPluginRoute = true)
    {
        DefaultHttpContext http = new();
        RouteData route = new();

        if (onAPluginRoute)
            route.Values["pluginId"] = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

        ActionContext action = new(http, route, new ActionDescriptor());

        return new(action, []) { Exception = exception };
    }

    private static Task Run(ExceptionContext context)
    {
        return new PluginRemovedMemberFilter(
            NullLogger<PluginRemovedMemberFilter>.Instance
        ).OnExceptionAsync(context);
    }

    [Fact]
    public async Task A_removed_member_answers_with_what_why_and_how_to_fix_it()
    {
        ExceptionContext context = ContextFor(
            new MissingMethodException(
                "Method not found: 'System.IServiceProvider IPluginContext.get_Services()'."
            )
        );

        await Run(context);

        context.ExceptionHandled.Should().BeTrue("a crash teaches the author nothing");

        ObjectResult result = context.Result.Should().BeOfType<ObjectResult>().Subject;
        result.StatusCode.Should().Be(501);

        PluginRefusalDto refusal = result.Value.Should().BeOfType<PluginRefusalDto>().Subject;
        refusal.Code.Should().Be("PLUGIN_HOST_SERVICES_REMOVED");
        refusal.Plugin.Should().Be("01ARZ3NDEKTSV4RRFFQ69G5FAV");
        refusal.What.Should().Contain("get_Services", "the author has to know which member");
        refusal.Why.Should().NotBeEmpty();
        refusal.Fix.Should().Contain("11.0", "the fix names the version to build against");
        refusal.Severity.Should().Be("blocked");
    }

    [Fact]
    public async Task A_removed_member_is_found_however_deeply_it_is_wrapped()
    {
        ExceptionContext context = ContextFor(
            new InvalidOperationException(
                "outer",
                new AggregateException(
                    new MissingFieldException("Field not found: 'IPluginContext.EventBus'.")
                )
            )
        );

        await Run(context);

        context.ExceptionHandled.Should().BeTrue();
        PluginRefusalDto refusal = context
            .Result.Should()
            .BeOfType<ObjectResult>()
            .Subject.Value.Should()
            .BeOfType<PluginRefusalDto>()
            .Subject;
        refusal.What.Should().Contain("EventBus");
    }

    [Fact]
    public async Task Any_other_failure_is_left_alone()
    {
        ExceptionContext context = ContextFor(new InvalidOperationException("something else"));

        await Run(context);

        context
            .ExceptionHandled.Should()
            .BeFalse("only a removed member is this filter's business");
        context.Result.Should().BeNull();
    }

    [Fact]
    public async Task A_route_that_is_not_a_plugins_is_left_alone()
    {
        ExceptionContext context = ContextFor(
            new MissingMethodException("Method not found."),
            onAPluginRoute: false
        );

        await Run(context);

        context.ExceptionHandled.Should().BeFalse("the server's own routes are not plugins");
    }
}
