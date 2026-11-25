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

# 4. Build standard Inno Setup installer
$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$isccCandidates = @(
    (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) is required to build the installer (winget install JRSoftware.InnoSetup)."
}

& $iscc (Join-Path $root "installer\InstantReplay.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setupOut = Join-Path $distDir "InstantReplaySetup.exe"
Write-Host "Built setup: $setupOut ($((Get-Item $setupOut).Length) bytes)"
