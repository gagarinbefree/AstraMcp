<#
.SYNOPSIS
Configures the current user's verified legacy Astra command-line profile for MCP.
.PARAMETER ConfigurationFile
Path to an MCP configuration file; relative paths resolve against the repository.
#>
[CmdletBinding()]
param([string] $ConfigurationFile = 'config.json')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$configurationPath = [IO.Path]::GetFullPath($ConfigurationFile, $root)
$configurationDirectory = Split-Path -Parent $configurationPath
$configuration = (Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json).Astra
$executable = [IO.Path]::GetFullPath($configuration.ExecutablePath, $configurationDirectory)
$dataDirectory = [IO.Path]::GetFullPath($configuration.DataDirectory, $configurationDirectory)
$exportDirectory = [IO.Path]::GetFullPath($configuration.ExportDirectory, $configurationDirectory)
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The configured Astra executable does not exist.' }
if (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($executable)) -ErrorAction SilentlyContinue | Where-Object Path -EQ $executable) {
    throw 'Finish active Astra calculations before changing its command-line profile.'
}
$settingsPath = 'HKCU:\Software\Technos\Астра Раскрой\Settings'
$parametersPath = $settingsPath + '\Parameters'
if (-not (Test-Path -LiteralPath $parametersPath)) { throw 'The verified legacy Astra profile was not found. Configure this installation through its UI first.' }
$settings = Get-ItemProperty -LiteralPath $settingsPath
$parameters = Get-ItemProperty -LiteralPath $parametersPath
$databaseDirectory = [IO.Path]::GetFullPath((Join-Path $dataDirectory 'astra-profile/database'))
New-Item -ItemType Directory -Path $databaseDirectory, $exportDirectory -Force | Out-Null
# Retain existing test catalogs and seed only missing catalogs from the previous installation profile.
foreach ($name in 'list_materials.xml', 'list_edges.xml', 'list_operation.xml', 'type_materials.xml', 'type_edges.xml', 'type_parts.xml') {
    $source = Join-Path $settings.PathDataBase $name
    $destination = Join-Path $databaseDirectory $name
    if (-not (Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $source)) { Copy-Item -LiteralPath $source -Destination $destination }
}
$evidenceDirectory = Join-Path $root '.cache/profile-configuration'
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
$evidencePath = Join-Path $evidenceDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json')
$before = [ordered]@{
    PathDataBase=$settings.PathDataBase; PathDataEx=$settings.PathDataEx
    ExportExit=$parameters.ExportExit; AutoSheetFromStock=$parameters.AutoSheetFromStock
    AdviceNo=$parameters.AdviceNo; NestTrouble=$parameters.NestTrouble
}
$before | ConvertTo-Json | Set-Content -LiteralPath $evidencePath -Encoding utf8
Set-ItemProperty -LiteralPath $settingsPath -Name PathDataBase -Value ($databaseDirectory.TrimEnd('\', '/') + '\')
Set-ItemProperty -LiteralPath $settingsPath -Name PathDataEx -Value ($exportDirectory.TrimEnd('\', '/') + '\')
Set-ItemProperty -LiteralPath $parametersPath -Name ExportExit -Value 1
Set-ItemProperty -LiteralPath $parametersPath -Name AutoSheetFromStock -Value 0
Set-ItemProperty -LiteralPath $parametersPath -Name AdviceNo -Value 1
# A partial layout is a valid result; its unplaced demand is checked by the adapter instead of a modal warning.
Set-ItemProperty -LiteralPath $parametersPath -Name NestTrouble -Value 1
Write-Output ('Astra command-line export directory: ' + $exportDirectory)
Write-Output ('Previous profile values: ' + $evidencePath)
