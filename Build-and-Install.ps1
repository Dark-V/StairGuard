# StairGuard one-click installer: gets a temporary SDK and restores NuGet packages from an explicit source.
# The published Windows executable is self-contained and does not depend on an installed .NET SDK.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$install = Join-Path $env:LOCALAPPDATA 'StairGuard'
$model = Join-Path $install 'yolov8n.onnx'
$source = 'https://huggingface.co/webml/yolov8n/resolve/main/onnx/yolov8n.onnx'
$expected = '190ba5f1e61411a001683e349d6b2cdb0804c0dc67a5e34cd8ff6fd00ee54b4d'
$buildDir = Join-Path $env:TEMP 'StairGuard-dotnet-build'
$sdkDir = Join-Path $buildDir 'sdk'
$bootstrap = Join-Path $buildDir 'dotnet-install.ps1'
$temporarySdk = $false

function Has-Sdk8OrNewer([string]$dotnetPath) {
    if ([string]::IsNullOrWhiteSpace($dotnetPath) -or -not (Test-Path -LiteralPath $dotnetPath)) { return $false }
    try {
        $versions = & $dotnetPath --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0) { return $false }
        foreach ($version in $versions) {
            if ($version -match '^\s*(\d+)\.') {
                if ([int]$Matches[1] -ge 8) { return $true }
            }
        }
    } catch { return $false }
    return $false
}

function Find-Dotnet {
    $candidate = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($candidate -and (Has-Sdk8OrNewer $candidate.Source)) { return $candidate.Source }
    $localDotnet = Join-Path $sdkDir 'dotnet.exe'
    if (Has-Sdk8OrNewer $localDotnet) { return $localDotnet }
    return $null
}

Write-Host '=== StairGuard for Windows (C# / ONNX, no Python) ==='
$proc = Get-Process -Name StairGuard -ErrorAction SilentlyContinue
if ($proc) { throw 'Close StairGuard from the tray menu before updating.' }

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$dotnet = Find-Dotnet
# A leftover temporary SDK from a failed build should also be removed after a successful build.
if ($dotnet -and [string]::Equals($dotnet, (Join-Path $sdkDir 'dotnet.exe'), [StringComparison]::OrdinalIgnoreCase)) {
    $temporarySdk = $true
}
if (-not $dotnet) {
    Write-Host 'No .NET 8+ SDK found. Downloading a temporary build SDK (no admin rights needed)...' -ForegroundColor Yellow
    New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
    try {
        Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $bootstrap -UseBasicParsing -MaximumRedirection 8 -TimeoutSec 120
        # dotnet-install.ps1 uses 'exit'; it must run in a child PowerShell process.
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $bootstrap -Channel '8.0' -InstallDir $sdkDir -Architecture 'x64' -NoPath
        if ($LASTEXITCODE -ne 0) { throw "Microsoft .NET SDK bootstrap failed (exit code $LASTEXITCODE)." }
        $dotnet = Join-Path $sdkDir 'dotnet.exe'
        if (-not (Has-Sdk8OrNewer $dotnet)) { throw 'SDK download finished but dotnet.exe with SDK 8+ is unavailable.' }
        $temporarySdk = $true
    } catch {
        Write-Host 'Could not download the temporary SDK. Internet access to dot.net and Microsoft download endpoints is required.' -ForegroundColor Red
        throw
    }
}
Write-Host "Using .NET SDK: $dotnet"

New-Item -ItemType Directory -Path $install -Force | Out-Null
if (Test-Path -LiteralPath $model) {
    $actual = (Get-FileHash -LiteralPath $model -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw 'Existing YOLO model has unexpected SHA256; remove it and retry.' }
    Write-Host 'YOLOv8n model SHA256: OK'
} else {
    Write-Host 'Downloading 12.8 MB YOLOv8n model...'
    $tmp = "$model.download"
    try {
        Invoke-WebRequest -Uri $source -OutFile $tmp -UseBasicParsing -MaximumRedirection 8 -TimeoutSec 120
        $actual = (Get-FileHash -LiteralPath $tmp -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "YOLO model hash mismatch: $actual" }
        Move-Item -LiteralPath $tmp -Destination $model -Force
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
}
# Do not depend on machine-wide or corporate NuGet settings: some Windows profiles
# have no registered package sources, causing an immediate NU1100.
$project = Join-Path $PSScriptRoot 'src\StairGuard.csproj'
$nugetConfig = Join-Path $PSScriptRoot 'NuGet.Config'
if (-not (Test-Path -LiteralPath $nugetConfig)) { throw "Missing NuGet.Config: $nugetConfig" }

Write-Host 'Restoring dependencies directly from nuget.org...'
Write-Host 'NuGet source: https://api.nuget.org/v3/index.json'
& $dotnet restore $project -r win-x64 `
    --configfile $nugetConfig `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    --force
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host 'NuGet restore failed. The required packages could not be downloaded.' -ForegroundColor Red
    Write-Host 'Check access to https://api.nuget.org/v3/index.json from this PC.' -ForegroundColor Yellow
    Write-Host 'If your network blocks NuGet or requires a proxy, the installer cannot bypass it.' -ForegroundColor Yellow
    Write-Host 'The SDK and already-downloaded YOLO model are kept for the next retry.'
    throw "NuGet restore failed with exit code $LASTEXITCODE"
}

Write-Host 'Compiling self-contained Windows EXE...'
& $dotnet publish $project `
    --no-restore -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false -p:PublishAot=false `
    -p:DebugType=None -p:DebugSymbols=false -o $install
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE" }

$exe = Join-Path $install 'StairGuard.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Expected executable not found at $exe" }
Write-Host "Installed: $exe" -ForegroundColor Green
if ($temporarySdk) {
    Write-Host 'Removing temporary build SDK...'
    try { Remove-Item -LiteralPath $buildDir -Recurse -Force -ErrorAction Stop }
    catch { Write-Host "Temporary SDK cleanup failed: $($_.Exception.Message)" -ForegroundColor Yellow }
}
Write-Host 'Opening StairGuard settings...'
Start-Process -FilePath $exe
