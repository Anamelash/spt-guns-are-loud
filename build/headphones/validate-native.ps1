param([ValidateSet('comtac2','sordin','cens')][string]$Profile = 'sordin',
      [ValidateSet('Outdoor','Indoor','Bunker')][string]$Snapshot = 'Outdoor',
      # Bundle built before the current one; the default controls must render identically to it.
      [string]$PreviousBundle = '',
      # Appended to report names so a new run sits beside earlier evidence instead of replacing it.
      [string]$Tag = '',
      # Stock: the variant of the game's mixer. Live: the variant of Sound Engine Backport's live mixer.
      [ValidateSet('Stock','Live')][string]$Variant = 'Stock',
      [string]$SptGameDir = 'D:\Games\SPT_4.1.5',
      # The Unity mixer project (see README.md here); reports go to its parent folder.
      [string]$MixerProject = $env:GAL_MIXER_PROJECT)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if (!$MixerProject) { throw 'Pass -MixerProject or set GAL_MIXER_PROJECT to the Unity mixer project folder.' }
$project = (Resolve-Path $MixerProject).Path
$evidence = Split-Path $project -Parent
if ($Variant -eq 'Live') {
    $bundleDir = Join-Path $project 'bundle-live'
    $candidateBundle = Join-Path $bundleDir 'gal_headphone_mixer_live'
    $candidateAsset = 'Assets/GunsAreLoud/AudioLive/MasterMixer.mixer'
    # SEB's own compiled bundle, not the re-imported copy: at neutral settings
    # the variant must sound exactly like the mixer SEB would have installed.
    $env:GAL_BASELINE_MIXER = (Join-Path $SptGameDir 'BepInEx/plugins/SoundEngineBackport/live_mastermixer.bundle') + '|Assets/Resources/Audio/MasterMixer.mixer'
    $bypass = 'Bypass'
    $env:GAL_PARAMETER_LIST = 'live-headphone-mixer-parameters.txt'
    # The live variant has no earlier build: its defaults are compared with itself,
    # and its Vanilla identity against SEB's own mixer is what the baseline proves.
    if (!$PreviousBundle) { $PreviousBundle = $candidateBundle }
    $reportVariant = '-live'
} else {
    $candidateBundle = Join-Path $project 'bundle/gal_headphone_mixer'
    $candidateAsset = 'Assets/GunsAreLoud/Audio/MasterMixer.mixer'
    $env:GAL_BASELINE_MIXER = (Join-Path $project 'bundle/gal_vanilla_repaired') + '|Assets/VanillaMasterMixer.mixer'
    $bypass = 'NonspatialBypass'
    Remove-Item Env:GAL_PARAMETER_LIST -ErrorAction SilentlyContinue
    if (!$PreviousBundle) { $PreviousBundle = Join-Path $evidence 'v1-mixer-backup-2026-10-09/bundle/gal_headphone_mixer' }
    $reportVariant = ''
}
if (!(Test-Path -LiteralPath $PreviousBundle)) { throw "Previous mixer bundle missing: $PreviousBundle" }
$suffix = if ($Tag) { "-$Tag" } else { '' }
$env:GAL_CANDIDATE_MIXER = $candidateBundle + '|' + $candidateAsset
$env:GAL_PREVIOUS_MIXER = $PreviousBundle + '|' + $candidateAsset
$env:GAL_BYPASS_GROUP = $bypass
$env:GAL_VANILLA_GROUPS = "Guns/Gunshots,Environment/ClientPlayer,Environment/ObservedPlayer,Environment/NPC,Environment/TechnicalSounds,Environment/NatureSounds,Environment/CommonSounds,Main/Ambient,Main/Returns,Occlusion/SimpleOccluded,$bypass,Voip,InGame/Inventory,UI,Music,Chat"
$env:GAL_PASSIVE_GROUP = 'World/GAL Passive'
$env:GAL_ELECTRONICS_GROUP = 'World/GAL Electronics'
$env:GAL_FIT_JSON = Join-Path $evidence "$Profile-fit.json"
$env:GAL_VALIDATION_OUTPUT = Join-Path $evidence "$Profile-$Snapshot-native-validation$reportVariant$suffix.json"
$env:GAL_SNAPSHOT = $Snapshot
$env:GAL_COMMON_PARAMETERS_JSON = Join-Path $evidence 'native-common.json'
$env:GAL_ELECTRONICS_JSON = Join-Path $evidence 'native-common-electronics.json'
# The harness exits the editor itself after PlayMode validation; do not add -quit.
$unityLog = Join-Path $evidence 'mixer-validation.log'
$unityArgs = '-batchmode -nographics -projectPath "' + $project + '" -executeMethod MixerOfflineValidation.Run -logFile "' + $unityLog + '"'
$unityRun = Start-Process -FilePath 'C:\Program Files\Unity\Hub\Editor\2022.3.43f1\Editor\Unity.exe' -ArgumentList $unityArgs -PassThru -Wait -WindowStyle Hidden
Copy-Item -LiteralPath $unityLog -Destination (Join-Path $evidence "$Profile-$Snapshot-native$reportVariant$suffix.log") -Force
if ($unityRun.ExitCode -ne 0) { throw "Native mixer validation failed: $Profile / $Snapshot ($Variant)" }
if (!(Select-String -LiteralPath $unityLog -SimpleMatch 'GAL_MIXER_OFFLINE_VALIDATION_COMPLETE' -Quiet)) {
    throw 'Unity exited without completing native mixer validation'
}
