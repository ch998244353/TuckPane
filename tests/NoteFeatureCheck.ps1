param(
    [string]$AppExe = (Join-Path $PSScriptRoot '..\src\TuckPane\bin\x64\Release\net10.0-windows10.0.22621.0\TuckPane.exe'),
    [switch]$TitleDragOnly,
    [switch]$ActivationOnly,
    [switch]$PortableNoteOnly,
    [switch]$RuledLinesOnly,
    [switch]$ChromeOnly,
    [switch]$NotePolishOnly,
    [switch]$ScrollStationOnly,
    [switch]$KeepRoot
)

$ErrorActionPreference = 'Stop'
$resolvedExe = (Resolve-Path -LiteralPath $AppExe).Path
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('TuckPane-note-ui-' + [Guid]::NewGuid().ToString('N'))
$storage = Join-Path $runRoot 'UserProfile\TuckPane\Windows\NoteProbe'
$targetStorage = Join-Path $runRoot 'UserProfile\TuckPane\Windows\TargetProbe'
$local = Join-Path $runRoot 'LocalAppData\TuckPane'
$statePath = Join-Path $local 'state.json'
$originalRoot = $env:TUCKPANE_TEST_ROOT
$originalExpanded = $env:GLASSFOLDER_TEST_EXPANDED
$originalClipboardText = Get-Clipboard -Raw -ErrorAction SilentlyContinue
$app = $null

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TuckPaneNoteInput {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, int data, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("shcore.dll")] public static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll", EntryPoint="DwmGetWindowAttribute")] public static extern int DwmGetWindowRectAttribute(IntPtr window, int attribute, out Rect value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    public static void MoveAbsolute(int x, int y) {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        uint absoluteX = (uint)Math.Round((x - left) * 65535d / Math.Max(1, width - 1));
        uint absoluteY = (uint)Math.Round((y - top) * 65535d / Math.Max(1, height - 1));
        mouse_event(0xC001, absoluteX, absoluteY, 0, UIntPtr.Zero);
    }
}
'@
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
if ($NotePolishOnly) { Add-Type -AssemblyName UIAutomationClient }
$primaryDevice = [Windows.Forms.Screen]::PrimaryScreen.DeviceName

function Wait-ForCondition([scriptblock]$Condition, [string]$Failure, [int]$TimeoutMs = 8000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $Failure
}

function Get-AppWindows([int]$ProcessId) {
    return @(winapp ui list-windows -a $ProcessId --json 2>$null | ConvertFrom-Json)
}

function Get-State {
    return Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
}

function Set-ProbeForeground([long]$Hwnd) {
    [TuckPaneNoteInput]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [TuckPaneNoteInput]::BringWindowToTop([IntPtr]$Hwnd) | Out-Null
    [TuckPaneNoteInput]::SetForegroundWindow([IntPtr]$Hwnd) | Out-Null
    [TuckPaneNoteInput]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 100
}

function Invoke-MouseDrag([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY) {
    [TuckPaneNoteInput]::MoveAbsolute($FromX, $FromY)
    Start-Sleep -Milliseconds 150
    [TuckPaneNoteInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    foreach ($step in 1..32) {
        [TuckPaneNoteInput]::MoveAbsolute(
            [int]($FromX + ($ToX - $FromX) * $step / 32),
            [int]($FromY + ($ToY - $FromY) * $step / 32))
        Start-Sleep -Milliseconds 20
    }
    Start-Sleep -Milliseconds 1600
    [TuckPaneNoteInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-UiDescendants($Nodes) {
    foreach ($node in @($Nodes)) {
        $node
        if ($null -ne $node.children) { Get-UiDescendants $node.children }
    }
}

function Open-NoteContextMenu([long]$MainHwnd, [string]$Selector) {
    Set-ProbeForeground $MainHwnd
    winapp ui focus CollapseButton -w $MainHwnd | Out-Null
    winapp ui click $Selector -w $MainHwnd --right | Out-Null
    winapp ui wait-for RenameNoteMenuItem -w $MainHwnd --timeout 3000 | Out-Null
}

function Test-NoteThemeChecked([long]$Hwnd, [string]$Theme) {
    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Hwnd)
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            "NoteTheme-$Theme")
        $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $element) { return $false }
        $toggle = [System.Windows.Automation.TogglePattern]$element.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern)
        return $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    }
    catch { return $false }
}

function Assert-NoteTheme([long]$Hwnd, [string]$ColorId, [string]$Theme) {
    winapp ui invoke $ColorId -w $Hwnd | Out-Null
    Wait-ForCondition { Test-NoteThemeChecked $Hwnd $Theme } "The open note did not adopt the $Theme theme."
    winapp ui send-keys escape -w $Hwnd | Out-Null
}

try {
    [IO.Directory]::CreateDirectory($storage) | Out-Null
    [IO.Directory]::CreateDirectory($local) | Out-Null
    $state = @{
        SchemaVersion = 5
        GlobalSettings = @{
            ThemeColorArgb = 4293060073; Material = 0; ThemeTransparency = 0.35
            StartWithWindows = $false; Language = 0
            CollapseOnOutsideClick = $false; ExpandOnHover = $false; CollapseOnPointerLeave = $false
        }
        ConsolePlacement = $null
        Organizers = @(@{
            Id = '77777777-7777-7777-7777-777777777777'
            Name = 'NoteProbe'; CreatedAtUtc = [DateTimeOffset]::UtcNow
            PlacementMode = 0; DockEdge = 2
            Layout = @{ Mode = 0; Rows = 2; Columns = 2 }
            CompactScale = 1.0; CanvasScale = 0.7; ItemScale = 1.0; NameScale = 1.0
            ManualCanvasBaseWidthDip = $null; ManualCanvasBaseHeightDip = $null; Position = $null
            StorageRelativePath = 'Windows\NoteProbe'; StorageAbsolutePath = $null
            ItemOrder = @(); Notes = @()
        })
    }
    if ($PortableNoteOnly) {
        $portableFileName = '便签 文件.tucknote'
        $portableSelector = "PortableNoteItem-$portableFileName"
        $portablePath = Join-Path $storage $portableFileName
        [IO.File]::WriteAllText($portablePath,
            '{"format":"TuckPane.Note","version":1,"theme":3,"fontSize":14,"showRuledLines":true,"placement":null,"html":"portable"}',
            [Text.UTF8Encoding]::new($false))
        $state.Organizers[0].ItemOrder = @($portableFileName)
        $state.Organizers[0].Position = @{
            MonitorDevice = $primaryDevice; XDip = 240; YDip = 120
            SavedWorkAreaWidthDip = 0; SavedWorkAreaHeightDip = 0
        }
        [IO.Directory]::CreateDirectory($targetStorage) | Out-Null
        $state.Organizers += @{
            Id = '88888888-8888-8888-8888-888888888888'
            Name = 'TargetProbe'; CreatedAtUtc = [DateTimeOffset]::UtcNow
            PlacementMode = 0; DockEdge = 2
            Layout = @{ Mode = 0; Rows = 2; Columns = 2 }
            CompactScale = 1.0; CanvasScale = 0.7; ItemScale = 1.0; NameScale = 1.0
            ManualCanvasBaseWidthDip = $null; ManualCanvasBaseHeightDip = $null
            Position = @{
                MonitorDevice = $primaryDevice; XDip = 1200; YDip = 120
                SavedWorkAreaWidthDip = 0; SavedWorkAreaHeightDip = 0
            }
            StorageRelativePath = 'Windows\TargetProbe'; StorageAbsolutePath = $null
            ItemOrder = @(); Notes = @()
        }
    }
    if ($RuledLinesOnly) {
        $ruledPortablePath = Join-Path $storage '横线输入便签.tucknote'
        [IO.File]::WriteAllText($ruledPortablePath,
            '{"format":"TuckPane.Note","version":1,"theme":3,"fontSize":14,"showRuledLines":false,"placement":null,"html":"<div>中文 gjpqy</div><div>第二行 gjpqy</div>"}',
            [Text.UTF8Encoding]::new($false))
    }
    if ($ChromeOnly) {
        $chromeId = '99999999-9999-9999-9999-999999999999'
        $chromeKey = 'note:' + $chromeId.Replace('-', '')
        $state.Organizers[0].ItemOrder = @($chromeKey)
        $state.Organizers[0].Notes = @(@{
            Id = $chromeId; Name = 'ChromeProbe'; Theme = 0; FontSize = 14
            ShowRuledLines = $false; Placement = $null
        })
        $notesRoot = Join-Path $local 'notes'
        [IO.Directory]::CreateDirectory($notesRoot) | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $notesRoot ($chromeId.Replace('-', '') + '.json')),
            '{"Version":1,"Html":""}',
            [Text.UTF8Encoding]::new($false))
    }
    if ($NotePolishOnly) {
        $firstNoteId = '11111111-1111-1111-1111-111111111111'
        $secondNoteId = '22222222-2222-2222-2222-222222222222'
        $firstNoteKey = 'note:' + $firstNoteId.Replace('-', '')
        $secondNoteKey = 'note:' + $secondNoteId.Replace('-', '')
        $polishPortableFileName = '便携主题探针.tucknote'
        $polishPortableSelector = "PortableNoteItem-$polishPortableFileName"
        $polishPortablePath = Join-Path $storage $polishPortableFileName
        $longHtml = ('<div>正文起始：检查字距、光标与滚动条</div>' +
            ((1..36 | ForEach-Object { "<div>第 $_ 行：TuckPane note polish visual probe</div>" }) -join ''))
        $portableHtml = ('<div>便携正文起始</div>' +
            ((1..36 | ForEach-Object { "<div>Portable line $_ for scrollbar inspection</div>" }) -join ''))
        $state.Organizers[0].Position = @{
            MonitorDevice = $primaryDevice; XDip = 160; YDip = 100
            SavedWorkAreaWidthDip = 0; SavedWorkAreaHeightDip = 0
        }
        $state.Organizers[0].ItemOrder = @($firstNoteKey, $secondNoteKey, $polishPortableFileName)
        $state.Organizers[0].Notes = @(
            @{
                Id = $firstNoteId; Name = '内部浅色探针'; Theme = 0; FontSize = 14
                ShowRuledLines = $false
                Placement = @{
                    MonitorDevice = $primaryDevice; XDip = 860; YDip = 80
                    WidthDip = 360; HeightDip = 300
                }
            },
            @{
                Id = $secondNoteId; Name = '内部深色探针'; Theme = 3; FontSize = 14
                ShowRuledLines = $false
                Placement = @{
                    MonitorDevice = $primaryDevice; XDip = 1240; YDip = 80
                    WidthDip = 360; HeightDip = 300
                }
            }
        )
        $notesRoot = Join-Path $local 'notes'
        [IO.Directory]::CreateDirectory($notesRoot) | Out-Null
        foreach ($noteId in @($firstNoteId, $secondNoteId)) {
            [IO.File]::WriteAllText(
                (Join-Path $notesRoot ($noteId.Replace('-', '') + '.json')),
                (@{ Version = 1; Html = $longHtml } | ConvertTo-Json -Compress),
                [Text.UTF8Encoding]::new($false))
        }
        [IO.File]::WriteAllText(
            $polishPortablePath,
            (@{
                format = 'TuckPane.Note'; version = 1; theme = 5; fontSize = 14
                showRuledLines = $false
                placement = @{
                    monitorDevice = $primaryDevice; xDip = 860; yDip = 430
                    widthDip = 360; heightDip = 300
                }
                html = $portableHtml
            } | ConvertTo-Json -Depth 5 -Compress),
            [Text.UTF8Encoding]::new($false))
    }
    if ($ScrollStationOnly) {
        $scrollNoteId = '33333333-3333-3333-3333-333333333333'
        $scrollNoteKey = 'note:' + $scrollNoteId.Replace('-', '')
        $scrollHtml = ('<div>滚轮探针起始</div>' +
            ((1..40 | ForEach-Object { "<div>第 $_ 行：鼠标滚轮上下滑动探针</div>" }) -join ''))
        $state.SchemaVersion = 6
        $state.GlobalSettings.NoteTheme = 2
        $state.Organizers[0].Name = '右侧中转站'
        $state.Organizers[0].PlacementMode = 2
        $state.Organizers[0].DockEdge = 2
        $state.Organizers[0].Layout = @{ Mode = 0; Rows = 1; Columns = 1 }
        $state.Organizers[0].ItemOrder = @($scrollNoteKey)
        $state.Organizers[0].Notes = @(@{
            Id = $scrollNoteId; Name = '学习计划'; Theme = 2; FontSize = 14
            ShowRuledLines = $false
            Placement = @{
                MonitorDevice = $primaryDevice; XDip = 420; YDip = 120
                WidthDip = 360; HeightDip = 300
            }
        })
        $notesRoot = Join-Path $local 'notes'
        [IO.Directory]::CreateDirectory($notesRoot) | Out-Null
        [IO.File]::WriteAllText(
            (Join-Path $notesRoot ($scrollNoteId.Replace('-', '') + '.json')),
            (@{ Version = 1; Html = $scrollHtml } | ConvertTo-Json -Compress),
            [Text.UTF8Encoding]::new($false))
    }
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))

    $env:TUCKPANE_TEST_ROOT = $runRoot
    $env:GLASSFOLDER_TEST_EXPANDED = '1'
    if ($ScrollStationOnly) {
        $primaryBounds = [Windows.Forms.Screen]::PrimaryScreen.Bounds
        [TuckPaneNoteInput]::SetCursorPos(
            $primaryBounds.Right - 1,
            [int]($primaryBounds.Top + $primaryBounds.Height / 2)) | Out-Null
    }
    $app = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
    $expectedOrganizerCount = if ($PortableNoteOnly) { 2 } else { 1 }
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq 'TuckPane').Count -eq $expectedOrganizerCount } `
        'Expanded organizer did not appear.'
    $organizerWindows = @(Get-AppWindows $app.Id | Where-Object title -eq 'TuckPane')
    $main = $organizerWindows[0]
    $targetMain = $null
    if ($PortableNoteOnly) {
        Wait-ForCondition {
            $script:portableSourceMain = $null
            $script:portableTargetMain = $null
            $script:portableOrganizerWindows = @(Get-AppWindows $app.Id | Where-Object title -eq 'TuckPane')
            foreach ($candidate in $script:portableOrganizerWindows) {
                if (@((winapp ui search 'NoteProbe' -w $candidate.hwnd --json 2>$null | ConvertFrom-Json).matches).Count -gt 0) {
                    $script:portableSourceMain = $candidate
                }
                if (@((winapp ui search 'TargetProbe' -w $candidate.hwnd --json 2>$null | ConvertFrom-Json).matches).Count -gt 0) {
                    $script:portableTargetMain = $candidate
                }
            }
            if ($null -eq $script:portableSourceMain -and $null -ne $script:portableTargetMain) {
                $script:portableSourceMain = @($script:portableOrganizerWindows | Where-Object hwnd -ne $script:portableTargetMain.hwnd)[0]
            }
            if ($null -eq $script:portableTargetMain -and $null -ne $script:portableSourceMain) {
                $script:portableTargetMain = @($script:portableOrganizerWindows | Where-Object hwnd -ne $script:portableSourceMain.hwnd)[0]
            }
            $null -ne $script:portableSourceMain -and $null -ne $script:portableTargetMain
        } 'Could not identify the portable-note source and target organizers.'
        $main = $script:portableSourceMain
        $targetMain = $script:portableTargetMain
        $collapseMatches = @((winapp ui search CollapseButton -w $main.hwnd --json 2>$null | ConvertFrom-Json).matches)
        if ($collapseMatches.Count -eq 0) {
            foreach ($attempt in 1..3) {
                $main = @(Get-AppWindows $app.Id | Where-Object hwnd -eq $main.hwnd)[0]
                if ($main.width -gt 200) { break }
                winapp ui focus CompactNameText -w $main.hwnd | Out-Null
                $compactTree = winapp ui inspect window -w $main.hwnd --json 2>$null | ConvertFrom-Json
                $compactImage = @(Get-UiDescendants $compactTree.windows[0].elements | Where-Object type -eq 'Image')[0]
                if ($null -eq $compactImage) { throw 'The portable-note source compact tile was not exposed to UI Automation.' }
                Set-ProbeForeground $main.hwnd
                winapp ui hover $compactImage.selector -w $main.hwnd | Out-Null
                winapp ui click $compactImage.selector -w $main.hwnd | Out-Null
                Start-Sleep -Milliseconds 1200
            }
            $main = @(Get-AppWindows $app.Id | Where-Object hwnd -eq $main.hwnd)[0]
            if ($main.width -le 200) { throw 'The portable-note source organizer did not expand.' }
        }
    }
    if (-not $ScrollStationOnly) {
        winapp ui wait-for CollapseButton -w $main.hwnd --timeout 5000 | Out-Null
    }
    Set-ProbeForeground $main.hwnd

    if ($ScrollStationOnly) {
        $scrollId = $scrollNoteId.Replace('-', '')
        $scrollSelector = "NoteItem-$scrollId"
        winapp ui wait-for $scrollSelector -w $main.hwnd --timeout 5000 | Out-Null
        $expandedShot = Join-Path $runRoot 'station-expanded-note.png'
        winapp ui screenshot $scrollSelector -w $main.hwnd -o $expandedShot | Out-Null

        winapp ui click $scrollSelector -w $main.hwnd | Out-Null
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '学习计划').Count -eq 1 } `
            'The Station note did not open.'
        $scrollWindow = (Get-AppWindows $app.Id | Where-Object title -eq '学习计划')[0]
        $scrollBody = @((winapp ui search '滚轮探针起始' -w $scrollWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches)[0]
        if ($null -eq $scrollBody) { throw 'The scroll probe body was not exposed to UI Automation.' }
        winapp ui click $scrollBody.selector -w $scrollWindow.hwnd | Out-Null

        foreach ($probe in @(
            @{ Name = 'down'; Wheel = -4; Expect = 'less'; Target = '滚轮探针起始' },
            @{ Name = 'up'; Wheel = 4; Expect = 'greater'; Target = '第 12 行：鼠标滚轮上下滑动探针' })) {
            $beforeMarker = @((winapp ui search '滚轮探针起始' -w $scrollWindow.hwnd --json 2>$null |
                ConvertFrom-Json).matches)[0]
            $wheelTarget = @((winapp ui search $probe.Target -w $scrollWindow.hwnd --json 2>$null |
                ConvertFrom-Json).matches)[0]
            if ($null -eq $wheelTarget -or $wheelTarget.width -le 0 -or $wheelTarget.height -le 0) {
                throw "The visible mouse-wheel $($probe.Name) target was not available."
            }
            $wheel = $probe.Wheel
            winapp ui scroll $wheelTarget.selector -w $scrollWindow.hwnd --wheel $wheel | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "winapp mouse-wheel $($probe.Name) input failed." }
            foreach ($frame in 1..3) {
                winapp ui screenshot -w $scrollWindow.hwnd --capture-screen `
                    -o (Join-Path $runRoot "note-wheel-$($probe.Name)-$frame.png") | Out-Null
            }
            $f2Json = winapp ui inspect -a $app.Id --interactive --json 2>$null
            if ($LASTEXITCODE -ne 0) { throw "F2 visibility inspection failed after mouse-wheel $($probe.Name)." }
            try { $f2Result = $f2Json | ConvertFrom-Json }
            catch { throw "F2 visibility inspection returned invalid JSON after mouse-wheel $($probe.Name)." }
            $visibleF2 = @($f2Result.windows.elements | Where-Object {
                ($_.name -eq 'F2' -or $_.automationId -eq 'F2') -and $_.width -gt 0 -and $_.height -gt 0
            })
            if ($visibleF2.Count -gt 0) { throw "Mouse-wheel $($probe.Name) exposed a visible F2 prompt." }
            $afterMarker = @((winapp ui search '滚轮探针起始' -w $scrollWindow.hwnd --json 2>$null |
                ConvertFrom-Json).matches)[0]
            if ($null -eq $beforeMarker -or $null -eq $afterMarker -or
                ($probe.Expect -eq 'less' -and $afterMarker.y -ge $beforeMarker.y) -or
                ($probe.Expect -eq 'greater' -and $afterMarker.y -le $beforeMarker.y)) {
                throw "Mouse-wheel $($probe.Name) did not move the note body in the expected direction."
            }
        }

        winapp ui send-keys f2 -w $scrollWindow.hwnd --via send-input | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Sending F2 to the note window failed.' }
        Start-Sleep -Milliseconds 500
        $titleEditorJson = winapp ui inspect -w $scrollWindow.hwnd --interactive --json 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'The post-F2 title-editor inspection failed.' }
        try { $titleEditorResult = $titleEditorJson | ConvertFrom-Json }
        catch { throw 'The post-F2 title-editor inspection returned invalid JSON.' }
        $visibleTitleEditor = @($titleEditorResult.windows.elements | Where-Object {
            $_.automationId -eq "NoteTitleEditor-$scrollId" -and $_.width -gt 0 -and $_.height -gt 0
        })
        if ($visibleTitleEditor.Count -gt 0) {
            throw 'F2 still opened the note title editor.'
        }
        Wait-ForCondition {
            if ($app.HasExited) { throw 'TuckPane exited while waiting for the Station to hide.' }
            $stationWindowsJson = winapp ui list-windows -a $app.Id --json 2>$null
            if ($LASTEXITCODE -ne 0) { throw 'The Station visibility query failed.' }
            try { $stationWindows = @($stationWindowsJson | ConvertFrom-Json) }
            catch { throw 'The Station visibility query returned invalid JSON.' }
            @($stationWindows | Where-Object title -eq 'TuckPane').Count -eq 0
        } `
            'The Station did not hide after the pointer left its expanded window.'

        $evidence = @($expandedShot) + @(foreach ($direction in @('down', 'up')) {
            foreach ($frame in 1..3) { Join-Path $runRoot "note-wheel-$direction-$frame.png" }
        })
        foreach ($shot in $evidence) {
            if (-not (Test-Path -LiteralPath $shot) -or (Get-Item -LiteralPath $shot).Length -eq 0) {
                throw "The targeted visual evidence screenshot was not created: $shot"
            }
        }

        Write-Host 'TuckPane note wheel and expanded Station icon UI: PASS'
        return
    }

    if ($NotePolishOnly) {
        Wait-ForCondition {
            try {
                $migrated = Get-State
                return $migrated.SchemaVersion -eq 7 -and
                    $migrated.GlobalSettings.NoteTheme -eq 2 -and
                    @($migrated.Organizers[0].Notes | Where-Object Theme -ne 2).Count -eq 0
            }
            catch { return $false }
        } 'Schema 5 note themes were not migrated to the global SunYellow theme.'
        if ((Get-Content -LiteralPath $polishPortablePath -Raw | ConvertFrom-Json).theme -ne 5) {
            throw 'Startup migration rewrote a closed portable note.'
        }

        $firstId = $firstNoteId.Replace('-', '')
        $secondId = $secondNoteId.Replace('-', '')
        winapp ui wait-for "NoteItem-$firstId" -w $main.hwnd --timeout 3000 | Out-Null
        winapp ui wait-for "NoteItem-$secondId" -w $main.hwnd --timeout 3000 | Out-Null
        winapp ui wait-for $polishPortableSelector -w $main.hwnd --timeout 3000 | Out-Null

        Set-ProbeForeground $main.hwnd
        winapp ui click "NoteItem-$firstId" -w $main.hwnd | Out-Null
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '内部浅色探针').Count -eq 1 } `
            'The first internal note did not open.'
        $firstWindow = (Get-AppWindows $app.Id | Where-Object title -eq '内部浅色探针')[0]
        winapp ui wait-for "NoteColor-$firstId" -w $firstWindow.hwnd --timeout 5000 | Out-Null

        Set-ProbeForeground $main.hwnd
        winapp ui click "NoteItem-$secondId" -w $main.hwnd | Out-Null
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '内部深色探针').Count -eq 1 } `
            'The second internal note did not open.'
        $secondWindow = (Get-AppWindows $app.Id | Where-Object title -eq '内部深色探针')[0]
        winapp ui wait-for "NoteColor-$secondId" -w $secondWindow.hwnd --timeout 5000 | Out-Null

        Set-ProbeForeground $main.hwnd
        winapp ui click $polishPortableSelector -w $main.hwnd | Out-Null
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '便携主题探针').Count -eq 1 } `
            'The portable note did not open.'
        $portableWindow = (Get-AppWindows $app.Id | Where-Object title -eq '便携主题探针')[0]
        $portableTitle = @((winapp ui search '便携主题探针' -w $portableWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches | Where-Object automationId -like 'NoteTitle-*')[0]
        if ($null -eq $portableTitle) { throw 'The portable note title was not exposed to UI Automation.' }
        $portableColorId = $portableTitle.automationId.Replace('NoteTitle-', 'NoteColor-')
        winapp ui wait-for $portableColorId -w $portableWindow.hwnd --timeout 5000 | Out-Null

        Assert-NoteTheme $firstWindow.hwnd "NoteColor-$firstId" 'SunYellow'
        Assert-NoteTheme $secondWindow.hwnd "NoteColor-$secondId" 'SunYellow'
        Assert-NoteTheme $portableWindow.hwnd $portableColorId 'SunYellow'
        $firstBody = @((winapp ui search '正文起始' -w $firstWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches)[0]
        if ($null -eq $firstBody) { throw 'The internal note body was not exposed to UI Automation.' }
        winapp ui click $firstBody.selector -w $firstWindow.hwnd | Out-Null
        $sunShot = Join-Path $runRoot 'note-polish-sun-yellow.png'
        winapp ui screenshot -w $firstWindow.hwnd -o $sunShot | Out-Null

        winapp ui invoke "NoteColor-$firstId" -w $firstWindow.hwnd | Out-Null
        winapp ui wait-for NoteTheme-Graphite -w $firstWindow.hwnd --timeout 3000 | Out-Null
        winapp ui invoke NoteTheme-Graphite -w $firstWindow.hwnd | Out-Null
        Wait-ForCondition {
            $themed = Get-State
            $themed.GlobalSettings.NoteTheme -eq 1 -and
                @($themed.Organizers[0].Notes | Where-Object Theme -ne 1).Count -eq 0
        } 'Selecting Graphite did not update the global theme and every internal note.'
        Assert-NoteTheme $firstWindow.hwnd "NoteColor-$firstId" 'Graphite'
        Assert-NoteTheme $secondWindow.hwnd "NoteColor-$secondId" 'Graphite'
        Assert-NoteTheme $portableWindow.hwnd $portableColorId 'Graphite'
        if ((Get-Content -LiteralPath $polishPortablePath -Raw | ConvertFrom-Json).theme -ne 5) {
            throw 'Changing the global theme rewrote the portable note before a normal save.'
        }

        $graphiteShot = Join-Path $runRoot 'note-polish-graphite-internal.png'
        $portableShot = Join-Path $runRoot 'note-polish-graphite-portable.png'
        $secondBody = @((winapp ui search '正文起始' -w $secondWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches)[0]
        if ($null -eq $secondBody) { throw 'The second internal note body was not exposed to UI Automation.' }
        winapp ui click $secondBody.selector -w $secondWindow.hwnd | Out-Null
        winapp ui screenshot -w $secondWindow.hwnd -o $graphiteShot | Out-Null

        $portableBody = @((winapp ui search '便携正文起始' -w $portableWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches)[0]
        if ($null -eq $portableBody) { throw 'The portable note body was not exposed to UI Automation.' }
        winapp ui click $portableBody.selector -w $portableWindow.hwnd | Out-Null
        winapp ui screenshot -w $portableWindow.hwnd -o $portableShot | Out-Null
        winapp ui send-keys 'ctrl+end' -w $portableWindow.hwnd | Out-Null
        $portableProbe = ' NOTE_POLISH_SAVE_PROBE '
        winapp ui send-keys --verbatim $portableProbe -w $portableWindow.hwnd | Out-Null
        Wait-ForCondition {
            try {
                $savedPortable = Get-Content -LiteralPath $polishPortablePath -Raw | ConvertFrom-Json
                return $savedPortable.theme -eq 1 -and $savedPortable.html -like "*$portableProbe*"
            }
            catch { return $false }
        } 'Editing the portable note did not save its body with the global Graphite theme.'

        $firstTitle = @((winapp ui search '内部浅色探针' -w $firstWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches | Where-Object automationId -eq "NoteTitle-$firstId")[0]
        if ($null -eq $firstTitle) { throw 'The internal note title was not exposed to UI Automation.' }
        winapp ui click $firstTitle.selector -w $firstWindow.hwnd | Out-Null
        $firstTitleEditor = "NoteTitleEditor-$firstId"
        winapp ui wait-for $firstTitleEditor -w $firstWindow.hwnd --timeout 3000 | Out-Null
        $committedTitle = '单击改名已提交'
        winapp ui set-value $firstTitleEditor $committedTitle -w $firstWindow.hwnd | Out-Null
        winapp ui focus "NoteColor-$firstId" -w $firstWindow.hwnd | Out-Null
        Wait-ForCondition {
            (Get-State).Organizers[0].Notes[0].Name -eq $committedTitle -and
                (Get-AppWindows $app.Id | Where-Object title -eq $committedTitle).Count -eq 1
        } 'Single-click title editing did not commit the internal note name.'
        $firstTitle = @((winapp ui search $committedTitle -w $firstWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches | Where-Object automationId -eq "NoteTitle-$firstId")[0]
        winapp ui click $firstTitle.selector -w $firstWindow.hwnd | Out-Null
        winapp ui wait-for $firstTitleEditor -w $firstWindow.hwnd --timeout 3000 | Out-Null
        winapp ui set-value $firstTitleEditor '不应保存的名称' -w $firstWindow.hwnd | Out-Null
        winapp ui send-keys escape -w $firstWindow.hwnd | Out-Null
        Start-Sleep -Milliseconds 200
        if ((Get-State).Organizers[0].Notes[0].Name -ne $committedTitle) {
            throw 'Escape did not cancel single-click title editing.'
        }

        $dragBefore = [TuckPaneNoteInput+Rect]::new()
        [TuckPaneNoteInput]::GetWindowRect([IntPtr]$firstWindow.hwnd, [ref]$dragBefore) | Out-Null
        $color = (winapp ui search "NoteColor-$firstId" -w $firstWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches[0]
        winapp ui drag "$([int]($color.x - 24)),$([int]($color.y + $color.height / 2))" `
            "$([int]($color.x + 46)),$([int]($color.y + $color.height / 2 + 50))" -w $firstWindow.hwnd | Out-Null
        $dragAfter = [TuckPaneNoteInput+Rect]::new()
        [TuckPaneNoteInput]::GetWindowRect([IntPtr]$firstWindow.hwnd, [ref]$dragAfter) | Out-Null
        if ($dragAfter.Left -eq $dragBefore.Left -and $dragAfter.Top -eq $dragBefore.Top) {
            throw 'The title area beside the clickable text no longer drags the note.'
        }

        winapp ui invoke "CloseNote-$firstId" -w $firstWindow.hwnd | Out-Null
        winapp ui invoke "CloseNote-$secondId" -w $secondWindow.hwnd | Out-Null
        $portableCloseId = $portableTitle.automationId.Replace('NoteTitle-', 'CloseNote-')
        winapp ui invoke $portableCloseId -w $portableWindow.hwnd | Out-Null
        Wait-ForCondition {
            (Get-AppWindows $app.Id | Where-Object title -in @($committedTitle, '内部深色探针', '便携主题探针')).Count -eq 0
        } 'The polish probe notes did not close before creating a new note.'

        $collapse = (winapp ui search CollapseButton -w $main.hwnd --json 2>$null | ConvertFrom-Json).matches[0]
        $blankX = [int]($collapse.x - 200)
        $blankY = [int]($collapse.y + 200)
        $newNoteInvoked = $false
        foreach ($attempt in 1..3) {
            Set-ProbeForeground $main.hwnd
            winapp ui drag "$blankX,$blankY" "$blankX,$blankY" -w $main.hwnd --right --hold-ms 60 | Out-Null
            Start-Sleep -Milliseconds 250
            $menu = winapp ui search NewNoteMenuItem -w $main.hwnd --json 2>$null | ConvertFrom-Json
            if ($menu.matchCount -gt 0) {
                winapp ui invoke NewNoteMenuItem -w $main.hwnd | Out-Null
                if ($LASTEXITCODE -eq 0) { $newNoteInvoked = $true; break }
            }
            winapp ui send-keys escape -w $main.hwnd | Out-Null
        }
        if (-not $newNoteInvoked) { throw 'The targeted New note command could not be invoked.' }
        Wait-ForCondition { (Get-State).Organizers[0].Notes.Count -eq 3 } 'The third note was not created.'
        $newNote = @((Get-State).Organizers[0].Notes |
            Where-Object Id -notin @($firstNoteId, $secondNoteId))[0]
        if ($null -eq $newNote -or $newNote.Theme -ne 1) {
            throw 'A newly created note did not inherit the global Graphite theme.'
        }
        $organizerShot = Join-Path $runRoot 'note-polish-organizer-icons.png'
        winapp ui screenshot -w $main.hwnd -o $organizerShot | Out-Null

        foreach ($shot in @($sunShot, $graphiteShot, $portableShot, $organizerShot)) {
            if (-not (Test-Path -LiteralPath $shot) -or (Get-Item -LiteralPath $shot).Length -eq 0) {
                throw "The visual evidence screenshot was not created: $shot"
            }
        }

        Stop-Process -Id $app.Id -Force
        $app.WaitForExit(5000) | Out-Null
        $app.Dispose()
        $app = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq 'TuckPane').Count -eq 1 } `
            'The organizer did not restart with the same isolated root.'
        Wait-ForCondition {
            try {
                $restarted = Get-State
                return $restarted.SchemaVersion -eq 7 -and
                    $restarted.GlobalSettings.NoteTheme -eq 1 -and
                    $restarted.Organizers[0].Notes.Count -eq 3 -and
                    @($restarted.Organizers[0].Notes | Where-Object Theme -ne 1).Count -eq 0 -and
                    (Get-Content -LiteralPath $polishPortablePath -Raw | ConvertFrom-Json).theme -eq 1
            }
            catch { return $false }
        } 'The global Graphite theme did not survive restart.'

        Write-Host 'TuckPane global note theme, single-click title, and polish evidence UI: PASS'
        return
    }

    if ($ChromeOnly) {
        $chromeId = '99999999999999999999999999999999'
        winapp ui wait-for "NoteItem-$chromeId" -w $main.hwnd --timeout 5000 | Out-Null
        winapp ui click "NoteItem-$chromeId" -w $main.hwnd | Out-Null
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq 'ChromeProbe').Count -eq 1 } `
            'Chrome probe note did not open.'
        $noteWindow = (Get-AppWindows $app.Id | Where-Object title -eq 'ChromeProbe')[0]
        winapp ui wait-for "CloseNote-$chromeId" -w $noteWindow.hwnd --timeout 5000 | Out-Null
        $visibleFrame = [TuckPaneNoteInput+Rect]::new()
        $frameResult = [TuckPaneNoteInput]::DwmGetWindowRectAttribute(
            [IntPtr]$noteWindow.hwnd, 9, [ref]$visibleFrame, 16)
        if ($frameResult -lt 0) { throw ('DwmGetWindowAttribute(EXTENDED_FRAME_BOUNDS) failed: 0x{0:X8}.' -f $frameResult) }
        $captureFrame = {
            param([string]$Path)
            $bitmap = [Drawing.Bitmap]::new($visibleFrame.Right - $visibleFrame.Left, $visibleFrame.Bottom - $visibleFrame.Top)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.CopyFromScreen($visibleFrame.Left, $visibleFrame.Top, 0, 0, $bitmap.Size)
                $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
            }
            finally {
                $graphics.Dispose()
                $bitmap.Dispose()
            }
        }
        $beforeShot = Join-Path $runRoot 'chrome-before.png'
        $afterShot = Join-Path $runRoot 'chrome-after-color-none.png'
        & $captureFrame $beforeShot
        [int]$noBorder = -2
        $setResult = [TuckPaneNoteInput]::DwmSetWindowAttribute(
            [IntPtr]$noteWindow.hwnd, 34, [ref]$noBorder, 4)
        if ($setResult -lt 0) { throw ('DwmSetWindowAttribute(BORDER_COLOR) failed: 0x{0:X8}.' -f $setResult) }
        [TuckPaneNoteInput]::DwmFlush() | Out-Null
        & $captureFrame $afterShot

        $beforeBitmap = [Drawing.Bitmap]::new($beforeShot)
        $afterBitmap = [Drawing.Bitmap]::new($afterShot)
        try {
            $width = $beforeBitmap.Width
            $height = $beforeBitmap.Height
            $positions = @(0.25, 0.5, 0.75)
            $sides = @{
                Top = @($positions | ForEach-Object {
                    [pscustomobject]@{ EdgeX = [int](($width - 1) * $_); EdgeY = 0; InnerX = [int](($width - 1) * $_); InnerY = 2 }
                })
                Bottom = @($positions | ForEach-Object {
                    [pscustomobject]@{ EdgeX = [int](($width - 1) * $_); EdgeY = $height - 1; InnerX = [int](($width - 1) * $_); InnerY = $height - 3 }
                })
                Left = @($positions | ForEach-Object {
                    [pscustomobject]@{ EdgeX = 0; EdgeY = [int](($height - 1) * $_); InnerX = 2; InnerY = [int](($height - 1) * $_) }
                })
                Right = @($positions | ForEach-Object {
                    [pscustomobject]@{ EdgeX = $width - 1; EdgeY = [int](($height - 1) * $_); InnerX = $width - 3; InnerY = [int](($height - 1) * $_) }
                })
            }
            $edgeChanges = @{}
            $innerContrasts = @{}
            foreach ($side in $sides.Keys) {
                $changes = @()
                $contrasts = @()
                foreach ($sample in $sides[$side]) {
                    $beforeEdge = $beforeBitmap.GetPixel($sample.EdgeX, $sample.EdgeY)
                    $afterEdge = $afterBitmap.GetPixel($sample.EdgeX, $sample.EdgeY)
                    $beforeInner = $beforeBitmap.GetPixel($sample.InnerX, $sample.InnerY)
                    $changes += ([Math]::Abs($beforeEdge.R - $afterEdge.R) +
                        [Math]::Abs($beforeEdge.G - $afterEdge.G) +
                        [Math]::Abs($beforeEdge.B - $afterEdge.B)) / 3.0
                    $edgeLuma = 0.2126 * $beforeEdge.R + 0.7152 * $beforeEdge.G + 0.0722 * $beforeEdge.B
                    $innerLuma = 0.2126 * $beforeInner.R + 0.7152 * $beforeInner.G + 0.0722 * $beforeInner.B
                    $contrasts += [Math]::Abs($edgeLuma - $innerLuma)
                }
                $edgeChanges[$side] = ($changes | Measure-Object -Average).Average
                $innerContrasts[$side] = ($contrasts | Measure-Object -Average).Average
            }
        }
        finally {
            $beforeBitmap.Dispose()
            $afterBitmap.Dispose()
        }

        $extendedStyle = [TuckPaneNoteInput]::GetWindowLongPtr([IntPtr]$noteWindow.hwnd, -20).ToInt64()
        $windowStyle = [TuckPaneNoteInput]::GetWindowLongPtr([IntPtr]$noteWindow.hwnd, -16).ToInt64()
        $failures = [Collections.Generic.List[string]]::new()
        $largestEdgeChange = ($edgeChanges.Values | Measure-Object -Maximum).Maximum
        if ($largestEdgeChange -gt 3) {
            $failures.Add(('Applying COLOR_NONE changed the visible border (RGB delta top={0:F1}, bottom={1:F1}, left={2:F1}, right={3:F1}); before edge/inner luminance contrast top={4:F1}, bottom={5:F1}, left={6:F1}, right={7:F1}.' -f
                $edgeChanges.Top, $edgeChanges.Bottom, $edgeChanges.Left, $edgeChanges.Right,
                $innerContrasts.Top, $innerContrasts.Bottom, $innerContrasts.Left, $innerContrasts.Right))
        }
        if (($extendedStyle -band 0x8) -ne 0) { $failures.Add('Note unexpectedly remained WS_EX_TOPMOST.') }
        if (($extendedStyle -band 0x80) -ne 0) { $failures.Add('Note unexpectedly remained WS_EX_TOOLWINDOW.') }
        if (($extendedStyle -band 0x40000) -eq 0) { $failures.Add('Note lost WS_EX_APPWINDOW.') }
        if (($windowStyle -band 0x40000) -eq 0) { $failures.Add('Note lost WS_THICKFRAME.') }
        if ($failures.Count -gt 0) { throw ($failures -join [Environment]::NewLine) }
        Write-Host 'TuckPane note chrome: PASS'
        return
    }

    if ($RuledLinesOnly) {
        $redirect = Start-Process -FilePath $resolvedExe -ArgumentList ('"{0}"' -f $ruledPortablePath) -PassThru
        try {
            Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '横线输入便签').Count -eq 1 } `
                'The ruled-lines portable note did not open in the primary instance.'
        }
        finally {
            if (-not $redirect.HasExited) { Stop-Process -Id $redirect.Id -Force -ErrorAction SilentlyContinue }
            $redirect.Dispose()
        }
        $noteWindow = (Get-AppWindows $app.Id | Where-Object title -eq '横线输入便签')[0]
        Wait-ForCondition {
            $focused = winapp ui get-focused -w $noteWindow.hwnd --json 2>$null | ConvertFrom-Json
            return $focused.hasFocus -and $focused.element.type -ne 'Button'
        } 'The opened note editor did not receive keyboard focus.'
        $ruledToggle = @((winapp ui search 'NoteRuledLines' -w $noteWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches | Where-Object automationId -like 'NoteRuledLines-*')[0]
        if ($null -eq $ruledToggle) { throw 'The ruled-lines toggle was not exposed to UI Automation.' }
        winapp ui invoke $ruledToggle.selector -w $noteWindow.hwnd | Out-Null
        Wait-ForCondition {
            if (-not (Test-Path -LiteralPath $ruledPortablePath)) { return $false }
            try { return (Get-Content -LiteralPath $ruledPortablePath -Raw | ConvertFrom-Json).showRuledLines -eq $true }
            catch { return $false }
        } `
            'Ruled lines were not enabled for the spacing check.'

        $defaultShot = Join-Path $runRoot 'ruled-lines-default.png'
        $inputShot = Join-Path $runRoot 'ruled-lines-input.png'
        winapp ui screenshot -w $noteWindow.hwnd -o $defaultShot | Out-Null
        $editorText = (winapp ui search '中文 gjpqy' -w $noteWindow.hwnd --json 2>$null | ConvertFrom-Json).matches[0]
        if ($null -eq $editorText) { throw 'The ruled-lines sample text was not exposed to UI Automation.' }
        $inputProbe = '键盘输入探针123'
        winapp ui send-keys 'ctrl+end' -w $noteWindow.hwnd | Out-Null
        winapp ui send-keys --verbatim $inputProbe -w $noteWindow.hwnd | Out-Null
        Wait-ForCondition {
            if (-not (Test-Path -LiteralPath $ruledPortablePath)) { return $false }
            try { return (Get-Content -LiteralPath $ruledPortablePath -Raw | ConvertFrom-Json).html -like "*$inputProbe*" }
            catch { return $false }
        } 'Typing into the note editor did not update the persisted note body.'
        winapp ui screenshot -w $noteWindow.hwnd -o $inputShot | Out-Null
        if ((Get-Item -LiteralPath $defaultShot).Length -eq 0 -or (Get-Item -LiteralPath $inputShot).Length -eq 0) {
            throw 'The ruled-lines screenshots were not created.'
        }
        Write-Host 'TuckPane ruled-lines and editor input UI: PASS'
        return
    }

    if ($ActivationOnly) {
        $portablePath = Join-Path $storage '中文 便签.tucknote'
        [IO.File]::WriteAllText($portablePath,
            '{"format":"TuckPane.Note","version":1,"theme":3,"fontSize":14,"showRuledLines":false,"placement":null,"html":"redirected"}',
            [Text.UTF8Encoding]::new($false))
        $redirect = Start-Process -FilePath $resolvedExe -ArgumentList ('"{0}"' -f $portablePath) -PassThru
        try {
            Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '中文 便签').Count -eq 1 } `
                'A redirected .tucknote path containing Chinese and spaces did not open in the primary instance.'
        }
        finally {
            if (-not $redirect.HasExited) { Stop-Process -Id $redirect.Id -Force -ErrorAction SilentlyContinue }
            $redirect.Dispose()
        }
        Write-Host 'TuckPane redirected portable-note activation: PASS'
        return
    }

    if ($PortableNoteOnly) {
        winapp ui wait-for $portableSelector -w $main.hwnd --timeout 3000 | Out-Null
        $sourceItem = @((winapp ui search $portableSelector -w $main.hwnd --json 2>$null | ConvertFrom-Json).matches)[0]
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object hwnd -eq $targetMain.hwnd).width -lt 200 } `
            'The portable-note target organizer did not collapse before the cross-window drag.'
        $targetMain = @(Get-AppWindows $app.Id | Where-Object hwnd -eq $targetMain.hwnd)[0]
        Set-ProbeForeground $main.hwnd
        $sourceX = [int]($sourceItem.x + $sourceItem.width / 2)
        $sourceY = [int]($sourceItem.y + $sourceItem.height / 2)
        [TuckPaneNoteInput]::MoveAbsolute($sourceX, $sourceY)
        $hit = [TuckPaneNoteInput]::WindowFromPoint([TuckPaneNoteInput+Point]@{ X = $sourceX; Y = $sourceY })
        $hitProcess = [uint32]0
        [TuckPaneNoteInput]::GetWindowThreadProcessId($hit, [ref]$hitProcess) | Out-Null
        if ($hitProcess -ne $app.Id) { throw "The portable-note drag source is covered by PID $hitProcess." }
        $targetRect = [TuckPaneNoteInput+Rect]::new()
        [TuckPaneNoteInput]::GetWindowRect([IntPtr]$targetMain.hwnd, [ref]$targetRect) | Out-Null
        $targetX = [int](($targetRect.Left + $targetRect.Right) / 2)
        $targetY = [int](($targetRect.Top + $targetRect.Bottom) / 2)
        Invoke-MouseDrag $sourceX $sourceY `
            $targetX $targetY
        $movedPath = Join-Path $targetStorage $portableFileName
        Wait-ForCondition {
            (Test-Path -LiteralPath $movedPath) -and
            -not (Test-Path -LiteralPath $portablePath) -and
            (Get-State).Organizers[1].ItemOrder[0] -eq $portableFileName
        } 'Moving a portable note between organizers did not move the real file and target ItemOrder.'
        $main = $targetMain
        $portablePath = $movedPath
        winapp ui wait-for $portableSelector -w $main.hwnd --timeout 3000 | Out-Null
        winapp ui screenshot -w $main.hwnd -o (Join-Path $runRoot 'portable-note-grid.png') | Out-Null
        winapp ui click $portableSelector -w $main.hwnd | Out-Null
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '便签 文件').Count -eq 1 } `
            'Single-clicking a portable note did not open it like an internal note.'
        $portableWindow = (Get-AppWindows $app.Id | Where-Object title -eq '便签 文件')[0]
        $title = @((winapp ui search '便签 文件' -w $portableWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches | Where-Object automationId -like 'NoteTitle-*')[0]
        if ($null -eq $title) { throw 'The portable note title was not exposed to UI Automation.' }
        Set-ProbeForeground $portableWindow.hwnd
        winapp ui click $title.selector -w $portableWindow.hwnd --double | Out-Null
        $editorId = $title.automationId.Replace('NoteTitle-', 'NoteTitleEditor-')
        winapp ui wait-for $editorId -w $portableWindow.hwnd --timeout 3000 | Out-Null
        winapp ui set-value $editorId '改名便签' -w $portableWindow.hwnd | Out-Null
        $colorId = $title.automationId.Replace('NoteTitle-', 'NoteColor-')
        winapp ui focus $colorId -w $portableWindow.hwnd | Out-Null
        $renamedPath = Join-Path $targetStorage '改名便签.tucknote'
        Wait-ForCondition {
            (Test-Path -LiteralPath $renamedPath) -and
            -not (Test-Path -LiteralPath $portablePath) -and
            (Get-State).Organizers[1].ItemOrder[0] -eq '改名便签.tucknote'
        } 'Renaming a portable note did not update the file and preserve its ItemOrder position.'
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '改名便签').Count -eq 1 } `
            'Renaming a portable note did not update its open window title.'
        $collisionPath = Join-Path $targetStorage '已有便签.tucknote'
        [IO.File]::WriteAllText($collisionPath,
            '{"format":"TuckPane.Note","version":1,"theme":3,"fontSize":14,"showRuledLines":false,"placement":null,"html":"collision"}',
            [Text.UTF8Encoding]::new($false))
        $title = @((winapp ui search '改名便签' -w $portableWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches | Where-Object automationId -like 'NoteTitle-*')[0]
        winapp ui click $title.selector -w $portableWindow.hwnd --double | Out-Null
        winapp ui wait-for $editorId -w $portableWindow.hwnd --timeout 3000 | Out-Null
        winapp ui set-value $editorId '已有便签' -w $portableWindow.hwnd | Out-Null
        winapp ui focus $colorId -w $portableWindow.hwnd | Out-Null
        Start-Sleep -Milliseconds 400
        $sourceStillExists = Test-Path -LiteralPath $renamedPath
        $orderStillMatches = (Get-State).Organizers[1].ItemOrder[0] -eq '改名便签.tucknote'
        if (-not $sourceStillExists -or -not $orderStillMatches) {
            throw 'A duplicate portable-note name replaced the original file or order entry.'
        }
        winapp ui set-value $editorId 'CON' -w $portableWindow.hwnd | Out-Null
        winapp ui focus $colorId -w $portableWindow.hwnd | Out-Null
        Start-Sleep -Milliseconds 400
        if (Test-Path -LiteralPath (Join-Path $targetStorage 'CON.tucknote')) {
            throw 'A reserved Windows file name was accepted for a portable note.'
        }
        winapp ui set-value $editorId '取消名称' -w $portableWindow.hwnd | Out-Null
        winapp ui send-keys escape -w $portableWindow.hwnd | Out-Null
        Start-Sleep -Milliseconds 200
        $renamedWindowCount = (Get-AppWindows $app.Id | Where-Object title -eq '改名便签').Count
        $cancelledPathExists = Test-Path -LiteralPath (Join-Path $targetStorage '取消名称.tucknote')
        if ($renamedWindowCount -ne 1 -or $cancelledPathExists) {
            throw 'Escape did not cancel portable-note renaming.'
        }
        Write-Host 'TuckPane portable-note parity and rename boundaries: PASS'
        return
    }

    $collapse = (winapp ui search CollapseButton -w $main.hwnd --json 2>$null | ConvertFrom-Json).matches[0]
    $blankX = [int]($collapse.x - 200)
    $blankY = [int]($collapse.y + 200)
    $clipboardText = "第一行`r`n  第二行 <>&"
    Set-Clipboard -Value $clipboardText
    $newNoteInvoked = $false
    foreach ($attempt in 1..3) {
        Set-ProbeForeground $main.hwnd
        winapp ui drag "$blankX,$blankY" "$blankX,$blankY" -w $main.hwnd --right --hold-ms 60 | Out-Null
        Start-Sleep -Milliseconds 250
        $menu = winapp ui search NewNoteMenuItem -w $main.hwnd --json 2>$null | ConvertFrom-Json
        if ($menu.matchCount -gt 0) {
            winapp ui invoke NewNoteMenuItem -w $main.hwnd | Out-Null
            if ($LASTEXITCODE -eq 0) { $newNoteInvoked = $true; break }
        }
        winapp ui send-keys escape -w $main.hwnd | Out-Null
    }
    if (-not $newNoteInvoked) { throw 'The background New note command could not be invoked.' }
    Wait-ForCondition { (Get-State).Organizers[0].Notes.Count -eq 1 } 'New note was not persisted.'

    $note = (Get-State).Organizers[0].Notes[0]
    if ($note.ShowRuledLines -eq $true) { throw 'New notes should start without ruled lines.' }
    $id = ([Guid]$note.Id).ToString('N')
    $notePath = Join-Path $local "notes\$id.json"
    Wait-ForCondition { Test-Path -LiteralPath $notePath } 'New note document was not created.'
    if ((Get-Content -LiteralPath $notePath -Raw | ConvertFrom-Json).Html -ne '') {
        throw 'New note imported clipboard text instead of remaining blank.'
    }

    # Keep the reported regression first: this path skips clipboard, image, rename, and delete checks.
    $selector = "NoteItem-$id"
    winapp ui wait-for $selector -w $main.hwnd --timeout 3000 | Out-Null
    if ($TitleDragOnly) { winapp ui screenshot -w $main.hwnd -o (Join-Path $runRoot 'internal-note-grid.png') | Out-Null }
    winapp ui click $selector -w $main.hwnd | Out-Null
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq $note.Name).Count -eq 1 } 'Single click did not open the note.'
    $noteWindow = (Get-AppWindows $app.Id | Where-Object title -eq $note.Name)[0]
    winapp ui wait-for "NoteColor-$id" -w $noteWindow.hwnd --timeout 5000 | Out-Null
    winapp ui wait-for "NoteRuledLines-$id" -w $noteWindow.hwnd --timeout 5000 | Out-Null
    winapp ui wait-for "CloseNote-$id" -w $noteWindow.hwnd --timeout 5000 | Out-Null

    $beforePlacement = (Get-State).Organizers[0].Notes[0].Placement
    $beforeRect = [TuckPaneNoteInput+Rect]::new()
    [TuckPaneNoteInput]::GetWindowRect([IntPtr]$noteWindow.hwnd, [ref]$beforeRect) | Out-Null
    $scale = [Math]::Max(1.0, [double][TuckPaneNoteInput]::GetDpiForWindow([IntPtr]$noteWindow.hwnd) / 96.0)
    $fromX = [int]($beforeRect.Left + 80 * $scale)
    $fromY = [int]($beforeRect.Top + 22 * $scale)
    $toX = $fromX + 110
    $toY = $fromY + 90
    Set-ProbeForeground $noteWindow.hwnd
    [TuckPaneNoteInput]::SetCursorPos($fromX, $fromY) | Out-Null
    [TuckPaneNoteInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    foreach ($step in 1..16) {
        [TuckPaneNoteInput]::SetCursorPos(
            [int]($fromX + ($toX - $fromX) * $step / 16),
            [int]($fromY + ($toY - $fromY) * $step / 16)) | Out-Null
        Start-Sleep -Milliseconds 12
    }
    [TuckPaneNoteInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 300

    $afterRect = [TuckPaneNoteInput+Rect]::new()
    [TuckPaneNoteInput]::GetWindowRect([IntPtr]$noteWindow.hwnd, [ref]$afterRect) | Out-Null
    if ([Math]::Abs($afterRect.Left - $beforeRect.Left) -le 2 -and
        [Math]::Abs($afterRect.Top - $beforeRect.Top) -le 2) {
        throw "Dragging the dark title region did not move the note from $($beforeRect.Left),$($beforeRect.Top); start=$fromX,$fromY."
    }
    Wait-ForCondition {
        $afterPlacement = (Get-State).Organizers[0].Notes[0].Placement
        if ($null -eq $afterPlacement) { return $false }
        if ($null -eq $beforePlacement) { return $true }
        [Math]::Abs($afterPlacement.XDip - $beforePlacement.XDip) -gt 2 -or
            [Math]::Abs($afterPlacement.YDip - $beforePlacement.YDip) -gt 2
    } 'Dragging the dark title region moved the window but did not persist its position.'

    $buttonRect = [TuckPaneNoteInput+Rect]::new()
    winapp ui invoke "NoteColor-$id" -w $noteWindow.hwnd | Out-Null
    Start-Sleep -Milliseconds 150
    [TuckPaneNoteInput]::GetWindowRect([IntPtr]$noteWindow.hwnd, [ref]$buttonRect) | Out-Null
    if ($buttonRect.Left -ne $afterRect.Left -or $buttonRect.Top -ne $afterRect.Top) {
        throw 'Invoking a title-bar button moved the note window.'
    }
    winapp ui send-keys escape -w $noteWindow.hwnd | Out-Null
    if ($TitleDragOnly) {
        $title = @((winapp ui search $note.Name -w $noteWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches | Where-Object automationId -like 'NoteTitle-*')[0]
        winapp ui click $title.selector -w $noteWindow.hwnd | Out-Null
        $titleEditor = "NoteTitleEditor-$id"
        $longTitle = '这是一个足以占满标题栏的超长内部便签名称'
        winapp ui wait-for $titleEditor -w $noteWindow.hwnd --timeout 3000 | Out-Null
        winapp ui set-value $titleEditor $longTitle -w $noteWindow.hwnd | Out-Null
        winapp ui focus "NoteColor-$id" -w $noteWindow.hwnd | Out-Null
        Wait-ForCondition {
            (Get-State).Organizers[0].Notes[0].Name -eq $longTitle -and
            (Get-State).Organizers[0].ItemOrder[0] -eq "note:$id"
        } 'Inline renaming an internal note did not update its title while preserving ItemOrder.'
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq $longTitle).Count -eq 1 } `
            'Inline renaming an internal note did not update its window title.'
        $title = @((winapp ui search $longTitle -w $noteWindow.hwnd --json 2>$null |
            ConvertFrom-Json).matches | Where-Object automationId -like 'NoteTitle-*')[0]
        winapp ui click $title.selector -w $noteWindow.hwnd | Out-Null
        winapp ui wait-for $titleEditor -w $noteWindow.hwnd --timeout 3000 | Out-Null
        winapp ui set-value $titleEditor '取消改名' -w $noteWindow.hwnd | Out-Null
        winapp ui send-keys escape -w $noteWindow.hwnd | Out-Null
        Start-Sleep -Milliseconds 200
        if ((Get-State).Organizers[0].Notes[0].Name -ne $longTitle) { throw 'Escape did not cancel inline note renaming.' }
        $longBefore = [TuckPaneNoteInput+Rect]::new()
        [TuckPaneNoteInput]::GetWindowRect([IntPtr]$noteWindow.hwnd, [ref]$longBefore) | Out-Null
        $color = (winapp ui search "NoteColor-$id" -w $noteWindow.hwnd --json 2>$null | ConvertFrom-Json).matches[0]
        winapp ui drag "$([int]($color.x - 24)),$([int]($color.y + $color.height / 2))" `
            "$([int]($color.x + 46)),$([int]($color.y + $color.height / 2 + 50))" -w $noteWindow.hwnd | Out-Null
        $longAfter = [TuckPaneNoteInput+Rect]::new()
        [TuckPaneNoteInput]::GetWindowRect([IntPtr]$noteWindow.hwnd, [ref]$longAfter) | Out-Null
        if ($longAfter.Left -eq $longBefore.Left -and $longAfter.Top -eq $longBefore.Top) {
            throw 'A long note title consumed the entire draggable caption region.'
        }
        winapp ui invoke "CloseNote-$id" -w $noteWindow.hwnd | Out-Null
        Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq $longTitle).Count -eq 0 } `
            'Close did not hide the note after the title-drag check.'
        Write-Host 'TuckPane dark-title drag and internal rename UI: PASS'
        return
    }
    winapp ui invoke "CloseNote-$id" -w $noteWindow.hwnd | Out-Null
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq $note.Name).Count -eq 0 } 'Close did not hide the note after the title-drag check.'

    $pasteInvoked = $false
    foreach ($attempt in 1..3) {
        Set-ProbeForeground $main.hwnd
        winapp ui drag "$blankX,$blankY" "$blankX,$blankY" -w $main.hwnd --right --hold-ms 60 | Out-Null
        Start-Sleep -Milliseconds 250
        $menu = winapp ui search PasteMenuItem -w $main.hwnd --json 2>$null | ConvertFrom-Json
        if ($menu.matchCount -gt 0) {
            winapp ui invoke PasteMenuItem -w $main.hwnd | Out-Null
            if ($LASTEXITCODE -eq 0) { $pasteInvoked = $true; break }
        }
        winapp ui send-keys escape -w $main.hwnd | Out-Null
    }
    if (-not $pasteInvoked) { throw 'The background Paste command was not enabled for clipboard text.' }
    Wait-ForCondition { (Get-State).Organizers[0].Notes.Count -eq 2 } 'Pasting clipboard text did not create a note.'
    $pastedNote = @((Get-State).Organizers[0].Notes | Where-Object Id -ne $note.Id)[0]
    $pastedId = ([Guid]$pastedNote.Id).ToString('N')
    $pastedPath = Join-Path $local "notes\$pastedId.json"
    Wait-ForCondition { Test-Path -LiteralPath $pastedPath } 'Pasted note document was not created.'
    if ((Get-Content -LiteralPath $pastedPath -Raw | ConvertFrom-Json).Html -cne '第一行<br>  第二行 &lt;&gt;&amp;') {
        throw 'Pasted note did not preserve multiline plain text, indentation, or escaped symbols.'
    }
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq $pastedNote.Name).Count -eq 1 } 'Pasted note did not open immediately.'

    $pastedSelector = "NoteItem-$pastedId"
    winapp ui wait-for $pastedSelector -w $main.hwnd --timeout 3000 | Out-Null
    Open-NoteContextMenu $main.hwnd $pastedSelector
    winapp ui invoke DeleteNoteMenuItem -w $main.hwnd | Out-Null
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '删除便签').Count -eq 1 } 'Pasted-note delete confirmation did not appear.'
    $deleteDialog = (Get-AppWindows $app.Id | Where-Object title -eq '删除便签')[0]
    winapp ui invoke '删除' -w $deleteDialog.hwnd | Out-Null
    Wait-ForCondition { (Get-State).Organizers[0].Notes.Count -eq 1 } 'Pasted note was not removed after its clipboard checks.'

    Set-Clipboard -Value " `r`n`t"
    Set-ProbeForeground $main.hwnd
    winapp ui drag "$blankX,$blankY" "$blankX,$blankY" -w $main.hwnd --right --hold-ms 60 | Out-Null
    Start-Sleep -Milliseconds 250
    winapp ui invoke PasteMenuItem -w $main.hwnd | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The background Paste command was unavailable for whitespace-only text.' }
    Start-Sleep -Milliseconds 300
    if ((Get-State).Organizers[0].Notes.Count -ne 1) { throw 'Whitespace-only clipboard text created a note.' }

    $onePixelPng = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Zl1EAAAAASUVORK5CYII='
    [IO.File]::WriteAllText($notePath,
        (@{ Version = 1; Html = "<img src=`"data:image/png;base64,$onePixelPng`" style=`"width: 96px;`">" } |
            ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    winapp ui wait-for $selector -w $main.hwnd --timeout 3000 | Out-Null
    winapp ui focus CollapseButton -w $main.hwnd | Out-Null
    winapp ui click $selector -w $main.hwnd | Out-Null
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq $note.Name).Count -eq 1 } 'Single click did not open the note.'
    $noteWindow = (Get-AppWindows $app.Id | Where-Object title -eq $note.Name)[0]
    winapp ui wait-for "NoteColor-$id" -w $noteWindow.hwnd --timeout 5000 | Out-Null
    winapp ui wait-for "NoteRuledLines-$id" -w $noteWindow.hwnd --timeout 5000 | Out-Null
    winapp ui wait-for "CloseNote-$id" -w $noteWindow.hwnd --timeout 5000 | Out-Null
    $extendedStyle = [TuckPaneNoteInput]::GetWindowLongPtr([IntPtr]$noteWindow.hwnd, -20).ToInt64()
    $windowStyle = [TuckPaneNoteInput]::GetWindowLongPtr([IntPtr]$noteWindow.hwnd, -16).ToInt64()
    if (($extendedStyle -band 0x8) -ne 0 -or ($extendedStyle -band 0x80) -ne 0 -or
        ($extendedStyle -band 0x40000) -eq 0 -or ($windowStyle -band 0x40000) -eq 0) {
        throw 'The note is not a non-topmost, resizable app window shown in switchers.'
    }
    $monitor = [TuckPaneNoteInput]::MonitorFromWindow([IntPtr]$noteWindow.hwnd, 2)
    [int]$scalePercent = 100
    if ([TuckPaneNoteInput]::GetScaleFactorForMonitor($monitor, [ref]$scalePercent) -ne 0) {
        throw 'Could not read the note monitor scale.'
    }
    $scale = [Math]::Max(1.0, [double]$scalePercent / 100.0)
    if ([Math]::Abs($noteWindow.width / $scale - 360) -gt 3 -or
        [Math]::Abs($noteWindow.height / $scale - 300) -gt 3) {
        throw "Unexpected initial note size: $($noteWindow.width)x$($noteWindow.height) px at $scalePercent%."
    }

    Wait-ForCondition {
        (winapp ui search '粘贴的图片' -w $noteWindow.hwnd --json 2>$null | ConvertFrom-Json).matchCount -gt 0
    } 'The persisted inline image did not render.' 5000
    $image = (winapp ui search '粘贴的图片' -w $noteWindow.hwnd --json 2>$null | ConvertFrom-Json).matches[0]
    winapp ui click $image.selector -w $noteWindow.hwnd | Out-Null
    winapp ui send-keys 'alt+right' -w $noteWindow.hwnd --via send-input | Out-Null
    Wait-ForCondition { ((Get-Content -LiteralPath $notePath -Raw | ConvertFrom-Json).Html -like '*width: 104px*') } 'Keyboard image resizing was not persisted.'
    Set-ProbeForeground $noteWindow.hwnd
    winapp ui click $image.selector -w $noteWindow.hwnd | Out-Null
    Start-Sleep -Milliseconds 150
    [TuckPaneNoteInput]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    try {
        winapp ui scroll $image.selector -w $noteWindow.hwnd --wheel 1 | Out-Null
    }
    finally {
        [TuckPaneNoteInput]::keybd_event(0x11, 0, 2, [UIntPtr]::Zero)
    }
    Wait-ForCondition { (Get-State).Organizers[0].Notes[0].FontSize -eq 15 } 'Ctrl+wheel did not persist the note font size.'

    winapp ui invoke "NoteRuledLines-$id" -w $noteWindow.hwnd | Out-Null
    Wait-ForCondition { (Get-State).Organizers[0].Notes[0].ShowRuledLines -eq $true } 'Ruled lines were not enabled and persisted.'

    winapp ui invoke "NoteColor-$id" -w $noteWindow.hwnd | Out-Null
    Start-Sleep -Milliseconds 250
    $themeItems = @((winapp ui inspect -a $app.Id --interactive --json 2>$null | ConvertFrom-Json).windows.elements |
        Where-Object automationId -like 'NoteTheme-*')
    if ($themeItems.Count -ne 7) { throw "Expected seven note colors, found $($themeItems.Count)." }
    winapp ui send-keys escape -w $noteWindow.hwnd | Out-Null

    winapp ui screenshot -w $noteWindow.hwnd -o (Join-Path $runRoot 'note-window.png') | Out-Null
    winapp ui invoke "CloseNote-$id" -w $noteWindow.hwnd | Out-Null
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq $note.Name).Count -eq 0 } 'Close did not hide the note.'
    if ((Get-State).Organizers[0].Notes.Count -ne 1) { throw 'Closing the note deleted its icon.' }

    Stop-Process -Id $app.Id -Force
    $app.WaitForExit(5000) | Out-Null
    $app.Dispose()
    $app = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq 'TuckPane').Count -eq 1 } 'Organizer did not restart.'
    if ((Get-AppWindows $app.Id | Where-Object title -eq $note.Name).Count -ne 0) { throw 'A note reopened automatically after restart.' }
    $main = (Get-AppWindows $app.Id | Where-Object title -eq 'TuckPane')[0]
    winapp ui wait-for $selector -w $main.hwnd --timeout 5000 | Out-Null
    if ((Get-State).Organizers[0].Notes[0].FontSize -ne 15 -or
        (Get-State).Organizers[0].Notes[0].ShowRuledLines -ne $true -or
        (Get-Content -LiteralPath $notePath -Raw | ConvertFrom-Json).Html -notlike '*width: 104px*') {
        throw 'Note content, image size, font size, or ruled-lines state did not survive restart.'
    }

    Open-NoteContextMenu $main.hwnd $selector
    winapp ui invoke RenameNoteMenuItem -w $main.hwnd | Out-Null
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '重命名便签').Count -eq 1 } 'Rename dialog did not appear.'
    $renameDialog = (Get-AppWindows $app.Id | Where-Object title -eq '重命名便签')[0]
    $input = @((winapp ui inspect -w $renameDialog.hwnd --interactive --json 2>$null | ConvertFrom-Json).windows.elements |
        Where-Object type -eq 'Edit')[0]
    winapp ui set-value $input.selector '工作便签' -w $renameDialog.hwnd | Out-Null
    winapp ui invoke '重命名' -w $renameDialog.hwnd | Out-Null
    Wait-ForCondition { (Get-State).Organizers[0].Notes[0].Name -eq '工作便签' } 'Renamed note was not persisted.'

    Open-NoteContextMenu $main.hwnd $selector
    winapp ui invoke DeleteNoteMenuItem -w $main.hwnd | Out-Null
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '删除便签').Count -eq 1 } 'Delete confirmation did not appear.'
    $deleteDialog = (Get-AppWindows $app.Id | Where-Object title -eq '删除便签')[0]
    winapp ui invoke '取消' -w $deleteDialog.hwnd | Out-Null
    if ((Get-State).Organizers[0].Notes.Count -ne 1) { throw 'Cancelling delete removed the note.' }

    Open-NoteContextMenu $main.hwnd $selector
    winapp ui invoke DeleteNoteMenuItem -w $main.hwnd | Out-Null
    Wait-ForCondition { (Get-AppWindows $app.Id | Where-Object title -eq '删除便签').Count -eq 1 } 'Second delete confirmation did not appear.'
    $deleteDialog = (Get-AppWindows $app.Id | Where-Object title -eq '删除便签')[0]
    winapp ui invoke '删除' -w $deleteDialog.hwnd | Out-Null
    Wait-ForCondition { (Get-State).Organizers[0].Notes.Count -eq 0 } 'Confirmed delete did not remove the note.'
    if (Test-Path -LiteralPath (Join-Path $local "notes\$id.json")) { throw 'Confirmed delete left the note document behind.' }

    Write-Host 'TuckPane note real UI flow: PASS'
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($app) { $app.Dispose() }
    $env:TUCKPANE_TEST_ROOT = $originalRoot
    $env:GLASSFOLDER_TEST_EXPANDED = $originalExpanded
    Set-Clipboard -Value ($originalClipboardText ?? '')
    if ($KeepRoot) { Write-Host "Kept test root: $runRoot" }
    elseif (Test-Path -LiteralPath $runRoot) {
        $resolvedRoot = [IO.Path]::GetFullPath($runRoot)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($resolvedRoot).StartsWith('TuckPane-note-ui-', [StringComparison]::Ordinal)) {
            throw "Refusing to delete unexpected test root: $resolvedRoot"
        }
        [IO.Directory]::Delete($resolvedRoot, $true)
    }
}
