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

using Microsoft.Extensions.Logging;
using MovieFileLibrary;

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// Ordered filename parser. Adapters are supplied by DI (so plugins extend the set)
/// and ordered by <see cref="FilenameParsingOptions"/> overrides first, then their
/// default <see cref="IFilenameParseAdapter.Order"/>. The first adapter to produce a
/// result wins.
/// </summary>
public sealed class FilenameParserPipeline : IFilenameParserPipeline
{
    private readonly IReadOnlyList<IFilenameParseAdapter> _adapters;
    private readonly ILogger<FilenameParserPipeline>? _logger;

    public FilenameParserPipeline(
        IEnumerable<IFilenameParseAdapter> adapters,
        FilenameParsingOptions? options = null,
        ILogger<FilenameParserPipeline>? logger = null
    )
    {
        options ??= new();
        _logger = logger;
        _adapters = adapters
            .Where(adapter => !options.Disabled.Contains(adapter.Name))
            .OrderBy(adapter =>
            {
                int index = options.Order.FindIndex(name =>
                    string.Equals(name, adapter.Name, StringComparison.OrdinalIgnoreCase)
                );
                return index >= 0 ? index : int.MaxValue;
            })
            .ThenBy(adapter => adapter.Order)
            .ToList();
    }

    /// <summary>The effective adapter order, for diagnostics.</summary>
    public IReadOnlyList<string> Order => _adapters.Select(adapter => adapter.Name).ToList();

    public MovieFile Parse(ParseContext context)
    {
        foreach (IFilenameParseAdapter adapter in _adapters)
        {
            MovieFile? result;
            try
            {
                result = adapter.TryParse(context);
            }
            catch (Exception e) when (e is OverflowException or FormatException)
            {
                // A number too long for an int (S01E20240101123000) is no episode (#243).
                _logger?.LogDebug(
                    "Adapter {Adapter} could not read a number in '{File}'",
                    [adapter.Name, context.FileNameWithExtension]
                );
                continue;
            }

            if (result is not null)
            {
                _logger?.LogDebug(
                    "Filename '{File}' parsed by adapter {Adapter}",
                    [context.FileNameWithExtension, adapter.Name]
                );
                return result;
            }
        }

        // Every adapter disabled — return an empty, unmatched result.
        return new(context.Title);
    }
}
