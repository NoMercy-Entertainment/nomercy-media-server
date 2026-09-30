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

using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// Reads a plugin assembly's metadata before it is mapped and lists what would
/// let it leave the SDK: code that is not IL, a server assembly, an escape-hatch
/// type or member, a pointer, an indirect call. Day one is a ban list; the BCL
/// allow-list is the follow-up.
/// </summary>
internal static class PluginCodeScanner
{
    private static readonly string[] BannedNamespaces =
    [
        "System.Reflection.",
        "System.Runtime.InteropServices.",
        "System.Runtime.Loader",
        "Microsoft.CSharp.RuntimeBinder",
    ];

    private static readonly HashSet<string> BannedTypes =
    [
        "System.AppDomain",
        "System.Activator",
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
    ];

    private static readonly HashSet<string> BannedMembers =
    [
        "System.Type::GetType",
        "System.Environment::Exit",
        "System.Environment::FailFast",
        "System.Delegate::DynamicInvoke",
        "System.Linq.Expressions.LambdaExpression::Compile",
        "System.Linq.Expressions.Expression`1::Compile",
    ];

    // Every member of Unsafe and MemoryMarshal is memory-unsafe. The C# compiler
    // still emits these for safe code (a collection expression into a span, a
    // call that binds to a params ReadOnlySpan overload), so only they pass,
    // and only their byref overloads: the pointer overloads are an author's.
    private static readonly HashSet<string> CompilerEmittedMembers =
    [
        "System.Runtime.CompilerServices.Unsafe::As",
        "System.Runtime.CompilerServices.Unsafe::AsRef",
        "System.Runtime.CompilerServices.Unsafe::Add",
        "System.Runtime.InteropServices.MemoryMarshal::CreateSpan",
        "System.Runtime.InteropServices.MemoryMarshal::CreateReadOnlySpan",
    ];

    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    internal static IReadOnlyList<string> Scan(string dllPath, bool entry)
    {
        using FileStream stream = File.OpenRead(dllPath);
        return Scan(stream, entry);
    }

    /// <summary>
    /// Findings for one assembly; empty means clean. A shared assembly beside
    /// the plugin is never loaded from the folder (the load context serves the
    /// host's copy), so it is skipped; the entry assembly never is.
    /// </summary>
    internal static IReadOnlyList<string> Scan(Stream stream, bool entry)
    {
        List<string> findings = [];
        try
        {
            using PEReader pe = new(stream, PEStreamOptions.LeaveOpen);
            CorHeader? cor = pe.HasMetadata ? pe.PEHeaders.CorHeader : null;
            if (cor is null || !cor.Flags.HasFlag(CorFlags.ILOnly))
                return ["not pure IL"];

            MetadataReader md = pe.GetMetadataReader();
            string assemblyName = md.GetString(md.GetAssemblyDefinition().Name);
            if (!entry && PluginHostOptions.DefaultSharedAssemblies.Contains(assemblyName))
                return findings;

            ScanTypeReferences(md, findings);
            ScanMemberReferences(md, findings);
            ScanDefinitions(pe, md, findings);
        }
        catch (BadImageFormatException)
        {
            findings.Add("not pure IL");
        }
        catch (Exception ex)
        {
            findings.Add($"unreadable: {ex.GetType().Name}");
        }

        return findings;
    }

    private static void ScanTypeReferences(MetadataReader md, List<string> findings)
    {
        foreach (TypeReferenceHandle handle in md.TypeReferences)
        {
            TypeReference type = md.GetTypeReference(handle);
            if (type.ResolutionScope.Kind == HandleKind.AssemblyReference)
            {
                AssemblyReferenceHandle scope = (AssemblyReferenceHandle)type.ResolutionScope;
                string assembly = md.GetString(md.GetAssemblyReference(scope).Name);
                if (
                    assembly.StartsWith("NoMercy.", StringComparison.Ordinal)
                    && !PluginHostOptions.DefaultSharedAssemblies.Contains(assembly)
                )
                    Add(findings, $"references server assembly {assembly}");
            }

            string ns = md.GetString(type.Namespace);
            string name = md.GetString(type.Name);
            if (IsBannedType(ns, name))
                Add(findings, $"references banned type {ns}.{name}");
        }
    }

    private static bool IsBannedType(string ns, string name) =>
        ns switch
        {
            // The compiler writes these attributes into every assembly; the
            // types that act (Assembly, MethodInfo, Marshal, GCHandle) are the ban.
            "System.Reflection" => !name.EndsWith("Attribute", StringComparison.Ordinal)
                && name != "AssemblyName",
            "System.Runtime.InteropServices" => !name.EndsWith(
                "Attribute",
                StringComparison.Ordinal
            )
                && name != "MemoryMarshal",
            _ => BannedTypes.Contains($"{ns}.{name}")
                || BannedNamespaces.Any(b => ns.StartsWith(b, StringComparison.Ordinal)),
        };

    private static void ScanMemberReferences(MetadataReader md, List<string> findings)
    {
        foreach (MemberReferenceHandle handle in md.MemberReferences)
        {
            MemberReference member = md.GetMemberReference(handle);
            string? parent = ParentTypeName(md, member.Parent);
            if (parent is null)
                continue;

            string key = $"{parent}::{md.GetString(member.Name)}";
            bool unsafeHelper =
                parent
                    is "System.Runtime.CompilerServices.Unsafe"
                        or "System.Runtime.InteropServices.MemoryMarshal";
            if (BannedMembers.Contains(key) || (unsafeHelper && !IsCompilerEmitted(member, key)))
                Add(findings, $"calls banned member {key}");
        }
    }

    // Unsafe.As<TFrom, TTo>(ref TFrom) is what the compiler emits; the
    // one-argument Unsafe.As<T>(object) is an unchecked cast an author wrote.
    private static bool IsCompilerEmitted(MemberReference member, string key)
    {
        if (!CompilerEmittedMembers.Contains(key) || member.GetKind() != MemberReferenceKind.Method)
            return false;

        MethodSignature<bool> signature = member.DecodeMethodSignature(PointerFinder.Instance, null);
        return !signature.ParameterTypes.Contains(true)
            && (
                key != "System.Runtime.CompilerServices.Unsafe::As"
                || signature.GenericParameterCount == 2
            );
    }

    private static string? ParentTypeName(MetadataReader md, EntityHandle parent)
    {
        if (parent.Kind == HandleKind.TypeSpecification)
        {
            TypeSpecification spec = md.GetTypeSpecification((TypeSpecificationHandle)parent);
            BlobReader blob = md.GetBlobReader(spec.Signature);
            if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                return null;
            blob.ReadSignatureTypeCode();
            parent = blob.ReadTypeHandle();
        }

        if (parent.Kind != HandleKind.TypeReference)
            return null;

        TypeReference type = md.GetTypeReference((TypeReferenceHandle)parent);
        return $"{md.GetString(type.Namespace)}.{md.GetString(type.Name)}";
    }

    private static void ScanDefinitions(PEReader pe, MetadataReader md, List<string> findings)
    {
        foreach (FieldDefinitionHandle handle in md.FieldDefinitions)
        {
            FieldDefinition field = md.GetFieldDefinition(handle);
            if (field.DecodeSignature(PointerFinder.Instance, null))
                Add(findings, $"Pointer field {Name(md, field.GetDeclaringType(), field.Name)}");
        }

        foreach (MethodDefinitionHandle handle in md.MethodDefinitions)
        {
            MethodDefinition method = md.GetMethodDefinition(handle);
            string name = Name(md, method.GetDeclaringType(), method.Name);
            // Delegates carry CodeType Runtime: the runtime supplies their
            // bodies, so only Native, OPTIL and Unmanaged mean foreign code.
            MethodImplAttributes codeType =
                method.ImplAttributes & MethodImplAttributes.CodeTypeMask;
            bool native =
                method.Attributes.HasFlag(MethodAttributes.PinvokeImpl)
                || method.ImplAttributes.HasFlag(MethodImplAttributes.Unmanaged)
                || codeType is MethodImplAttributes.Native or MethodImplAttributes.OPTIL;
            if (native)
                Add(findings, $"P/Invoke or native method {name}");

            MethodSignature<bool> signature = method.DecodeSignature(PointerFinder.Instance, null);
            if (signature.ReturnType || signature.ParameterTypes.Contains(true))
                Add(findings, $"Pointer in signature of {name}");

            if (method.RelativeVirtualAddress == 0)
                continue;

            MethodBodyBlock body = pe.GetMethodBody(method.RelativeVirtualAddress);
            if (
                !body.LocalSignature.IsNil
                && md.GetStandaloneSignature(body.LocalSignature)
                    .DecodeLocalSignature(PointerFinder.Instance, null)
                    .Contains(true)
            )
                Add(findings, $"Pointer local in {name}");

            if (UsesCalli(body.GetILReader()))
                Add(findings, $"calli in {name}");
        }
    }

    private static bool UsesCalli(BlobReader il)
    {
        while (il.RemainingBytes > 0)
        {
            short value = il.ReadByte();
            if (value == 0xFE)
                value = (short)(0xFE00 | il.ReadByte());

            OpCode op = OpCodesByValue[value];
            if (op == OpCodes.Calli)
                return true;

            int operand = op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget
                or OperandType.ShortInlineI
                or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 * il.ReadInt32(),
                _ => 4,
            };
            il.Offset += operand;
        }

        return false;
    }

    private static string Name(MetadataReader md, TypeDefinitionHandle type, StringHandle member) =>
        $"{md.GetString(md.GetTypeDefinition(type).Name)}.{md.GetString(member)}";

    private static void Add(List<string> findings, string finding)
    {
        if (!findings.Contains(finding))
            findings.Add(finding);
    }

    /// <summary>True when a signature holds a pointer or function pointer anywhere.</summary>
    private sealed class PointerFinder : ISignatureTypeProvider<bool, object?>
    {
        public static readonly PointerFinder Instance = new();

        public bool GetPointerType(bool elementType) => true;

        public bool GetFunctionPointerType(MethodSignature<bool> signature) => true;

        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;

        public bool GetTypeFromDefinition(
            MetadataReader reader,
            TypeDefinitionHandle handle,
            byte rawTypeKind
        ) => false;

        public bool GetTypeFromReference(
            MetadataReader reader,
            TypeReferenceHandle handle,
            byte rawTypeKind
        ) => false;

        public bool GetTypeFromSpecification(
            MetadataReader reader,
            object? genericContext,
            TypeSpecificationHandle handle,
            byte rawTypeKind
        ) => false;

        public bool GetSZArrayType(bool elementType) => elementType;

        public bool GetArrayType(bool elementType, ArrayShape shape) => elementType;

        public bool GetByReferenceType(bool elementType) => elementType;

        public bool GetPinnedType(bool elementType) => elementType;

        public bool GetGenericInstantiation(bool genericType, ImmutableArray<bool> typeArguments) =>
            genericType || typeArguments.Contains(true);

        public bool GetGenericMethodParameter(object? genericContext, int index) => false;

        public bool GetGenericTypeParameter(object? genericContext, int index) => false;

        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) =>
            unmodifiedType;
    }
}
