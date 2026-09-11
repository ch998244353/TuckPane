[CmdletBinding()]
param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot '..\..\app\current\TuckPane.exe')
)

$ErrorActionPreference = 'Stop'
$animationExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$animationDirectory = Split-Path -Parent $animationExecutable
if ($env:TUCKPANE_TEST_ROOT) {
    throw 'TUCKPANE_TEST_ROOT is set. Use a normal PowerShell session to collect the existing configuration baseline.'
}

# A second instance cannot enable diagnostics in an already running process.
# Do not stop anything: the user owns all application exit/start operations.
$animationRunning = @(Get-Process | Where-Object {
    try {
        $_.ProcessName -eq 'TuckPane' -or
            ($_.Path -and (Split-Path -Parent $_.Path) -eq $animationDirectory)
    }
    catch { $false }
})
if ($animationRunning.Count -gt 0) {
    throw 'TuckPane is still running. Exit it normally from the tray, then run this script again.'
}

$animationPreviousTrace = $env:TUCKPANE_PERF_TRACE
try {
    $env:TUCKPANE_PERF_TRACE = '1'
    Start-Process -FilePath $animationExecutable -WorkingDirectory $animationDirectory -WindowStyle Hidden
    Write-Host 'Animation diagnostics enabled for this launch only. Open/collapse an ordinary pane and a Station three times each.'
    Write-Host 'Keep content, theme, performance profile and monitor unchanged. Then report whether the stutter remains.'
}
finally {
    $env:TUCKPANE_PERF_TRACE = $animationPreviousTrace
}
