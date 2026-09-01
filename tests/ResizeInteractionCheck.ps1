param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$resolvedExe = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $resolvedExe)) { throw "Executable not found: $resolvedExe" }

$runRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\interaction-runs\$([Guid]::NewGuid().ToString('N'))"
$localRoot = Join-Path $runRoot 'LocalAppData\TuckPane'
$itemsRoot = Join-Path $runRoot 'UserProfile\TuckPane\Windows\ResizeProbe-22222222\Items'
New-Item -ItemType Directory -Path $localRoot, $itemsRoot -Force | Out-Null

@'
{
  "SchemaVersion": 7,
  "GlobalSettings": { "ThemeColorArgb": 4293060073, "Material": 0, "ThemeTransparency": 0.35, "StartWithWindows": false, "Language": 0 },
  "ConsolePlacement": null,
  "Organizers": [
    {
      "Id": "22222222-2222-2222-2222-222222222222",
      "Name": "ResizeProbe",
      "CreatedAtUtc": "2026-08-23T00:00:00+00:00",
      "PlacementMode": 0,
      "Layout": { "Mode": 0, "Rows": 3, "Columns": 3 },
      "CompactScale": 0.8,
      "CanvasScale": 0.7,
      "ItemScale": 1.0,
      "NameScale": 1.0,
      "Position": null,
      "StorageRelativePath": "Windows\\ResizeProbe-22222222\\Items",
      "StorageAbsolutePath": null,
      "ItemOrder": []
    }
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $localRoot 'state.json') -Encoding UTF8

$env:TUCKPANE_TEST_ROOT = $runRoot
$env:GLASSFOLDER_TEST_EXPANDED = '1'
$env:TUCKPANE_TEST_RESIZE_AUTORUN = '1'
$resultPath = Join-Path $localRoot 'resize-probe.json'
$probeProcess = $null
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Get-RunValue([string]$Name) {
    $property = (Get-ItemProperty -LiteralPath $runKey).PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return [string]$property.Value
}

$startupBefore = Get-RunValue 'TuckPane'
$legacyStartupBefore = Get-RunValue 'GlassFolder'

try {
    $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    while ([DateTime]::UtcNow -lt $deadline -and -not (Test-Path -LiteralPath $resultPath)) {
        Start-Sleep -Milliseconds 100
        if ($probeProcess.HasExited) { throw 'TuckPane exited before the resize probe completed.' }
    }
    if (-not (Test-Path -LiteralPath $resultPath)) { throw 'Resize probe timed out.' }

    $result = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $result | Format-List
    if (-not $result.Passed) {
        Get-Content -LiteralPath (Join-Path $localRoot 'TuckPane.log') -Tail 80 -ErrorAction SilentlyContinue
        throw 'Resize interaction regression check failed.'
    }
}
finally {
    if ($null -ne $probeProcess -and -not $probeProcess.HasExited) {
        Stop-Process -Id $probeProcess.Id -ErrorAction SilentlyContinue
        Wait-Process -Id $probeProcess.Id -ErrorAction SilentlyContinue
    }
    if ((Get-RunValue 'TuckPane') -ne $startupBefore -or
        (Get-RunValue 'GlassFolder') -ne $legacyStartupBefore) {
        throw 'The isolated resize probe changed the real startup registry values.'
    }
}
