<#
.SYNOPSIS
  Builds the distributable app into publish\LocalAI: self-contained (no .NET install needed) with the llama.cpp
  runtime included and no models. Everything the app creates goes into publish\LocalAI\data; republishing replaces
  the program files and keeps data\. Also writes publish\LocalAI.zip (the same files without data\) to share or
  attach to a release.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
  .\publish\LocalAI\LocalAI.exe
#>
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root "publish\LocalAI"
$runtime = Join-Path $root "runtime\llama.cpp"
$catalog = Get-Content (Join-Path $root "src\LocalAI.Configuration\catalog.json") -Raw | ConvertFrom-Json

if (-not (Test-Path (Join-Path $runtime "llama-server.exe"))) {
    Write-Host "Downloading $($catalog.runtime.displayName)..."
    $downloads = Join-Path $root "runtime\_downloads"
    New-Item -ItemType Directory -Force $downloads, $runtime | Out-Null
    foreach ($url in $catalog.runtime.urls) {
        $zip = Join-Path $downloads (Split-Path $url -Leaf)
        & curl.exe -L --fail --retry 5 -C - -o $zip $url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $url" }
        Expand-Archive -Force $zip $runtime
    }
    Remove-Item -Recurse -Force $downloads
}

# Replace the program files but never the user's data.
if (Test-Path $out) {
    Get-ChildItem $out | Where-Object { $_.Name -ne "data" } | Remove-Item -Recurse -Force
}
dotnet publish (Join-Path $root "src\LocalAI.App\LocalAI.App.csproj") -c Release -r win-x64 --self-contained true -o $out -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Zip without data\: the user's database, settings, logs and models never go into the package.
# Windows' bsdtar writes zip archives larger than 2 GB and much faster than Compress-Archive.
$zipPath = Join-Path $root "publish\LocalAI.zip"
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
& "$env:SystemRoot\System32\tar.exe" -a -c -f $zipPath -C (Split-Path $out) --exclude "LocalAI/data" "LocalAI"
if ($LASTEXITCODE -ne 0) { throw "Creating $zipPath failed" }

Write-Host ""
Write-Host "Published to $out"
Write-Host "Zipped to $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1MB)) MB, without data\)"
Write-Host "Copy that folder to any Windows PC and run LocalAI.exe. The folder must be writable (not under Program Files)."
Write-Host "Everything the app creates is in its data folder: delete the folder to remove the app completely."
