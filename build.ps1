param(
    [switch]$SkipEngineBuild,
    [string]$InnoCompiler
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
if (-not $SkipEngineBuild) {
    if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
        throw "Cargo is required to rebuild the native engine. Use -SkipEngineBuild to package the checked-in DLL."
    }
    Push-Location (Join-Path $root "engine\rust")
    try {
        cargo test --locked --quiet
        if ($LASTEXITCODE -ne 0) { throw "cargo test failed" }
        cargo build --release --locked
        if ($LASTEXITCODE -ne 0) { throw "cargo build --release failed" }
    }
    finally { Pop-Location }
    Copy-Item (Join-Path $root "engine\rust\target\release\engine.dll") (Join-Path $root "engine\engine.dll") -Force
}

# 3. Publish into a fresh directory so old artifacts cannot enter this installer.
$project = Join-Path $root "Replay.csproj"
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Replay.csproj must define a three-part release version." }
$publishDir = Join-Path $root ("build\v$version-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
dotnet publish $project -c Release -p:Platform=x64 -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
$publishedVersion = (Get-Item -LiteralPath (Join-Path $publishDir "replay.exe")).VersionInfo.ProductVersion
if (($publishedVersion -split '\+', 2)[0] -ne $version) { throw "Published app version $publishedVersion does not match $version." }

# 4. Build standard Inno Setup installer
$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$isccCandidates = @(
    $InnoCompiler,
    (Get-Command iscc -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 (ISCC.exe) is required to build the installer (winget install JRSoftware.InnoSetup)."
}

& $iscc "/DMyAppVersion=$version" "/DPublishSource=$publishDir" (Join-Path $root "installer\InstantReplay.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setupOut = Join-Path $distDir "InstantReplaySetup.exe"
Write-Host "Built setup: $setupOut ($((Get-Item $setupOut).Length) bytes)"
