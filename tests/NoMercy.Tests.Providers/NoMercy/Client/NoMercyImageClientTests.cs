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
using NoMercy.Providers.NoMercy.Client;

namespace NoMercy.Tests.Providers.NoMercy.Client;

/// <summary>
/// PROV-H16: Tests verifying that NoMercyImageClient.Download reads the response
/// content exactly once. The bug: ReadAsByteArrayAsync consumed the content for
/// file writing, then ReadAsStreamAsync re-read the already-consumed response
/// content — producing corrupt/empty images.
/// The fix: Read content once as byte[] and use it for both file writing and image loading.
/// </summary>
[Trait("Category", "Unit")]
public class NoMercyImageClientTests
{
    private static MethodInfo GetDownloadMethod()
    {
        MethodInfo? method = typeof(NoMercyImageClient).GetMethod(
            "Download",
            BindingFlags.Public | BindingFlags.Static
        );

        Assert.NotNull(method);
        return method;
    }

    /// <summary>
    /// The local async function Task() inside Download is compiled into a
    /// compiler-generated state machine nested type. Find it by looking for
    /// nested types with AsyncStateMachineAttribute on their MoveNext.
    /// </summary>
    private static (Type StateMachineType, MethodInfo MoveNext) GetLocalFunctionStateMachine()
    {
        // The compiler generates a display class (e.g., <>c__DisplayClass0_0)
        // containing the local function, which itself has a state machine.
        // We search all nested types (including nested-of-nested) for one
        // that has MoveNext and IAsyncStateMachine.
        Type[] allNested = typeof(NoMercyImageClient).GetNestedTypes(
            BindingFlags.NonPublic | BindingFlags.Public
        );

        foreach (Type nested in allNested)
        {
            // Check nested types within the display class
            Type[] deepNested = nested.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public);

            foreach (Type deep in deepNested)
            {
                if (typeof(IAsyncStateMachine).IsAssignableFrom(deep))
                {
                    MethodInfo? moveNext = deep.GetMethod(
                        "MoveNext",
                        BindingFlags.NonPublic | BindingFlags.Instance
                    );

                    if (moveNext != null)
                        return (deep, moveNext);
                }
            }

            // Also check the nested type itself
            if (typeof(IAsyncStateMachine).IsAssignableFrom(nested))
            {
                MethodInfo? moveNext = nested.GetMethod(
                    "MoveNext",
                    BindingFlags.NonPublic | BindingFlags.Instance
                );

                if (moveNext != null)
                    return (nested, moveNext);
            }
        }

        throw new InvalidOperationException(
            "Could not find async state machine for the local Task() function in NoMercyImageClient.Download"
        );
    }

    private static List<string> GetCalledMethodNames(MethodInfo moveNext)
    {
        byte[] ilBytes = moveNext.GetMethodBody()!.GetILAsByteArray()!;
        Module module = moveNext.DeclaringType!.Module;
        List<string> names = [];

        for (int i = 0; i < ilBytes.Length; i++)
        {
            if ((ilBytes[i] == 0x28 || ilBytes[i] == 0x6F) && i + 4 < ilBytes.Length)
            {
                int token = BitConverter.ToInt32(ilBytes, i + 1);
                try
                {
                    MethodBase? calledMethod = module.ResolveMethod(token);
                    if (calledMethod != null)
                        names.Add(calledMethod.Name);
                }
                catch (Exception)
                {
                    // Token may not resolve — skip
                }
            }
        }

        return names;
    }

    [Fact]
    public void Download_IsStaticAndReturnsTask()
    {
        MethodInfo method = GetDownloadMethod();

        Assert.True(method.IsStatic, "Download should be a static method");
        Assert.True(method.ReturnType.IsGenericType);
        Assert.Equal(typeof(Task<>), method.ReturnType.GetGenericTypeDefinition());
    }

    [Fact]
    public void Download_AcceptsStringPathAndOptionalBoolParameters()
    {
        MethodInfo method = GetDownloadMethod();
        ParameterInfo[] parameters = method.GetParameters();

        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
        Assert.Equal("path", parameters[0].Name);
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
    public void Download_LocalFunction_DoesNotCallReadAsStreamAsync()
    {
        (Type _, MethodInfo moveNext) = GetLocalFunctionStateMachine();
        List<string> calledMethods = GetCalledMethodNames(moveNext);

        Assert.DoesNotContain("ReadAsStreamAsync", calledMethods);
    }

    [Fact]
    public void Download_LocalFunction_CallsReadAsByteArrayAsync()
    {
        (Type _, MethodInfo moveNext) = GetLocalFunctionStateMachine();
        List<string> calledMethods = GetCalledMethodNames(moveNext);

        Assert.Contains("ReadAsByteArrayAsync", calledMethods);
    }

    [Fact]
    public void Download_LocalFunction_CallsReadAsByteArrayAsyncExactlyOnce()
    {
        (Type _, MethodInfo moveNext) = GetLocalFunctionStateMachine();
        List<string> calledMethods = GetCalledMethodNames(moveNext);

        int count = calledMethods.Count(n => n == "ReadAsByteArrayAsync");
        Assert.Equal(1, count);
    }

    [Fact]
    public void Download_LocalFunction_DoesNotCallContentReadMultipleTimes()
    {
        (Type _, MethodInfo moveNext) = GetLocalFunctionStateMachine();
        List<string> calledMethods = GetCalledMethodNames(moveNext);

        int contentReadCalls = calledMethods.Count(n =>
            n is "ReadAsByteArrayAsync" or "ReadAsStreamAsync" or "ReadAsStringAsync"
        );

        Assert.Equal(1, contentReadCalls);
    }

    [Fact]
    public void Download_LocalFunction_ImageLoadUsesByteArrayOverload()
    {
        (Type stateMachineType, MethodInfo moveNext) = GetLocalFunctionStateMachine();

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
            "PROV-H16: Image.Load should use the byte[] overload, not Stream, "
                + "to avoid consuming a stream that might be reused."
        );
    }

    [Fact]
    public void Download_LocalFunction_HasAsyncStateMachine()
    {
        // Verify we can actually find the state machine — this is a prerequisite
        // for all other IL-based tests. If this fails, the compiler changed how
        // it generates local async functions.
        (Type stateMachineType, MethodInfo moveNext) = GetLocalFunctionStateMachine();

        Assert.NotNull(stateMachineType);
        Assert.NotNull(moveNext);
        Assert.True(typeof(IAsyncStateMachine).IsAssignableFrom(stateMachineType));
    }
}
