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
    $content = [System.IO.File]::ReadAllText($f, [System.Text.Encoding]::UTF8)
    # Replace standalone lowercase 'storageDriver' (not preceded by _ or letter) with PascalCase 'StorageDriver'
    # These are references to the inherited property that was renamed from StorageBackend
    $updated = [System.Text.RegularExpressions.Regex]::Replace(
        $content,
        '(?<![_a-zA-Z])storageDriver\b',
        'StorageDriver'
    )
    if ($updated -ne $content) {
        [System.IO.File]::WriteAllText($f, $updated, [System.Text.Encoding]::UTF8)
        Write-Host "Updated: $f"
    } else {
        Write-Host "No changes: $f"
    }
}
