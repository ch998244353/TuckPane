param(
    [string]$AppExe = (Join-Path $PSScriptRoot '..\src\TuckPane\bin\x64\Release\net10.0-windows10.0.22621.0\TuckPane.exe'),
    [string]$ProbeExe = (Join-Path $PSScriptRoot 'TuckPane.LogicChecks\bin\x64\Release\net10.0-windows10.0.22621.0\TuckPane.LogicChecks.exe'),
    [int[]]$Modes = @(0, 1, 2),
    [switch]$Aug26Only,
    [switch]$FileActionsOnly,
    [switch]$KeepRoot
)

$ErrorActionPreference = 'Stop'
if ($FileActionsOnly -and ($Modes.Count -ne 1 -or $Modes[0] -ne 0)) {
    throw '-FileActionsOnly requires exactly -Modes 0.'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
public static class TuckPaneDropInput {
    public delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint x, uint y, uint data, UIntPtr extraInfo);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    public static void MoveAbsolute(int x, int y) {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        uint absoluteX = (uint)Math.Round((x - left) * 65535d / Math.Max(1, width - 1));
        uint absoluteY = (uint)Math.Round((y - top) * 65535d / Math.Max(1, height - 1));
        mouse_event(0xC001, absoluteX, absoluteY, 0, UIntPtr.Zero);
    }
    public static IntPtr FindPopupMenu(uint processId) {
        IntPtr result = IntPtr.Zero;
        EnumWindows((window, parameter) => {
            uint ownerProcessId;
            GetWindowThreadProcessId(window, out ownerProcessId);
            var className = new StringBuilder(32);
            if (ownerProcessId == processId && IsWindowVisible(window) &&
                GetClassName(window, className, className.Capacity) > 0 && className.ToString() == "#32768") {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }
}

public sealed class TuckPaneClipboardState {
    public bool HasFileDrop;
    public string[] Paths = Array.Empty<string>();
    public uint PreferredEffect;
}

public static class TuckPaneClipboardProbe {
    public static TuckPaneClipboardState Read() {
        TuckPaneClipboardState result = null;
        Exception failure = null;
        var thread = new Thread(() => {
            for (int attempt = 0; attempt < 20; attempt++) {
                try {
                    System.Windows.Forms.IDataObject data = Clipboard.GetDataObject();
                    if (data == null) throw new InvalidOperationException("Clipboard is empty.");
                    result = new TuckPaneClipboardState { HasFileDrop = data.GetDataPresent(DataFormats.FileDrop) };
                    if (result.HasFileDrop && data.GetData(DataFormats.FileDrop) is string[] paths) result.Paths = paths;
                    object preferred = data.GetData("Preferred DropEffect", false);
                    byte[] bytes = preferred is MemoryStream stream ? stream.ToArray() : preferred as byte[];
                    if (bytes != null && bytes.Length >= sizeof(uint)) result.PreferredEffect = BitConverter.ToUInt32(bytes, 0);
                    return;
                }
                catch (ExternalException ex) when (attempt < 19) {
                    failure = ex;
                    Thread.Sleep(50);
                }
                catch (Exception ex) {
                    failure = ex;
                    return;
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (result == null) throw failure ?? new InvalidOperationException("Clipboard could not be read.");
        return result;
    }
}
'@ -ReferencedAssemblies System.Windows.Forms,System.Threading.Thread

function Wait-VisibleWindow([int]$AppPid, [int]$TimeoutMs = 10000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        $windows = @(winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json)
        $window = $windows | Where-Object title -eq 'TuckPane' | Select-Object -First 1
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "TuckPane window did not become visible for PID $AppPid."
}

function Wait-AppWindowTitle([int]$AppPid, [string]$Title, [int]$TimeoutMs = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
    do {
        $window = @(winapp ui list-windows -a $AppPid --json 2>$null | ConvertFrom-Json) |
            Where-Object title -eq $Title | Select-Object -First 1
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Window '$Title' did not become visible for PID $AppPid."
}

function Expand-Station([int]$AppPid) {
    $middle = [int]([TuckPaneDropInput]::GetSystemMetrics(0) / 2)
    [TuckPaneDropInput]::SetCursorPos($middle, 0) | Out-Null
    return Wait-VisibleWindow $AppPid
}

function Invoke-MouseDrag([int]$FromX, [int]$FromY, [int]$ToX, [int]$ToY, [long]$Hwnd) {
    [TuckPaneDropInput]::MoveAbsolute($FromX, $FromY)
    Start-Sleep -Milliseconds 150
    [TuckPaneDropInput]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 400
    foreach ($step in 1..32) {
        [TuckPaneDropInput]::MoveAbsolute(
            [int]($FromX + ($ToX - $FromX) * $step / 32),
            [int]($FromY + ($ToY - $FromY) * $step / 32))
        Start-Sleep -Milliseconds 20
    }
    Start-Sleep -Milliseconds 600
    [TuckPaneDropInput]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Start-DropTarget(
    [string]$Root,
    [ValidateSet('Copy', 'Move', 'None')][string]$Effect = 'Copy',
    [string]$EvidencePath = '') {
    $stdout = Join-Path $Root ("target-" + [Guid]::NewGuid().ToString('N') + '.out')
    $stderr = [IO.Path]::ChangeExtension($stdout, '.err')
    $targetArgument = if ([string]::IsNullOrWhiteSpace($EvidencePath)) { $Effect } else { "$Effect|$EvidencePath" }
    $process = Start-Process -FilePath $ProbeExe -ArgumentList '--external-file-drop-target', $targetArgument `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        if (Test-Path -LiteralPath $stdout) {
            $ready = Get-Content -LiteralPath $stdout -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($ready -match '^READY\t(-?\d+)\t(-?\d+)$') {
                return [pscustomobject]@{
                    Process = $process; X = [int]$Matches[1]; Y = [int]$Matches[2]
                    Out = $stdout; Err = $stderr; Effect = $Effect; EvidencePath = $EvidencePath
                }
            }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $process.HasExited) { $process.Kill() }
    throw 'External drop target did not become ready.'
}

function Test-Drop([int]$AppPid, [long]$Hwnd, [string]$Name, [string]$ExpectedPath, [string]$Root, [bool]$FocusSource) {
    $match = (winapp ui search $Name -w $Hwnd --json 2>$null | ConvertFrom-Json).matches | Select-Object -First 1
    if ($null -eq $match) { throw "Could not find '$Name' in TuckPane." }
    $target = $null
    try {
        if ($FocusSource) { winapp ui focus CollapseButton -w $Hwnd | Out-Null }
        [TuckPaneDropInput]::SetForegroundWindow([IntPtr]$Hwnd) | Out-Null
        $sourceX = [int]($match.x + $match.width / 2)
        $sourceY = [int]($match.y + $match.height / 2)
        $hit = [TuckPaneDropInput]::WindowFromPoint([TuckPaneDropInput+Point]@{ X = $sourceX; Y = $sourceY })
        $hitProcess = [uint32]0
        [TuckPaneDropInput]::GetWindowThreadProcessId($hit, [ref]$hitProcess) | Out-Null
        if ($hitProcess -ne $AppPid) { throw "Source '$Name' for app PID $AppPid is covered by PID $hitProcess at $sourceX,$sourceY." }
        $target = Start-DropTarget $Root
        if ($FocusSource) { winapp ui focus CollapseButton -w $Hwnd | Out-Null }
        [TuckPaneDropInput]::SetForegroundWindow([IntPtr]$Hwnd) | Out-Null
        Invoke-MouseDrag $sourceX $sourceY $target.X $target.Y $Hwnd
        if (-not $target.Process.WaitForExit(5000)) {
            $observedOut = Get-Content -LiteralPath $target.Out -ErrorAction SilentlyContinue
            $observedErr = Get-Content -LiteralPath $target.Err -ErrorAction SilentlyContinue
            throw "Drop target did not receive '$Name'. stdout=$observedOut stderr=$observedErr"
        }
        $drop = Get-Content -LiteralPath $target.Out | Select-Object -Last 1
        $enter = Get-Content -LiteralPath $target.Err | Select-Object -Last 1
        if ($drop -notmatch '^DROP\t(.+)$') { throw "Drop target did not report a DROP for '$Name': $drop" }
        $paths = @($Matches[1] | ConvertFrom-Json)
        if ($paths.Count -ne 1 -or [IO.Path]::GetFullPath($paths[0]) -ne [IO.Path]::GetFullPath($ExpectedPath)) {
            throw "Drop target received the wrong path for '$Name': $drop"
        }
        if ($enter -notmatch 'FileDrop=True' -or $enter -notmatch 'Allowed=Copy, Move, Link') {
            throw "Drop target did not receive FileDrop with Copy|Move|Link for '$Name': $enter"
        }
        if (-not (Test-Path -LiteralPath $ExpectedPath)) { throw "Copy drop removed '$ExpectedPath'." }
    }
    finally {
        if ($null -ne $target) {
            if (-not $target.Process.HasExited) { $target.Process.Kill() }
            $target.Process.Dispose()
        }
    }
}

function Assert-PortableNoteEvidence(
    [string]$EvidencePath,
    [string]$StagingPath,
    [string]$ExpectedHtml) {
    if (Test-Path -LiteralPath $StagingPath) {
        throw "Portable-note staging source was not cleaned after Drop returned: $StagingPath"
    }
    if (-not (Test-Path -LiteralPath $EvidencePath -PathType Leaf)) {
        throw "Portable-note evidence did not survive staging cleanup: $EvidencePath"
    }
    $portable = [IO.File]::ReadAllText($EvidencePath) | ConvertFrom-Json
    foreach ($required in @('format', 'version', 'theme', 'fontSize', 'showRuledLines', 'placement', 'html')) {
        if ($required -notin $portable.PSObject.Properties.Name) {
            throw "Portable-note evidence is missing '$required'."
        }
    }
    if ($portable.format -ne 'TuckPane.Note' -or $portable.version -ne 1 -or
        $portable.theme -ne 6 -or $portable.fontSize -ne 17 -or
        $portable.showRuledLines -ne $true -or $portable.html -ne $ExpectedHtml) {
        throw "Portable-note evidence fields changed during cross-process Drop: $EvidencePath"
    }
    if ($null -eq $portable.placement -or $portable.placement.monitorDevice -ne 'TEST-MONITOR' -or
        $portable.placement.xDip -ne 40 -or $portable.placement.yDip -ne 50 -or
        $portable.placement.widthDip -ne 360 -or $portable.placement.heightDip -ne 300) {
        throw "Portable-note evidence placement is not readable: $EvidencePath"
    }
    if ($portable.html -notmatch 'data:image/png;base64,AA==') {
        throw "Portable-note evidence did not preserve the inline image data URI."
    }
}

function Test-NoteDrop(
    [int]$AppPid,
    [long]$Hwnd,
    [string]$Name,
    [string]$NoteId,
    [string]$StatePath,
    [string]$NotePath,
    [string]$Root,
    [bool]$FocusSource,
    [ValidateSet('Copy', 'Move', 'None')][string]$Effect,
    [string]$ExpectedHtml) {
    $match = (winapp ui search $Name -w $Hwnd --json 2>$null | ConvertFrom-Json).matches | Select-Object -First 1
    if ($null -eq $match) { throw "Could not find note '$Name' in TuckPane." }
    $evidence = if ($Effect -eq 'None') { '' } else {
        Join-Path $Root ("note-$($Effect.ToLowerInvariant())-" + [Guid]::NewGuid().ToString('N') + '.tucknote')
    }
    $target = $null
    try {
        if ($FocusSource) { winapp ui focus CollapseButton -w $Hwnd | Out-Null }
        [TuckPaneDropInput]::SetForegroundWindow([IntPtr]$Hwnd) | Out-Null
        $sourceX = [int]($match.x + $match.width / 2)
        $sourceY = [int]($match.y + $match.height / 2)
        $hit = [TuckPaneDropInput]::WindowFromPoint([TuckPaneDropInput+Point]@{ X = $sourceX; Y = $sourceY })
        $hitProcess = [uint32]0
        [TuckPaneDropInput]::GetWindowThreadProcessId($hit, [ref]$hitProcess) | Out-Null
        if ($hitProcess -ne $AppPid) { throw "Note '$Name' is covered by PID $hitProcess at $sourceX,$sourceY." }

        $target = Start-DropTarget $Root $Effect $evidence
        if ($FocusSource) { winapp ui focus CollapseButton -w $Hwnd | Out-Null }
        [TuckPaneDropInput]::SetForegroundWindow([IntPtr]$Hwnd) | Out-Null
        Invoke-MouseDrag $sourceX $sourceY $target.X $target.Y $Hwnd

        if ($Effect -ne 'None' -and -not $target.Process.WaitForExit(5000)) {
            $observedOut = Get-Content -LiteralPath $target.Out -ErrorAction SilentlyContinue
            $observedErr = Get-Content -LiteralPath $target.Err -ErrorAction SilentlyContinue
            throw "Drop target did not finish the $Effect note Drop. stdout=$observedOut stderr=$observedErr"
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            $pathLine = Get-Content -LiteralPath $target.Out -ErrorAction SilentlyContinue |
                Where-Object { $_ -match '^PATHS\t' } | Select-Object -Last 1
            if ($null -eq $pathLine) { Start-Sleep -Milliseconds 50 }
        } while ($null -eq $pathLine -and [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $pathLine) { throw "Drop target did not observe the note FileDrop paths for $Effect." }
        $paths = @($pathLine.Substring(6) | ConvertFrom-Json)
        if ($paths.Count -ne 1 -or [IO.Path]::GetExtension($paths[0]) -ne '.tucknote') {
            throw "Drop target observed an invalid portable-note path: $pathLine"
        }
        $stagingPath = [IO.Path]::GetFullPath($paths[0])
        $isolatedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
        if (-not $stagingPath.StartsWith($isolatedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Portable-note staging escaped TUCKPANE_TEST_ROOT: $stagingPath"
        }

        $enter = Get-Content -LiteralPath $target.Err -ErrorAction SilentlyContinue |
            Where-Object { $_ -match '^ENTER\t' } | Select-Object -Last 1
        if ($enter -notmatch 'FileDrop=True' -or $enter -notmatch 'Allowed=Copy, Move' -or
            $enter -notmatch "Selected=$Effect") {
            throw "Drop target did not negotiate note $Effect from Copy|Move: $enter"
        }
        $drop = Get-Content -LiteralPath $target.Out -ErrorAction SilentlyContinue |
            Where-Object { $_ -match '^DROP\t' } | Select-Object -Last 1
        if ($Effect -eq 'None' -and $null -ne $drop) {
            throw "Cancelled note drag unexpectedly reached Drop: $drop"
        }
        if ($Effect -ne 'None' -and $null -eq $drop) {
            throw "Drop target did not report a $Effect note Drop."
        }

        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        while ((Test-Path -LiteralPath $stagingPath) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 50
        }
        if (Test-Path -LiteralPath $stagingPath) {
            throw "Portable-note staging was not cleaned after ${Effect}: $stagingPath"
        }

        $shouldRemain = $Effect -ne 'Move'
        $noteKey = "note:$NoteId"
        $deadline = [DateTime]::UtcNow.AddSeconds(5)
        do {
            try {
                $savedState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
                $organizer = $savedState.Organizers[0]
                $definitionRemains = @($organizer.Notes | Where-Object { ([Guid]$_.Id).ToString('N') -eq $NoteId }).Count -eq 1
                $orderRemains = $noteKey -in @($organizer.ItemOrder)
                $documentRemains = Test-Path -LiteralPath $NotePath
                $settled = $definitionRemains -eq $shouldRemain -and
                    $orderRemains -eq $shouldRemain -and $documentRemains -eq $shouldRemain
            }
            catch { $settled = $false }
            if (-not $settled) { Start-Sleep -Milliseconds 50 }
        } while (-not $settled -and [DateTime]::UtcNow -lt $deadline)
        if (-not $settled) {
            throw "Note retention state did not settle after $Effect (expected retained=$shouldRemain)."
        }

        if ($Effect -ne 'None') {
            Assert-PortableNoteEvidence $evidence $stagingPath $ExpectedHtml
        }
    }
    finally {
        if ($null -ne $target) {
            if (-not $target.Process.HasExited) { $target.Process.Kill() }
            $target.Process.Dispose()
        }
    }
}

function Test-Reorder([int]$AppPid, [long]$Hwnd, [string]$SourceName, [string]$TargetName, [string]$StatePath) {
    $source = (winapp ui search $SourceName -w $Hwnd --json 2>$null | ConvertFrom-Json).matches | Select-Object -First 1
    $target = (winapp ui search $TargetName -w $Hwnd --json 2>$null | ConvertFrom-Json).matches | Select-Object -First 1
    if ($null -eq $source -or $null -eq $target) { throw "Could not find reorder pair '$SourceName' -> '$TargetName'." }
    $before = @((Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json).Organizers[0].ItemOrder)
    [TuckPaneDropInput]::SetForegroundWindow([IntPtr]$Hwnd) | Out-Null
    Invoke-MouseDrag ([int]($source.x + $source.width / 2)) ([int]($source.y + $source.height / 2)) `
        ([int]($target.x + $target.width / 2)) ([int]($target.y + $target.height / 2)) $Hwnd
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    do {
        Start-Sleep -Milliseconds 100
        $after = @((Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json).Organizers[0].ItemOrder)
    } while (($after -join '|') -eq ($before -join '|') -and [DateTime]::UtcNow -lt $deadline)
    if (($after -join '|') -eq ($before -join '|')) { throw "Internal reorder did not persist for '$SourceName'." }
}

function Open-FileContextMenu([int]$AppPid, [long]$Hwnd, [string]$Name) {
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        $match = (winapp ui search $Name -w $Hwnd --json 2>$null | ConvertFrom-Json).matches | Select-Object -First 1
        if ($null -eq $match) { Start-Sleep -Milliseconds 50 }
    } while ($null -eq $match -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $match) { throw "Could not find '$Name' for the context-menu check." }
    winapp ui focus CollapseButton -w $Hwnd | Out-Null
    [TuckPaneDropInput]::SetForegroundWindow([IntPtr]$Hwnd) | Out-Null
    $clicked = $false
    foreach ($attempt in 1..3) {
        winapp ui click $match.selector -w $Hwnd --right -q 2>$null
        if ($LASTEXITCODE -eq 0) {
            $clicked = $true
            break
        }
        $match = (winapp ui search $Name -w $Hwnd --json 2>$null | ConvertFrom-Json).matches |
            Select-Object -First 1
        if ($null -eq $match) { break }
    }
    if (-not $clicked) { throw "Could not right-click '$Name'." }
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 50
        try {
            $menuCondition = [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $AppPid),
                [System.Windows.Automation.PropertyCondition]::new(
                    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                    [System.Windows.Automation.ControlType]::MenuItem))
            $menuItems = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants, $menuCondition)
            $fileMenuItems = @($menuItems |
                Where-Object { -not $_.Current.IsOffscreen -and
                    $_.Current.AutomationId -match '^(Rename|Copy|Cut|Delete)FileMenuItem$' } |
                Sort-Object @{ Expression = { $_.Current.BoundingRectangle.Top } },
                    @{ Expression = { $_.Current.BoundingRectangle.Left } })
            $fileMenuIds = @($fileMenuItems | ForEach-Object { $_.Current.AutomationId })
        }
        catch {
            $fileMenuItems = @()
            $fileMenuIds = @()
        }
    } while ($fileMenuIds.Count -ne 4 -and [DateTime]::UtcNow -lt $deadline)
    if (($fileMenuIds -join ',') -ne 'RenameFileMenuItem,CopyFileMenuItem,CutFileMenuItem,DeleteFileMenuItem') {
        throw "File menu for '$Name' was not Rename/Copy/Cut/Delete in order: $($fileMenuIds -join ',')"
    }
    return $fileMenuItems
}

function Invoke-AutomationElement([System.Windows.Automation.AutomationElement]$Element) {
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw "Element does not support InvokePattern: $($Element.Current.AutomationId)"
    }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Close-ContextMenu {
    [TuckPaneDropInput]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [TuckPaneDropInput]::keybd_event(0x1B, 0, 0x0002, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 200
}

function Test-ContextMenu([int]$AppPid, [long]$Hwnd, [string]$Name) {
    $null = @(Open-FileContextMenu $AppPid $Hwnd $Name)
    Close-ContextMenu
}

function Test-CopyClipboard([int]$AppPid, [long]$Hwnd, [string]$Name, [string]$ExpectedPath) {
    $items = @(Open-FileContextMenu $AppPid $Hwnd $Name)
    Invoke-AutomationElement ($items | Where-Object { $_.Current.AutomationId -eq 'CopyFileMenuItem' })
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 50
        try {
            $clipboard = [TuckPaneClipboardProbe]::Read()
            $settled = $clipboard.HasFileDrop -and $clipboard.Paths.Count -eq 1 -and
                [IO.Path]::GetFullPath($clipboard.Paths[0]) -eq [IO.Path]::GetFullPath($ExpectedPath) -and
                $clipboard.PreferredEffect -eq 1
        }
        catch { $settled = $false }
    } while (-not $settled -and [DateTime]::UtcNow -lt $deadline)
    if (-not $settled) {
        throw "Copy for '$Name' did not publish CF_HDROP with Preferred DropEffect=Copy."
    }
}

function Invoke-FileRename(
    [int]$AppPid,
    [long]$Hwnd,
    [string]$Name,
    [string]$NewBaseName,
    [string]$OldPath,
    [string]$ExpectedPath) {
    $items = @(Open-FileContextMenu $AppPid $Hwnd $Name)
    Invoke-AutomationElement ($items | Where-Object { $_.Current.AutomationId -eq 'RenameFileMenuItem' })
    $dialog = Wait-AppWindowTitle $AppPid '重命名项目'
    $input = @((winapp ui inspect -w $dialog.hwnd --interactive --json 2>$null | ConvertFrom-Json).windows.elements |
        Where-Object type -eq 'Edit')[0]
    if ($null -eq $input) { throw "Rename input did not appear for '$Name'." }
    winapp ui set-value $input.selector $NewBaseName -w $dialog.hwnd | Out-Null
    winapp ui invoke '重命名' -w $dialog.hwnd | Out-Null
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (((Test-Path -LiteralPath $OldPath) -or -not (Test-Path -LiteralPath $ExpectedPath)) -and
        [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 50
    }
    if ((Test-Path -LiteralPath $OldPath) -or -not (Test-Path -LiteralPath $ExpectedPath)) {
        throw "Rename for '$Name' did not produce '$ExpectedPath'."
    }
}

$originalRoot = $env:TUCKPANE_TEST_ROOT
$originalExpanded = $env:GLASSFOLDER_TEST_EXPANDED
$originalClipboardText = if ($FileActionsOnly) { [string](Get-Clipboard -Raw -ErrorAction SilentlyContinue) } else { $null }
$root = Join-Path ([IO.Path]::GetTempPath()) ("TuckPane-main-window-drop-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    foreach ($mode in $Modes) {
        $modeRoot = Join-Path $root "mode-$mode"
        $storage = Join-Path $modeRoot 'UserProfile\TuckPane\Windows\Probe'
        $local = Join-Path $modeRoot 'LocalAppData\TuckPane'
        New-Item -ItemType Directory -Path (Join-Path $storage 'mode-folder'), $local -Force | Out-Null
        $file = Join-Path $storage 'mode-file.txt'
        $folder = Join-Path $storage 'mode-folder'
        $shortcut = Join-Path $storage 'mode-link.lnk'
        $internetShortcut = Join-Path $storage 'mode-site.url'
        $portableNote = Join-Path $storage 'mode-portable.tucknote'
        $noteId = [Guid]("10000000-0000-0000-0000-00000000000$mode")
        $noteIdN = $noteId.ToString('N')
        $noteName = "mode-$mode-note"
        $noteHtml = "<div>mode $mode portable evidence</div><img src=`"data:image/png;base64,AA==`">"
        $notesRoot = Join-Path $local 'notes'
        $notePath = Join-Path $notesRoot "$noteIdN.json"
        [IO.File]::WriteAllText($file, 'file probe')
        [IO.File]::WriteAllText((Join-Path $folder 'inside.txt'), 'folder probe')
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($shortcut)
        $link.TargetPath = $file
        $link.Save()
        [IO.File]::WriteAllText($internetShortcut, "[InternetShortcut]`r`nURL=https://example.com/`r`n")
        [IO.File]::WriteAllText($portableNote,
            '{"format":"TuckPane.Note","version":1,"theme":3,"fontSize":14,"showRuledLines":false,"placement":null,"html":"portable"}',
            [Text.UTF8Encoding]::new($false))
        New-Item -ItemType Directory -Path $notesRoot -Force | Out-Null
        @{ Version = 1; Html = $noteHtml } | ConvertTo-Json -Compress |
            Set-Content -LiteralPath $notePath -Encoding utf8
        $state = @{
            SchemaVersion = 7
            GlobalSettings = @{ ThemeColorArgb = 4293060073; Material = 0; ThemeTransparency = 0.35; StartWithWindows = $false; Language = 0; CollapseOnOutsideClick = $false; ExpandOnHover = $false }
            ConsolePlacement = $null
            Organizers = @(@{
                Id = "00000000-0000-0000-0000-00000000000$mode"
                Name = "Mode $mode probe"
                CreatedAtUtc = [DateTimeOffset]::UtcNow
                PlacementMode = $mode
                DockEdge = 1
                Layout = @{ Mode = 0; Rows = 2; Columns = 3 }
                CompactScale = 1.2; CanvasScale = 0.55; ItemScale = 0.8; NameScale = 1.0
                ManualCanvasBaseWidthDip = $null; ManualCanvasBaseHeightDip = $null; Position = $null
                StorageRelativePath = 'Windows\Probe'; StorageAbsolutePath = $null
                ItemOrder = @('mode-file.txt', 'mode-folder', 'mode-link.lnk', 'mode-site.url', 'mode-portable.tucknote', "note:$noteIdN")
                Notes = @(@{
                    Id = $noteId; Name = $noteName; Theme = 6; FontSize = 17; ShowRuledLines = $true
                    Placement = @{ MonitorDevice = 'TEST-MONITOR'; XDip = 40; YDip = 50; WidthDip = 360; HeightDip = 300 }
                })
            })
        }
        $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $local 'state.json') -Encoding utf8
        $statePath = Join-Path $local 'state.json'

        $env:TUCKPANE_TEST_ROOT = $modeRoot
        $env:GLASSFOLDER_TEST_EXPANDED = if ($mode -eq 2) { '0' } else { '1' }
        $app = Start-Process -FilePath $AppExe -ArgumentList '--startup' -PassThru
        try {
            Write-Host "Testing mode $mode..."
            $window = if ($mode -eq 2) { Expand-Station $app.Id } else { Wait-VisibleWindow $app.Id }
            if ($mode -ne 2) { winapp ui wait-for CollapseButton -w $window.hwnd --timeout 10000 | Out-Null }
            Start-Sleep -Milliseconds 700
            if ($FileActionsOnly) {
                foreach ($name in @('mode-file.txt', 'mode-folder', 'mode-link', 'mode-site', 'mode-portable')) {
                    Test-ContextMenu $app.Id $window.hwnd $name
                }
                Test-CopyClipboard $app.Id $window.hwnd 'mode-file.txt' $file
                $renamedFile = Join-Path $storage 'renamed-file.txt'
                $renamedPortableNote = Join-Path $storage 'renamed-portable.tucknote'
                Invoke-FileRename $app.Id $window.hwnd 'mode-file.txt' 'renamed-file' $file $renamedFile
                Invoke-FileRename $app.Id $window.hwnd 'mode-portable' 'renamed-portable' $portableNote $renamedPortableNote
                $deadline = [DateTime]::UtcNow.AddSeconds(5)
                do {
                    $savedOrder = @((Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).Organizers[0].ItemOrder)
                    $orderSettled = 'renamed-file.txt' -in $savedOrder -and 'renamed-portable.tucknote' -in $savedOrder -and
                        'mode-file.txt' -notin $savedOrder -and 'mode-portable.tucknote' -notin $savedOrder
                    if (-not $orderSettled) { Start-Sleep -Milliseconds 50 }
                } while (-not $orderSettled -and [DateTime]::UtcNow -lt $deadline)
                if (-not $orderSettled) {
                    throw "Renamed file entries were not persisted in ItemOrder: $($savedOrder -join ',')"
                }
                Write-Host 'Mode 0 Rename/Copy/Cut/Delete menus, Copy clipboard, and file/.tucknote rename: PASS'
                return
            }
            if ($Aug26Only) {
                if ($mode -eq 0) {
                    Test-ContextMenu $app.Id $window.hwnd 'mode-file.txt'
                    Test-ContextMenu $app.Id $window.hwnd 'mode-portable'
                    Test-Drop $app.Id $window.hwnd 'mode-link' $shortcut $modeRoot $true
                    Write-Host 'Mode 0 Cut/Delete menus and shortcut native drop: PASS'
                }
                elseif ($mode -eq 2) {
                    Test-NoteDrop $app.Id $window.hwnd $noteName $noteIdN $statePath $notePath `
                        $modeRoot $false 'Move' $noteHtml
                    Write-Host 'Station note Move drop: PASS'
                }
                continue
            }
            if ($mode -eq 0) {
                Test-ContextMenu $app.Id $window.hwnd 'mode-file.txt'
                Test-ContextMenu $app.Id $window.hwnd 'mode-link'
            }
            elseif ($mode -eq 1) { Test-ContextMenu $app.Id $window.hwnd 'mode-folder' }
            Test-Reorder $app.Id $window.hwnd 'mode-file.txt' 'mode-folder' $statePath
            Start-Sleep -Milliseconds 700
            Test-Reorder $app.Id $window.hwnd 'mode-link' 'mode-site' $statePath
            Start-Sleep -Milliseconds 700
            Test-Drop $app.Id $window.hwnd 'mode-file.txt' $file $modeRoot ($mode -ne 2)
            Start-Sleep -Milliseconds 700
            if ($mode -eq 2) {
                $window = Expand-Station $app.Id
                Start-Sleep -Milliseconds 700
            }
            Test-Drop $app.Id $window.hwnd 'mode-folder' $folder $modeRoot ($mode -ne 2)
            foreach ($entry in @(
                @{ Name = 'mode-link'; Path = $shortcut },
                @{ Name = 'mode-site'; Path = $internetShortcut }
            )) {
                Start-Sleep -Milliseconds 700
                if ($mode -eq 2) {
                    $window = Expand-Station $app.Id
                    Start-Sleep -Milliseconds 700
                }
                Test-Drop $app.Id $window.hwnd $entry.Name $entry.Path $modeRoot ($mode -ne 2)
            }
            foreach ($effect in @('Copy', 'None', 'Move')) {
                Start-Sleep -Milliseconds 700
                if ($mode -eq 2) {
                    $window = Expand-Station $app.Id
                    Start-Sleep -Milliseconds 700
                }
                Test-NoteDrop $app.Id $window.hwnd $noteName $noteIdN $statePath $notePath `
                    $modeRoot ($mode -ne 2) $effect $noteHtml
            }
            Write-Host "Mode $mode file/folder/shortcut/url and note Copy|Move|cancel external drop: PASS"
        }
        finally {
            if (-not $app.HasExited) { $app.Kill() }
            $app.Dispose()
        }
    }
    Write-Host "TuckPane MainWindow mode(s) $($Modes -join ',') file/folder/shortcut/url and note drop: PASS"
}
finally {
    $env:TUCKPANE_TEST_ROOT = $originalRoot
    $env:GLASSFOLDER_TEST_EXPANDED = $originalExpanded
    if ($FileActionsOnly) { Set-Clipboard -Value ($originalClipboardText ?? '') }
    if ($KeepRoot) { Write-Host "Kept test root: $root" }
    elseif (Test-Path -LiteralPath $root) { [IO.Directory]::Delete($root, $true) }
}
