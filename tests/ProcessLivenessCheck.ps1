param(
    [int]$DurationSeconds = 12,
    [string]$ExecutablePath = (Join-Path $PSScriptRoot '..\..\app\current\TuckPane.exe')
)

$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ("TuckPane-liveness-{0}" -f ([Guid]::NewGuid().ToString('N')))
$localRoot = Join-Path $root 'LocalAppData\TuckPane'
New-Item -ItemType Directory -Force -Path $root | Out-Null
$process = $null
try {
    $env:TUCKPANE_TEST_ROOT = $root
    $process = Start-Process -FilePath (Resolve-Path $ExecutablePath) -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds($DurationSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
        if ($process.HasExited) {
            $log = Join-Path $localRoot 'TuckPane.log'
            $tail = if (Test-Path $log) { (Get-Content $log -Tail 40) -join [Environment]::NewLine } else { '<no isolated log>' }
            throw "TuckPane exited early with code $($process.ExitCode).`n$tail"
        }
    }
    $logPath = Join-Path $localRoot 'TuckPane.log'
    $recentErrors = if (Test-Path $logPath) {
        @(Get-Content $logPath | Where-Object { $_ -match '\[ERROR\]|Unhandled UI exception' })
    } else { @() }
    if ($recentErrors.Count -gt 0) { throw "Isolated run logged errors:`n$($recentErrors -join [Environment]::NewLine)" }
    [pscustomobject]@{ DurationSeconds = $DurationSeconds; ProcessId = $process.Id; RecentErrors = 0 }
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item Env:TUCKPANE_TEST_ROOT -ErrorAction SilentlyContinue
}
