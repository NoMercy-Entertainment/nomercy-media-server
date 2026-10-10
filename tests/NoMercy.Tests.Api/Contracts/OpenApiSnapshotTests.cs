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

using Asp.Versioning;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using Moq;
using NoMercy.Api.Controllers;
using NoMercy.Service.Configuration.Swagger;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace NoMercy.Tests.Api.Contracts;

[Trait("Category", "Contract")]
public class OpenApiSnapshotTests
{
    [Fact]
    public async Task V1Swagger_MatchesCommittedSnapshot()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddOptions();
        services.AddRouting();
        IWebHostEnvironment environment = Mock.Of<IWebHostEnvironment>();
        services.AddSingleton(environment);
        services.AddSingleton<IHostEnvironment>(environment);
        services
            .AddControllers()
            .AddApplicationPart(typeof(HealthController).Assembly)
            .AddNewtonsoftJson();
        services
            .AddApiVersioning(options =>
            {
                options.AssumeDefaultVersionWhenUnspecified = true;
                options.DefaultApiVersion = new(1, 0);
            })
            .AddApiExplorer(options =>
            {
                options.GroupNameFormat = "'v'V";
                options.SubstituteApiVersionInUrl = true;
                options.DefaultApiVersion = new(1, 0);
            });
        SwaggerConfiguration.AddSwagger(services);

        using ServiceProvider provider = services.BuildServiceProvider();
        ISwaggerProvider swagger = provider.GetRequiredService<ISwaggerProvider>();
        OpenApiDocument document = swagger.GetSwagger("v1");
        string json = await document.SerializeAsJsonAsync(OpenApiSpecVersion.OpenApi3_0);
        Assert.NotEmpty(document.Paths);

        string path = SnapshotPath("openapi.json");
        if (Environment.GetEnvironmentVariable("NOMERCY_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, json + "\n");
            return;
        }

        Assert.Equal(await File.ReadAllTextAsync(path), json + "\n");
    }

    private static string SnapshotPath(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Server.sln"))
        )
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, name);
    }
}
