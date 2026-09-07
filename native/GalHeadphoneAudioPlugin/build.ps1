$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deps = Join-Path $root '.deps\NativeAudioPlugins'
$sdkRevision = 'bc7893edbba4c8a592777e590e34f21a11d762b4'
if (!(Test-Path (Join-Path $deps 'NativeCode\AudioPluginInterface.h'))) {
  New-Item -ItemType Directory -Force (Split-Path $deps) | Out-Null
  git clone https://github.com/Unity-Technologies/NativeAudioPlugins.git $deps
}
git -C $deps fetch origin $sdkRevision --depth 1
git -C $deps checkout --detach $sdkRevision
if ((git -C $deps rev-parse HEAD) -ne $sdkRevision) { throw 'Unity native audio SDK revision mismatch.' }
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$build = Join-Path $PSScriptRoot 'build\win-x64'
New-Item -ItemType Directory -Force $build | Out-Null
$sdk = Join-Path $deps 'NativeCode'
$src = Join-Path $PSScriptRoot 'src'
$mingw = Get-Command g++.exe -ErrorAction SilentlyContinue
$compiler = if ($mingw) { $mingw.Source } else { (Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\BrechtSanders.WinLibs.POSIX.UCRT*') -Filter g++.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1).FullName }
if ($compiler) {
  & $compiler -std=c++17 -O2 -Wall -Wextra -shared -static '-Wl,--no-insert-timestamp' -I $sdk -I $src (Join-Path $src 'GalHeadphoneAudioPlugin.cpp') (Join-Path $src 'GalHeadphoneDsp.cpp') -o (Join-Path $build 'AudioPluginGalHeadphones.dll')
  if ($LASTEXITCODE) { throw 'Native DLL build failed.' }
  & $compiler -std=c++17 -O2 -Wall -Wextra -static -I $src (Join-Path $PSScriptRoot 'tests\GalHeadphoneDspTests.cpp') (Join-Path $src 'GalHeadphoneDsp.cpp') -o (Join-Path $build 'GalHeadphoneDspTests.exe')
  if ($LASTEXITCODE) { throw 'Native DSP test build failed.' }
  & $compiler -std=c++17 -O2 -Wall -Wextra -static -I $sdk (Join-Path $PSScriptRoot 'tests\GalHeadphoneRegistrationTests.cpp') -o (Join-Path $build 'GalHeadphoneRegistrationTests.exe')
  if ($LASTEXITCODE) { throw 'Native registration test build failed.' }
  & (Join-Path $build 'GalHeadphoneDspTests.exe')
  if ($LASTEXITCODE) { throw 'Native DSP tests failed.' }
  & (Join-Path $build 'GalHeadphoneRegistrationTests.exe') (Join-Path $build 'AudioPluginGalHeadphones.dll')
  if ($LASTEXITCODE) { throw 'Native registration tests failed.' }
} else {
$commands = @(
  '@echo off',
  ('call "' + $vcvars + '"'),
  ('cl /nologo /std:c++17 /EHsc /W4 /O2 /LD /I"' + $sdk + '" /I"' + $src + '" "' + (Join-Path $src 'GalHeadphoneAudioPlugin.cpp') + '" "' + (Join-Path $src 'GalHeadphoneDsp.cpp') + '" /link /OUT:"' + (Join-Path $build 'AudioPluginGalHeadphones.dll') + '"'),
  ('cl /nologo /std:c++17 /EHsc /W4 /O2 /I"' + $src + '" "' + (Join-Path $PSScriptRoot 'tests\GalHeadphoneDspTests.cpp') + '" "' + (Join-Path $src 'GalHeadphoneDsp.cpp') + '" /link /OUT:"' + (Join-Path $build 'GalHeadphoneDspTests.exe') + '"'),
  ('cl /nologo /std:c++17 /EHsc /W4 /O2 /I"' + $sdk + '" "' + (Join-Path $PSScriptRoot 'tests\GalHeadphoneRegistrationTests.cpp') + '" /link /OUT:"' + (Join-Path $build 'GalHeadphoneRegistrationTests.exe') + '"'),
  ('"' + (Join-Path $build 'GalHeadphoneDspTests.exe') + '"'),
  ('"' + (Join-Path $build 'GalHeadphoneRegistrationTests.exe') + '" "' + (Join-Path $build 'AudioPluginGalHeadphones.dll') + '"')
) -join "`r`nif errorlevel 1 exit /b 1`r`n"
$commandFile = Join-Path $build 'build-native.cmd'
Set-Content -LiteralPath $commandFile -Value $commands -Encoding Ascii
cmd /d /c $commandFile
if ($LASTEXITCODE) { throw 'Native build or tests failed.' }
}
$artifact = Join-Path $PSScriptRoot 'artifacts\win-x64'
New-Item -ItemType Directory -Force $artifact | Out-Null
Copy-Item (Join-Path $build 'AudioPluginGalHeadphones.dll') $artifact -Force
Get-FileHash (Join-Path $artifact 'AudioPluginGalHeadphones.dll') -Algorithm SHA256
