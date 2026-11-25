param(
    [switch]$SkipEngineBuild
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# 1. Verify .NET 8 SDK
$sdks = & dotnet --list-sdks 2>$null
if (-not ($sdks | Where-Object { $_ -match '^8\.' })) {
    throw ".NET 8 SDK is required to build from source (https://dotnet.microsoft.com/download/dotnet/8.0)."
}

# 2. Build native Rust engine if cargo is available
if (-not $SkipEngineBuild -and (Get-Command cargo -ErrorAction SilentlyContinue)) {
    Push-Location (Join-Path $root "engine\rust")
    try {
        cargo test --quiet
        if ($LASTEXITCODE -ne 0) { throw "cargo test failed" }
        cargo build --release
        if ($LASTEXITCODE -ne 0) { throw "cargo build --release failed" }
    }
    finally { Pop-Location }
    Copy-Item (Join-Path $root "engine\rust\target\release\engine.dll") (Join-Path $root "engine\engine.dll") -Force
}

# 3. Publish WinUI 3 app
Get-Process -Name "replay" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 250

if (Test-Path (Join-Path $root "build")) {
    Remove-Item (Join-Path $root "build\*") -Recurse -Force -ErrorAction SilentlyContinue
}
dotnet publish (Join-Path $root "Replay.csproj") -c Release -p:Platform=x64 -o (Join-Path $root "build")
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 4. Pack compressed payload.zip and build native Setup.exe
$payloadZip = Join-Path $root "installer\setup\payload.zip"
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $root "build"),
    $payloadZip,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false
)

$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$env:IR_PAYLOAD_ZIP = $payloadZip
Push-Location (Join-Path $root "installer\setup")
try {
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "setup cargo build failed" }
}
finally {
    Pop-Location
    Remove-Item Env:IR_PAYLOAD_ZIP -ErrorAction SilentlyContinue
}

$setupSrc = Join-Path $root "installer\setup\target\release\InstantReplaySetup.exe"
$setupOut = Join-Path $distDir "InstantReplaySetup.exe"
Copy-Item $setupSrc $setupOut -Force
Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue

Write-Host "Built setup: $setupOut ($((Get-Item $setupOut).Length) bytes)"
