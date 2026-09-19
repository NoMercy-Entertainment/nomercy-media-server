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
using System.Text.Json.Serialization;
using Newtonsoft.Json.Serialization;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Gives the plugin contract one set of key names on the wire.
/// <para>
/// The contract types are read from a manifest by System.Text.Json and written
/// to a client by Newtonsoft, and Newtonsoft cannot see a
/// <see cref="JsonPropertyNameAttribute" />. Without this a view ships
/// <c>Components</c> where every client reads <c>components</c>, the renderer
/// finds nothing under the key it knows, and the page draws blank with no error
/// anywhere.
/// </para>
/// <para>
/// Every contract property is lower camel case, which is what the web and
/// Android hosts both read, and an explicit attribute still wins where a name
/// is not simply the property in another case.
/// </para>
/// <para>
/// Scoped to the contract assembly on purpose. Every other response keeps the
/// names it has always had, so no client that works today stops working.
/// </para>
/// </summary>
public class PluginContractResolver : DefaultContractResolver
{
    private static readonly Assembly ContractAssembly = typeof(PluginView).Assembly;
    private static readonly CamelCaseNamingStrategy CamelCase = new();

    protected override JsonProperty CreateProperty(
        MemberInfo member,
        Newtonsoft.Json.MemberSerialization memberSerialization
    )
    {
        JsonProperty property = base.CreateProperty(member, memberSerialization);

        if (member.DeclaringType?.Assembly != ContractAssembly)
            return property;

        JsonPropertyNameAttribute? name = member.GetCustomAttribute<JsonPropertyNameAttribute>();
        property.PropertyName = name is not null
            ? name.Name
            : CamelCase.GetPropertyName(member.Name, false);

        return property;
    }
}
