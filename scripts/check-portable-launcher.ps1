[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checks = Join-Path $projectRoot 'artifacts\checks\portable-launcher'
dotnet publish (Join-Path $projectRoot 'tests\TuckPane.LauncherChecks\TuckPane.LauncherChecks.csproj') -c Release -o $checks
if ($LASTEXITCODE -ne 0) { throw 'Launcher check build failed.' }
& (Join-Path $checks 'TuckPane.LauncherChecks.exe') ([IO.Path]::GetFullPath($PublishDirectory))
if ($LASTEXITCODE -ne 0) { throw 'Portable launcher checks failed.' }
