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

using NoMercy.Api.DTOs.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Builder for container components (Grid, List, Carousel, Container).
/// </summary>
public class ContainerComponentBuilder : IComponentBuilder
{
    private readonly ComponentEnvelope _envelope;
    private readonly ContainerProps _props;

    public ContainerComponentBuilder(string componentType, ContainerProps props)
    {
        _props = props;
        _envelope = new() { Component = componentType, Props = props };
    }

    public ContainerComponentBuilder WithId(dynamic id)
    {
        _props.Id = id;
        return this;
    }

    public ContainerComponentBuilder WithNavigation(
        dynamic? previousId = null,
        dynamic? nextId = null
    )
    {
        _props.PreviousId = previousId;
        _props.NextId = nextId;
        return this;
    }

    public ContainerComponentBuilder WithTitle(string? title)
    {
        _props.Title = title.OrEmpty();
        return this;
    }

    public ContainerComponentBuilder WithMoreLink(Uri? moreLink)
    {
        _props.MoreLink = moreLink;
        return this;
    }

    public ContainerComponentBuilder WithMoreLink(string? moreLink)
    {
        _props.MoreLink = moreLink != null ? new Uri(moreLink, UriKind.Relative) : null;
        return this;
    }

    public ContainerComponentBuilder WithItems(IEnumerable<ComponentEnvelope> items)
    {
        _props.Items = items;
        return this;
    }

    public ContainerComponentBuilder WithItems(params ComponentEnvelope[] items)
    {
        _props.Items = items;
        return this;
    }

    public ContainerComponentBuilder WithItems(IEnumerable<IComponentBuilder> builders)
    {
        _props.Items = builders.Select(b => b.Build());
        return this;
    }

    public ContainerComponentBuilder WithItems(params IComponentBuilder[] builders)
    {
        _props.Items = builders.Select(b => b.Build());
        return this;
    }

    public ContainerComponentBuilder WithContextMenu(IEnumerable<ContextMenuItemDto>? items)
    {
        _props.ContextMenuItems = items;
        return this;
    }

    public ContainerComponentBuilder WithUrl(Uri? url)
    {
        _props.Url = url;
        return this;
    }

    public ContainerComponentBuilder WithProperties(Dictionary<string, dynamic>? properties)
    {
        _props.Properties = properties;
        return this;
    }

    public ContainerComponentBuilder WithUpdate(UpdateDto update)
    {
        _envelope.Update = update;
        return this;
    }

    public ContainerComponentBuilder WithUpdate(string when, string link)
    {
        _envelope.Update = new()
        {
            When = when,
            Link = new(link, UriKind.Relative),
            Body = new { replace_id = _envelope.Id },
        };
        return this;
    }

    public ContainerComponentBuilder WithReplacing(Ulid replacingId)
    {
        _envelope.Replacing = replacingId;
        return this;
    }

    public ComponentEnvelope Build()
    {
        return _envelope;
    }

    public static implicit operator ComponentEnvelope(ContainerComponentBuilder builder) =>
        builder.Build();
}
