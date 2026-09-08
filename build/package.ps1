# Builds the complete BepInEx archive ready to unpack into the SPT root.
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
$preloaderProject = Join-Path $repoRoot "client\GunsAreLoud.Preloader\GunsAreLoud.Preloader.csproj"
if (-not $SkipBuild) {
    & dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Client build failed"
    }
    & dotnet build $preloaderProject -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Preloader build failed" }
}
$native = Join-Path $repoRoot 'native\GalHeadphoneAudioPlugin\artifacts\win-x64\AudioPluginGalHeadphones.dll'
if (!(Test-Path $native) -or (Get-FileHash $native).Hash -ne '5B9CB1DF468A40689137D7AA39E049AC9CA4E6EB7AD2A428CEF4DF47A07CE6A1') { throw 'Pinned native DSP missing or changed' }

$dist = Join-Path $repoRoot "dist"
$stage = [IO.Path]::GetFullPath((Join-Path $dist "stage"))
if ($stage -ne [IO.Path]::GetFullPath((Join-Path $repoRoot "dist\stage"))) { throw "Unexpected staging path" }
$pluginDir = Join-Path $stage "BepInEx\plugins\GunsAreLoud"

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $pluginDir | Out-Null

$assembly = Join-Path $repoRoot "client\GunsAreLoud.Client\bin\$Configuration\GunsAreLoud.Client.dll"
Copy-Item -LiteralPath $assembly -Destination $pluginDir -Force
$patcherDir = Join-Path $stage 'BepInEx\patchers\GunsAreLoud'
New-Item -ItemType Directory -Path $patcherDir -Force | Out-Null
Copy-Item -LiteralPath $native -Destination $patcherDir
Copy-Item -LiteralPath (Join-Path $repoRoot "client\GunsAreLoud.Preloader\bin\$Configuration\GunsAreLoud.Preloader.dll") -Destination $patcherDir

foreach ($document in @("README.md", "MODEL.md", "CHANGELOG.md", "LICENSE")) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $document) -Destination $stage
}

# Keep reference links usable in the standalone archive.
foreach ($document in @("README.md", "MODEL.md")) {
    $documentPath = Join-Path $stage $document
    $text = [IO.File]::ReadAllText($documentPath)
    $text = $text.Replace("](docs/", "](https://github.com/Anamelash/spt-guns-are-loud/blob/v$version/docs/")
    $text = $text.Replace("](build/", "](https://github.com/Anamelash/spt-guns-are-loud/blob/v$version/build/")
    [IO.File]::WriteAllText($documentPath, $text)
}

$zip = Join-Path $dist "Guns-Are-Loud-$version.zip"
if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -LiteralPath $stage -Recurse -Force

$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
[IO.File]::WriteAllText("$zip.sha256", "$($hash.ToLowerInvariant())  $([IO.Path]::GetFileName($zip))`n")
Write-Host "Release -> $zip" -ForegroundColor Green
Write-Host "SHA256: $hash" -ForegroundColor Green
