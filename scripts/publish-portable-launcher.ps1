[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PublishDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
$launcherRoot = Join-Path $projectRoot 'artifacts\publish\portable-launcher'
$project = Join-Path $projectRoot 'src\TuckPane.Launcher\TuckPane.Launcher.csproj'
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'TuckPane.exe'))) {
    throw 'Publish the application before adding its launcher.'
}
dotnet publish $project -c Release -o $launcherRoot
if ($LASTEXITCODE -ne 0) { throw 'Portable launcher publish failed.' }

$launcherFiles = @('TuckPane.Launcher.exe', 'TuckPane.Launcher.dll',
    'TuckPane.Launcher.deps.json', 'TuckPane.Launcher.runtimeconfig.json')
foreach ($name in $launcherFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $launcherRoot $name) -PathType Leaf)) {
        throw "Launcher publish is incomplete: $name"
    }
}
# Both self-contained projects use the same SDK/runtime. Never overwrite the application's runtime.
foreach ($file in Get-ChildItem -LiteralPath $launcherRoot -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($launcherRoot, $file.FullName)
    if ($relative -in $launcherFiles) { continue }
    $existing = Join-Path $publishRoot $relative
    if (-not (Test-Path -LiteralPath $existing -PathType Leaf) -or
        (Get-FileHash -LiteralPath $existing).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
        throw "Launcher runtime does not match the application: $relative"
    }
}
foreach ($name in $launcherFiles) {
    $destination = if ($name -eq 'TuckPane.Launcher.exe') { '00-启动 TuckPane.exe' } else { $name }
    Copy-Item -LiteralPath (Join-Path $launcherRoot $name) -Destination (Join-Path $publishRoot $destination) -Force
}
if ((Get-FileHash -LiteralPath (Join-Path $publishRoot '00-启动 TuckPane.exe')).Hash -eq
    (Get-FileHash -LiteralPath (Join-Path $publishRoot 'TuckPane.exe')).Hash) {
    throw 'The portable entry must be a launcher, not a renamed application.'
}
