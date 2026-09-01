[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExecutablePath,

    [switch]$CheckExpandedOrganizer,

    [ValidateSet('None', 'Disabled', 'Enabled')]
    [string]$OutsideClickMode = 'None',

    [switch]$CreateWindowOnly
)

$ErrorActionPreference = 'Stop'
$expandOrganizer = $CheckExpandedOrganizer.IsPresent -or $OutsideClickMode -ne 'None'
if ($CreateWindowOnly -and $expandOrganizer) {
    throw 'CreateWindowOnly cannot be combined with organizer expansion or outside-click checks.'
}
$resolvedExecutable = [IO.Path]::GetFullPath($ExecutablePath)
if (-not (Test-Path -LiteralPath $resolvedExecutable -PathType Leaf)) {
    throw "TuckPane executable was not found: $resolvedExecutable"
}

if (Get-Process -Name 'TuckPane' -ErrorAction SilentlyContinue) {
    throw 'TuckPane is already running. Exit it from the tray before running this check.'
}

Add-Type -AssemblyName UIAutomationClient

if (-not ('TuckPaneTaskbarProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed class TuckPaneWindowRecord
{
    public long Handle { get; set; }
    public bool Visible { get; set; }
    public long Owner { get; set; }
    public long ExtendedStyle { get; set; }
    public double WidthDip { get; set; }
    public double HeightDip { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public string Title { get; set; } = "";
}

public static class TuckPaneTaskbarProbe
{
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const uint WM_CLOSE = 0x0010;
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint IconId;
        public Guid Guid;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect iconLocation);

    public static List<TuckPaneWindowRecord> ForProcess(uint targetProcessId)
    {
        var result = new List<TuckPaneWindowRecord>();
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint processId);
            if (processId != targetProcessId) return true;

            var title = new StringBuilder(512);
            GetWindowText(window, title, title.Capacity);
            GetWindowRect(window, out Rect bounds);
            double dpi = Math.Max(96u, GetDpiForWindow(window));
            result.Add(new TuckPaneWindowRecord
            {
                Handle = window.ToInt64(),
                Visible = IsWindowVisible(window),
                Owner = GetWindow(window, GW_OWNER).ToInt64(),
                ExtendedStyle = GetWindowLongPtr(window, GWL_EXSTYLE).ToInt64(),
                WidthDip = (bounds.Right - bounds.Left) * 96d / dpi,
                HeightDip = (bounds.Bottom - bounds.Top) * 96d / dpi,
                Left = bounds.Left,
                Top = bounds.Top,
                Right = bounds.Right,
                Bottom = bounds.Bottom,
                Title = title.ToString()
            });
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool IsTaskbarCandidate(TuckPaneWindowRecord window) =>
        window.Visible && window.Owner == 0 && (window.ExtendedStyle & WS_EX_TOOLWINDOW) == 0;

    public static bool HasTrayIcon(uint targetProcessId, uint iconId)
    {
        foreach (TuckPaneWindowRecord window in ForProcess(targetProcessId))
        {
            var identifier = new NotifyIconIdentifier
            {
                Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
                Window = new IntPtr(window.Handle),
                IconId = iconId
            };
            if (Shell_NotifyIconGetRect(ref identifier, out _) == 0) return true;
        }
        return false;
    }

    public static bool Close(long handle) =>
        PostMessage(new IntPtr(handle), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public static void LeftClick(int x, int y)
    {
        SetCursorPos(x, y);
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }

    public static long WindowAt(int x, int y) => WindowFromPoint(new Point { X = x, Y = y }).ToInt64();
}
'@
}

function Start-TuckPane([string]$Path, [string]$TestRoot, [bool]$ExpandOrganizer, [string[]]$Arguments = @()) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($Path)
    $startInfo.UseShellExecute = $false
    $startInfo.Environment['TUCKPANE_TEST_ROOT'] = $TestRoot
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    if ($ExpandOrganizer) {
        $startInfo.Environment['GLASSFOLDER_TEST_EXPANDED'] = '1'
    }
    return [Diagnostics.Process]::Start($startInfo)
}

function Get-TuckPaneWindows([Diagnostics.Process]$Process) {
    return @([TuckPaneTaskbarProbe]::ForProcess([uint32]$Process.Id))
}

function Get-TaskbarCandidates([Diagnostics.Process]$Process) {
    return @(Get-TuckPaneWindows $Process | Where-Object { [TuckPaneTaskbarProbe]::IsTaskbarCandidate($_) })
}

function Format-Window([TuckPaneWindowRecord]$Window) {
    return "HWND=0x$('{0:X}' -f $Window.Handle) Visible=$($Window.Visible) Owner=0x$('{0:X}' -f $Window.Owner) ExStyle=0x$('{0:X}' -f $Window.ExtendedStyle) Size=$([Math]::Round($Window.WidthDip, 1))x$([Math]::Round($Window.HeightDip, 1))dip Title=$($Window.Title)"
}

function Read-TuckPaneState([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($Path)) }
    catch { return $null }
}

function Get-TuckPaneTaskbarButtons {
    $classCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ClassNameProperty,
        'Shell_TrayWnd')
    $taskbar = $null
    foreach ($attempt in 1..10) {
        $taskbar = [Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [Windows.Automation.TreeScope]::Children,
            $classCondition)
        if ($null -ne $taskbar) { break }
        Start-Sleep -Milliseconds 50
    }
    if ($null -eq $taskbar) {
        throw 'Windows taskbar could not be found through UI Automation.'
    }

    $buttons = $taskbar.FindAll(
        [Windows.Automation.TreeScope]::Descendants,
        [Windows.Automation.Condition]::TrueCondition)
    if ($buttons.Count -eq 0) { return @() }
    return @(0..($buttons.Count - 1) | ForEach-Object {
        $button = $buttons.Item($_)
        if ($button.Current.ClassName -eq 'Taskbar.TaskListButtonAutomationPeer' -and
            ($button.Current.AutomationId -match 'TuckPane' -or $button.Current.Name -match 'TuckPane')) {
            $button.Current.Name
        }
    })
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$testRoot = [IO.Path]::GetFullPath((Join-Path $tempBase ("TuckPane-tray-check-{0}" -f [Guid]::NewGuid().ToString('N'))))
if (-not $testRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe test root: $testRoot"
}

[IO.Directory]::CreateDirectory($testRoot) | Out-Null

if ($expandOrganizer) {
    $localRoot = Join-Path $testRoot 'LocalAppData\TuckPane'
    $userRoot = Join-Path $testRoot 'UserProfile\TuckPane'
    [IO.Directory]::CreateDirectory($localRoot) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $userRoot 'Items')) | Out-Null

    $state = @{
        SchemaVersion = 3
        GlobalSettings = @{
            Theme = 0
            StartWithWindows = $false
            Language = 0
            CollapseOnOutsideClick = $OutsideClickMode -eq 'Enabled'
        }
        Organizers = @(
            @{
                Id = [Guid]::NewGuid()
                Name = 'Expansion regression'
                CreatedAtUtc = [DateTimeOffset]::UtcNow
                ThemeOverride = $null
                PlacementMode = 0
                Layout = @{
                    Mode = 0
                    Rows = 3
                    Columns = 3
                }
                CompactScale = 1.56
                CanvasScale = 1.0
                ItemScale = 1.0
                NameScale = 1.0
                Position = $null
                StorageRelativePath = 'Items'
                StorageAbsolutePath = $null
                ItemOrder = @()
            }
        )
    }
    $stateJson = $state | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $localRoot 'state.json'), $stateJson, [Text.UTF8Encoding]::new($false))
}

$primary = $null
$secondary = $null
$clickTargetForm = $null

try {
    if ($CreateWindowOnly) {
        $folderPath = [IO.Path]::GetFullPath((Join-Path $testRoot '中文 空格目录'))
        [IO.Directory]::CreateDirectory($folderPath) | Out-Null
        $statePath = Join-Path $testRoot 'LocalAppData\TuckPane\state.json'
        $consoleShown = $false

        $primary = Start-TuckPane $resolvedExecutable $testRoot $false @('--create-organizer')
        $createClock = [Diagnostics.Stopwatch]::StartNew()
        $state = $null
        $visibleWindows = @()
        while ($createClock.Elapsed -lt [TimeSpan]::FromSeconds(15)) {
            if ($primary.HasExited) {
                throw "TuckPane exited during desktop organizer creation with code $($primary.ExitCode)."
            }
            $state = Read-TuckPaneState $statePath
            $visibleWindows = @(Get-TuckPaneWindows $primary | Where-Object Visible)
            if ((Get-TaskbarCandidates $primary).Count -gt 0) { $consoleShown = $true }
            if ($null -ne $state -and @($state.Organizers).Count -eq 1 -and $visibleWindows.Count -eq 1) { break }
            Start-Sleep -Milliseconds 25
        }
        if ($null -eq $state -or @($state.Organizers).Count -ne 1 -or $visibleWindows.Count -ne 1) {
            throw "Cold --create-organizer did not create exactly one organizer. State=$(@($state.Organizers).Count), visible=$($visibleWindows.Count)."
        }

        $secondary = Start-TuckPane $resolvedExecutable $testRoot $false @('--create-organizer-in', $folderPath)
        if (-not $secondary.WaitForExit(5000)) {
            throw 'The folder organizer secondary launch did not exit after signaling the primary instance.'
        }

        $createClock.Restart()
        while ($createClock.Elapsed -lt [TimeSpan]::FromSeconds(10)) {
            $state = Read-TuckPaneState $statePath
            $visibleWindows = @(Get-TuckPaneWindows $primary | Where-Object Visible)
            if ((Get-TaskbarCandidates $primary).Count -gt 0) { $consoleShown = $true }
            if ($null -ne $state -and @($state.Organizers).Count -eq 2 -and $visibleWindows.Count -eq 2) { break }
            Start-Sleep -Milliseconds 25
        }

        $organizers = @($state.Organizers)
        if ($organizers.Count -ne 2 -or $visibleWindows.Count -ne 2) {
            throw "The two Shell commands did not leave exactly two organizers. State=$($organizers.Count), visible=$($visibleWindows.Count)."
        }
        $folderOrganizers = @($organizers | Where-Object {
            $_.StorageAbsolutePath -eq $folderPath
        })
        if ($folderOrganizers.Count -ne 1) {
            throw "No unique organizer was bound to '$folderPath'."
        }
        $folderOrganizer = $folderOrganizers[0]
        if ($folderOrganizer.Name -ne [IO.Path]::GetFileName($folderPath) -or
            -not [string]::IsNullOrEmpty([string]$folderOrganizer.StorageRelativePath)) {
            throw 'The folder organizer name or storage fields were not persisted exactly.'
        }

        $taskbarCandidates = @(Get-TaskbarCandidates $primary)
        $taskbarButtons = @(Get-TuckPaneTaskbarButtons)
        if ($consoleShown -or $taskbarCandidates.Count -gt 0 -or $taskbarButtons.Count -gt 0) {
            throw "A console/taskbar window appeared during Shell creation. Windows=$($taskbarCandidates.Count), buttons=$($taskbarButtons.Count)."
        }

        Write-Output 'TuckPane create-window-only check: PASS'
        return
    }

    $primary = Start-TuckPane $resolvedExecutable $testRoot $expandOrganizer
    $startupClock = [Diagnostics.Stopwatch]::StartNew()
    $firstWindowSeenAt = $null
    $expandedWindowSeenAt = $null
    $startupTaskbarWindow = $null
    $startupTaskbarButton = $null
    $observedWindowStates = [Collections.Generic.List[string]]::new()
    $lastWindowState = $null

    while ($startupClock.Elapsed -lt [TimeSpan]::FromSeconds(15)) {
        if ($primary.HasExited) {
            throw "TuckPane exited during startup with code $($primary.ExitCode)."
        }

        $windows = @(Get-TuckPaneWindows $primary)
        $visibleWindow = @($windows | Where-Object Visible) | Select-Object -First 1
        if ($null -ne $visibleWindow) {
            $windowState = Format-Window $visibleWindow
            if ($windowState -ne $lastWindowState) {
                $observedWindowStates.Add($windowState)
                $lastWindowState = $windowState
            }
        }
        if ($windows.Count -gt 0 -and $null -eq $firstWindowSeenAt) {
            $firstWindowSeenAt = $startupClock.Elapsed
        }
        if ($expandOrganizer -and $null -eq $expandedWindowSeenAt) {
            $expandedWindow = @($windows | Where-Object {
                $_.Visible -and $_.WidthDip -gt 180 -and $_.HeightDip -gt 160
            }) | Select-Object -First 1
            if ($null -ne $expandedWindow) {
                $expandedWindowSeenAt = $startupClock.Elapsed
            }
        }

        $startupTaskbarWindow = @(Get-TaskbarCandidates $primary) | Select-Object -First 1
        $startupTaskbarButton = @(Get-TuckPaneTaskbarButtons) | Select-Object -First 1
        if ($expandOrganizer) {
            if ($null -ne $expandedWindowSeenAt -and ($startupClock.Elapsed - $expandedWindowSeenAt) -ge [TimeSpan]::FromSeconds(1)) { break }
        }
        elseif ($null -ne $firstWindowSeenAt -and ($startupClock.Elapsed - $firstWindowSeenAt) -ge [TimeSpan]::FromSeconds(2)) {
            break
        }
        Start-Sleep -Milliseconds 10
    }

    if ($null -eq $firstWindowSeenAt) {
        throw 'TuckPane did not create a top-level window within 15 seconds.'
    }
    if ($expandOrganizer -and $null -eq $expandedWindowSeenAt -and $null -eq $startupTaskbarButton) {
        throw 'TuckPane did not expand the isolated organizer within 15 seconds.'
    }
    $startupTaskbarButton = @(Get-TuckPaneTaskbarButtons) | Select-Object -First 1
    $startupTaskbarWindow = @(Get-TaskbarCandidates $primary) | Select-Object -First 1
    if ($null -ne $startupTaskbarButton) {
        $phase = if ($expandOrganizer) { 'Expanding an organizer' } else { 'First launch' }
        $windowDetails = if ($null -ne $startupTaskbarWindow) { Format-Window $startupTaskbarWindow } else { 'no taskbar-eligible HWND found' }
        throw "$phase exposed taskbar button '$startupTaskbarButton': $windowDetails. Observed states: $($observedWindowStates -join ' -> ')"
    }
    if (-not [TuckPaneTaskbarProbe]::HasTrayIcon([uint32]$primary.Id, 1)) {
        throw 'TuckPane did not register its system tray icon.'
    }

    if ($OutsideClickMode -ne 'None') {
        Add-Type -AssemblyName System.Windows.Forms
        $expandedWindow = @(Get-TuckPaneWindows $primary | Where-Object {
            $_.Visible -and $_.WidthDip -gt 180 -and $_.HeightDip -gt 160
        }) | Select-Object -First 1
        if ($null -eq $expandedWindow) { throw 'No expanded organizer was available for the outside-click check.' }

        $workingArea = [Windows.Forms.Screen]::PrimaryScreen.WorkingArea
        $targetWidth = 280
        $targetHeight = 150
        $locations = @(
            [Drawing.Point]::new($workingArea.Left + 12, $workingArea.Top + 12),
            [Drawing.Point]::new($workingArea.Right - $targetWidth - 12, $workingArea.Top + 12),
            [Drawing.Point]::new($workingArea.Left + 12, $workingArea.Bottom - $targetHeight - 12),
            [Drawing.Point]::new($workingArea.Right - $targetWidth - 12, $workingArea.Bottom - $targetHeight - 12)
        )
        $targetLocation = $locations | Where-Object {
            $centerX = $_.X + [int]($targetWidth / 2)
            $centerY = $_.Y + [int]($targetHeight / 2)
            $centerX -lt $expandedWindow.Left -or $centerX -ge $expandedWindow.Right -or
                $centerY -lt $expandedWindow.Top -or $centerY -ge $expandedWindow.Bottom
        } | Select-Object -First 1
        if ($null -eq $targetLocation) { throw 'No outside-click target location was available.' }

        $clickTargetForm = [Windows.Forms.Form]::new()
        $clickTargetForm.Text = 'TuckPane outside-click target'
        $clickTargetForm.StartPosition = 'Manual'
        $clickTargetForm.Bounds = [Drawing.Rectangle]::new($targetLocation.X, $targetLocation.Y, $targetWidth, $targetHeight)
        $clickTargetForm.TopMost = $true
        $targetButton = [Windows.Forms.Button]::new()
        $targetButton.Text = 'Click target'
        $targetButton.Dock = 'Fill'
        $targetButton.Tag = 'pending'
        $targetButton.Add_Click({ $this.Tag = 'clicked' })
        $clickTargetForm.Controls.Add($targetButton)
        $clickTargetForm.Show()
        $clickTargetForm.Activate()
        $clickTargetForm.BringToFront()
        foreach ($attempt in 1..10) {
            [Windows.Forms.Application]::DoEvents()
            Start-Sleep -Milliseconds 20
        }
        $targetPoint = $targetButton.PointToScreen([Drawing.Point]::new([int]($targetButton.Width / 2), [int]($targetButton.Height / 2)))
        $clickTargetForm.Activate()
        $clickTargetForm.BringToFront()
        [Windows.Forms.Application]::DoEvents()
        $windowAtTarget = [TuckPaneTaskbarProbe]::WindowAt($targetPoint.X, $targetPoint.Y)
        if ($windowAtTarget -ne $targetButton.Handle.ToInt64()) {
            throw "The click target was obscured. Expected HWND 0x$('{0:X}' -f $targetButton.Handle.ToInt64()), found 0x$('{0:X}' -f $windowAtTarget)."
        }
        [TuckPaneTaskbarProbe]::LeftClick($targetPoint.X, $targetPoint.Y)

        $outsideClock = [Diagnostics.Stopwatch]::StartNew()
        $afterClick = $expandedWindow
        while ($outsideClock.Elapsed -lt [TimeSpan]::FromSeconds(3)) {
            [Windows.Forms.Application]::DoEvents()
            $afterClick = @(Get-TuckPaneWindows $primary | Where-Object Handle -eq $expandedWindow.Handle) | Select-Object -First 1
            $isCollapsed = $null -ne $afterClick -and ($afterClick.WidthDip -le 180 -or $afterClick.HeightDip -le 160)
            if ($targetButton.Tag -eq 'clicked' -and
                (($OutsideClickMode -eq 'Enabled' -and $isCollapsed) -or
                 ($OutsideClickMode -eq 'Disabled' -and -not $isCollapsed -and $outsideClock.ElapsedMilliseconds -ge 500))) {
                break
            }
            Start-Sleep -Milliseconds 25
        }
        if ($targetButton.Tag -ne 'clicked') { throw 'The outside click was swallowed before reaching its target.' }
        $collapsedAfterClick = $null -ne $afterClick -and ($afterClick.WidthDip -le 180 -or $afterClick.HeightDip -le 160)
        if ($OutsideClickMode -eq 'Enabled' -and -not $collapsedAfterClick) {
            throw 'The organizer did not collapse when outside-click behavior was enabled.'
        }
        if ($OutsideClickMode -eq 'Disabled' -and $collapsedAfterClick) {
            throw 'The organizer collapsed while outside-click behavior was disabled.'
        }
        $clickTargetForm.Close()
        $clickTargetForm.Dispose()
        $clickTargetForm = $null
    }

    $startupWindowHandles = @(Get-TuckPaneWindows $primary | Where-Object Visible | ForEach-Object Handle)

    $secondary = Start-TuckPane $resolvedExecutable $testRoot $expandOrganizer
    if (-not $secondary.WaitForExit(5000)) {
        throw 'The secondary launch did not exit after signaling the primary instance.'
    }

    $openClock = [Diagnostics.Stopwatch]::StartNew()
    $consoleWindow = $null
    while ($openClock.Elapsed -lt [TimeSpan]::FromSeconds(5)) {
        $consoleWindow = @(Get-TuckPaneWindows $primary | Where-Object {
            $_.Handle -notin $startupWindowHandles -and [TuckPaneTaskbarProbe]::IsTaskbarCandidate($_)
        }) | Select-Object -First 1
        if ($null -ne $consoleWindow) { break }
        Start-Sleep -Milliseconds 25
    }
    if ($null -eq $consoleWindow) {
        $windowDetails = @((Get-TuckPaneWindows $primary) | ForEach-Object { Format-Window $_ }) -join '; '
        $taskbarDetails = @(Get-TuckPaneTaskbarButtons) -join '; '
        throw "The secondary launch did not expose a taskbar-eligible console window. Windows: $windowDetails. Taskbar: $taskbarDetails"
    }

    if (-not [TuckPaneTaskbarProbe]::Close($consoleWindow.Handle)) {
        throw 'WM_CLOSE could not be posted to the console window.'
    }

    $closeClock = [Diagnostics.Stopwatch]::StartNew()
    while ($closeClock.Elapsed -lt [TimeSpan]::FromSeconds(5) -and
           ((Get-TuckPaneTaskbarButtons).Count -gt 0 -or (Get-TaskbarCandidates $primary).Count -gt 0)) {
        Start-Sleep -Milliseconds 25
    }
    if ($primary.HasExited) {
        throw 'Closing the console terminated TuckPane instead of hiding it to the tray.'
    }
    if ((Get-TuckPaneTaskbarButtons).Count -gt 0 -or (Get-TaskbarCandidates $primary).Count -gt 0) {
        throw 'Closing the console left a taskbar window visible.'
    }

    $scope = if ($OutsideClickMode -ne 'None') {
        "outside click ($($OutsideClickMode.ToLowerInvariant()))"
    } elseif ($CheckExpandedOrganizer) {
        'startup and organizer expansion'
    } else {
        'tray startup'
    }
    Write-Output "TuckPane $scope check: PASS"
}
finally {
    if ($clickTargetForm) {
        $clickTargetForm.Close()
        $clickTargetForm.Dispose()
    }
    if ($secondary -and -not $secondary.HasExited) {
        $secondary.Kill($true)
        $secondary.WaitForExit(5000) | Out-Null
    }
    if ($primary -and -not $primary.HasExited) {
        $primary.Kill($true)
        $primary.WaitForExit(5000) | Out-Null
    }
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        if (-not $resolvedTestRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove unsafe test root: $resolvedTestRoot"
        }
        [IO.Directory]::Delete($resolvedTestRoot, $true)
    }
}
