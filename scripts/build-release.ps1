[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '3.0.1'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsRoot = Join-Path $projectRoot 'artifacts'
$publishRoot = Join-Path $artifactsRoot 'publish\win-x64'
$releaseRoot = Join-Path $artifactsRoot 'release'
$shellArtifactsRoot = Join-Path $artifactsRoot 'shell-extension'
$webView2Root = Join-Path $artifactsRoot 'dependencies\webview2'
$webView2Installer = Join-Path $webView2Root 'MicrosoftEdgeWebView2RuntimeInstallerX64.exe'
$webView2DownloadUrl = 'https://go.microsoft.com/fwlink/?linkid=2124701'
$project = Join-Path $projectRoot 'src\TuckPane\TuckPane.csproj'
$installer = Join-Path $projectRoot 'installer\TuckPane.iss'

function Test-WebView2Installer([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }

    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    $signerName = if ($signature.SignerCertificate) {
        $signature.SignerCertificate.GetNameInfo(
            [Security.Cryptography.X509Certificates.X509NameType]::SimpleName,
            $false)
    }

    return $signature.Status -eq [Management.Automation.SignatureStatus]::Valid -and
        $signerName -eq 'Microsoft Corporation' -and
        $fileInfo.CompanyName -eq 'Microsoft Corporation' -and
        $fileInfo.ProductName -eq 'Microsoft Edge Update' -and
        $fileInfo.OriginalFilename -eq 'MicrosoftEdgeUpdateSetup.exe'
}

function Reset-BuildDirectory([string]$Path) {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifactsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear a path outside artifacts: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath) { Remove-Item -LiteralPath $resolvedPath -Recurse -Force }
    New-Item -ItemType Directory -Path $resolvedPath -Force | Out-Null
}

Reset-BuildDirectory $publishRoot
Reset-BuildDirectory $releaseRoot
Reset-BuildDirectory $shellArtifactsRoot

New-Item -ItemType Directory -Path $webView2Root -Force | Out-Null
if (-not (Test-WebView2Installer $webView2Installer)) {
    if (Test-Path -LiteralPath $webView2Installer) {
        Remove-Item -LiteralPath $webView2Installer -Force
    }
    $downloadPath = "$webView2Installer.download"
    if (Test-Path -LiteralPath $downloadPath) { Remove-Item -LiteralPath $downloadPath -Force }
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $webView2DownloadUrl -OutFile $downloadPath
        if (-not (Test-WebView2Installer $downloadPath)) {
            throw 'Downloaded WebView2 installer is not valid Microsoft Edge Update code.'
        }
        Move-Item -LiteralPath $downloadPath -Destination $webView2Installer -Force
    }
    finally {
        if (Test-Path -LiteralPath $downloadPath) { Remove-Item -LiteralPath $downloadPath -Force }
    }
}

if (-not (Test-WebView2Installer $webView2Installer)) {
    throw 'Cached WebView2 installer failed validation.'
}

dotnet restore $project --locked-mode -p:Platform=x64
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

dotnet publish $project `
    -c Release `
    --no-restore `
    -p:Platform=x64 `
    -p:RuntimeIdentifier=win-x64 `
    -p:SelfContained=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -o $publishRoot
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

$requiredFiles = @(
    'TuckPane.exe',
    'TuckPane.dll',
    'TuckPane.pri',
    'hostfxr.dll',
    'Microsoft.WindowsAppRuntime.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.Core.Projection.dll',
    'WebView2Loader.dll'
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot $requiredFile))) {
        throw "Publish is incomplete. Missing: $requiredFile"
    }
}

$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publishRoot 'TuckPane.exe'))
if ($fileVersion.FileVersion -ne "$Version.0" -or $fileVersion.ProductName -ne 'TuckPane') {
    throw "Unexpected executable metadata: $($fileVersion.FileVersion), $($fileVersion.ProductName)"
}

Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md') -Destination $publishRoot
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $publishRoot -Recurse

$portableLauncher = Join-Path $publishRoot '00-启动 TuckPane.exe'
Copy-Item -LiteralPath (Join-Path $publishRoot 'TuckPane.exe') -Destination $portableLauncher
if ((Get-FileHash -LiteralPath $portableLauncher -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $publishRoot 'TuckPane.exe') -Algorithm SHA256).Hash) {
    throw 'Portable launcher does not match TuckPane.exe.'
}

$privateArtifacts = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
    $_.Extension -eq '.pdb' -or $_.Name -in @('state.json', 'state.json.bak') -or $_.Extension -eq '.log'
})
if ($privateArtifacts.Count -gt 0) {
    throw "Private/debug files entered the package: $($privateArtifacts.FullName -join ', ')"
}

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }
$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 is required to build the offline installer.' }

& $iscc `
    "/DMyAppVersion=$Version" `
    "/DPublishDir=$publishRoot" `
    "/DOutputDir=$releaseRoot" `
    "/DWebView2Installer=$webView2Installer" `
    $installer
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

$setupPath = Join-Path $releaseRoot "TuckPane-$Version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $setupPath)) { throw "Installer was not created: $setupPath" }

$portablePath = Join-Path $releaseRoot "TuckPane-$Version-win-x64-portable.zip"
Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $portablePath -CompressionLevel Optimal

$hashPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$hashLines = @($setupPath, $portablePath) | ForEach-Object {
    $hash = Get-FileHash -LiteralPath $_ -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $(Split-Path $_ -Leaf)"
}
[IO.File]::WriteAllLines($hashPath, $hashLines, [Text.UTF8Encoding]::new($false))

Get-ChildItem -LiteralPath $releaseRoot -File | Select-Object Name, Length, LastWriteTime
