$ErrorActionPreference = 'Stop'
# Reproducible build of the native DSP. The output depends only on the pinned
# toolchain, the pinned Unity SDK revision, the sources and the constants below,
# not on the repository path or the build time.
$root = Split-Path -Parent $PSScriptRoot
$deps = Join-Path $root '.deps\NativeAudioPlugins'
$sdkRevision = 'bc7893edbba4c8a592777e590e34f21a11d762b4'
# WinLibs GCC 16.1.0 + MinGW-w64 14.0.0 (UCRT, POSIX threads), release r4:
#   winget install BrechtSanders.WinLibs.POSIX.UCRT --version 16.1.0-14.0.0-r4
$toolchain = 'MinGW-W64 x86_64-ucrt-posix-seh, built by Brecht Sanders, r4) 16.1.0'
# PE timestamp written by the linker (SOURCE_DATE_EPOCH). Bump it when the DSP
# sources change. It must stay non-zero: a zero timestamp makes the DLL look like
# a hand-rolled loader to generic antivirus heuristics.
$sourceDateEpoch = '1791558000'
$imageBase = '0x180000000'

if (!(Test-Path (Join-Path $deps 'NativeCode\AudioPluginInterface.h'))) {
  New-Item -ItemType Directory -Force (Split-Path $deps) | Out-Null
  git clone https://github.com/Unity-Technologies/NativeAudioPlugins.git $deps
}
git -C $deps fetch origin $sdkRevision --depth 1
git -C $deps checkout --detach $sdkRevision
if ((git -C $deps rev-parse HEAD) -ne $sdkRevision) { throw 'Unity native audio SDK revision mismatch.' }

$build = Join-Path $PSScriptRoot 'build\win-x64'
New-Item -ItemType Directory -Force $build | Out-Null
$sdk = Join-Path $deps 'NativeCode'
$src = Join-Path $PSScriptRoot 'src'

$winget = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\BrechtSanders.WinLibs.POSIX.UCRT*') -Filter g++.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$compiler = if ($winget) { $winget.FullName } else { (Get-Command g++.exe -ErrorAction SilentlyContinue).Source }
if (!$compiler) { throw 'g++ not found. Install the pinned WinLibs toolchain (see the comment above).' }
$version = (& $compiler --version | Select-Object -First 1)
if ($version -notlike "*$toolchain*") { throw "Unsupported toolchain: '$version'. Expected $toolchain." }
$windres = Join-Path (Split-Path -Parent $compiler) 'windres.exe'
if (!(Test-Path $windres)) { throw 'windres.exe not found next to g++.' }

$env:SOURCE_DATE_EPOCH = $sourceDateEpoch
$dll = Join-Path $build 'AudioPluginGalHeadphones.dll'
$resource = Join-Path $build 'version.o'
& $windres -O coff -o $resource (Join-Path $src 'version.rc')
if ($LASTEXITCODE) { throw 'Version resource build failed.' }
& $compiler -std=c++17 -O2 -Wall -Wextra -shared -static `
  "-Wl,--subsystem,windows" "-Wl,--image-base=$imageBase" `
  -I $sdk -I $src (Join-Path $src 'GalHeadphoneAudioPlugin.cpp') (Join-Path $src 'GalHeadphoneDsp.cpp') $resource -o $dll
if ($LASTEXITCODE) { throw 'Native DLL build failed.' }
& $compiler -std=c++17 -O2 -Wall -Wextra -static -I $src (Join-Path $PSScriptRoot 'tests\GalHeadphoneDspTests.cpp') (Join-Path $src 'GalHeadphoneDsp.cpp') -o (Join-Path $build 'GalHeadphoneDspTests.exe')
if ($LASTEXITCODE) { throw 'Native DSP test build failed.' }
& $compiler -std=c++17 -O2 -Wall -Wextra -static -I $sdk (Join-Path $PSScriptRoot 'tests\GalHeadphoneRegistrationTests.cpp') -o (Join-Path $build 'GalHeadphoneRegistrationTests.exe')
if ($LASTEXITCODE) { throw 'Native registration test build failed.' }
& (Join-Path $build 'GalHeadphoneDspTests.exe')
if ($LASTEXITCODE) { throw 'Native DSP tests failed.' }
& (Join-Path $build 'GalHeadphoneRegistrationTests.exe') $dll
if ($LASTEXITCODE) { throw 'Native registration tests failed.' }

$artifact = Join-Path $PSScriptRoot 'artifacts\win-x64'
New-Item -ItemType Directory -Force $artifact | Out-Null
Copy-Item $dll $artifact -Force
Get-FileHash (Join-Path $artifact 'AudioPluginGalHeadphones.dll') -Algorithm SHA256
