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

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Builder for the NMLolomo page component. It keeps only the carousel rows that
/// hold at least one item and relinks next and previous over the rows that are sent.
/// </summary>
public class LolomoComponentBuilder : IComponentBuilder
{
    private readonly ComponentEnvelope _envelope;
    private readonly LolomoProps _props;

    public LolomoComponentBuilder(string id)
    {
        _props = new() { Id = id };
        _envelope = new() { Component = ComponentTypes.Lolomo, Props = _props };
    }

    /// <summary>"none" hides the hero; any other value reads as "focus".</summary>
    public LolomoComponentBuilder WithHero(string? hero)
    {
        _props.Hero = hero == LolomoProps.HeroNone ? LolomoProps.HeroNone : LolomoProps.HeroFocus;
        return this;
    }

    /// <summary>
    /// Sets the rows. A wrapping NMContainer is flattened one level; anything that is
    /// not a carousel, and every carousel without items, is dropped.
    /// </summary>
    public LolomoComponentBuilder WithItems(IEnumerable<ComponentEnvelope> rows)
    {
        List<ComponentEnvelope> kept =
        [
            .. rows.SelectMany(Flatten)
                .Where(row => row.Component == ComponentTypes.Carousel)
                .Where(row => row.Props is ContainerProps { Items: { } items } && items.Any()),
        ];

        List<ComponentEnvelope> linked = [];
        for (int index = 0; index < kept.Count; index++)
        {
            ContainerProps props = (ContainerProps)kept[index].Props;
            linked.Add(
                kept[index] with
                {
                    Props = props with
                    {
                        PreviousId = index == 0 ? null : ((ContainerProps)kept[index - 1].Props).Id,
                        NextId =
                            index == kept.Count - 1
                                ? null
                                : ((ContainerProps)kept[index + 1].Props).Id,
                    },
                }
            );
        }

        _props.Items = linked;
        return this;
    }

    private static IEnumerable<ComponentEnvelope> Flatten(ComponentEnvelope row) =>
        row.Component == ComponentTypes.Container && row.Props is ContainerProps container
            ? container.Items
            : [row];

    public ComponentEnvelope Build()
    {
        return _envelope;
    }

    public static implicit operator ComponentEnvelope(LolomoComponentBuilder builder) =>
        builder.Build();
}
