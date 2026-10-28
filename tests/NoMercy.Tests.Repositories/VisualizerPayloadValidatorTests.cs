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
using Newtonsoft.Json.Linq;
using NoMercy.Api.Services.Visualizers;
using NoMercy.Data.Visualizers;
using NoMercy.Encoder.Errors;
using Xunit;

namespace NoMercy.Tests.Repositories;

public class VisualizerPayloadValidatorTests
{
    private static JObject Preset() =>
        (JObject)
            VisualizerBundle
                .LoadEmbedded()
                .Entries.First(e => e.Format == "milkdrop")
                .Payload.DeepClone();

    private static JObject Shader() =>
        new()
        {
            ["schema"] = 1,
            ["format"] = "twigl-geek",
            ["source"] = "made up",
            ["passes"] = new JArray
            {
                Pass("bufferA", new JObject { ["0"] = "music" }),
                Pass("image", new JObject { ["0"] = "bufferA", ["1"] = "music" }),
            },
        };

    private static JObject Pass(string name, JObject channels) =>
        new()
        {
            ["name"] = name,
            ["channels"] = channels,
            ["program"] = "#version 300 es\nvoid main() {}",
        };

    private static JObject Kode() =>
        JObject.Parse(
            """
            {"schema":1,"format":"kodelife","passes":[
              {"name":"spec","draw":"fullscreen","target":"spectrum","vertex":"v","fragment":"f"},
              {"name":"main","draw":{"mode":"LINES","vertices":10,"instances":1},"target":"screen",
               "camera":{"eye":[0,0,1],"center":[0,0,0],"up":[0,1,0],"fov":60,"near":0.1,"far":100},
               "vertex":"v","fragment":"f"}]}
            """
        );

    private static string[] Ids(JToken payload, string format) =>
        VisualizerPayloadValidator.Validate(payload, format).Select(r => r.Id).ToArray();

    [Fact]
    public void EveryShippedEntryIsAccepted()
    {
        foreach (VisualizerBundleEntry entry in VisualizerBundle.LoadEmbedded().Entries)
            VisualizerPayloadValidator
                .Validate(entry.Payload, entry.Format)
                .Should()
                .BeEmpty(entry.Slug);
    }

    [Fact]
    public void ShaderAndKodeLifeSamplesAreAccepted()
    {
        VisualizerPayloadValidator.Validate(Shader(), "twigl-geek").Should().BeEmpty();
        VisualizerPayloadValidator.Validate(Kode(), "kodelife").Should().BeEmpty();
    }

    [Fact]
    public void EmptyEqsStrIsAccepted()
    {
        JObject payload = Preset();
        payload["preset"]!["frame_eqs_str"] = "";
        ((JObject)payload["preset"]!["shapes"]![2]!)["init_eqs_str"] = "";

        VisualizerPayloadValidator.Validate(payload, "milkdrop").Should().BeEmpty();
    }

    [Fact]
    public void UseJsAtTheTopOfThePresetIsRefused()
    {
        JObject payload = Preset();
        payload["preset"]!["useJS"] = 1;

        IReadOnlyList<EncoderRule> rules = VisualizerPayloadValidator.Validate(payload, "milkdrop");

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be(VisualizerRuleId.PayloadHasCode);
        rules[0].Field.Should().Be("/payload/preset/useJS");
    }

    [Fact]
    public void NonEmptyFrameEqsStrIsRefused()
    {
        JObject payload = Preset();
        payload["preset"]!["frame_eqs_str"] = "a=1;";

        IReadOnlyList<EncoderRule> rules = VisualizerPayloadValidator.Validate(payload, "milkdrop");

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be(VisualizerRuleId.PayloadHasCode);
        rules[0].Field.Should().Be("/payload/preset/frame_eqs_str");
    }

    [Fact]
    public void NonEmptyInitEqsStrInAShapeIsRefused()
    {
        JObject payload = Preset();
        ((JObject)payload["preset"]!["shapes"]![2]!)["init_eqs_str"] = "x";

        IReadOnlyList<EncoderRule> rules = VisualizerPayloadValidator.Validate(payload, "milkdrop");

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be(VisualizerRuleId.PayloadHasCode);
        rules[0].Field.Should().Be("/payload/preset/shapes/2/init_eqs_str");
    }

    [Fact]
    public void NonEmptyPointEqsStrInAWaveIsRefused()
    {
        JObject payload = Preset();
        ((JObject)payload["preset"]!["waves"]![0]!)["point_eqs_str"] = "x";

        IReadOnlyList<EncoderRule> rules = VisualizerPayloadValidator.Validate(payload, "milkdrop");

        rules.Should().ContainSingle();
        rules[0].Id.Should().Be(VisualizerRuleId.PayloadHasCode);
        rules[0].Field.Should().Be("/payload/preset/waves/0/point_eqs_str");
    }

    [Fact]
    public void UnknownKeyAtTheTopIsRefused()
    {
        JObject payload = Preset();
        payload["onload"] = "alert(1)";

        Ids(payload, "milkdrop").Should().Equal(VisualizerRuleId.SchemaInvalid);
    }

    [Fact]
    public void UnknownKeyInAShaderPassIsRefused()
    {
        JObject payload = Shader();
        payload["passes"]![0]!["script"] = "x";

        Ids(payload, "twigl-geek").Should().Equal(VisualizerRuleId.SchemaInvalid);
    }

    [Fact]
    public void SchemaAboveOneIsRefused()
    {
        JObject payload = Preset();
        payload["schema"] = 2;

        Ids(payload, "milkdrop").Should().Equal(VisualizerRuleId.SchemaVersionUnsupported);
    }

    [Fact]
    public void WrongFormatIsRefused()
    {
        JObject payload = Preset();

        Ids(payload, "kodelife").Should().Equal(VisualizerRuleId.FormatMismatch);

        payload["format"] = "winamp";
        Ids(payload, "milkdrop").Should().Equal(VisualizerRuleId.UnknownFormat);
    }

    [Fact]
    public void PassOrderAndChannelChecksRefuse()
    {
        JObject reversed = new()
        {
            ["schema"] = 1,
            ["format"] = "twigl-geek",
            ["source"] = "x",
            ["passes"] = new JArray(Shader()["passes"]!.Reverse()),
        };
        Ids(reversed, "twigl-geek").Should().Equal(VisualizerRuleId.PassOrder);

        JObject twice = Shader();
        ((JArray)twice["passes"]!).Insert(0, Pass("bufferA", new JObject()));
        Ids(twice, "twigl-geek").Should().Equal(VisualizerRuleId.PassOrder);

        JObject unknown = Shader();
        unknown["passes"]![1]!["channels"]!["2"] = "bufferC";
        Ids(unknown, "twigl-geek").Should().Equal(VisualizerRuleId.ChannelUnknownPass);

        JObject kode = Kode();
        kode["passes"]![1]!["target"] = "offscreen";
        Ids(kode, "kodelife").Should().Equal(VisualizerRuleId.PassOrder);
    }

    [Fact]
    public void ADeeplyNestedPayloadIsRefused()
    {
        JObject payload = Shader();
        JToken deep = new JObject();
        for (int i = 0; i < 9; i++)
            deep = new JObject { ["a"] = deep };
        payload["extra"] = deep;

        VisualizerPayloadValidator
            .Validate(payload, "twigl-geek")
            .Should()
            .ContainSingle(r => r.Id == VisualizerRuleId.SchemaInvalid && r.Field == "/payload");
    }
}
