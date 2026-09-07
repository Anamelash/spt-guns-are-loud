[CmdletBinding(DefaultParameterSetName = 'DryRun')]
param(
    [Parameter(ParameterSetName = 'Apply', Mandatory = $true)][switch]$Apply,
    [Parameter(ParameterSetName = 'Restore', Mandatory = $true)][switch]$Restore,
    [Parameter(ParameterSetName = 'DryRun')][switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$supportedGame = 'D:\Games\SPT_4.1.3'
$baselineGlobalHash = 'E75E77D831BCCCD96981896147F53F572EF78B4AC22FCE1FCBB8DC93AF3079F9'
# Pinned only after the DLL passed actual Unity 2022.3 Editor registration.
$pluginHash = '5B9CB1DF468A40689137D7AA39E049AC9CA4E6EB7AD2A428CEF4DF47A07CE6A1'
$pluginName = 'AudioPluginGalHeadphones'
$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..\..'))
$globalPath = Join-Path $supportedGame 'EscapeFromTarkov_Data\globalgamemanagers'
$pluginSource = Join-Path $repoRoot 'native\GalHeadphoneAudioPlugin\artifacts\win-x64\AudioPluginGalHeadphones.dll'
$pluginTarget = Join-Path $supportedGame 'EscapeFromTarkov_Data\Plugins\x86_64\AudioPluginGalHeadphones.dll'
$configPath = Join-Path $supportedGame 'BepInEx\config\com.anamelash.gunsareloud.cfg'
$backupDir = Join-Path $supportedGame 'BepInEx\GunsAreLoud.NativePluginBackup'
$globalBackup = Join-Path $backupDir 'globalgamemanagers.original'
$dllBackup = Join-Path $backupDir 'AudioPluginGalHeadphones.original.dll'
$manifestPath = Join-Path $backupDir 'manifest.json'
$patcher = Join-Path $scriptRoot 'native_plugin_registration.py'

function Get-Hash([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-GameClosed {
    if (Get-Process -Name 'EscapeFromTarkov' -ErrorAction SilentlyContinue) {
        throw 'EscapeFromTarkov is running. Close the game yourself before apply or restore.'
    }
}

function Assert-ConfigUnchanged([string]$Before) {
    if ((Get-Hash $configPath) -ne $Before) { throw 'Guns Are Loud configuration changed during the operation' }
}

if ([IO.Path]::GetFullPath($supportedGame).TrimEnd('\') -ne 'D:\Games\SPT_4.1.3') {
    throw 'This tool is locked to D:\Games\SPT_4.1.3'
}
if (!(Test-Path -LiteralPath $globalPath -PathType Leaf)) { throw 'globalgamemanagers is missing' }
if (!(Test-Path -LiteralPath $patcher -PathType Leaf)) { throw 'registration patcher is missing' }

if ($Restore) {
    Assert-GameClosed
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'registration backup manifest is missing' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.gamePath -ne $supportedGame -or $manifest.pluginName -ne $pluginName) { throw 'backup manifest targets another installation' }
    if ((Get-Hash $globalPath) -ne $manifest.patchedGlobalSha256) { throw 'installed globalgamemanagers no longer matches the guarded patch' }
    if ((Get-Hash $pluginTarget) -ne $manifest.installedPluginSha256) { throw 'installed native plugin no longer matches the guarded patch' }
    if ((Get-Hash $globalBackup) -ne $manifest.originalGlobalSha256) { throw 'globalgamemanagers backup hash mismatch' }
    if ($manifest.originalPluginPresent -and (Get-Hash $dllBackup) -ne $manifest.originalPluginSha256) {
        throw 'native plugin backup hash mismatch'
    }
    $configBefore = Get-Hash $configPath
    $restoreTemp = Join-Path (Split-Path -Parent $globalPath) ('.globalgamemanagers.gal-restore-' + [Guid]::NewGuid().ToString('N'))
    Copy-Item -LiteralPath $globalBackup -Destination $restoreTemp
    [IO.File]::Replace($restoreTemp, $globalPath, [NullString]::Value)
    if ($manifest.originalPluginPresent) {
        $dllTemp = Join-Path (Split-Path -Parent $pluginTarget) ('.AudioPluginGalHeadphones.restore-' + [Guid]::NewGuid().ToString('N') + '.dll')
        Copy-Item -LiteralPath $dllBackup -Destination $dllTemp
        [IO.File]::Replace($dllTemp, $pluginTarget, [NullString]::Value)
    } else {
        Remove-Item -LiteralPath $pluginTarget -Force
    }
    if ((Get-Hash $globalPath) -ne $manifest.originalGlobalSha256) { throw 'restored globalgamemanagers hash mismatch' }
    if ($manifest.originalPluginPresent -and (Get-Hash $pluginTarget) -ne $manifest.originalPluginSha256) { throw 'restored plugin hash mismatch' }
    if (!$manifest.originalPluginPresent -and (Test-Path -LiteralPath $pluginTarget)) { throw 'plugin removal failed' }
    Assert-ConfigUnchanged $configBefore
    Move-Item -LiteralPath $manifestPath -Destination (Join-Path $backupDir ('manifest.restored-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'))
    Write-Host 'Restored the exact original native-plugin metadata and DLL state.'
    exit 0
}

if ((Get-Hash $globalPath) -ne $baselineGlobalHash) { throw 'globalgamemanagers is not the supported unmodified baseline' }
if (!(Test-Path -LiteralPath $pluginSource -PathType Leaf)) { throw 'built native plugin artifact is missing' }
if ((Get-Hash $pluginSource) -ne $pluginHash) { throw 'native plugin artifact hash differs from the reviewed build' }
if (Test-Path -LiteralPath $pluginTarget) { throw 'target native plugin already exists' }
if (Test-Path -LiteralPath $manifestPath) { throw 'an active registration backup already exists' }

$patchTemp = if ($Apply) {
    Join-Path (Split-Path -Parent $globalPath) ('.globalgamemanagers.gal-patch-' + [Guid]::NewGuid().ToString('N'))
} else {
    Join-Path ([IO.Path]::GetTempPath()) ('gal-globalgamemanagers-' + [Guid]::NewGuid().ToString('N'))
}
try {
    $reportJson = & uv run --with UnityPy python $patcher --source $globalPath --output $patchTemp --plugin $pluginName --expected-sha256 $baselineGlobalHash
    if ($LASTEXITCODE -ne 0) { throw 'UnityPy registration validation failed' }
    $report = $reportJson | ConvertFrom-Json
    if (!$Apply) {
        $report | ConvertTo-Json -Depth 5
        Write-Host 'Dry run only. No game files were changed.'
        exit 0
    }

    Assert-GameClosed
    $configBefore = Get-Hash $configPath
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    Copy-Item -LiteralPath $globalPath -Destination $globalBackup
    $originalPluginPresent = Test-Path -LiteralPath $pluginTarget -PathType Leaf
    $originalPluginHash = if ($originalPluginPresent) { Get-Hash $pluginTarget } else { $null }
    if ($originalPluginPresent) { Copy-Item -LiteralPath $pluginTarget -Destination $dllBackup }
    $manifest = [ordered]@{
        schema = 1
        gamePath = $supportedGame
        unityVersion = $report.unityVersion
        buildSettingsPathId = $report.pathId
        pluginName = $pluginName
        originalGlobalSha256 = $baselineGlobalHash
        patchedGlobalSha256 = $report.patchedSha256
        installedPluginSha256 = $pluginHash
        originalPluginPresent = $originalPluginPresent
        originalPluginSha256 = $originalPluginHash
        configSha256AtApply = $configBefore
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    try {
        $dllInstallTemp = Join-Path (Split-Path -Parent $pluginTarget) ('.AudioPluginGalHeadphones.install-' + [Guid]::NewGuid().ToString('N') + '.dll')
        Copy-Item -LiteralPath $pluginSource -Destination $dllInstallTemp
        Move-Item -LiteralPath $dllInstallTemp -Destination $pluginTarget
        Assert-GameClosed
        [IO.File]::Replace($patchTemp, $globalPath, [NullString]::Value)
        if ((Get-Hash $globalPath) -ne $report.patchedSha256 -or (Get-Hash $pluginTarget) -ne $pluginHash) { throw 'installed file verification failed' }
        Assert-ConfigUnchanged $configBefore
        Write-Host 'Native headphone plugin registration applied and verified.'
    } catch {
        if ((Get-Hash $globalPath) -ne $baselineGlobalHash -and (Get-Hash $globalBackup) -eq $baselineGlobalHash) {
            $rollbackTemp = Join-Path (Split-Path -Parent $globalPath) ('.globalgamemanagers.gal-rollback-' + [Guid]::NewGuid().ToString('N'))
            Copy-Item -LiteralPath $globalBackup -Destination $rollbackTemp
            [IO.File]::Replace($rollbackTemp, $globalPath, [NullString]::Value)
        }
        if (!$originalPluginPresent -and (Test-Path -LiteralPath $pluginTarget)) {
            Remove-Item -LiteralPath $pluginTarget -Force
        }
        if ($dllInstallTemp -and (Test-Path -LiteralPath $dllInstallTemp)) {
            Remove-Item -LiteralPath $dllInstallTemp -Force
        }
        if ((Get-Hash $globalPath) -eq $baselineGlobalHash -and !(Test-Path -LiteralPath $pluginTarget) -and
            (Test-Path -LiteralPath $manifestPath)) {
            Move-Item -LiteralPath $manifestPath -Destination (Join-Path $backupDir ('manifest.rolled-back-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json'))
        }
        throw
    }
} finally {
    if (Test-Path -LiteralPath $patchTemp) { Remove-Item -LiteralPath $patchTemp -Force }
}
