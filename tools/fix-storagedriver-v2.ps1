$files = @(
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/CollectionExtrasJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/LibraryScanJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/LibraryRescanJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/FileRescanJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/MovieExtrasJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/VideoEncodeJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/ShowExtrasJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/CollectionImportJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/ReleaseImportJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/ShowImportJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/MovieImportJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/MusicEncodeJob.cs',
  'C:/Projects/NoMercy/server/nomercy-media-server/src/NoMercy.MediaProcessing/Jobs/MediaJobs/AudioImportJob.cs'
)

foreach ($f in $files) {
    if (-not (Test-Path $f)) { Write-Host "Not found: $f"; continue }
    $bytes = [System.IO.File]::ReadAllBytes($f)
    $content = [System.Text.Encoding]::UTF8.GetString($bytes)

    # Simple string replace - no regex, just exact token replacement
    # We want to replace 'storageDriver' only where it's used as a standalone identifier
    # (not as part of '_storageDriver' which is a field)
    $lines = $content -split "`n"
    $changed = $false
    $newLines = foreach ($line in $lines) {
        # Replace ' storageDriver' and ',\nstorageDriver' patterns (standalone usage)
        # but NOT '_storageDriver' (field references)
        if ($line -match '\bstorageDriver\b' -and $line -notmatch '_storageDriver') {
            $newLine = $line -replace '\bstorageDriver\b', 'StorageDriver'
            $changed = $true
            $newLine
        } else {
            $line
        }
    }

    if ($changed) {
        $updated = $newLines -join "`n"
        [System.IO.File]::WriteAllText($f, $updated, [System.Text.Encoding]::UTF8)
        Write-Host "Updated: $f"
    } else {
        Write-Host "No changes: $f"
    }
}
