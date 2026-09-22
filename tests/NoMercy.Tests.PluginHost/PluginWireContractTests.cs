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
using System.Runtime.Serialization;
using FluentAssertions;
using NoMercy.PluginSdk.Ipc;
using ProtoBuf.Meta;
using Xunit;

namespace NoMercy.Tests.PluginHost;

/// <summary>
/// Every message the channel carries, built the way the serializer builds it.
/// <para>
/// protobuf-net creates the instance before it has any values, so a positional
/// record with no parameterless constructor fails at the far end with a
/// ProtoException naming the type. Six of the seven were shaped that way, and
/// nothing caught it because no test had ever put one on a real wire.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class PluginWireContractTests
{
    public static TheoryData<Type> WireTypes
    {
        get
        {
            TheoryData<Type> data = [];

            foreach (
                Type type in typeof(PluginCallRequest)
                    .Assembly.GetTypes()
                    .Where(one => one.GetCustomAttribute<DataContractAttribute>() is not null)
                    .OrderBy(one => one.Name)
            )
            {
                data.Add(type);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(WireTypes))]
    public void EveryWireMessageCanBeBuiltByTheSerializer(Type type)
    {
        ConstructorInfo? parameterless = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            Type.EmptyTypes,
            modifiers: null
        );

        parameterless
            .Should()
            .NotBeNull(
                $"{type.Name} crosses the channel, and protobuf-net builds it before it has any "
                    + "values. Without a parameterless constructor every call carrying one fails "
                    + "at the far end with a ProtoException naming the type."
            );
    }

    /// <summary>
    /// The model is what actually serializes. A type the attributes describe
    /// but the model cannot build fails only on a real call, in a process
    /// whose log the author never sees.
    /// </summary>
    [Theory]
    [MemberData(nameof(WireTypes))]
    public void EveryWireMessageRoundTripsThroughTheModel(Type type)
    {
        RuntimeTypeModel model = RuntimeTypeModel.Create();
        model.Add(type, applyDefaultBehaviour: true);

        object? built = model[type]
            .Type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                Type.EmptyTypes,
                modifiers: null
            )
            ?.Invoke([]);

        built
            .Should()
            .NotBeNull($"{type.Name} cannot be created the way the serializer creates it");

        using MemoryStream buffer = new();

        model.Serialize(buffer, built!);
        buffer.Position = 0;

        object back = model.Deserialize(buffer, null, type);

        back.Should().NotBeNull($"{type.Name} did not survive a round trip");
    }
}
