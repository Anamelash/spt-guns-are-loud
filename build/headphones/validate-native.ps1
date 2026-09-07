param([ValidateSet('comtac2','sordin','cens')][string]$Profile = 'sordin',
      [ValidateSet('Outdoor','Indoor','Bunker')][string]$Snapshot = 'Outdoor')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$evidence = Join-Path $repo 'docs/internal/headphone-model-2026-09-07'
$project = Join-Path $evidence 'mixer-project'
$env:GAL_BASELINE_MIXER = (Join-Path $project 'bundle/gal_vanilla_repaired') + '|Assets/VanillaMasterMixer.mixer'
$env:GAL_CANDIDATE_MIXER = (Join-Path $project 'bundle/gal_headphone_mixer') + '|Assets/GunsAreLoud/Audio/MasterMixer.mixer'
$env:GAL_VANILLA_GROUPS = 'Guns/Gunshots,Environment/ClientPlayer,Environment/ObservedPlayer,Environment/NPC,Environment/TechnicalSounds,Environment/NatureSounds,Environment/CommonSounds,Main/Ambient,Main/Returns,Occlusion/SimpleOccluded,NonspatialBypass,Voip,InGame/Inventory,UI,Music,Chat'
$env:GAL_PASSIVE_GROUP = 'World/GAL Passive'
$env:GAL_ELECTRONICS_GROUP = 'World/GAL Electronics'
$env:GAL_FIT_JSON = Join-Path $evidence "$Profile-fit.json"
$env:GAL_VALIDATION_OUTPUT = Join-Path $evidence "$Profile-$Snapshot-native-validation.json"
$env:GAL_SNAPSHOT = $Snapshot
$env:GAL_COMMON_PARAMETERS_JSON = Join-Path $evidence 'native-common.json'
$env:GAL_ELECTRONICS_JSON = Join-Path $evidence 'native-common-electronics.json'
# The harness exits the editor itself after PlayMode validation; do not add -quit.
$unityLog = Join-Path $evidence 'mixer-validation.log'
$unityArgs = '-batchmode -nographics -projectPath "' + $project + '" -executeMethod MixerOfflineValidation.Run -logFile "' + $unityLog + '"'
$unityRun = Start-Process -FilePath 'C:\Program Files\Unity\Hub\Editor\2022.3.43f1\Editor\Unity.exe' -ArgumentList $unityArgs -PassThru -Wait -WindowStyle Hidden
Copy-Item -LiteralPath $unityLog -Destination (Join-Path $evidence "$Profile-$Snapshot-native.log") -Force
if ($unityRun.ExitCode -ne 0) { throw "Native mixer validation failed: $Profile / $Snapshot" }
if (!(Select-String -LiteralPath $unityLog -SimpleMatch 'GAL_MIXER_OFFLINE_VALIDATION_COMPLETE' -Quiet)) {
    throw 'Unity exited without completing native mixer validation'
}
