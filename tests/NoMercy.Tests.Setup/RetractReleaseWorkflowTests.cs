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

namespace NoMercy.Tests.Setup;

public sealed class RetractReleaseWorkflowTests
{
    [Fact]
    public void ReasonIsPassedToShellThroughEnvironment()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (
            directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Server.sln"))
        )
            directory = directory.Parent;

        Assert.NotNull(directory);
        string workflowPath =
            Environment.GetEnvironmentVariable("NOMERCY_RETRACT_WORKFLOW")
            ?? Path.Combine(directory.FullName, ".github", "workflows", "retract-release.yml");
        string[] lines = File.ReadAllLines(workflowPath);

        // AUD-1106: the reason input reaches the scripts only through the job environment.
        string[] reasonReferences =
        [
            .. lines
                .Where(line => line.Contains("${{ inputs.reason }}", StringComparison.Ordinal))
                .Select(line => line.Trim()),
        ];
        Assert.Equal(["REASON: ${{ inputs.reason }}"], reasonReferences);

        string workflow = string.Join('\n', lines);
        Assert.Contains("\"$REASON\"", workflow, StringComparison.Ordinal);
        Assert.Contains("echo \"- Reason: $REASON\"", workflow, StringComparison.Ordinal);
    }
}
