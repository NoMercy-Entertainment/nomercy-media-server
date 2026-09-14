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
/// Builder for leaf components (Card, HomeCard, MusicCard, etc.).
/// </summary>
public class LeafComponentBuilder<TData> : IComponentBuilder
{
    private readonly ComponentEnvelope _envelope;
    private readonly LeafProps<TData> _props;

    public LeafComponentBuilder(string componentType)
    {
        _props = new();
        _envelope = new() { Component = componentType, Props = _props };
    }

    public LeafComponentBuilder<TData> WithId(dynamic id)
    {
        _props.Id = id;
        return this;
    }

    public LeafComponentBuilder<TData> WithNavigation(
        dynamic? previousId = null,
        dynamic? nextId = null
    )
    {
        _props.PreviousId = previousId;
        _props.NextId = nextId;
        return this;
    }

    public LeafComponentBuilder<TData> WithTitle(string? title)
    {
        _props.Title = title.OrEmpty();
        return this;
    }

    public LeafComponentBuilder<TData> WithData(TData data)
    {
        _props.Data = data;
        return this;
    }

    public LeafComponentBuilder<TData> WithWatch(bool watch = true)
    {
        _props.Watch = watch;
        return this;
    }

    public LeafComponentBuilder<TData> WithContextMenu(IEnumerable<ContextMenuItemDto>? items)
    {
        _props.ContextMenuItems = items;
        return this;
    }

    public LeafComponentBuilder<TData> WithUrl(Uri? url)
    {
        _props.Url = url;
        return this;
    }

    public LeafComponentBuilder<TData> WithProperties(Dictionary<string, dynamic>? properties)
    {
        _props.Properties = properties;
        return this;
    }

    public LeafComponentBuilder<TData> WithUpdate(UpdateDto update)
    {
        _envelope.Update = update;
        return this;
    }

    public LeafComponentBuilder<TData> WithUpdate(string when, string link)
    {
        _envelope.Update = new()
        {
            When = when,
            Link = new(link, UriKind.Relative),
            Body = new { replace_id = _envelope.Id },
        };
        return this;
    }

    public LeafComponentBuilder<TData> WithReplacing(Ulid replacingId)
    {
        _envelope.Replacing = replacingId;
        return this;
    }

    public ComponentEnvelope Build()
    {
        return _envelope;
    }

    public static implicit operator ComponentEnvelope(LeafComponentBuilder<TData> builder) =>
        builder.Build();
}
