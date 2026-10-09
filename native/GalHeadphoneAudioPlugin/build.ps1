$ErrorActionPreference = 'Stop'
# Reproducible build of the native DSP. The output depends only on the pinned
# toolchain, the pinned Unity SDK revision and the sources, not on the repository
# path or the build time.
$root = Split-Path -Parent $PSScriptRoot
$deps = Join-Path $root '.deps\NativeAudioPlugins'
$sdkRevision = 'bc7893edbba4c8a592777e590e34f21a11d762b4'
# zig (clang/LLVM + LLD with the bundled mingw-w64 runtime):
#   winget install zig.zig --version 0.17.0 --scope user
$zigVersion = '0.17.0'
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

$winget = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\zig.zig*') -Filter zig.exe -Recurse -ErrorAction SilentlyContinue |
  Where-Object { $_.DirectoryName -like "*zig-x86_64-windows-$zigVersion*" } | Select-Object -First 1
$zig = if ($winget) { $winget.FullName } else { (Get-Command zig.exe -ErrorAction SilentlyContinue).Source }
if (!$zig) { throw "zig not found. Install zig $zigVersion (see the comment above)." }
$version = (& $zig version)
if ($version -ne $zigVersion) { throw "Unsupported zig version '$version'. Expected $zigVersion." }

# -g0 and --strip-all keep debug data and local paths out of the binary;
# -fno-exceptions -fno-rtti keep the C++ runtime out of the link.
$common = @('-target', 'x86_64-windows-gnu', '-std=c++17', '-O2', '-Wall', '-Wextra', '-g0', '-fno-exceptions', '-fno-rtti')
# zig compiles its bundled runtime on first use and occasionally fails to open one
# of its own files while checking that cache ("checking cache failed: file_open");
# a second attempt succeeds, so each invocation gets a few tries.
function Invoke-Zig([string]$What, [string[]]$Arguments) {
  for ($attempt = 1; $attempt -le 3; $attempt++) {
    & $zig c++ @Arguments
    if (!$LASTEXITCODE) { return }
    Write-Warning "$What failed (attempt $attempt of 3)."
  }
  throw "$What failed."
}
$dll = Join-Path $build 'GalHeadphoneElectronics.dll'
Invoke-Zig 'Native DLL build' ($common + @('-shared', '-I', $sdk, '-I', $src,
  (Join-Path $src 'GalHeadphoneAudioPlugin.cpp'), (Join-Path $src 'GalHeadphoneDsp.cpp'), (Join-Path $src 'version.rc'),
  '-Wl,--subsystem,windows', "-Wl,--image-base=$imageBase", '-Wl,--strip-all', '-o', $dll))
Remove-Item (Join-Path $build 'GalHeadphoneElectronics.lib') -ErrorAction SilentlyContinue
Invoke-Zig 'Native DSP test build' ($common + @('-I', $src,
  (Join-Path $PSScriptRoot 'tests\GalHeadphoneDspTests.cpp'), (Join-Path $src 'GalHeadphoneDsp.cpp'),
  '-o', (Join-Path $build 'GalHeadphoneDspTests.exe')))
Invoke-Zig 'Native registration test build' ($common + @('-I', $sdk,
  (Join-Path $PSScriptRoot 'tests\GalHeadphoneRegistrationTests.cpp'),
  '-o', (Join-Path $build 'GalHeadphoneRegistrationTests.exe')))
& (Join-Path $build 'GalHeadphoneDspTests.exe')
if ($LASTEXITCODE) { throw 'Native DSP tests failed.' }
& (Join-Path $build 'GalHeadphoneRegistrationTests.exe') $dll
if ($LASTEXITCODE) { throw 'Native registration tests failed.' }

$artifact = Join-Path $PSScriptRoot 'artifacts\win-x64'
New-Item -ItemType Directory -Force $artifact | Out-Null
Copy-Item $dll $artifact -Force
Get-FileHash (Join-Path $artifact 'GalHeadphoneElectronics.dll') -Algorithm SHA256
