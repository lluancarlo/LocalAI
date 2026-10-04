<#
.SYNOPSIS
  One-time setup: downloads the llama.cpp CUDA runtime and the local models described in models/catalog.json.

.DESCRIPTION
  This is the ONLY component that uses the Internet. The application itself never downloads anything.
  Run it once while online; afterwards the assistant works fully offline.

  The LLM is chosen automatically from the GPU's total VRAM (nvidia-smi) unless -Llm is given.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -Llm gemma-4-26b-a4b-qat-q4kxl
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -SkipLlm
  powershell -ExecutionPolicy Bypass -File scripts\setup.ps1 -AllVoices   # every voice in the catalog (~1 GB)
#>
[CmdletBinding()]
param(
    [string]$Llm = "auto",
    [switch]$SkipLlm,
    [switch]$SkipRuntime,
    [switch]$AllVoices,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$modelsDir = Join-Path $root "models"
$runtimeDir = Join-Path $root "runtime"
$catalog = Get-Content (Join-Path $modelsDir "catalog.json") -Raw | ConvertFrom-Json

function Get-File([string]$url, [string]$dest, [long]$expectedSize = 0) {
    if ((Test-Path $dest) -and -not $Force) {
        $len = (Get-Item $dest).Length
        if ($expectedSize -le 0 -or $len -eq $expectedSize) {
            Write-Host "  [skip] $(Split-Path $dest -Leaf) already present"
            return
        }
    }
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    $partial = "$dest.partial"
    Write-Host "  [get ] $url"
    # curl.exe ships with Windows 10+; -C - resumes interrupted downloads.
    & curl.exe -L --fail --retry 5 --retry-delay 3 -C - -o $partial $url
    if ($LASTEXITCODE -ne 0) { throw "Download failed ($LASTEXITCODE): $url" }
    if ($expectedSize -gt 0 -and (Get-Item $partial).Length -ne $expectedSize) {
        throw "Size mismatch for $dest (expected $expectedSize, got $((Get-Item $partial).Length))"
    }
    Move-Item -Force $partial $dest
}

function Get-VramMb {
    try {
        $out = & nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>$null
        if ($LASTEXITCODE -eq 0 -and $out) { return [int]($out | Select-Object -First 1).Trim() }
    } catch { }
    return 0
}

Write-Host "== LocalAI setup =="
Write-Host "Root: $root"

# --- Runtime: llama.cpp CUDA build + CUDA runtime DLLs (also used by Whisper.net's CUDA backend) ---
if (-not $SkipRuntime) {
    $llamaDir = Join-Path $runtimeDir "llama.cpp"
    $marker = Join-Path $llamaDir "BUILD.txt"
    $wanted = $catalog.runtime.llamaCppBuild
    if ($Force -or -not (Test-Path $marker) -or ((Get-Content $marker -Raw).Trim() -ne $wanted)) {
        Write-Host "Runtime: llama.cpp $wanted (CUDA 13.4)"
        $dl = Join-Path $runtimeDir "_downloads"
        $zip1 = Join-Path $dl "llama-cuda.zip"
        $zip2 = Join-Path $dl "cudart.zip"
        Get-File $catalog.runtime.llamaCppUrl $zip1
        Get-File $catalog.runtime.cudartUrl $zip2
        if (Test-Path $llamaDir) { Remove-Item -Recurse -Force $llamaDir }
        New-Item -ItemType Directory -Force $llamaDir | Out-Null
        Expand-Archive -Force $zip1 $llamaDir
        Expand-Archive -Force $zip2 $llamaDir
        Set-Content -Path $marker -Value $wanted -Encoding ascii
        Remove-Item -Recurse -Force $dl
    } else {
        Write-Host "Runtime: llama.cpp $wanted already installed"
    }
}

# --- LLM (auto-selected from VRAM) ---
if (-not $SkipLlm) {
    $vram = Get-VramMb
    if ($Llm -eq "auto") {
        $choice = $catalog.llm | Where-Object { $vram -ge $_.minVramMb } | Select-Object -First 1
        if (-not $choice) { $choice = $catalog.llm | Select-Object -Last 1 }
        Write-Host "LLM: auto-selected '$($choice.id)' for ${vram} MB VRAM"
    } else {
        $choice = $catalog.llm | Where-Object { $_.id -eq $Llm }
        if (-not $choice) { throw "Unknown LLM id '$Llm'. Known: $($catalog.llm.id -join ', ')" }
        Write-Host "LLM: $($choice.id)"
    }
    Get-File $choice.url (Join-Path $modelsDir $choice.file) ([long]$choice.sizeBytes)
}

Write-Host "Embedding model:"
foreach ($m in $catalog.embedding) { Get-File $m.url (Join-Path $modelsDir $m.file) ([long]$m.sizeBytes) }

Write-Host "Whisper model:"
foreach ($m in $catalog.whisper) { Get-File $m.url (Join-Path $modelsDir $m.file) ([long]$m.sizeBytes) }

Write-Host "VAD model:"
foreach ($m in $catalog.vad) { Get-File $m.url (Join-Path $modelsDir $m.file) }

Write-Host "TTS voices:"
$voices = if ($AllVoices) { $catalog.tts } else { $catalog.tts | Where-Object { $_.default } }
foreach ($v in $voices) {
    $dir = Join-Path $modelsDir $v.dir
    if ((Test-Path $dir) -and -not $Force) { Write-Host "  [skip] $($v.id) already present"; continue }
    $tmp = Join-Path $modelsDir "tts\_$($v.id).tar.bz2"
    Get-File $v.url $tmp
    # bsdtar (tar.exe) ships with Windows 10+ and handles .tar.bz2
    & tar.exe -xjf $tmp -C (Join-Path $modelsDir "tts")
    if ($LASTEXITCODE -ne 0) { throw "Failed to extract $tmp" }
    Remove-Item -Force $tmp
}

Write-Host ""
Write-Host "Setup complete. The application can now run fully offline."
