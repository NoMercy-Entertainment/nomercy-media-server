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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.NmSystem.NewtonSoftConverters;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// Pins <see cref="ParseNumbersAsInt32Converter"/>, which <see cref="JsonHelper.Settings"/>
/// applies to every <c>object</c>-typed slot: SignalR hub arguments and every
/// <c>FromJson</c> target. A scalar keeps its reader value (an integer as a long); a JSON
/// object or array loads whole as a <see cref="JToken"/>, so the reader stays aligned.
/// </summary>
[Trait("Category", "Unit")]
public class ParseNumbersAsInt32ConverterTests
{
    private sealed class Holder
    {
        [JsonProperty("data")]
        public object? Data { get; set; }

        [JsonProperty("after")]
        public string? After { get; set; }
    }

    private static object? ReadAsObject(string json) =>
        JsonConvert.DeserializeObject<object>(json, new ParseNumbersAsInt32Converter());

    [Theory]
    [InlineData("10", 10L)]
    [InlineData("-3", -3L)]
    [InlineData("9007199254740993", 9007199254740993L)]
    public void AnInteger_IsReadAsALong(string json, long expected)
    {
        object? value = ReadAsObject(json);

        value.Should().BeOfType<long>().Which.Should().Be(expected);
    }

    [Fact]
    public void AFloat_IsReadAsADouble()
    {
        ReadAsObject("1.5").Should().BeOfType<double>().Which.Should().Be(1.5);
    }

    [Fact]
    public void AString_IsReadAsAString()
    {
        ReadAsObject("\"10\"").Should().Be("10");
    }

    [Fact]
    public void ABoolean_IsReadAsABoolean()
    {
        ReadAsObject("true").Should().Be(true);
    }

    [Fact]
    public void Null_IsReadAsNull()
    {
        ReadAsObject("null").Should().BeNull();
    }

    [Fact]
    public void AnObject_IsLoadedWhole_AsAJObject()
    {
        object? value = ReadAsObject("""{"time":10,"nested":{"name":"a"},"list":[1,2]}""");

        JObject loaded = value.Should().BeOfType<JObject>().Subject;
        loaded["time"]!.Type.Should().Be(JTokenType.Integer);
        loaded["time"]!.Value<int>().Should().Be(10);
        loaded["nested"]!["name"]!.Value<string>().Should().Be("a");
        loaded["list"]!.Should().BeOfType<JArray>().Which.Should().HaveCount(2);
    }

    [Fact]
    public void AnEmptyObject_IsLoadedAsAnEmptyJObject()
    {
        ReadAsObject("{}").Should().BeOfType<JObject>().Which.Should().BeEmpty();
    }

    [Fact]
    public void AnArray_IsLoadedWhole_AsAJArray()
    {
        object? value = ReadAsObject("""[1,"two",{"three":3}]""");

        JArray loaded = value.Should().BeOfType<JArray>().Subject;
        loaded.Should().HaveCount(3);
        loaded[2]["three"]!.Value<int>().Should().Be(3);
    }

    [Fact]
    public void AnObjectInAnObjectTypedMember_LeavesTheReaderOnTheNextMember()
    {
        Holder? holder = """{"data":{"time":10},"after":"kept"}""".FromJson<Holder>();

        holder.Should().NotBeNull();
        holder!.Data.Should().BeOfType<JObject>().Which["time"]!.Value<int>().Should().Be(10);
        holder.After.Should().Be("kept");
    }

    [Fact]
    public void AnArrayInAnObjectTypedMember_LeavesTheReaderOnTheNextMember()
    {
        Holder? holder = """{"data":[{"a":1},{"b":2}],"after":"kept"}""".FromJson<Holder>();

        holder.Should().NotBeNull();
        holder!.Data.Should().BeOfType<JArray>().Which.Should().HaveCount(2);
        holder.After.Should().Be("kept");
    }

    [Fact]
    public void AScalarInAnObjectTypedMember_KeepsItsReaderValue()
    {
        Holder? holder = """{"data":10,"after":"kept"}""".FromJson<Holder>();

        holder!.Data.Should().BeOfType<long>().Which.Should().Be(10L);
        holder.After.Should().Be("kept");
    }

    [Fact]
    public void ObjectValuesOfADictionary_AreLoadedWhole()
    {
        Dictionary<string, object?>? values = """
            {"n":1,"s":"x","o":{"k":"v"},"a":[1]}
            """.FromJson<Dictionary<string, object?>>();

        values.Should().NotBeNull();
        values!["n"].Should().Be(1L);
        values["s"].Should().Be("x");
        values["o"].Should().BeOfType<JObject>().Which["k"]!.Value<string>().Should().Be("v");
        values["a"].Should().BeOfType<JArray>();
    }
}
