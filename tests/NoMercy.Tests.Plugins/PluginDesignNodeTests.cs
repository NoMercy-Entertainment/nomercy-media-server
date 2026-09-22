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
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Design;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// A plugin can name any component the design system publishes.
///
/// <para>
/// It could name ten of the fifty-six: the tags lived in this assembly and the
/// generated contract lived in the web project, which a plugin cannot reference.
/// An accordion, a stepper, a tree view and every other component were unreachable
/// from the place most of them get used.
/// </para>
/// </summary>
public class PluginDesignNodeTests
{
    // The wire is written by Newtonsoft everywhere in this server, so a test
    // that serialises any other way agrees with itself rather than with a client.
    private static readonly JsonSerializerSettings ApiSettings =
        new MvcNewtonsoftJsonOptions().SerializerSettings;

    private static JObject Wire(PluginComponent node) =>
        JObject.Parse(JsonConvert.SerializeObject(node, ApiSettings));

    [Fact]
    public void ThePropsRecordDecidesWhichComponentIsDrawn()
    {
        PluginComponent node = PluginDesign.Node("a", new NMAccordionProps());

        node.Component.Should().Be("NMAccordion");
    }

    [Fact]
    public void EveryOptionOnTheRecordReachesTheWireUnderItsOwnName()
    {
        PluginComponent node = PluginDesign.Node(
            "steps",
            new NMStepIndicatorProps { Id = "steps", Size = "lg" }
        );

        JObject props = (JObject)Wire(node)["props"]!;

        props["size"]!.Value<string>().Should().Be("lg");
    }

    // A component nobody wired an option onto still has to arrive as itself.
    [Fact]
    public void AComponentWithNothingSetStillNamesItself()
    {
        JObject wire = Wire(PluginDesign.Node("t", new NMTreeViewProps()));

        wire["component"]!.Value<string>().Should().Be("NMTreeView");
        wire["props"].Should().NotBeNull();
    }

    // Children set the way every other factory sets them must survive a design
    // record, or a plugin has to know which of the two ways built its parent.
    [Fact]
    public void ChildrenSetOnTheNodeWinOverTheRecordsOwn()
    {
        PluginComponent node = new()
        {
            Id = "card",
            Component = PluginDesign.ComponentOf(new NMCardProps()),
            Design = new NMCardProps(),
            Items = [PluginViews.Text("card-title", "Hello")],
        };

        JArray items = (JArray)Wire(node)["props"]!["items"]!;

        items.Should().HaveCount(1);
        items[0]["id"]!.Value<string>().Should().Be("card-title");
    }

    [Fact]
    public void AnActionTravelsBesideTheOptions()
    {
        PluginComponent node = PluginDesign.Node(
            "open",
            new NMButtonProps { Variant = "primary" },
            new PluginActionIntent
            {
                Type = PluginActionType.Navigate,
                Payload = new() { ["route"] = "/somewhere" },
            }
        );

        JObject props = (JObject)Wire(node)["props"]!;

        props["variant"]!.Value<string>().Should().Be("primary");
        props["action"]!["type"].Should().NotBeNull();
    }

    // Every generated record states its own component, so a plugin cannot pair a
    // card's props with a button's name however it builds the node.
    [Fact]
    public void EveryComponentTheDesignSystemPublishesCanBeNamed()
    {
        NmProps[] sample =
        [
            new NMAccordionProps(),
            new NMStepIndicatorProps(),
            new NMTreeViewProps(),
            new NMCommandPaletteProps(),
            new NMDatePickerProps(),
        ];

        foreach (NmProps props in sample)
        {
            PluginDesign.Node("x", props).Component.Should().Be(props.Component);
        }
    }
}
