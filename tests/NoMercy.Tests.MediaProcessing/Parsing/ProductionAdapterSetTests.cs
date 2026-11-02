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

using System.Text.RegularExpressions;
using NoMercy.MediaProcessing.Files.Parsing;

namespace NoMercy.Tests.MediaProcessing.Parsing;

public partial class ProductionAdapterSetTests
{
    [GeneratedRegex(
        @"IFilenameParseAdapter,\s*MediaProcessing\.Files\.Parsing\.Adapters\.(?<name>\w+)\s*>",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex RegisteredAdapter();

    [Fact]
    public void The_test_pipeline_holds_the_adapters_the_server_registers_in_the_same_order()
    {
        string source = File.ReadAllText(RegistrationFile());

        string[] registered = RegisteredAdapter()
            .Matches(source)
            .Select(match => match.Groups["name"].Value)
            .ToArray();
        string[] ours = ProductionFilenameParser
            .Adapters()
            .Select(adapter => adapter.GetType().Name)
            .ToArray();

        registered.Should().HaveCount(11);
        ours.Should().Equal(registered);
    }

    [Fact]
    public void The_test_pipeline_orders_its_adapters_the_way_the_container_would()
    {
        FilenameParserPipeline pipeline = new(ProductionFilenameParser.Adapters());

        pipeline.Order.Should().HaveCount(11);
        pipeline.Order[^1].Should().Be("movie-detector");
    }

    private static string RegistrationFile()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "NoMercy.Service",
                "Configuration",
                "ServiceConfiguration.Core.cs"
            );
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "ServiceConfiguration.Core.cs was not found above the test output."
        );
    }
}
