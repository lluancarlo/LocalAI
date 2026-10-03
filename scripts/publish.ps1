<#
.SYNOPSIS
  Builds a Release version of the app into publish\LocalAI (framework-dependent, win-x64).

.DESCRIPTION
  The published app finds models/ and runtime/ by walking up to the repository root (localai.home marker).
  To run it from elsewhere, set LOCALAI_HOME to the folder that contains models\ and runtime\.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
  .\publish\LocalAI\LocalAI.exe
#>
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$out = Join-Path $root "publish\LocalAI"

dotnet publish (Join-Path $root "src\LocalAI.App\LocalAI.App.csproj") -c Release -r win-x64 --self-contained false -o $out -p:DebugType=none
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host ""
Write-Host "Published to $out"
Write-Host "Run: $out\LocalAI.exe"
