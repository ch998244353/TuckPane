#requires -Version 5.1
[CmdletBinding()]
param(
    [DateTimeOffset]$At = [DateTimeOffset]::Now
)

# Read-only, on-demand collection. All output goes to the success stream.
# Deliberately do not read state files, note content, WER dumps or command lines.
$ErrorActionPreference = 'Stop'
$windowStart = $At.AddMinutes(-10)
$windowEnd = $At.AddMinutes(10)
$invariant = [Globalization.CultureInfo]::InvariantCulture

function Write-Section([string]$Title) {
    Write-Output ''
    Write-Output ('=== {0} ===' -f $Title)
}

function Write-Unavailable([string]$Category, $Failure) {
    Write-Output ('UNAVAILABLE [{0}]: {1}' -f $Category, $Failure.Exception.Message)
}

function Read-WindowEvents([string]$LogName, [int[]]$Ids) {
    try {
        Get-WinEvent -FilterHashtable @{
            LogName = $LogName
            Id = $Ids
            StartTime = $windowStart.LocalDateTime
            EndTime = $windowEnd.LocalDateTime
        } -ErrorAction Stop | Sort-Object TimeCreated, RecordId
    }
    catch {
        if ($_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') {
            Write-Output ('No matching event IDs in the {0} time window.' -f $LogName)
        }
        else {
            Write-Unavailable ($LogName + ' events') $_
        }
    }
}

function Get-EventFields($Event) {
    $fields = @{}
    $eventXml = [xml]$Event.ToXml()
    foreach ($datum in $eventXml.Event.EventData.Data) {
        if ($datum -is [System.Xml.XmlElement] -and $datum.HasAttribute('Name')) {
            $fields[$datum.GetAttribute('Name')] = $datum.InnerText
        }
    }
    return $fields
}

function Test-TuckPaneApplicationEvent($Event, [hashtable]$Fields) {
    if ($Event.Id -eq 1000 -and $Event.ProviderName -eq 'Application Error') {
        return ($Fields['AppName'] -ieq 'TuckPane.exe')
    }
    if ($Event.Id -eq 1001 -and $Event.ProviderName -eq 'Windows Error Reporting') {
        return ($Fields['P1'] -ieq 'TuckPane.exe')
    }
    if ($Event.Id -eq 1026 -and $Event.ProviderName -eq '.NET Runtime') {
        # The runtime payload starts with a localized "Application: name" line.
        # Match the exact executable, never substrings such as TuckPane.LogicChecks.exe.
        $eventXml = [xml]$Event.ToXml()
        foreach ($datum in $eventXml.Event.EventData.Data) {
            $payload = if ($datum -is [System.Xml.XmlElement]) { $datum.InnerText } else { [string]$datum }
            $firstLine = @($payload -split '\r?\n' | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
            if ($firstLine.Count -gt 0 -and $firstLine[0] -match '^\s*[^:\uFF1A\r\n]+[:\uFF1A]\s*TuckPane\.exe\s*$') {
                return $true
            }
        }
    }
    return $false
}

Write-Output 'TuckPane lifecycle diagnostic report (read-only)'
Write-Output ('Collected at: {0:O}' -f [DateTimeOffset]::Now)
Write-Output ('Incident: {0:O}; UTC: {1:O}' -f $At, $At.ToUniversalTime())
Write-Output ('Window: {0:O} through {1:O} (inclusive)' -f $windowStart, $windowEnd)
Write-Output ('Collector local time zone: {0}' -f [TimeZoneInfo]::Local.Id)

Write-Section 'Windows and CURRENT process snapshot'
try {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    Write-Output ('Windows: {0}; version={1}; build={2}; architecture={3}' -f $os.Caption, $os.Version, $os.BuildNumber, $os.OSArchitecture)
}
catch { Write-Unavailable 'Windows version' $_ }
try {
    $processes = @(Get-CimInstance -ClassName Win32_Process -Filter "Name='TuckPane.exe'")
    if ($processes.Count -eq 0) { Write-Output 'TuckPane.exe is not running at collection time.' }
    foreach ($process in $processes) {
        $processPath = if ($process.ExecutablePath) { $process.ExecutablePath } else { '<unavailable: access or process ended>' }
        Write-Output ('TuckPane.exe: pid={0}; created={1:O}; path={2}' -f $process.ProcessId, $process.CreationDate, $processPath)
    }
}
catch { Write-Unavailable 'current TuckPane process snapshot' $_ }

Write-Section 'Application lifecycle records'
if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
    Write-Output 'UNAVAILABLE [lifecycle logs]: LOCALAPPDATA is not set.'
}
else {
    foreach ($dataDirectory in @('TuckPane', 'GlassFolder')) {
        $logPath = Join-Path (Join-Path $env:LOCALAPPDATA $dataDirectory) 'TuckPane.log'
        Write-Output ('Log: {0}' -f $logPath)
        $stream = $null
        $reader = $null
        try {
            if (-not (Test-Path -LiteralPath $logPath -PathType Leaf)) {
                Write-Output 'Not present (this data root may not be used).'
                continue
            }
            $stream = [IO.File]::Open($logPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8, $true)
            $precedingStartup = $null
            $precedingStartupTime = [DateTimeOffset]::MinValue
            $records = [Collections.Generic.List[string]]::new()
            $unparsedCount = 0
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line -notmatch '^(?<time>\S+) \[LIFECYCLE\] (?<details>.*)$') { continue }
                $recordTime = [DateTimeOffset]::MinValue
                if (-not [DateTimeOffset]::TryParse($Matches['time'], $invariant, [Globalization.DateTimeStyles]::None, [ref]$recordTime)) {
                    $unparsedCount++
                    continue
                }
                if ($recordTime -lt $windowStart -and $recordTime -ge $precedingStartupTime -and $Matches['details'] -match '(?:^|\s)event=startup(?:\s|$)') {
                    $precedingStartup = $line
                    $precedingStartupTime = $recordTime
                }
                if ($recordTime -ge $windowStart -and $recordTime -le $windowEnd) { $records.Add($line) }
            }
            Write-Output 'Most recent startup before the window:'
            if ($precedingStartup) { Write-Output $precedingStartup } else { Write-Output '<not recorded in this log>' }
            Write-Output 'Lifecycle records inside the window:'
            if ($records.Count -gt 0) { $records | Write-Output } else { Write-Output '<none>' }
            if ($unparsedCount -gt 0) { Write-Output ('UNAVAILABLE [log timestamps]: {0} lifecycle lines could not be parsed.' -f $unparsedCount) }
        }
        catch { Write-Unavailable ('lifecycle log ' + $logPath) $_ }
        finally {
            if ($null -ne $reader) { $reader.Dispose() }
            elseif ($null -ne $stream) { $stream.Dispose() }
        }
    }
}

Write-Section 'TuckPane.exe Application events: 1000 / 1001 / 1026'
$applicationMatchCount = 0
foreach ($event in @(Read-WindowEvents 'Application' @(1000, 1001, 1026))) {
    if ($event -is [string]) { Write-Output $event; continue }
    try {
        $fields = Get-EventFields $event
        if (-not (Test-TuckPaneApplicationEvent $event $fields)) { continue }
        $applicationMatchCount++
        Write-Output ('{0:O} provider={1}; id={2}; record={3}; level={4}' -f $event.TimeCreated, $event.ProviderName, $event.Id, $event.RecordId, $event.LevelDisplayName)
        # Only known crash identity fields: do not print exception messages or arbitrary payloads.
        foreach ($key in @('AppName', 'AppVersion', 'ModuleName', 'ModuleVersion', 'ExceptionCode', 'FaultingOffset', 'ProcessId', 'AppPath', 'ModulePath', 'IntegratorReportId', 'EventName', 'P1', 'P2', 'P4', 'P5')) {
            if ($fields.ContainsKey($key)) { Write-Output ('  {0}={1}' -f $key, $fields[$key]) }
        }
        if ($event.Id -eq 1026) { Write-Output '  Application=TuckPane.exe; .NET Runtime termination recorded; full exception payload omitted.' }
    }
    catch { Write-Unavailable ('Application event record ' + $event.RecordId) $_ }
}
Write-Output ('Matched exact TuckPane.exe events: {0}. TuckPane.LogicChecks.exe is excluded.' -f $applicationMatchCount)

Write-Section 'System power / resume / session-ending events'
$systemSources = @{
    'Microsoft-Windows-Kernel-Power' = @(41, 42, 107, 109, 506, 507)
    'Microsoft-Windows-Power-Troubleshooter' = @(1)
    'Microsoft-Windows-Kernel-General' = @(12, 13)
    'User32' = @(1074)
    'EventLog' = @(6005, 6006, 6008)
}
$systemMatchCount = 0
foreach ($event in @(Read-WindowEvents 'System' @(1, 12, 13, 41, 42, 107, 109, 506, 507, 1074, 6005, 6006, 6008))) {
    if ($event -is [string]) { Write-Output $event; continue }
    if (-not $systemSources.ContainsKey($event.ProviderName) -or $event.Id -notin $systemSources[$event.ProviderName]) { continue }
    $systemMatchCount++
    Write-Output ('{0:O} provider={1}; id={2}; record={3}; level={4}' -f $event.TimeCreated, $event.ProviderName, $event.Id, $event.RecordId, $event.LevelDisplayName)
    try {
        if ($event.Message) { Write-Output $event.Message }
        else { Write-Output 'UNAVAILABLE [System event message]: provider description is missing.' }
    }
    catch { Write-Unavailable ('System event message ' + $event.RecordId) $_ }
}
Write-Output ('Matched System events: {0}.' -f $systemMatchCount)
Write-Output ''
Write-Output 'No application, desktop, registry, monitoring or configuration changes were made.'
Write-Output 'Missing records do not prove a clean exit or rule out a crash. Send the report with your incident description.'
