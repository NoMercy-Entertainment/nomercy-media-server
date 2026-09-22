using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Tests.Api.NmComponents;

/// <summary>
/// What a plugin's tree looks like once it is on the wire.
///
/// The design system declares children and the action on
/// <c>NMComponentBase</c> — the props every component extends — and every client
/// renders from that. This type declared them beside props instead, which type
/// checks on both sides and draws nothing: the payload carried children the
/// clients never looked for, so every card arrived with an empty body and every
/// card that named a destination sat there.
///
/// Nothing caught it, because every test asserted the C# objects rather than the
/// bytes. Then these asserted the bytes — through
/// <c>System.Text.Json</c>, which no response in this server is written with.
/// They passed on a serializer nobody uses while every card on screen stayed
/// empty. The settings below are the ones MVC builds for the pipeline the API
/// registers, so a wire assertion here is an assertion about the wire.
/// </summary>
public class PluginComponentWireTests
{
    private static readonly JsonSerializerSettings ApiSettings =
        new MvcNewtonsoftJsonOptions().SerializerSettings;

    private static JToken Wire(PluginComponent component)
    {
        return JToken.Parse(JsonConvert.SerializeObject(component, ApiSettings));
    }

    private static PluginComponent Leaf(string id) =>
        new()
        {
            Id = id,
            Component = "NMText",
            Props = new() { ["text"] = id },
        };

    [Fact]
    public void PutsChildrenWhereTheComponentsReadThem()
    {
        JToken wire = Wire(
            new()
            {
                Id = "card",
                Component = "NMCard",
                Items = [Leaf("one"), Leaf("two")],
            }
        );

        Assert.Null(wire["items"]);
        Assert.Equal(2, wire["props"]!["items"]!.Count());
    }

    [Fact]
    public void PutsTheActionThereToo()
    {
        JToken wire = Wire(
            new()
            {
                Id = "card",
                Component = "NMCard",
                Action = PluginActionIntent.Navigate("/somewhere"),
            }
        );

        Assert.Null(wire["action"]);
        Assert.Equal("navigate", wire["props"]!["action"]!["type"]!.Value<string>());
    }

    [Fact]
    public void SendsTheEnvelopeAndNothingBesideIt()
    {
        // The envelope is id, component, props. A fourth key was going out —
        // `wireProps`, the name of the property that built the bag — because the
        // attribute renaming it was System.Text.Json's and this pipeline is
        // Newtonsoft's. The clients read `props`, found children nowhere in it,
        // and drew a page of empty cards.
        JObject wire = (JObject)Wire(
            new()
            {
                Id = "card",
                Component = "NMCard",
                Items = [Leaf("one")],
                Action = PluginActionIntent.Navigate("/somewhere"),
            }
        );

        Assert.Equal(
            ["id", "component", "props"],
            wire.Properties().Select(property => property.Name)
        );
    }

    [Fact]
    public void LeavesBothOutWhenAPluginSentNeither()
    {
        // An empty items array on every leaf tells a client the component has
        // children, and a component handed an empty slot renders it instead of
        // its own content — which is how every leaf came out blank once.
        JToken wire = Wire(
            new()
            {
                Id = "text",
                Component = "NMText",
                Props = new() { ["text"] = "hello" },
            }
        );

        Assert.Null(wire["props"]!["items"]);
        Assert.Null(wire["props"]!["action"]);
        Assert.Equal("hello", wire["props"]!["text"]!.Value<string>());
    }

    [Fact]
    public void KeepsNestingAllTheWayDown()
    {
        JToken wire = Wire(
            new()
            {
                Id = "card",
                Component = "NMCard",
                Items =
                [
                    new()
                    {
                        Id = "header",
                        Component = "NMContentHeader",
                        Items = [Leaf("title")],
                    },
                ],
            }
        );

        JToken header = wire["props"]!["items"]![0]!;

        Assert.Equal("NMContentHeader", header["component"]!.Value<string>());
        Assert.Equal("title", header["props"]!["items"]![0]!["props"]!["text"]!.Value<string>());
    }

    [Fact]
    public void BuildsTheSameBagWhicheverOrderAnAuthorWroteIt()
    {
        // Props named after children would have erased them if the bag were
        // built as each was set, and an author has no reason to suspect the
        // order matters.
        JToken wire = Wire(
            new()
            {
                Id = "card",
                Component = "NMCard",
                Items = [Leaf("child")],
                Props = new() { ["box"] = "padded" },
            }
        );

        Assert.Equal("padded", wire["props"]!["box"]!.Value<string>());
        Assert.Single(wire["props"]!["items"]!);
    }

    [Fact]
    public void DoesNotLetAPropCalledItemsBeOverwritten()
    {
        // A component whose own props carry `items` and which also has children
        // would silently lose one of them. The children win, and this states
        // that out loud rather than leaving it to whichever ran last.
        JToken wire = Wire(
            new()
            {
                Id = "list",
                Component = "NMList",
                Props = new() { ["items"] = "from props" },
                Items = [Leaf("child")],
            }
        );

        Assert.Single(wire["props"]!["items"]!);
    }
}
