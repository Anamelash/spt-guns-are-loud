# Builds a client-only archive ready to unpack into the SPT installation root.
param(
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
[xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props")
$version = $props.Project.PropertyGroup.GunsAreLoudVersion
if (-not $version) {
    throw "GunsAreLoudVersion not found in Directory.Build.props"
}

$project = Join-Path $repoRoot "client\GunsAreLoud.Client\GunsAreLoud.Client.csproj"
if (-not $SkipBuild) {
    & dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Client build failed"
    }
}

$dist = Join-Path $repoRoot "dist"
$stage = Join-Path $dist "stage"
$pluginDir = Join-Path $stage "BepInEx\plugins\GunsAreLoud"

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

$assembly = Join-Path $repoRoot "client\GunsAreLoud.Client\bin\$Configuration\GunsAreLoud.Client.dll"
Copy-Item -LiteralPath $assembly -Destination $pluginDir -Force

$zip = Join-Path $dist "Guns-Are-Loud-$version.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Write-Host "Release -> $zip" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
