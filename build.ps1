#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build and publish LiteConnect for win-x64 and win-arm64.

.DESCRIPTION
    Produces two self-contained single-file releases in ./publish/:
      publish/win-x64/    -> LiteConnect.exe  (for x86-64 machines)
      publish/win-arm64/  -> LiteConnect.exe  (for ARM64 machines)

    Each output folder also contains:
      portable.marker     -> activates portable data storage
      data/               -> created on first run by PortableAppEnvironment
      logs/               -> created on first run by PortableAppEnvironment

.PARAMETER Version
    Version string to embed in the zip filenames. Default: "0.1.0"

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: "Release"
#>
param(
    [string]$Version       = "0.1.0",
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$Root       = $PSScriptRoot
$AppProject = Join-Path $Root "src" "LiteConnect.App" "LiteConnect.App.csproj"
$PublishDir = Join-Path $Root "publish"

$Rids = @("win-x64", "win-arm64")

foreach ($rid in $Rids) {
    $outDir = Join-Path $PublishDir $rid

    Write-Host ""
    Write-Host "━━━ Publishing for $rid ━━━" -ForegroundColor Cyan

    dotnet publish $AppProject `
        --configuration $Configuration `
        --runtime $rid `
        --self-contained `
        --output $outDir `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    # Drop portable marker into the output
    Copy-Item (Join-Path $Root "portable.marker") (Join-Path $outDir "portable.marker") -Force

    # Create data and logs stubs (PortableAppEnvironment creates them on first run,
    # but including them makes the zip self-explanatory)
    New-Item -ItemType Directory -Path (Join-Path $outDir "data") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $outDir "logs") -Force | Out-Null

    # Zip the output
    $zipName = "LiteConnect-v${Version}-portable-${rid}.zip"
    $zipPath = Join-Path $PublishDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath }

    Compress-Archive -Path "$outDir\*" -DestinationPath $zipPath
    Write-Host "  ✓ $zipName" -ForegroundColor Green
}

Write-Host ""
Write-Host "━━━ Done ━━━" -ForegroundColor Cyan
Write-Host "Outputs in: $PublishDir"
