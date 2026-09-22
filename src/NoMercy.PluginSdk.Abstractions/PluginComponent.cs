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

using System.Runtime.Serialization;
using Newtonsoft.Json.Linq;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// One node of a declarative view.
/// <para>
/// The node stays generic — a tag, a bag of props, children, an optional action
/// — so a client renders any view with one recursive walk and a lookup table.
/// The types live in <see cref="PluginComponentType"/> and the shape of each
/// tag's props is what <see cref="PluginViews"/> exists to get right.
/// </para>
/// </summary>
public class PluginComponent
{
    private readonly Dictionary<string, object?> _props = new();

    public required string Id { get; init; }

    public required string Component { get; init; }

    /// <summary>
    /// The bag every client renders from, children and action included.
    /// <para>
    /// The design system's contract keeps both inside a component's props,
    /// because both belong to every component rather than to a container kind —
    /// <c>NmComponentBase</c> is where they are declared. A second envelope
    /// shape would mean the components a plugin names cannot draw what it sends
    /// them, which is exactly what happened: every card arrived with an empty
    /// body.
    /// </para>
    /// <para>
    /// The merge happens on read rather than on write so an object initializer
    /// can name <see cref="Props"/>, <see cref="Items"/> and <see cref="Action"/>
    /// in any order. Written into the bag as they were set, whichever came last
    /// would erase the others.
    /// </para>
    /// </summary>
    public Dictionary<string, object?> Props
    {
        get
        {
            Dictionary<string, object?> wire = new(_props);

            if (Design is not null)
            {
                foreach (KeyValuePair<string, JToken?> option in JObject.FromObject(Design))
                {
                    wire[option.Key] = option.Value;
                }
            }

            if (Items.Count > 0)
                wire["items"] = Items;

            if (Action is not null)
                wire["action"] = Action;

            return wire;
        }
        init => _props = value;
    }

    /// <summary>
    /// Children, as an author writes them — readable so a plugin can keep
    /// building a card after constructing it.
    /// <para>
    /// <c>IgnoreDataMember</c> and not <c>System.Text.Json</c>'s
    /// <c>JsonIgnore</c>: every response in this server is written by
    /// Newtonsoft, which cannot see that attribute and dutifully sent this
    /// beside <see cref="Props"/> — a second copy of the children in a place no
    /// client reads. Newtonsoft is deliberately kept out of this assembly (see
    /// <see cref="PluginHubMessage"/>) because a plugin's own copy takes a
    /// distinct identity in its load context, so the attribute used here has to
    /// be one the shared framework already carries.
    /// </para>
    /// </summary>
    [IgnoreDataMember]
    public List<PluginComponent> Items { get; init; } = [];

    /// <inheritdoc cref="Items"/>
    [IgnoreDataMember]
    public PluginActionIntent? Action { get; init; }

    /// <summary>
    /// The design system's own props record for this component.
    /// <para>
    /// A plugin naming <c>NMAccordion</c> has an <c>NMAccordionProps</c> to fill
    /// in, and every option on it is a compiler-checked field rather than a
    /// string a typo turns into silence. Set this and its fields become the
    /// props; <see cref="Props"/> stays for the handful of keys the design
    /// system does not name, and children set through <see cref="Items"/> still
    /// win, so a component can be built either way without knowing which.
    /// </para>
    /// <para>
    /// Read through Newtonsoft rather than reflection because the record's own
    /// <c>[JsonProperty]</c> attributes are what decide the wire names, and
    /// those are generated from the same manifest the clients read. Spelling
    /// them a second time here is how the two ends drift apart.
    /// </para>
    /// </summary>
    [IgnoreDataMember]
    public object? Design { get; init; }
}
