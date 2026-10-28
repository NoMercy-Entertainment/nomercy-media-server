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

using System.IO.Compression;
using System.Text.Json;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.Cli;

/// <summary>
/// Builds a plugin source folder into the one zip the marketplace signs.
/// <para>
/// It never touches a network. The restore is pinned to the offline feed by a
/// config file written here, so a nuget.config shipped inside the source cannot
/// add a source of its own. It also refuses to guess: two manifests, two
/// projects, or a manifest claiming a newer contract than the one the plugin
/// compiled against are errors, because a guess is what would get signed.
/// </para>
/// </summary>
public static class PackCommand
{
    private const string SdkPackage = "NoMercy.PluginSdk.Abstractions";

    private static readonly string[] SkippedFolders = ["bin", "obj", ".git", "node_modules"];

    // Mirrors PluginHostOptions.DefaultSharedAssemblies. A copy of any of these
    // beside the plugin gives the load context two identities of one type.
    private static readonly HashSet<string> HostOwnedAssemblies = new(
        [
            "NoMercy.PluginSdk.Abstractions",
            "NoMercy.PluginSdk.Mvc",
            "NoMercy.Events",
            "NoMercy.Design",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.DependencyInjection",
            "Newtonsoft.Json",
            "Ulid",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static async Task<PackResult> RunAsync(
        PackOptions options,
        TextWriter? log = null,
        CancellationToken ct = default
    )
    {
        log ??= TextWriter.Null;

        if (!Directory.Exists(options.SourceFolder))
            throw new PackException($"The source folder {options.SourceFolder} does not exist.");

        if (string.IsNullOrWhiteSpace(options.FeedFolder) || !Directory.Exists(options.FeedFolder))
            throw new PackException(
                "pack builds from an offline feed only and none was given. "
                    + "Pass --feed <folder> or set NOMERCY_PLUGIN_FEED to a folder of .nupkg files."
            );

        if (
            Directory.Exists(options.OutputFolder)
            && Directory.GetFiles(options.OutputFolder, "*.zip").Length > 0
        )
            throw new PackException(
                $"The output folder {options.OutputFolder} already holds a zip. pack writes exactly one, so it will not choose between them."
            );

        string manifestFolder = LocateManifestFolder(options.SourceFolder);
        string manifestPath = Path.Combine(manifestFolder, "plugin.json");

        ScanReport scan = new() { Refusals = ManifestScan.Run(manifestFolder) };
        if (scan.ExitCode != 0)
            throw new PackException(
                "plugin.json is blocked:\n"
                    + string.Join(
                        "\n",
                        scan.Refusals.Where(refusal =>
                                refusal.Severity == PluginRefusalSeverity.Blocked
                            )
                            .Select(refusal => $"  {refusal.What} {refusal.Fix}")
                    )
            );

        byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, ct);
        PluginManifest manifest =
            JsonSerializer.Deserialize<PluginManifest>(manifestBytes, ManifestOptions)
            ?? throw new PackException("plugin.json is empty.");

        string zipName =
            $"{Path.GetFileNameWithoutExtension(manifest.Assembly)}-{manifest.Version}.zip";
        if (Path.GetFileName(zipName) != zipName)
            throw new PackException(
                $"The manifest version '{manifest.Version}' cannot be part of a file name."
            );

        string project = LocateProject(manifestFolder);
        string work = Path.Combine(Path.GetTempPath(), "nomercy-pack-" + Path.GetRandomFileName());

        try
        {
            string source = Path.Combine(work, "src");
            CopyTree(options.SourceFolder, source);

            string relative = Path.GetRelativePath(options.SourceFolder, project);
            string workProject = Path.Combine(source, relative);
            string workProjectFolder = Path.GetDirectoryName(workProject)!;

            string config = Path.Combine(work, "nuget.config");
            await File.WriteAllTextAsync(config, OfflineConfig(options.FeedFolder), ct);

            await log.WriteLineAsync($"Restoring from the offline feed {options.FeedFolder}");
            (int restoreCode, string restoreOutput) = await DotnetProcess.RunAsync(
                ["restore", workProject, "--configfile", config, "-p:NuGetAudit=false"],
                workProjectFolder,
                options.PackagesFolder,
                ct
            );

            if (restoreCode != 0)
                throw new PackException(
                    "Restore failed against the offline feed only. A package this plugin needs is not in it, "
                        + "and the marketplace build has no network to fetch it.\n"
                        + DotnetProcess.Errors(restoreOutput)
                );

            CheckAbi(Path.Combine(workProjectFolder, "obj", "project.assets.json"), manifest);

            string published = Path.Combine(work, "publish");
            await log.WriteLineAsync("Publishing (Release)");
            (int publishCode, string publishOutput) = await DotnetProcess.RunAsync(
                ["publish", workProject, "-c", "Release", "--no-restore", "-o", published],
                workProjectFolder,
                options.PackagesFolder,
                ct
            );

            if (publishCode != 0)
                throw new PackException(
                    "The plugin did not build.\n" + DotnetProcess.Errors(publishOutput)
                );

            string folderName = Path.GetFileNameWithoutExtension(manifest.Assembly);
            string stage = Path.Combine(work, "stage", folderName);
            Stage(published, stage, manifest.Assembly);
            await File.WriteAllBytesAsync(Path.Combine(stage, "plugin.json"), manifestBytes, ct);

            string builtZip = Path.Combine(work, zipName);
            ZipFile.CreateFromDirectory(
                Path.Combine(work, "stage"),
                builtZip,
                CompressionLevel.Optimal,
                includeBaseDirectory: false
            );

            Directory.CreateDirectory(options.OutputFolder);
            string zipPath = Path.Combine(options.OutputFolder, zipName);
            string outManifest = Path.Combine(options.OutputFolder, "plugin.json");
            File.Copy(builtZip, zipPath);
            await File.WriteAllBytesAsync(outManifest, manifestBytes, ct);

            await log.WriteLineAsync($"Wrote {zipPath} and {outManifest}");
            return new(zipPath, outManifest);
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private static string LocateManifestFolder(string sourceFolder)
    {
        List<string> manifests = Walk(sourceFolder)
            .Where(file => Path.GetFileName(file).Equals("plugin.json", StringComparison.Ordinal))
            .Order()
            .ToList();

        if (manifests.Count == 0)
            throw new PackException(
                $"There is no plugin.json under {sourceFolder}. A plugin is what its manifest says it is, so there is nothing to build."
            );

        if (manifests.Count > 1)
            throw new PackException(
                "There is more than one plugin.json, so which project is the plugin is a guess:\n"
                    + string.Join(
                        "\n",
                        manifests.Select(path => "  " + Path.GetRelativePath(sourceFolder, path))
                    )
            );

        return Path.GetDirectoryName(manifests[0])!;
    }

    private static string LocateProject(string manifestFolder)
    {
        string[] projects = Directory.GetFiles(manifestFolder, "*.csproj");

        if (projects.Length == 0)
            throw new PackException(
                $"There is no .csproj beside plugin.json in {manifestFolder}. The project that builds the plugin lives next to its manifest."
            );

        if (projects.Length > 1)
            throw new PackException(
                $"There is more than one project beside plugin.json in {manifestFolder}, so which one is the plugin is a guess."
            );

        return projects[0];
    }

    private static IEnumerable<string> Walk(string folder)
    {
        foreach (string file in Directory.GetFiles(folder))
            yield return file;

        foreach (string child in Directory.GetDirectories(folder))
            if (!SkippedFolders.Contains(Path.GetFileName(child), StringComparer.Ordinal))
                foreach (string file in Walk(child))
                    yield return file;
    }

    private static void CopyTree(string from, string to)
    {
        foreach (string file in Walk(from))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string OfflineConfig(string feed) =>
        $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="offline" value="{System.Security.SecurityElement.Escape(
                Path.GetFullPath(feed)
            )}" />
              </packageSources>
              <auditSources>
                <clear />
              </auditSources>
            </configuration>
            """;

    private static void CheckAbi(string assetsPath, PluginManifest manifest)
    {
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(assetsPath));

        string? resolved = assets
            .RootElement.GetProperty("libraries")
            .EnumerateObject()
            .Select(library => library.Name)
            .FirstOrDefault(name => name.StartsWith(SdkPackage + "/", StringComparison.Ordinal));

        if (resolved is null)
            throw new PackException(
                $"The plugin does not reference {SdkPackage}. Everything a plugin does goes through that package, so there is nothing for a server to load."
            );

        Version sdk = Version.Parse(resolved[(SdkPackage.Length + 1)..].Split('-')[0]);
        Version target = Version.Parse(manifest.TargetAbi!);

        if (target.Major > sdk.Major || (target.Major == sdk.Major && target.Minor > sdk.Minor))
            throw new PackException(
                $"plugin.json says targetAbi {manifest.TargetAbi}, but the plugin compiled against {SdkPackage} {sdk}. "
                    + "A manifest may not claim a newer contract than the one it was built with."
            );
    }

    private static void Stage(string published, string stage, string assembly)
    {
        Directory.CreateDirectory(stage);

        foreach (string file in Directory.GetFiles(published, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(file);
            string relative = Path.GetRelativePath(published, file);

            if (name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                continue;

            if (
                name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                && HostOwnedAssemblies.Contains(Path.GetFileNameWithoutExtension(name))
            )
                continue;

            string target = Path.Combine(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        if (!File.Exists(Path.Combine(stage, assembly)))
            throw new PackException(
                $"The build did not produce {assembly}, the assembly plugin.json names. Set \"assembly\" to the file the project builds."
            );
    }
}
