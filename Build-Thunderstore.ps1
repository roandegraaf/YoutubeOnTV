# Thunderstore Package Build Script for YoutubeOnTV
# This script creates a properly structured Thunderstore package

param(
    [string]$Version = "0.2.5",
    [string]$BuildConfig = "Debug"
)

# Set strict mode for better error handling
$ErrorActionPreference = "Stop"

# Paths
$ProjectRoot = $PSScriptRoot
$BinPath = Join-Path $ProjectRoot "YoutubeOnTV\bin\$BuildConfig"
$PackageDir = Join-Path $ProjectRoot "thunderstore_package"
$PluginDir = Join-Path $PackageDir "BepInEx\plugins\YoutubeOnTV"
$ZipName = "YoutubeOnTV-$Version.zip"
$ZipPath = Join-Path $ProjectRoot $ZipName

Write-Host "================================" -ForegroundColor Cyan
Write-Host "YoutubeOnTV Thunderstore Package Builder" -ForegroundColor Cyan
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""

# Validate required files exist
Write-Host "[1/7] Validating files..." -ForegroundColor Yellow

$RequiredFiles = @{
    "manifest.json"       = Join-Path $ProjectRoot "manifest.json"
    "README.md"           = Join-Path $ProjectRoot "README.md"
    "icon.png"            = Join-Path $ProjectRoot "icon.png"
    "CHANGELOG.md"        = Join-Path $ProjectRoot "CHANGELOG.md"
    "YoutubeOnTV.dll" = Join-Path $BinPath "YoutubeOnTV.dll"
    "fallback.mp4"        = Join-Path $BinPath "fallback.mp4"
}

$MissingFiles = @()
foreach ($file in $RequiredFiles.GetEnumerator()) {
    if (-not (Test-Path $file.Value)) {
        $MissingFiles += $file.Key
        Write-Host "  X Missing: $($file.Key)" -ForegroundColor Red
    }
    else {
        Write-Host "  OK Found: $($file.Key)" -ForegroundColor Green
    }
}

if ($MissingFiles.Count -gt 0) {
    Write-Host ""
    Write-Host "ERROR: Missing required files!" -ForegroundColor Red
    Write-Host "Please ensure all files exist before building." -ForegroundColor Red
    exit 1
}

# Clean up old package directory and zip
Write-Host ""
Write-Host "[2/7] Cleaning old build artifacts..." -ForegroundColor Yellow
if (Test-Path $PackageDir) {
    Remove-Item $PackageDir -Recurse -Force
    Write-Host "  OK Removed old package directory" -ForegroundColor Green
}
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
    Write-Host "  OK Removed old zip file" -ForegroundColor Green
}

# Create directory structure
Write-Host ""
Write-Host "[3/7] Creating package structure..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $PluginDir -Force | Out-Null
Write-Host "  OK Created BepInEx/plugins/YoutubeOnTV/" -ForegroundColor Green

# Copy mod files to plugin directory
Write-Host ""
Write-Host "[4/7] Copying mod files..." -ForegroundColor Yellow
Copy-Item (Join-Path $BinPath "YoutubeOnTV.dll") $PluginDir
Write-Host "  OK Copied YoutubeOnTV.dll" -ForegroundColor Green

Copy-Item (Join-Path $BinPath "fallback.mp4") $PluginDir
Write-Host "  OK Copied fallback.mp4" -ForegroundColor Green

# Copy package files to root
Write-Host ""
Write-Host "[5/7] Copying package metadata files..." -ForegroundColor Yellow
Copy-Item (Join-Path $ProjectRoot "manifest.json") $PackageDir
Write-Host "  OK Copied manifest.json" -ForegroundColor Green

Copy-Item (Join-Path $ProjectRoot "README.md") $PackageDir
Write-Host "  OK Copied README.md" -ForegroundColor Green

Copy-Item (Join-Path $ProjectRoot "icon.png") $PackageDir
Write-Host "  OK Copied icon.png" -ForegroundColor Green

Copy-Item (Join-Path $ProjectRoot "CHANGELOG.md") $PackageDir
Write-Host "  OK Copied CHANGELOG.md" -ForegroundColor Green

# Validate icon dimensions
Write-Host ""
Write-Host "[6/7] Validating icon.png dimensions..." -ForegroundColor Yellow
try {
    Add-Type -AssemblyName System.Drawing
    $Icon = [System.Drawing.Image]::FromFile((Join-Path $PackageDir "icon.png"))
    if ($Icon.Width -eq 256 -and $Icon.Height -eq 256) {
        Write-Host "  OK Icon is 256x256 (valid)" -ForegroundColor Green
    }
    else {
        Write-Host "  WARNING Icon is $($Icon.Width)x$($Icon.Height), should be 256x256" -ForegroundColor Yellow
    }
    $Icon.Dispose()
}
catch {
    Write-Host "  WARNING Could not validate icon dimensions" -ForegroundColor Yellow
}

# Create zip file (zipping contents, not the folder)
Write-Host ""
Write-Host "[7/7] Creating Thunderstore package zip..." -ForegroundColor Yellow

# Change to package directory to zip contents correctly
Push-Location $PackageDir
try {
    # Compress files at root level, not the parent folder
    Compress-Archive -Path * -DestinationPath $ZipPath -Force
    Write-Host "  OK Created $ZipName" -ForegroundColor Green
}
finally {
    Pop-Location
}

# Display summary
Write-Host ""
Write-Host "================================" -ForegroundColor Cyan
Write-Host "Build Complete!" -ForegroundColor Green
Write-Host "================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Package Information:" -ForegroundColor White
Write-Host "  Name: YoutubeOnTV" -ForegroundColor White
Write-Host "  Version: $Version" -ForegroundColor White
Write-Host "  Output: $ZipName" -ForegroundColor White
Write-Host ""
Write-Host "Package Contents:" -ForegroundColor White
Write-Host "  - manifest.json" -ForegroundColor White
Write-Host "  - README.md" -ForegroundColor White
Write-Host "  - icon.png" -ForegroundColor White
Write-Host "  - CHANGELOG.md" -ForegroundColor White
Write-Host "  - BepInEx/plugins/YoutubeOnTV/" -ForegroundColor White
Write-Host "    - YoutubeOnTV.dll" -ForegroundColor White
Write-Host "    - fallback.mp4" -ForegroundColor White
Write-Host ""
Write-Host "Note: yt-dlp.exe will download automatically on first run via YoutubeDLSharp dependency" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Yellow
Write-Host "  1. Test the package by extracting and verifying the structure" -ForegroundColor White
Write-Host "  2. Upload to Thunderstore at https://thunderstore.io/c/lethal-company/create/" -ForegroundColor White
Write-Host "  3. Fill in the upload form and attach $ZipName" -ForegroundColor White
Write-Host ""
Write-Host "Build artifacts can be found in:" -ForegroundColor White
Write-Host "  Package folder: $PackageDir" -ForegroundColor Cyan
Write-Host "  Zip file: $ZipPath" -ForegroundColor Cyan
Write-Host ""
