[CmdletBinding()]
param(
    [string]$ProductionPublish = (Join-Path $PSScriptRoot '..\artifacts\publish\v4.0.0')
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$productionRoot = [IO.Path]::GetFullPath($ProductionPublish)
$outputBase = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\update-validation'))
$runName = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$runRoot = [IO.Path]::GetFullPath((Join-Path $outputBase $runName))
if (-not $runRoot.StartsWith($outputBase.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation output must remain inside artifacts/update-validation.'
}
if (Test-Path -LiteralPath $runRoot) { throw 'Refusing to overwrite an existing validation run.' }
foreach ($name in @('TuckPane.Updater.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses')) {
    if (-not (Test-Path -LiteralPath (Join-Path $productionRoot $name))) { throw "Missing production dependency: $name" }
}
$webView2 = Join-Path $projectRoot 'artifacts\dependencies\webview2\MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
if (-not (Test-Path -LiteralPath $webView2)) { throw 'Build the production candidate first; its WebView2 dependency is required.' }
$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required.' }
New-Item -ItemType Directory -Path $runRoot | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)

function Write-JsonFile([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), $utf8)
}

function Write-Manifest([string]$Directory, [string]$Version) {
    $files = [ordered]@{}
    Get-ChildItem -LiteralPath $Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Directory, $_.FullName).Replace('\', '/')
        if ($relative -ne 'update-files.json') {
            if ($_.Extension -in @('.pdb', '.log') -or $_.Name -like 'state.json*') { throw "Unexpected private file: $relative" }
            $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    Write-JsonFile (Join-Path $Directory 'update-files.json') @{ Version = $Version; Files = $files }
}

function Copy-Directory([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { throw "Refusing to overwrite $Destination" }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse
}

$project = Join-Path $projectRoot 'src\TuckPane\TuckPane.csproj'
$setupRoot = Join-Path $runRoot 'setups'
New-Item -ItemType Directory -Path $setupRoot | Out-Null
foreach ($version in @('3.0.9', '3.1.0')) {
    $publish = Join-Path $runRoot "publish-$version"
    # These are distinct validation versions. Run after the production build, not concurrently.
    dotnet publish $project -c Release --no-restore -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
        -p:SelfContained=true -p:WindowsAppSDKSelfContained=true -p:UpdateValidation=true `
        "-p:Version=$version" "-p:AssemblyVersion=$version.0" "-p:FileVersion=$version.0" `
        -p:DebugSymbols=false -p:DebugType=None -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Validation publish failed: $version" }
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'TuckPane.exe')).FileVersion -ne "$version.0") {
        throw "Validation executable has the wrong version: $version"
    }
    foreach ($name in @('TuckPane.Updater.exe', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $productionRoot $name) -Destination $publish -Force
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publish 'licenses'))) {
        Copy-Item -LiteralPath (Join-Path $productionRoot 'licenses') -Destination $publish -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $publish 'TuckPane.exe') -Destination (Join-Path $publish '00-启动 TuckPane.exe')
    Write-Manifest $publish $version
    $installed = Join-Path $runRoot "installed-payload-$version"
    Copy-Directory $publish $installed
    [IO.File]::WriteAllText((Join-Path $installed 'validation-installed'), 'isolated update validation', $utf8)
    Write-Manifest $installed $version
    & $iscc '/DUpdateValidation' "/DMyAppVersion=$version" "/DPublishDir=$installed" `
        "/DOutputDir=$setupRoot" "/DWebView2Installer=$webView2" (Join-Path $projectRoot 'installer\TuckPane.iss')
    if ($LASTEXITCODE -ne 0) { throw "Validation installer build failed: $version" }
}

$portableZip = Join-Path $runRoot 'TuckPane-3.1.0-win-x64-portable.zip'
Compress-Archive -Path (Join-Path $runRoot 'publish-3.1.0\*') -DestinationPath $portableZip -CompressionLevel Optimal
Copy-Directory (Join-Path $runRoot 'publish-3.0.9') (Join-Path $runRoot 'portable-app')
[IO.File]::WriteAllText((Join-Path $runRoot 'portable-app\user-extra.txt'), 'Keep this extra file unchanged during the update.', $utf8)

$assets = @(
    (Join-Path $setupRoot 'TuckPane-3.1.0-win-x64-setup.exe'),
    $portableZip
)
$hashLines = @($assets | ForEach-Object {
    ((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()) + '  ' + [IO.Path]::GetFileName($_)
})
$checksumPath = Join-Path $runRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllLines($checksumPath, $hashLines, $utf8)
$feedAssets = @($assets + $checksumPath | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    @{
        name = $item.Name
        browser_download_url = 'https://github.com/ch998244353/TuckPane/releases/download/v3.1.0/' + $item.Name
        size = $item.Length
        digest = 'sha256:' + (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
foreach ($edition in @('portable', 'installed')) {
    $feed = Join-Path $runRoot "data\$edition\update-feed"
    New-Item -ItemType Directory -Path $feed -Force | Out-Null
    foreach ($asset in $assets + $checksumPath) { Copy-Item -LiteralPath $asset -Destination $feed }
    Write-JsonFile (Join-Path $feed 'release.json') @{
        tag_name = 'v3.1.0'; draft = $false; prerelease = $false
        body = 'Isolated update validation: 3.0.9 -> 3.1.0. This is not a published GitHub release.'
        assets = $feedAssets
    }
}

# The generated scripts launch GUI only when the user explicitly runs them.
# EnvironmentVariables changes affect only the new process and its descendants.
$launcher = @'
$ErrorActionPreference = 'Stop'
$edition = '__EDITION__'
$dataRoot = Join-Path $PSScriptRoot ('data\' + $edition)
$target = Join-Path $PSScriptRoot $(if ($edition -eq 'installed') { 'installed-app' } else { 'portable-app' })
function Start-ValidationProcess([string]$File, [string]$Arguments, [bool]$Wait) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $File
    $start.Arguments = $Arguments
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $PSScriptRoot
    $start.EnvironmentVariables['TUCKPANE_TEST_ROOT'] = $dataRoot
    $process = [Diagnostics.Process]::Start($start)
    if ($null -eq $process) { throw 'Could not start validation process.' }
    if ($Wait) {
        $process.WaitForExit()
        $code = $process.ExitCode
        $process.Dispose()
        if ($code -eq 3010) { throw 'Installation needs a Windows restart. Restart before running this launcher again.' }
        if ($code -ne 0) { throw ('Validation installer failed: ' + $code + '. See initial-install.log.') }
    } else { $process.Dispose() }
}
if ($edition -eq 'installed' -and -not (Test-Path -LiteralPath (Join-Path $target 'TuckPane.exe'))) {
    if (Test-Path -LiteralPath $target) { throw 'Partial installation target exists; keep it for diagnosis and use a fresh validation run.' }
    $installer = Join-Path $PSScriptRoot 'setups\TuckPane-3.0.9-win-x64-setup.exe'
    $arguments = '/SP- /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /RESTARTEXITCODE=3010 /DIR="' + $target + '" /LOG="' + (Join-Path $PSScriptRoot 'initial-install.log') + '"'
    Start-ValidationProcess $installer $arguments $true
}
if ($edition -eq 'installed' -and -not (Test-Path -LiteralPath (Join-Path $target 'validation-installed'))) {
    throw 'The target is not an isolated installation validation copy.'
}
Start-ValidationProcess (Join-Path $target 'TuckPane.exe') '' $false
'@
foreach ($edition in @('portable', 'installed')) {
    # UTF-8 BOM allows the built-in Windows PowerShell launcher to read non-ASCII paths safely.
    [IO.File]::WriteAllText((Join-Path $runRoot "start-$edition.ps1"), $launcher.Replace('__EDITION__', $edition), [Text.UTF8Encoding]::new($true))
    $cmd = "@echo off`r`npowershell.exe -NoProfile -ExecutionPolicy Bypass -File `"%~dp0start-$edition.ps1`"`r`nif errorlevel 1 pause`r`n"
    [IO.File]::WriteAllText((Join-Path $runRoot "start-$edition.cmd"), $cmd, [Text.Encoding]::ASCII)
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\UPDATE_HANDTEST.zh-CN.md') -Destination $runRoot
Write-JsonFile (Join-Path $runRoot 'validation-build.json') @{
    createdUtc = [DateTimeOffset]::UtcNow.ToString('o')
    productionPublish = $productionRoot
    fromVersion = '3.0.9'; toVersion = '3.1.0'
    updaterSha256 = (Get-FileHash -LiteralPath (Join-Path $productionRoot 'TuckPane.Updater.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
    guiExecutedByBuild = $false
}
Write-Output "Validation bundle: $runRoot"
Write-Output 'User launchers: start-portable.cmd and start-installed.cmd. No application or installer has been run.'
