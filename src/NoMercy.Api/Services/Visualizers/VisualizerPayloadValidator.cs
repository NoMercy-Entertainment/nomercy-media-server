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

using Newtonsoft.Json.Linq;
using NoMercy.Encoder.Errors;

namespace NoMercy.Api.Services.Visualizers;

/// <summary>
/// Checks a payload against schema v1 (contract 2a, 6.2) and the checks S1-S4
/// and S7 (6.4). It only reads JSON; it never runs any of the text.
/// </summary>
public static class VisualizerPayloadValidator
{
    public const int MaxText = 262144;
    public const int MaxDepth = 8;

    public static readonly IReadOnlyList<string> ShaderFormats =
    [
        "twigl-classic",
        "twigl-geek",
        "twigl-geeker",
        "twigl-geekest",
        "shadertoy",
    ];

    private const int MaxEel = 65536;
    private const string PassPrefix = "#version 300 es\n";
    private static readonly string[] PassNames =
    [
        "bufferA",
        "bufferB",
        "bufferC",
        "bufferD",
        "image",
    ];
    private static readonly string[] Buffers = ["bufferA", "bufferB", "bufferC", "bufferD"];
    private static readonly string[] PresetKeys =
    [
        "version",
        "baseVals",
        "shapes",
        "waves",
        "init_eqs_eel",
        "frame_eqs_eel",
        "pixel_eqs_eel",
        "warp",
        "comp",
    ];
    private static readonly string[] SlotKeys =
    [
        "baseVals",
        "init_eqs_eel",
        "frame_eqs_eel",
        "point_eqs_eel",
    ];

    public static bool IsKnownFormat(string format) =>
        ShaderFormats.Contains(format) || format is "kodelife" or "milkdrop";

    /// <summary>Returns the error rules. An empty list means the payload is accepted.</summary>
    public static IReadOnlyList<EncoderRule> Validate(JToken? payload, string entryFormat)
    {
        List<EncoderRule> rules = [];
        const string root = "/payload";

        if (payload is not JObject obj)
        {
            Add(rules, VisualizerRuleId.SchemaInvalid, root, "The payload must be a JSON object.");
            return rules;
        }

        if (Depth(obj) > MaxDepth)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                root,
                $"The payload nests deeper than {MaxDepth} levels."
            );
            return rules;
        }

        if (!obj.TryGetValue("schema", out JToken? schema) || schema.Type != JTokenType.Integer)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                root + "/schema",
                "schema must be the integer 1."
            );
            return rules;
        }
        if (schema.Value<long>() != 1)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaVersionUnsupported,
                root + "/schema",
                "Only schema 1 is supported."
            );
            return rules;
        }

        if (
            !obj.TryGetValue("format", out JToken? formatToken)
            || formatToken.Type != JTokenType.String
        )
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                root + "/format",
                "format must be a string."
            );
            return rules;
        }
        string format = formatToken.Value<string>()!;
        if (!IsKnownFormat(format))
        {
            Add(
                rules,
                VisualizerRuleId.UnknownFormat,
                root + "/format",
                "format is not one of the seven formats."
            );
            return rules;
        }
        if (format != entryFormat)
        {
            Add(
                rules,
                VisualizerRuleId.FormatMismatch,
                root + "/format",
                "payload.format must equal the entry format."
            );
            return rules;
        }

        if (ShaderFormats.Contains(format))
            ValidateShader(obj, rules);
        else if (format == "kodelife")
            ValidateKodeLife(obj, rules);
        else
            ValidateMilkdrop(obj, rules);

        return rules;
    }

    private static void ValidateShader(JObject obj, List<EncoderRule> rules)
    {
        const string root = "/payload";
        ClosedKeys(obj, root, ["schema", "format", "source", "passes"], rules);
        Text(obj, "source", root, MaxText, true, rules);

        if (Array(obj, "passes", root, 1, 5, rules) is not { } passes)
            return;

        List<string> names = [];
        int before = rules.Count;
        for (int i = 0; i < passes.Count; i++)
        {
            string at = $"{root}/passes/{i}";
            if (passes[i] is not JObject pass)
            {
                Add(rules, VisualizerRuleId.SchemaInvalid, at, "A pass must be an object.");
                continue;
            }
            ClosedKeys(pass, at, ["name", "channels", "program"], rules);
            string? name = Enum(pass, "name", at, PassNames, true, rules);
            if (name is not null)
                names.Add(name);
            Channels(pass, at, rules);
            if (
                Text(pass, "program", at, MaxText, true, rules) is { } program
                && !program.StartsWith(PassPrefix, StringComparison.Ordinal)
            )
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    at + "/program",
                    "program must start with \"#version 300 es\"."
                );
        }
        if (rules.Count > before)
            return;

        if (names.Count(n => n == "image") != 1)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                root + "/passes",
                "Exactly one pass must be named image."
            );
            return;
        }
        if (names.Distinct().Count() != names.Count || names[^1] != "image")
        {
            Add(
                rules,
                VisualizerRuleId.PassOrder,
                root + "/passes",
                "Pass names must be unique and image must be last."
            );
            return;
        }
        for (int i = 0; i < passes.Count; i++)
        {
            if (passes[i]["channels"] is not JObject channels)
                continue;
            foreach (JProperty channel in channels.Properties())
            {
                string value = channel.Value.Value<string>() ?? "";
                if (value != "music" && !names.Contains(value))
                    Add(
                        rules,
                        VisualizerRuleId.ChannelUnknownPass,
                        $"{root}/passes/{i}/channels/{channel.Name}",
                        "A channel names a pass that is not in the list."
                    );
            }
        }
    }

    private static void Channels(JObject pass, string at, List<EncoderRule> rules)
    {
        if (pass["channels"] is not JObject channels)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                at + "/channels",
                "channels must be an object."
            );
            return;
        }
        ClosedKeys(channels, at + "/channels", ["0", "1", "2", "3"], rules);
        foreach (JProperty channel in channels.Properties())
        {
            if (
                channel.Value.Type != JTokenType.String
                || (
                    channel.Value.Value<string>() != "music"
                    && !Buffers.Contains(channel.Value.Value<string>())
                )
            )
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    $"{at}/channels/{Escape(channel.Name)}",
                    "A channel must be music or a buffer name."
                );
        }
    }

    private static void ValidateKodeLife(JObject obj, List<EncoderRule> rules)
    {
        const string root = "/payload";
        ClosedKeys(obj, root, ["schema", "format", "passes"], rules);
        if (Array(obj, "passes", root, 1, 8, rules) is not { } passes)
            return;

        List<string> names = [];
        List<string> targets = [];
        int before = rules.Count;
        for (int i = 0; i < passes.Count; i++)
        {
            string at = $"{root}/passes/{i}";
            if (passes[i] is not JObject pass)
            {
                Add(rules, VisualizerRuleId.SchemaInvalid, at, "A pass must be an object.");
                continue;
            }
            ClosedKeys(pass, at, ["name", "draw", "target", "camera", "vertex", "fragment"], rules);
            string? name = Text(pass, "name", at, 64, true, rules);
            if (name is not null)
            {
                if (!IsIdentifier(name))
                    Add(
                        rules,
                        VisualizerRuleId.SchemaInvalid,
                        at + "/name",
                        "name must be an identifier of up to 64 characters."
                    );
                names.Add(name);
            }
            Text(pass, "vertex", at, MaxText, true, rules);
            Text(pass, "fragment", at, MaxText, true, rules);
            string? target = Enum(
                pass,
                "target",
                at,
                ["screen", "spectrum", "offscreen"],
                true,
                rules
            );
            if (target is not null)
                targets.Add(target);
            bool fullscreen = Draw(pass, at, rules);
            if (pass.TryGetValue("camera", out JToken? camera))
                Camera(camera, at + "/camera", rules);
            else if (!fullscreen && pass.ContainsKey("draw"))
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    at + "/camera",
                    "camera is required unless draw is fullscreen."
                );
        }
        if (rules.Count > before)
            return;

        if (
            names.Distinct().Count() != names.Count
            || targets[^1] != "screen"
            || targets.Count(t => t == "screen") != 1
        )
            Add(
                rules,
                VisualizerRuleId.PassOrder,
                root + "/passes",
                "Names must be unique and only the last pass targets screen."
            );
    }

    private static bool Draw(JObject pass, string at, List<EncoderRule> rules)
    {
        if (!pass.TryGetValue("draw", out JToken? draw))
        {
            Add(rules, VisualizerRuleId.SchemaInvalid, at + "/draw", "draw is required.");
            return true;
        }
        if (draw.Type == JTokenType.String && draw.Value<string>() == "fullscreen")
            return true;
        if (draw is not JObject drawObj)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                at + "/draw",
                "draw must be \"fullscreen\" or an object."
            );
            return true;
        }
        ClosedKeys(drawObj, at + "/draw", ["mode", "vertices", "instances"], rules);
        Enum(drawObj, "mode", at + "/draw", ["LINES", "POINTS", "TRIANGLES"], true, rules);
        foreach (string key in new[] { "vertices", "instances" })
        {
            if (
                !drawObj.TryGetValue(key, out JToken? count)
                || count.Type != JTokenType.Integer
                || count.Value<long>() is < 1 or > 1048576
            )
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    $"{at}/draw/{key}",
                    $"{key} must be an integer from 1 to 1048576."
                );
        }
        return false;
    }

    private static void Camera(JToken token, string at, List<EncoderRule> rules)
    {
        if (token is not JObject camera)
        {
            Add(rules, VisualizerRuleId.SchemaInvalid, at, "camera must be an object.");
            return;
        }
        ClosedKeys(camera, at, ["eye", "center", "up", "fov", "near", "far"], rules);
        foreach (string key in new[] { "eye", "center", "up" })
        {
            if (camera[key] is not JArray vec || vec.Count != 3 || vec.Any(v => !IsNumber(v)))
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    $"{at}/{key}",
                    $"{key} must be three numbers."
                );
        }
        if (!IsNumber(camera["fov"]) || camera["fov"]!.Value<double>() is <= 0 or >= 180)
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                at + "/fov",
                "fov must be above 0 and below 180."
            );
        foreach (string key in new[] { "near", "far" })
        {
            if (!IsNumber(camera[key]) || camera[key]!.Value<double>() <= 0)
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    $"{at}/{key}",
                    $"{key} must be above 0."
                );
        }
    }

    private static void ValidateMilkdrop(JObject obj, List<EncoderRule> rules)
    {
        const string root = "/payload";
        ClosedKeys(obj, root, ["schema", "format", "preset", "textures", "milk"], rules);
        Text(obj, "milk", root, MaxText, false, rules);

        if (Array(obj, "textures", root, 0, 8, rules) is { } textures)
        {
            List<string> seen = [];
            for (int i = 0; i < textures.Count; i++)
            {
                string? name =
                    textures[i].Type == JTokenType.String ? textures[i].Value<string>() : null;
                if (name is null || !IsTextureName(name) || seen.Contains(name))
                    Add(
                        rules,
                        VisualizerRuleId.SchemaInvalid,
                        $"{root}/textures/{i}",
                        "A texture is a unique name of a-z, 0-9 and underscore, up to 64 characters."
                    );
                else
                    seen.Add(name);
            }
        }

        if (obj["preset"] is not JObject preset)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                root + "/preset",
                "preset must be an object."
            );
            return;
        }
        string at = root + "/preset";
        NoCode(preset, at, PresetKeys, rules);
        foreach (string key in PresetKeys)
            if (!preset.ContainsKey(key))
                Add(rules, VisualizerRuleId.SchemaInvalid, $"{at}/{key}", $"{key} is required.");

        if (
            preset.TryGetValue("version", out JToken? version)
            && (version.Type != JTokenType.Integer || version.Value<long>() is < 1 or > 2)
        )
            Add(rules, VisualizerRuleId.SchemaInvalid, at + "/version", "version must be 1 or 2.");
        BaseVals(preset, at, rules);
        foreach (
            string key in new[] { "init_eqs_eel", "frame_eqs_eel", "pixel_eqs_eel", "warp", "comp" }
        )
            if (preset.ContainsKey(key))
                Text(preset, key, at, MaxEel, true, rules);

        foreach (string list in new[] { "shapes", "waves" })
        {
            if (preset.ContainsKey(list) && Array(preset, list, at, 4, 4, rules) is { } slots)
            {
                for (int i = 0; i < slots.Count; i++)
                    Slot(slots[i], $"{at}/{list}/{i}", rules);
            }
        }
    }

    private static void Slot(JToken token, string at, List<EncoderRule> rules)
    {
        if (token is not JObject slot)
        {
            Add(rules, VisualizerRuleId.SchemaInvalid, at, "A slot must be an object.");
            return;
        }
        NoCode(slot, at, SlotKeys, rules);
        if (!slot.ContainsKey("baseVals"))
            Add(rules, VisualizerRuleId.SchemaInvalid, at + "/baseVals", "baseVals is required.");
        BaseVals(slot, at, rules);
        foreach (string key in new[] { "init_eqs_eel", "frame_eqs_eel", "point_eqs_eel" })
            if (slot.ContainsKey(key))
                Text(slot, key, at, MaxEel, true, rules);
    }

    private static void BaseVals(JObject owner, string at, List<EncoderRule> rules)
    {
        if (!owner.TryGetValue("baseVals", out JToken? token))
            return;
        if (token is not JObject vals)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                at + "/baseVals",
                "baseVals must be an object."
            );
            return;
        }
        if (vals.Count > 128)
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                at + "/baseVals",
                "baseVals holds at most 128 keys."
            );
        foreach (JProperty value in vals.Properties())
        {
            if (!IsIdentifierKey(value.Name))
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    $"{at}/baseVals/{Escape(value.Name)}",
                    "A baseVals key is 1 to 64 letters, digits or underscores."
                );
            else if (!IsNumber(value.Value) && value.Value.Type != JTokenType.Null)
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    $"{at}/baseVals/{value.Name}",
                    "A baseVals value is a number or null."
                );
        }
    }

    /// <summary>useJS and any non-empty *_eqs_str are code; other unknown keys are schema errors.</summary>
    private static void NoCode(JObject obj, string at, string[] allowed, List<EncoderRule> rules)
    {
        foreach (JProperty property in obj.Properties())
        {
            string field = $"{at}/{Escape(property.Name)}";
            if (property.Name == "useJS")
                Add(
                    rules,
                    VisualizerRuleId.PayloadHasCode,
                    field,
                    "useJS is not allowed: a payload carries no JavaScript."
                );
            else if (property.Name.EndsWith("_eqs_str", StringComparison.Ordinal))
            {
                if (
                    property.Value.Type != JTokenType.String
                    || property.Value.Value<string>() != ""
                )
                    Add(
                        rules,
                        VisualizerRuleId.PayloadHasCode,
                        field,
                        "A *_eqs_str key must be the empty string: a payload carries no JavaScript."
                    );
            }
            else if (!allowed.Contains(property.Name))
                Add(rules, VisualizerRuleId.SchemaInvalid, field, "Unknown key.");
        }
    }

    private static void ClosedKeys(
        JObject obj,
        string at,
        string[] allowed,
        List<EncoderRule> rules
    )
    {
        foreach (JProperty property in obj.Properties())
            if (!allowed.Contains(property.Name))
                Add(
                    rules,
                    VisualizerRuleId.SchemaInvalid,
                    $"{at}/{Escape(property.Name)}",
                    "Unknown key."
                );
    }

    private static JArray? Array(
        JObject obj,
        string key,
        string at,
        int min,
        int max,
        List<EncoderRule> rules
    )
    {
        if (obj[key] is JArray array && array.Count >= min && array.Count <= max)
            return array;
        Add(
            rules,
            VisualizerRuleId.SchemaInvalid,
            $"{at}/{key}",
            $"{key} must be an array of {min} to {max} items."
        );
        return null;
    }

    private static string? Text(
        JObject obj,
        string key,
        string at,
        int max,
        bool required,
        List<EncoderRule> rules
    )
    {
        if (!obj.TryGetValue(key, out JToken? token))
        {
            if (required)
                Add(rules, VisualizerRuleId.SchemaInvalid, $"{at}/{key}", $"{key} is required.");
            return null;
        }
        if (token.Type != JTokenType.String || token.Value<string>()!.Length > max)
        {
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                $"{at}/{key}",
                $"{key} must be a string of up to {max} characters."
            );
            return null;
        }
        return token.Value<string>();
    }

    private static string? Enum(
        JObject obj,
        string key,
        string at,
        string[] values,
        bool required,
        List<EncoderRule> rules
    )
    {
        string? value =
            obj.TryGetValue(key, out JToken? token) && token.Type == JTokenType.String
                ? token.Value<string>()
                : null;
        if (value is not null && values.Contains(value))
            return value;
        if (required || obj.ContainsKey(key))
            Add(
                rules,
                VisualizerRuleId.SchemaInvalid,
                $"{at}/{key}",
                $"{key} must be one of {string.Join(", ", values)}."
            );
        return null;
    }

    private static bool IsNumber(JToken? token) =>
        token?.Type is JTokenType.Integer or JTokenType.Float;

    private static bool IsIdentifierKey(string name) =>
        name.Length is >= 1 and <= 64
        && name.All(c =>
            c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'
        );

    private static bool IsIdentifier(string name) =>
        name.Length is >= 1 and <= 64 && !char.IsAsciiDigit(name[0]) && IsIdentifierKey(name);

    private static bool IsTextureName(string name) =>
        name.Length is >= 1 and <= 64
        && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

    private static int Depth(JToken token)
    {
        int deepest = 0;
        foreach (JToken child in token.Children())
            deepest = Math.Max(
                deepest,
                child is JProperty property ? Depth(property.Value) : Depth(child)
            );
        return token is JContainer and not JProperty ? deepest + 1 : deepest;
    }

    private static string Escape(string name) => name.Replace("~", "~0").Replace("/", "~1");

    private static void Add(List<EncoderRule> rules, string id, string field, string message) =>
        rules.Add(new(id, EncoderRuleSeverity.Error, field, message, FixFor(id)));

    private static string FixFor(string id) =>
        id switch
        {
            VisualizerRuleId.PayloadHasCode =>
                "Remove the key, or set a *_eqs_str key to an empty string.",
            VisualizerRuleId.PassOrder =>
                "Give each pass a unique name and end with the image pass.",
            VisualizerRuleId.ChannelUnknownPass =>
                "Point the channel at a pass that exists, or at music.",
            VisualizerRuleId.FormatMismatch =>
                "Set payload.format to the same value as the entry format.",
            VisualizerRuleId.SchemaVersionUnsupported => "Send schema 1.",
            _ => "Match the payload schema v1 of the visualizer library contract.",
        };
}
