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
using System.Runtime.CompilerServices;
using ImageMagick;
using NoMercy.Providers.CoverArt.Client;

namespace NoMercy.Tests.Providers.CoverArt.Client;

/// <summary>
/// PROV-H12: Tests verifying that CoverArtCoverArtClient.Download reads the response
/// content exactly once. The bug: ReadAsStreamAsync consumed the stream, then
/// ReadAsByteArrayAsync re-read the (exhausted) response content, and
/// Image.Load was called on the already-consumed stream — producing corrupt images.
/// The fix: Read content once as byte[] and use it for both file writing and image loading.
/// </summary>
[Trait("Category", "Unit")]
public class CoverArtCoverArtClientTests
{
    private static MethodInfo GetDownloadMethod()
    {
        MethodInfo? method = typeof(CoverArtCoverArtClient).GetMethod(
            "Download",
            BindingFlags.Public | BindingFlags.Static
        );

        Assert.NotNull(method);
        return method;
    }

    private static Type GetStateMachineType(MethodInfo method)
    {
        AsyncStateMachineAttribute? attr = method.GetCustomAttribute<AsyncStateMachineAttribute>();

        Assert.NotNull(attr);
        return attr.StateMachineType;
    }

    [Fact]
    public void Download_IsStaticAsync()
    {
        MethodInfo method = GetDownloadMethod();

        Assert.True(method.IsStatic, "Download should be a static method");

        AsyncStateMachineAttribute? attr = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        Assert.NotNull(attr);
    }

    [Fact]
    public void Download_ReturnsTaskOfNullableImage()
    {
        MethodInfo method = GetDownloadMethod();

        Assert.True(method.ReturnType.IsGenericType);
        Assert.Equal(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
    }

    [Fact]
    public void Download_AcceptsNullableUriAndOptionalBoolParameters()
    {
        MethodInfo method = GetDownloadMethod();
        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(Uri), parameters[0].ParameterType);
        Assert.Equal("url", parameters[0].Name);
        Assert.Equal(typeof(bool?), parameters[1].ParameterType);
        Assert.Equal("download", parameters[1].Name);
        Assert.True(parameters[1].HasDefaultValue);
        Assert.Equal(true, parameters[1].DefaultValue);
        Assert.Equal("maxDecodeSize", parameters[2].Name);
        Assert.Equal("Size", Nullable.GetUnderlyingType(parameters[2].ParameterType)?.Name);
        Assert.True(parameters[2].HasDefaultValue);
        Assert.Null(parameters[2].DefaultValue);
    }

    [Fact]
    public void Download_StateMachine_DoesNotCallReadAsStreamAsync()
    {
        MethodInfo method = GetDownloadMethod();
        Type stateMachineType = GetStateMachineType(method);

        MethodInfo moveNext = stateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;

        Assert.NotNull(moveNext);

        byte[] ilBytes = moveNext.GetMethodBody()!.GetILAsByteArray()!;
        Module module = stateMachineType.Module;

        bool callsReadAsStream = false;

        for (int i = 0; i < ilBytes.Length; i++)
        {
            if ((ilBytes[i] == 0x28 || ilBytes[i] == 0x6F) && i + 4 < ilBytes.Length)
            {
                int token = BitConverter.ToInt32(ilBytes, i + 1);
                try
                {
                    MethodBase? calledMethod = module.ResolveMethod(token);
                    if (calledMethod?.Name == "ReadAsStreamAsync")
                    {
                        callsReadAsStream = true;
                        break;
                    }
                }
                catch (Exception)
                {
                    // Token may not resolve — skip
                }
            }
        }

        Assert.False(
            callsReadAsStream,
            "PROV-H12 regression: Download should NOT call ReadAsStreamAsync. "
                + "Content must be read once as byte[] to avoid stream-consumed-then-reused bug."
        );
    }

    [Fact]
    public void Download_StateMachine_CallsReadAsByteArrayAsync()
    {
        MethodInfo method = GetDownloadMethod();
        Type stateMachineType = GetStateMachineType(method);

        MethodInfo moveNext = stateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;

        Assert.NotNull(moveNext);

        byte[] ilBytes = moveNext.GetMethodBody()!.GetILAsByteArray()!;
        Module module = stateMachineType.Module;

        bool callsReadAsByteArray = false;

        for (int i = 0; i < ilBytes.Length; i++)
        {
            if ((ilBytes[i] == 0x28 || ilBytes[i] == 0x6F) && i + 4 < ilBytes.Length)
            {
                int token = BitConverter.ToInt32(ilBytes, i + 1);
                try
                {
                    MethodBase? calledMethod = module.ResolveMethod(token);
                    if (calledMethod?.Name == "ReadAsByteArrayAsync")
                    {
                        callsReadAsByteArray = true;
                        break;
                    }
                }
                catch (Exception)
                {
                    // Token may not resolve — skip
                }
            }
        }

        Assert.True(
            callsReadAsByteArray,
            "PROV-H12: Download must call ReadAsByteArrayAsync to buffer the content once."
        );
    }

    [Fact]
    public void Download_StateMachine_CallsReadAsByteArrayAsyncExactlyOnce()
    {
        MethodInfo method = GetDownloadMethod();
        Type stateMachineType = GetStateMachineType(method);

        MethodInfo moveNext = stateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;

        byte[] ilBytes = moveNext.GetMethodBody()!.GetILAsByteArray()!;
        Module module = stateMachineType.Module;

        int readByteArrayCount = 0;

        for (int i = 0; i < ilBytes.Length; i++)
        {
            if ((ilBytes[i] == 0x28 || ilBytes[i] == 0x6F) && i + 4 < ilBytes.Length)
            {
                int token = BitConverter.ToInt32(ilBytes, i + 1);
                try
                {
                    MethodBase? calledMethod = module.ResolveMethod(token);
                    if (calledMethod?.Name == "ReadAsByteArrayAsync")
                        readByteArrayCount++;
                }
                catch (Exception)
                {
                    // Token may not resolve — skip
                }
            }
        }

        Assert.Equal(1, readByteArrayCount);
    }

    [Fact]
    public void Download_StateMachine_DoesNotCallContentReadMultipleTimes()
    {
        MethodInfo method = GetDownloadMethod();
        Type stateMachineType = GetStateMachineType(method);

        MethodInfo moveNext = stateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;

        byte[] ilBytes = moveNext.GetMethodBody()!.GetILAsByteArray()!;
        Module module = stateMachineType.Module;

        int contentReadCalls = 0;

        for (int i = 0; i < ilBytes.Length; i++)
        {
            if ((ilBytes[i] == 0x28 || ilBytes[i] == 0x6F) && i + 4 < ilBytes.Length)
            {
                int token = BitConverter.ToInt32(ilBytes, i + 1);
                try
                {
                    MethodBase? calledMethod = module.ResolveMethod(token);
                    if (
                        calledMethod?.Name
                        is "ReadAsByteArrayAsync"
                            or "ReadAsStreamAsync"
                            or "ReadAsStringAsync"
                    )
                    {
                        contentReadCalls++;
                    }
                }
                catch (Exception)
                {
                    // Token may not resolve — skip
                }
            }
        }

        Assert.Equal(1, contentReadCalls);
    }

    [Fact]
    public void Download_StateMachine_ImageLoadUsesByteArrayOverload()
    {
        MethodInfo method = GetDownloadMethod();
        Type stateMachineType = GetStateMachineType(method);

        MethodInfo moveNext = stateMachineType.GetMethod(
            "MoveNext",
            BindingFlags.NonPublic | BindingFlags.Instance
        )!;

        byte[] ilBytes = moveNext.GetMethodBody()!.GetILAsByteArray()!;
        Module module = stateMachineType.Module;

        bool hasImageLoadWithByteArray = false;

        for (int i = 0; i < ilBytes.Length; i++)
        {
            if (
                (ilBytes[i] == 0x28 || ilBytes[i] == 0x6F || ilBytes[i] == 0x73)
                && i + 4 < ilBytes.Length
            )
            {
                int token = BitConverter.ToInt32(ilBytes, i + 1);
                try
                {
                    MethodBase? calledMethod = module.ResolveMethod(token);
                    bool isImageLoad =
                        calledMethod?.Name == "Load"
                        || (
                            calledMethod?.IsConstructor == true
                            && calledMethod.DeclaringType == typeof(MagickImage)
                        );
                    if (isImageLoad && calledMethod!.GetParameters().Length > 0)
                    {
                        ParameterInfo firstParam = calledMethod.GetParameters()[0];
                        if (
                            firstParam.ParameterType == typeof(byte[])
                            || firstParam.ParameterType == typeof(ReadOnlySpan<byte>)
                        )
                        {
                            hasImageLoadWithByteArray = true;
                        }
                    }
                }
                catch (Exception)
                {
                    // Token may not resolve — skip
                }
            }
        }

        Assert.True(
            hasImageLoadWithByteArray,
            "PROV-H12: Image.Load should use the byte[] overload, not Stream, "
                + "to avoid consuming a stream that might be reused."
        );
    }
}
