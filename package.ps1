# Builds RunSaves and creates the Thunderstore zip in dist\.
# Usage:  .\package.ps1 -GameDir "D:\SteamLibrary\steamapps\common\The Spell Brigade"
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\The Spell Brigade",
    [string]$BepInExDir = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$props = @("-c", "Release", "-p:GameDir=$GameDir")
if ($BepInExDir) { $props += "-p:BepInExDir=$BepInExDir" }
dotnet build src\RunSaves.csproj @props
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$manifest = Get-Content thunderstore\manifest.json -Raw | ConvertFrom-Json
$version = $manifest.version_number
$stage = "dist\stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$stage\plugins" | Out-Null
Copy-Item thunderstore\manifest.json, thunderstore\icon.png, thunderstore\README.md, thunderstore\CHANGELOG.md $stage
Copy-Item src\bin\Release\net6.0\RunSaves.dll "$stage\plugins\"

$zip = "dist\RunSaves-$version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "Created $zip"
