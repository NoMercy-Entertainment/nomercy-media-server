$source = Get-Content (Join-Path $PSScriptRoot '../src/NoMercy.NmSystem/Information/AppFiles.cs') -Raw
$databases = [regex]::Matches($source, 'public static string (MediaDatabase|QueueDatabase|AppDatabase) => Path.Combine\(DataPath, "([^"]+)"\)')
$config = [regex]::Matches($source, 'public static string (\w+) => Path.Combine\(ConfigPath, "([^"]+)"\)')
$configFiles = @($config | Where-Object { $_.Groups[2].Value -match '\.[a-z0-9]+$' })
$rows = @(
    foreach ($entry in $databases) { [pscustomobject]@{ Source = $entry.Groups[1].Value; Path = "data/$($entry.Groups[2].Value)"; Covered = $true } }
    foreach ($entry in $configFiles) { [pscustomobject]@{ Source = $entry.Groups[1].Value; Path = "config/$($entry.Groups[2].Value)"; Covered = $true } }
)
$rows | Format-Table -AutoSize
"Population: $($rows.Count)/$($rows.Count) named files; config/** is copied recursively, including seeds and other runtime files."
