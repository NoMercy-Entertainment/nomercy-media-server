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
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Verification;
using Xunit;

namespace NoMercy.Tests.Plugins;

/// <summary>
/// What a real plugin build holds that is not an escape hatch: a reference to
/// the plugin's own second assembly (Automix ships NoMercy.Plugin.Automix and
/// NoMercy.Plugin.Automix.Analysis), and <c>Type.Name</c>, which C# binds to
/// <c>MemberInfo.get_Name</c> (<c>ex.GetType().Name</c> in a log line). Each
/// fixture is a metadata-only image built here, so a test plants exactly one
/// reference; the guards beside them keep the real escapes refused.
/// </summary>
public class PluginCodeScannerOwnCodeTests
{
    private const string Entry = "NoMercy.Plugin.Fixture";

    [Fact]
    public void APluginsOwnAssemblyIsNotAServerAssembly()
    {
        byte[] image = Image(md =>
            TypeIn(md, Assembly(md, $"{Entry}.Analysis"), "Fixture", "Parts")
        );

        Scan(image).Should().BeEmpty();
    }

    [Theory]
    [InlineData("NoMercy.Database")]
    [InlineData("NoMercy.Plugins")]
    [InlineData("NoMercy.PluginHost")]
    [InlineData("NoMercy.PluginSdk.Ipc")]
    public void AServerAssemblyIsStillNamed(string server)
    {
        byte[] image = Image(md => TypeIn(md, Assembly(md, server), "NoMercy", "Thing"));

        Scan(image)
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be($"references server assembly {server}");
    }

    [Fact]
    public void TypeNameIsNotAnEscape()
    {
        byte[] image = Image(md =>
        {
            TypeReferenceHandle memberInfo = TypeIn(
                md,
                Runtime(md),
                "System.Reflection",
                "MemberInfo"
            );
            Member(md, memberInfo, "get_Name");
        });

        Scan(image).Should().BeEmpty();
    }

    [Theory]
    [InlineData("GetCustomAttributes")]
    [InlineData("get_Module")]
    public void AnyOtherMemberInfoMemberIsStillBanned(string member)
    {
        byte[] image = Image(md =>
        {
            TypeReferenceHandle memberInfo = TypeIn(
                md,
                Runtime(md),
                "System.Reflection",
                "MemberInfo"
            );
            Member(md, memberInfo, "get_Name");
            Member(md, memberInfo, member);
        });

        Scan(image).Should().Contain("references banned type System.Reflection.MemberInfo");
    }

    [Fact]
    public void AMemberInfoWithNoNameReadIsStillBanned()
    {
        byte[] image = Image(md => TypeIn(md, Runtime(md), "System.Reflection", "MemberInfo"));

        Scan(image).Should().Contain("references banned type System.Reflection.MemberInfo");
    }

    [Fact]
    public void NoServerProjectCarriesThePluginPrefix()
    {
        // The prefix marks a plugin's own code only while no server assembly
        // has it. A server project named NoMercy.Plugin.* would be loadable
        // from a plugin folder under the server's name.
        string src = Path.Combine(RepoRoot(), "src");

        IEnumerable<string> projects = Directory
            .EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)!;

        projects
            .Should()
            .NotContain(name =>
                name!.StartsWith(
                    PluginHostOptions.PluginAssemblyPrefix,
                    StringComparison.OrdinalIgnoreCase
                )
            );
    }

    [Fact]
    public void MethodInfoIsStillBanned()
    {
        byte[] image = Image(md =>
        {
            TypeReferenceHandle methodInfo = TypeIn(
                md,
                Runtime(md),
                "System.Reflection",
                "MethodInfo"
            );
            Member(md, methodInfo, "get_Name");
        });

        Scan(image).Should().Contain("references banned type System.Reflection.MethodInfo");
    }

    private static string RepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "NoMercy.Plugins")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("repository root not found");
    }

    private static IReadOnlyList<string> Scan(byte[] image)
    {
        using MemoryStream stream = new(image);
        return PluginCodeScanner.Scan(stream, $"{Entry}.dll", entry: true);
    }

    private static byte[] Image(Action<MetadataBuilder> plant)
    {
        MetadataBuilder md = new();
        md.AddModule(
            0,
            md.GetOrAddString($"{Entry}.dll"),
            md.GetOrAddGuid(Guid.NewGuid()),
            default,
            default
        );
        md.AddAssembly(
            md.GetOrAddString(Entry),
            new Version(1, 0, 0, 0),
            default,
            default,
            0,
            AssemblyHashAlgorithm.Sha1
        );
        md.AddTypeDefinition(
            default,
            default,
            md.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1)
        );
        plant(md);

        BlobBuilder pe = new();
        new ManagedPEBuilder(
            new PEHeaderBuilder(
                imageCharacteristics: Characteristics.Dll | Characteristics.ExecutableImage
            ),
            new MetadataRootBuilder(md),
            new BlobBuilder()
        ).Serialize(pe);
        return pe.ToArray();
    }

    private static AssemblyReferenceHandle Runtime(MetadataBuilder md) =>
        Assembly(md, "System.Runtime");

    private static AssemblyReferenceHandle Assembly(MetadataBuilder md, string name) =>
        md.AddAssemblyReference(
            md.GetOrAddString(name),
            new Version(1, 0, 0, 0),
            default,
            default,
            0,
            default
        );

    private static TypeReferenceHandle TypeIn(
        MetadataBuilder md,
        AssemblyReferenceHandle scope,
        string ns,
        string name
    ) => md.AddTypeReference(scope, md.GetOrAddString(ns), md.GetOrAddString(name));

    private static void Member(MetadataBuilder md, TypeReferenceHandle parent, string name)
    {
        BlobBuilder signature = new();
        new BlobEncoder(signature)
            .MethodSignature(isInstanceMethod: true)
            .Parameters(0, returnType => returnType.Type().String(), _ => { });
        md.AddMemberReference(parent, md.GetOrAddString(name), md.GetOrAddBlob(signature));
    }
}
