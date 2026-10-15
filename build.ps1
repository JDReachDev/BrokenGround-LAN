# Builds the mod and the installer, optionally patches your local game, and creates
# dist\BrokenGround_LAN_v<version>.zip (the GitHub release).
#
#   .\build.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Broken Ground"
#   .\build.ps1 -NoPatch      # only build the release zip
param(
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\Broken Ground",
    [switch]$NoPatch
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$managed = Join-Path $GameDir "BrokenGround_Data\Managed"

$version = (Select-String -Path "$root\src\BGLan\Mod.cs" -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value
Write-Host "Broken Ground LAN $version"

# The mod compiles against the original (unpatched) game assembly, which the installer keeps as Assembly-CSharp.orig.dll.
if (-not (Test-Path "$managed\Assembly-CSharp.orig.dll")) {
    Copy-Item "$managed\Assembly-CSharp.dll" "$managed\Assembly-CSharp.orig.dll"
}

dotnet build "$root\src\BGLan\BGLan.csproj" -c Release --nologo -v q "-p:GameManaged=$managed"
if ($LASTEXITCODE -ne 0) { throw "Mod build failed" }
dotnet build "$root\src\Patcher\Patcher.csproj" -c Release -o "$root\build\patcher" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
Copy-Item "$root\src\BGLan\bin\Release\BGLan.dll" "$root\build\patcher\" -Force

if (-not $NoPatch) {
    if (Get-Process BrokenGround -ErrorAction SilentlyContinue) { throw "Close Broken Ground before patching (or use -NoPatch)" }
    "" | & "$root\build\patcher\BGLanPatcher.exe" $GameDir
    if ($LASTEXITCODE -ne 0) { throw "Patching failed" }
}

$stage = "$root\dist\BrokenGround_LAN_v$version"
Remove-Item "$root\dist" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
$files = "BGLanPatcher.exe", "BGLanPatcher.exe.config", "Mono.Cecil.dll", "Mono.Cecil.Rocks.dll", "BGLan.dll"
foreach ($f in $files) { Copy-Item "$root\build\patcher\$f" $stage }
Copy-Item "$root\release\*" $stage
Copy-Item "$root\CHANGELOG.md" "$stage\CHANGELOG.txt"
Copy-Item "$root\LICENSE" "$stage\LICENSE.txt"
(Get-Content "$stage\README.txt" -Raw).Replace("{VERSION}", $version) | Set-Content "$stage\README.txt" -NoNewline

$zip = "$root\dist\BrokenGround_LAN_v$version.zip"
# The zip contains a single folder with the files inside, not loose files.
Compress-Archive -Path $stage -DestinationPath $zip -Force
Write-Host "Release ready: $zip"
