param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [switch]$StationCoveredOnly,
    [switch]$HoverCollapseOnly
)

$ErrorActionPreference = 'Stop'
$resolvedExe = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $resolvedExe -PathType Leaf)) { throw "Executable not found: $resolvedExe" }
if ($StationCoveredOnly -or $HoverCollapseOnly) { Add-Type -AssemblyName UIAutomationClient }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class WindowLayerProbe
{
    private const int GwlpOwner = -8;
    private const int GwlExStyle = -20;
    private const long WsExTopmost = 0x00000008L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint WsOverlappedWindow = 0x00CF0000;
    private const int SwShow = 5;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new IntPtr(-1);
    private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
    private const string ProbeClass = "TuckPaneWindowLayerProbe";
    private static readonly WindowProc ProbeWindowProc = DefWindowProc;

    public delegate bool EnumProc(IntPtr window, IntPtr parameter);
    private delegate IntPtr WindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProc WindowProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out System.Drawing.Point point);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(System.Drawing.Point point);

    [DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr window);

    public static bool IsPointOverWindow(IntPtr window, System.Drawing.Point point)
    {
        IntPtr hit = WindowFromPoint(point);
        return hit == window || IsChild(window, hit);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    public static IntPtr CreateProbeWindow()
    {
        IntPtr instance = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProc = ProbeWindowProc,
            Instance = instance,
            ClassName = ProbeClass
        };
        ushort atom = RegisterClassEx(ref windowClass);
        int error = Marshal.GetLastWin32Error();
        if (atom == 0 && error != 1410) throw new InvalidOperationException("RegisterClassEx failed: " + error);

        IntPtr window = CreateWindowEx(
            0,
            ProbeClass,
            "TuckPane normal-window probe",
            WsOverlappedWindow,
            120,
            120,
            760,
            520,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (window == IntPtr.Zero) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        ShowWindow(window, SwShow);
        UpdateWindow(window);
        return window;
    }

    public static IntPtr FindOrganizerWindow(int processId, bool expanded)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            Rect rect;
            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            if (owner == processId && IsWindowVisible(window) && GetWindowRect(window, out rect) &&
                className.ToString() == "WinUIDesktopWin32WindowClass" &&
                (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExNoActivate) != 0)
            {
                int width = rect.Right - rect.Left;
                int height = rect.Bottom - rect.Top;
                if ((expanded && width >= 300 && height >= 250) || (!expanded && width <= 300 && height <= 300))
                {
                    result = window;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindVisibleStationWindow(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            Rect rect;
            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            if (owner == processId && IsWindowVisible(window) && GetWindowRect(window, out rect) &&
                className.ToString() == "WinUIDesktopWin32WindowClass" &&
                (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExNoActivate) != 0 &&
                rect.Right - rect.Left < 250 && rect.Bottom - rect.Top > 500)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindDesktopIconView()
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            IntPtr child = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child == IntPtr.Zero) return true;
            result = child;
            return false;
        }, IntPtr.Zero);
        if (result != IntPtr.Zero) return result;
        IntPtr progman = FindWindow("Progman", null);
        return progman == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
    }

    public static IntPtr GetOwner(IntPtr window) => GetWindowLongPtr(window, GwlpOwner);

    public static bool IsTopmost(IntPtr window) =>
        (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExTopmost) != 0;

    public static bool IsNoActivate(IntPtr window) =>
        (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExNoActivate) != 0;

    public static Rect GetPrimaryMonitorBounds() => new Rect
    {
        Right = GetSystemMetrics(SmCxScreen),
        Bottom = GetSystemMetrics(SmCyScreen)
    };

    public static int GetZOrderIndex(IntPtr target)
    {
        int current = 0;
        int result = -1;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            if (window == target)
            {
                result = current;
                return false;
            }
            current++;
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static bool IsAbove(IntPtr first, IntPtr second)
    {
        int firstIndex = GetZOrderIndex(first);
        int secondIndex = GetZOrderIndex(second);
        return firstIndex >= 0 && secondIndex >= 0 && firstIndex < secondIndex;
    }

    public static void BringNormalToTop(IntPtr window)
    {
        uint flags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow;
        if (!SetWindowPos(window, HwndTopmost, 0, 0, 0, 0, flags) ||
            !SetWindowPos(window, HwndNotTopmost, 0, 0, 0, 0, flags))
            throw new InvalidOperationException("Transient normal-window raise failed: " + Marshal.GetLastWin32Error());
    }

    public static void CoverTarget(IntPtr probe, IntPtr target)
    {
        Rect bounds;
        if (!GetWindowRect(target, out bounds)) throw new InvalidOperationException("GetWindowRect(target) failed.");
        CoverBounds(probe, bounds);
    }

    public static void CoverBounds(IntPtr probe, Rect bounds)
    {
        if (!SetWindowPos(probe, HwndTopmost, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SwpNoActivate | SwpShowWindow) ||
            !SetWindowPos(probe, HwndNotTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow))
            throw new InvalidOperationException("CoverBounds failed: " + Marshal.GetLastWin32Error());
    }

    public static void MoveProbeAway(IntPtr probe)
    {
        if (!SetWindowPos(probe, HwndNotTopmost, 20, 700, 240, 180, SwpNoActivate | SwpShowWindow))
            throw new InvalidOperationException("MoveProbeAway failed: " + Marshal.GetLastWin32Error());
    }

}
'@

function Wait-OrganizerWindow([int]$ProcessId, [bool]$Expanded, [int]$TimeoutSeconds = 10) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 100
        $window = [WindowLayerProbe]::FindOrganizerWindow($ProcessId, $Expanded)
    } while ($window -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    return $window
}

function Wait-VisibleStationWindow([int]$ProcessId, [int]$TimeoutSeconds = 6) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 100
        $window = [WindowLayerProbe]::FindVisibleStationWindow($ProcessId)
    } while ($window -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    return $window
}

function Wait-WindowHidden([IntPtr]$Window, [int]$TimeoutSeconds = 5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([WindowLayerProbe]::IsWindowVisible($Window) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    return -not [WindowLayerProbe]::IsWindowVisible($Window)
}

if ($StationCoveredOnly -or $HoverCollapseOnly) {
function Find-AppAutomationElement([int]$ProcessId, [string]$AutomationId, [bool]$VisibleOnly = $true) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $ProcessId),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId))
    $element = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($VisibleOnly -and $null -ne $element -and $element.Current.IsOffscreen) { return $null }
    return $element
}

function Wait-AppAutomationElement([int]$ProcessId, [string]$AutomationId, [int]$TimeoutSeconds = 5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $element = Find-AppAutomationElement $ProcessId $AutomationId
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Select-AutomationElement([System.Windows.Automation.AutomationElement]$Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
        return
    }
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        return
    }
    throw "Element cannot be selected: $($Element.Current.AutomationId)"
}

function Set-AutomationRange([System.Windows.Automation.AutomationElement]$Element, [double]$Value) {
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern, [ref]$pattern)) {
        throw "Element does not support RangeValuePattern: $($Element.Current.AutomationId)"
    }
    ([System.Windows.Automation.RangeValuePattern]$pattern).SetValue($Value)
}

function Close-AutomationWindow([System.Windows.Automation.AutomationElement]$Element) {
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    for ($current = $Element; $null -ne $current; $current = $walker.GetParent($current)) {
        if ($current.Current.ControlType -ne [System.Windows.Automation.ControlType]::Window) { continue }
        $pattern = $null
        if (-not $current.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$pattern)) {
            throw 'The settings window does not support WindowPattern.'
        }
        ([System.Windows.Automation.WindowPattern]$pattern).Close()
        return
    }
    throw 'The settings window was not found from the slider element.'
}

function Wait-HoverDelayState([string]$Path, [int]$ExpandMs, [int]$CollapseMs, [int]$TimeoutSeconds = 5) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 100
        try {
            $state = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
            if ($state.GlobalSettings.HoverExpandDelayMs -eq $ExpandMs -and
                $state.GlobalSettings.PointerLeaveCollapseDelayMs -eq $CollapseMs) { return }
        }
        catch { }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Hover delays were not persisted as $ExpandMs/$CollapseMs ms."
}
}

function Test-HoverCollapseBehavior([Diagnostics.Process]$Process, [string]$ModeName) {
    $compactWindow = Wait-OrganizerWindow $Process.Id $false
    if ($compactWindow -eq [IntPtr]::Zero) { throw "$ModeName compact organizer was not found." }
    $initialBounds = New-Object WindowLayerProbe+Rect
    [WindowLayerProbe]::GetWindowRect($compactWindow, [ref]$initialBounds) | Out-Null
    $compactX = [int](($initialBounds.Left + $initialBounds.Right) / 2)
    $compactY = [int](($initialBounds.Top + $initialBounds.Bottom) / 2)
    [WindowLayerProbe]::BringNormalToTop($compactWindow)
    [WindowLayerProbe]::SetCursorPos($compactX, $compactY) | Out-Null
    if (-not [WindowLayerProbe]::IsPointOverWindow(
        $compactWindow, [Drawing.Point]::new($compactX, $compactY))) {
        throw "$ModeName compact organizer is covered at the hover test point."
    }
    Start-Sleep -Milliseconds 450
    if ([WindowLayerProbe]::FindOrganizerWindow($Process.Id, $true) -ne [IntPtr]::Zero) {
        throw "$ModeName expanded before the configured 800 ms hover threshold."
    }

    $expandedWindow = Wait-OrganizerWindow $Process.Id $true 4
    if ($expandedWindow -eq [IntPtr]::Zero) { throw "$ModeName did not expand after the configured hover threshold." }
    Start-Sleep -Milliseconds 400
    $expandedBounds = New-Object WindowLayerProbe+Rect
    [WindowLayerProbe]::GetWindowRect($expandedWindow, [ref]$expandedBounds) | Out-Null
    $primary = [WindowLayerProbe]::GetPrimaryMonitorBounds()
    $dragStartX = [int](($expandedBounds.Left + $expandedBounds.Right) / 2)
    $dragStartY = [int](($expandedBounds.Top + $expandedBounds.Bottom) / 2)
    $dragDeltaX = if ($expandedBounds.Right + 100 -lt $primary.Right) { 80 } else { -80 }
    $dragDeltaY = if ($expandedBounds.Bottom + 80 -lt $primary.Bottom) { 60 } else { -60 }
    [WindowLayerProbe]::SetCursorPos($dragStartX, $dragStartY) | Out-Null
    [WindowLayerProbe]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    try {
        Start-Sleep -Milliseconds 450
        [WindowLayerProbe]::SetCursorPos($dragStartX + $dragDeltaX, $dragStartY + $dragDeltaY) | Out-Null
        Start-Sleep -Milliseconds 250
    }
    finally {
        [WindowLayerProbe]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 300
    $movedBounds = New-Object WindowLayerProbe+Rect
    [WindowLayerProbe]::GetWindowRect($expandedWindow, [ref]$movedBounds) | Out-Null
    if ($movedBounds.Left -eq $expandedBounds.Left -and $movedBounds.Top -eq $expandedBounds.Top) {
        throw "$ModeName expanded organizer did not move through the real long-press drag path."
    }
    $insideX = [int](($movedBounds.Left + $movedBounds.Right) / 2)
    $insideY = [int](($movedBounds.Top + $movedBounds.Bottom) / 2)
    [WindowLayerProbe]::SetCursorPos($insideX, $insideY) | Out-Null

    $outsideX = if ($insideX -gt ($primary.Left + $primary.Right) / 2) { $primary.Left + 4 } else { $primary.Right - 4 }
    $outsideY = if ($insideY -gt ($primary.Top + $primary.Bottom) / 2) { $primary.Top + 4 } else { $primary.Bottom - 4 }
    [WindowLayerProbe]::SetCursorPos($outsideX, $outsideY) | Out-Null
    Start-Sleep -Milliseconds 450
    if ([WindowLayerProbe]::FindOrganizerWindow($Process.Id, $true) -eq [IntPtr]::Zero) {
        throw "$ModeName collapsed before the configured 900 ms leave threshold."
    }
    [WindowLayerProbe]::SetCursorPos($insideX, $insideY) | Out-Null
    Start-Sleep -Milliseconds 1100
    if ([WindowLayerProbe]::FindOrganizerWindow($Process.Id, $true) -eq [IntPtr]::Zero) {
        throw "$ModeName did not cancel collapse when the pointer returned before the threshold."
    }
    [WindowLayerProbe]::SetCursorPos($outsideX, $outsideY) | Out-Null
    Start-Sleep -Milliseconds 450
    if ([WindowLayerProbe]::FindOrganizerWindow($Process.Id, $true) -eq [IntPtr]::Zero) {
        throw "$ModeName collapsed before the second leave threshold."
    }
    $compactWindow = Wait-OrganizerWindow $Process.Id $false 4
    if ($compactWindow -eq [IntPtr]::Zero) { throw "$ModeName did not collapse after the configured leave threshold." }
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    $stableSamples = 0
    $previousBounds = $null
    do {
        Start-Sleep -Milliseconds 100
        $finalBounds = New-Object WindowLayerProbe+Rect
        [WindowLayerProbe]::GetWindowRect($compactWindow, [ref]$finalBounds) | Out-Null
        $compactSizeRestored = $finalBounds.Right - $finalBounds.Left -eq $initialBounds.Right - $initialBounds.Left -and
            $finalBounds.Bottom - $finalBounds.Top -eq $initialBounds.Bottom - $initialBounds.Top
        $unchanged = $null -ne $previousBounds -and
            $finalBounds.Left -eq $previousBounds.Left -and $finalBounds.Top -eq $previousBounds.Top -and
            $finalBounds.Right -eq $previousBounds.Right -and $finalBounds.Bottom -eq $previousBounds.Bottom
        $stableSamples = if ($compactSizeRestored -and $unchanged) { $stableSamples + 1 } else { 0 }
        $previousBounds = $finalBounds
    } while ($stableSamples -lt 2 -and [DateTime]::UtcNow -lt $deadline)
    if ($stableSamples -lt 2) { throw "$ModeName compact bounds did not settle after collapse." }
    if ($finalBounds.Left -ne $initialBounds.Left -or $finalBounds.Top -ne $initialBounds.Top) {
        throw "$ModeName collapsed to $($finalBounds.Left),$($finalBounds.Top), expected $($initialBounds.Left),$($initialBounds.Top)."
    }
}

$projectRoot = Split-Path $PSScriptRoot -Parent
$runRoot = Join-Path $projectRoot "artifacts\layer-runs\$([Guid]::NewGuid().ToString('N'))"
$localRoot = Join-Path $runRoot 'LocalAppData\TuckPane'
$itemsRoot = Join-Path $runRoot 'UserProfile\TuckPane\Windows\LayerProbe-55555555\Items'
New-Item -ItemType Directory -Path $localRoot, $itemsRoot -Force | Out-Null
if ($StationCoveredOnly) {
    $peerItemsRoot = Join-Path $runRoot 'UserProfile\TuckPane\Windows\LayerPeer-66666666\Items'
    New-Item -ItemType Directory -Path $peerItemsRoot -Force | Out-Null
}
$placementMode = if ($StationCoveredOnly) { 2 } else { 0 }
$layoutRows = if ($StationCoveredOnly) { 9 } else { 3 }
$layoutColumns = if ($StationCoveredOnly) { 1 } else { 3 }
$compactScale = if ($StationCoveredOnly) { '1.559999942779541' } else { '0.8' }
$canvasScale = if ($StationCoveredOnly) { '0.34582272344925186' } else { '0.7' }
$itemScale = if ($StationCoveredOnly) { '0.8' } else { '1.0' }
$schemaVersion = 7
$hoverSettingsJson = if ($HoverCollapseOnly) { ', "ExpandOnHover": true, "CollapseOnPointerLeave": true, "HoverExpandDelayMs": 350, "PointerLeaveCollapseDelayMs": 400' } else { '' }
$floatingPeerJson = if ($StationCoveredOnly) { @'
,
    {
      "Id": "66666666-6666-6666-6666-666666666666",
      "Name": "LayerPeer",
      "CreatedAtUtc": "2026-08-23T00:00:00+00:00",
      "PlacementMode": 0,
      "Layout": { "Mode": 0, "Rows": 3, "Columns": 3 },
      "CompactScale": 0.8,
      "CanvasScale": 0.7,
      "ItemScale": 1.0,
      "NameScale": 1.0,
      "Position": null,
      "StorageRelativePath": "Windows\\LayerPeer-66666666\\Items",
      "StorageAbsolutePath": null,
      "ItemOrder": []
    }
'@ } else { '' }
[IO.File]::WriteAllText((Join-Path $localRoot 'state.json'), @"
{
  "SchemaVersion": $schemaVersion,
  "GlobalSettings": { "ThemeColorArgb": 4293060073, "Material": 0, "ThemeTransparency": 0.35, "StartWithWindows": false, "Language": 0$hoverSettingsJson },
  "ConsolePlacement": null,
  "Organizers": [
    {
      "Id": "55555555-5555-5555-5555-555555555555",
      "Name": "LayerProbe",
      "CreatedAtUtc": "2026-08-23T00:00:00+00:00",
      "PlacementMode": $placementMode,
      "DockEdge": 2,
      "Layout": { "Mode": 0, "Rows": $layoutRows, "Columns": $layoutColumns },
      "CompactScale": $compactScale,
      "CanvasScale": $canvasScale,
      "ItemScale": $itemScale,
      "NameScale": 1.0,
      "Position": null,
      "StorageRelativePath": "Windows\\LayerProbe-55555555\\Items",
      "StorageAbsolutePath": null,
      "ItemOrder": []
    }$floatingPeerJson
  ]
}
"@, [Text.UTF8Encoding]::new($false))

$probeWindow = [WindowLayerProbe]::CreateProbeWindow()
$originalCursor = [Drawing.Point]::Empty
[WindowLayerProbe]::GetCursorPos([ref]$originalCursor) | Out-Null
$probeProcess = $null
try {
    $env:TUCKPANE_TEST_ROOT = $runRoot
    Remove-Item Env:GLASSFOLDER_TEST_EXPANDED -ErrorAction SilentlyContinue
    Remove-Item Env:GLASSFOLDER_TEST_TRANSITION_CYCLES -ErrorAction SilentlyContinue
    Remove-Item Env:TUCKPANE_TEST_RESIZE_AUTORUN -ErrorAction SilentlyContinue

    if ($HoverCollapseOnly) {
        [WindowLayerProbe]::DestroyWindow($probeWindow) | Out-Null
        $probeWindow = [IntPtr]::Zero
        $statePath = Join-Path $localRoot 'state.json'
        $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
        if ((Wait-OrganizerWindow $probeProcess.Id $false) -eq [IntPtr]::Zero) {
            throw 'Floating compact organizer was not found before the settings check.'
        }

        $consoleOpener = Start-Process -FilePath $resolvedExe -PassThru
        $consoleOpener.WaitForExit(5000) | Out-Null
        $generalNav = Wait-AppAutomationElement $probeProcess.Id 'GeneralNavItem' 5
        if ($null -eq $generalNav) { throw 'The General settings navigation item was not found.' }
        Select-AutomationElement $generalNav
        $expandSlider = Wait-AppAutomationElement $probeProcess.Id 'HoverExpandDelaySlider' 5
        $collapseSlider = Wait-AppAutomationElement $probeProcess.Id 'PointerLeaveCollapseDelaySlider' 5
        if ($null -eq $expandSlider -or $null -eq $collapseSlider) { throw 'The two hover delay sliders were not found.' }
        if (-not $expandSlider.Current.IsEnabled -or -not $collapseSlider.Current.IsEnabled) {
            throw 'An enabled hover option did not expose an enabled delay slider.'
        }
        if ($null -ne (Find-AppAutomationElement $probeProcess.Id 'CollapseToCenterToggle' $false)) {
            throw 'CollapseToCenterToggle is still exposed in settings.'
        }
        Set-AutomationRange $expandSlider 800
        Set-AutomationRange $collapseSlider 900
        Wait-HoverDelayState $statePath 800 900
        Close-AutomationWindow $collapseSlider
        Start-Sleep -Milliseconds 300
        if ($probeProcess.HasExited) { throw 'Closing settings exited TuckPane.' }

        Test-HoverCollapseBehavior $probeProcess 'Floating'
        Stop-Process -Id $probeProcess.Id -Force
        $probeProcess.WaitForExit(5000) | Out-Null
        $probeProcess = $null

        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        $state.Organizers[0].PlacementMode = 1
        $state.Organizers[0].Position = $null
        $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding utf8
        $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
        Start-Sleep -Milliseconds 1800
        Test-HoverCollapseBehavior $probeProcess 'Positioned'

        [pscustomobject]@{
            Passed = $true
            DelaySlidersPersisted = $true
            CollapseToCenterRemoved = $true
            FloatingThresholdsAndOrigin = $true
            PositionedThresholdsAndOrigin = $true
        } | Format-List
        return
    }

    if ($StationCoveredOnly) {
        $primaryBounds = [WindowLayerProbe]::GetPrimaryMonitorBounds()
        $safeX = [int](($primaryBounds.Left + $primaryBounds.Right) / 2)
        $safeY = [int](($primaryBounds.Top + $primaryBounds.Bottom) / 2)
        [WindowLayerProbe]::SetCursorPos($safeX, $safeY) | Out-Null
        [WindowLayerProbe]::CoverBounds($probeWindow, $primaryBounds)
        if ([WindowLayerProbe]::IsTopmost($probeWindow)) {
            throw 'The covering probe window unexpectedly remained topmost.'
        }

        $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
        Start-Sleep -Milliseconds 1800
        if ($probeProcess.HasExited) { throw 'TuckPane exited before the Station covered-window check.' }
        [WindowLayerProbe]::CoverBounds($probeWindow, $primaryBounds)
        if ([WindowLayerProbe]::IsTopmost($probeWindow)) {
            throw 'The restaged covering probe window unexpectedly remained topmost.'
        }
        if ([WindowLayerProbe]::WindowFromPoint([Drawing.Point]::new($safeX, $safeY)) -ne $probeWindow) {
            throw 'The covering probe window does not cover the primary-screen center.'
        }
        $layerPeer = [WindowLayerProbe]::FindOrganizerWindow($probeProcess.Id, $false)
        if ($layerPeer -eq [IntPtr]::Zero) { throw 'The preconfigured LayerPeer organizer was not found.' }
        if (-not [WindowLayerProbe]::IsAbove($probeWindow, $layerPeer)) {
            throw 'The covering normal window was not above LayerPeer before Station expansion.'
        }
        $foregroundBefore = [WindowLayerProbe]::GetForegroundWindow()
        [WindowLayerProbe]::SetCursorPos($primaryBounds.Right - 1, $safeY) | Out-Null

        $expandedWindow = Wait-VisibleStationWindow $probeProcess.Id 6
        if ($expandedWindow -eq [IntPtr]::Zero) { throw 'The primary right-edge Station did not expand.' }
        foreach ($sample in 1..15) {
            Start-Sleep -Milliseconds 100
            if (-not [WindowLayerProbe]::IsWindowVisible($expandedWindow) -or
                [WindowLayerProbe]::FindVisibleStationWindow($probeProcess.Id) -ne $expandedWindow) {
                throw 'The Station did not remain continuously expanded while the pointer stayed on the edge.'
            }
        }
        $desktopIconView = [WindowLayerProbe]::FindDesktopIconView()
        if ($desktopIconView -eq [IntPtr]::Zero) { throw 'Explorer desktop icon view was not found.' }
        if ([WindowLayerProbe]::GetOwner($expandedWindow) -eq $desktopIconView) {
            throw 'The expanded Station is still owned by the Explorer desktop layer.'
        }
        if (-not [WindowLayerProbe]::IsTopmost($expandedWindow)) {
            throw 'The expanded Station does not keep WS_EX_TOPMOST.'
        }
        if (-not [WindowLayerProbe]::IsNoActivate($expandedWindow)) {
            throw 'The expanded Station lost WS_EX_NOACTIVATE.'
        }
        $foregroundAfter = [WindowLayerProbe]::GetForegroundWindow()
        if ($foregroundAfter -eq $expandedWindow) {
            throw 'The expanded Station unexpectedly became the foreground window.'
        }
        if ($foregroundAfter -ne $foregroundBefore) {
            throw 'The expanded Station changed the foreground window.'
        }
        if (-not [WindowLayerProbe]::IsAbove($expandedWindow, $probeWindow)) {
            throw 'The expanded Station is behind the covering normal window.'
        }
        if (-not [WindowLayerProbe]::IsAbove($probeWindow, $layerPeer)) {
            throw 'Expanding the Station raised LayerPeer above the covering normal window.'
        }

        [WindowLayerProbe]::SetCursorPos($safeX, $safeY) | Out-Null
        if (-not (Wait-WindowHidden $expandedWindow 5)) {
            throw 'The Station did not hide after the pointer left.'
        }
        if ([WindowLayerProbe]::IsTopmost($expandedWindow)) {
            throw 'The hidden Station still keeps WS_EX_TOPMOST.'
        }
        foreach ($sample in 1..12) {
            Start-Sleep -Milliseconds 100
            if ([WindowLayerProbe]::FindVisibleStationWindow($probeProcess.Id) -ne [IntPtr]::Zero) {
                throw 'The Station reopened after the pointer left its expanded safe region.'
            }
        }

        [pscustomobject]@{
            Passed = $true
            StationExpanded = $true
            StableAtEdgeFor1500Ms = $true
            ExpandedDetachedFromDesktop = $true
            ExpandedTopmost = $true
            ExpandedNoActivate = $true
            ExpandedDidNotActivate = $true
            ExpandedAboveCoveringWindow = $true
            LayerPeerStayedBehindCover = $true
            HiddenAfterPointerLeave = $true
            StayedHiddenAfterPointerLeave = $true
            HiddenTopmostCleared = $true
        } | Format-List
        return
    }

    $env:GLASSFOLDER_TEST_EXPANDED = '1'
    $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru

    Start-Sleep -Milliseconds 1800
    $expandedWindow = Wait-OrganizerWindow $probeProcess.Id $true
    if ($expandedWindow -eq [IntPtr]::Zero) { throw 'Expanded organizer window was not found.' }
    $desktopIconView = [WindowLayerProbe]::FindDesktopIconView()
    if ($desktopIconView -eq [IntPtr]::Zero) { throw 'Explorer desktop icon view was not found.' }
    if ([WindowLayerProbe]::GetOwner($expandedWindow) -eq $desktopIconView) {
        throw 'Expanded organizer is still owned by the Explorer desktop layer.'
    }
    if ([WindowLayerProbe]::IsTopmost($expandedWindow)) {
        throw 'Expanded organizer unexpectedly uses WS_EX_TOPMOST.'
    }
    if (-not [WindowLayerProbe]::IsAbove($expandedWindow, $probeWindow)) {
        throw 'Expanded organizer was not raised above the existing normal window.'
    }

    [WindowLayerProbe]::BringNormalToTop($probeWindow)
    Start-Sleep -Milliseconds 200
    if (-not [WindowLayerProbe]::IsAbove($probeWindow, $expandedWindow)) {
        throw 'A subsequently raised normal window could not cover the expanded organizer.'
    }

    Stop-Process -Id $probeProcess.Id -Force
    $probeProcess.WaitForExit(5000) | Out-Null
    $probeProcess = $null

    $statePath = Join-Path $localRoot 'state.json'
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $state.GlobalSettings | Add-Member -NotePropertyName ExpandOnHover -NotePropertyValue $true -Force
    $state.GlobalSettings | Add-Member -NotePropertyName CollapseOnPointerLeave -NotePropertyValue $true -Force
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding utf8
    Remove-Item Env:GLASSFOLDER_TEST_EXPANDED -ErrorAction SilentlyContinue
    Remove-Item Env:GLASSFOLDER_TEST_TRANSITION_CYCLES -ErrorAction SilentlyContinue
    $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
    $compactWindow = Wait-OrganizerWindow $probeProcess.Id $false
    if ($compactWindow -eq [IntPtr]::Zero) { throw 'Compact organizer was not found for the covered-hover check.' }
    $compactBounds = New-Object WindowLayerProbe+Rect
    [WindowLayerProbe]::GetWindowRect($compactWindow, [ref]$compactBounds) | Out-Null
    $compactX = [int](($compactBounds.Left + $compactBounds.Right) / 2)
    $compactY = [int](($compactBounds.Top + $compactBounds.Bottom) / 2)
    [WindowLayerProbe]::CoverTarget($probeWindow, $compactWindow)
    [WindowLayerProbe]::SetCursorPos($compactX, $compactY) | Out-Null
    Start-Sleep -Milliseconds 800
    if ((Wait-OrganizerWindow $probeProcess.Id $true 1) -ne [IntPtr]::Zero) {
        throw 'A covered ordinary organizer expanded even though WindowFromPoint hit another app.'
    }
    if ([WindowLayerProbe]::WindowFromPoint([Drawing.Point]::new($compactX, $compactY)) -ne $probeWindow) {
        throw 'The normal probe window did not actually cover the compact organizer.'
    }

    [WindowLayerProbe]::MoveProbeAway($probeWindow)
    $expandedWindow = Wait-OrganizerWindow $probeProcess.Id $true 5
    if ($expandedWindow -eq [IntPtr]::Zero) { throw 'The exposed organizer did not resume hover expansion.' }
    $expandedBounds = New-Object WindowLayerProbe+Rect
    [WindowLayerProbe]::GetWindowRect($expandedWindow, [ref]$expandedBounds) | Out-Null
    $insideX = [int](($expandedBounds.Left + $expandedBounds.Right) / 2)
    $insideY = [int](($expandedBounds.Top + $expandedBounds.Bottom) / 2)
    [WindowLayerProbe]::SetCursorPos($expandedBounds.Left - 40, $expandedBounds.Top - 40) | Out-Null
    Start-Sleep -Milliseconds 200
    [WindowLayerProbe]::SetCursorPos($insideX, $insideY) | Out-Null
    Start-Sleep -Milliseconds 500
    if ((Wait-OrganizerWindow $probeProcess.Id $true 1) -eq [IntPtr]::Zero) {
        throw 'Returning within 400 milliseconds did not cancel pointer-leave collapse.'
    }
    [WindowLayerProbe]::SetCursorPos($expandedBounds.Left - 40, $expandedBounds.Top - 40) | Out-Null
    $compactWindow = Wait-OrganizerWindow $probeProcess.Id $false 5
    if ($compactWindow -eq [IntPtr]::Zero) { throw 'Pointer-leave collapse did not run after 400 milliseconds.' }

    Stop-Process -Id $probeProcess.Id -Force
    $probeProcess.WaitForExit(5000) | Out-Null
    $probeProcess = $null
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $state.GlobalSettings.ExpandOnHover = $false
    $state.GlobalSettings.CollapseOnPointerLeave = $false
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $statePath -Encoding utf8
    Remove-Item Env:GLASSFOLDER_TEST_EXPANDED -ErrorAction SilentlyContinue
    $env:GLASSFOLDER_TEST_TRANSITION_CYCLES = '1'
    $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru
    Start-Sleep -Milliseconds 3000
    if ($probeProcess.HasExited) { throw 'TuckPane exited during the expand-collapse layer check.' }
    $compactWindow = Wait-OrganizerWindow $probeProcess.Id $false
    if ($compactWindow -eq [IntPtr]::Zero) { throw 'Organizer did not finish collapsing.' }
    if ([WindowLayerProbe]::GetOwner($compactWindow) -ne $desktopIconView) {
        throw 'Collapsed organizer was not reattached to the Explorer desktop layer.'
    }

    [pscustomobject]@{
        Passed = $true
        ExpandedDetachedFromDesktop = $true
        ExpandedRaisedOnce = $true
        PersistentTopmost = $false
        NormalWindowCanCover = $true
        CoveredHoverSuppressed = $true
        ExposedHoverRestored = $true
        PointerLeaveCollapse = $true
        PointerReturnCancelledCollapse = $true
        CollapsedReattachedToDesktop = $true
    } | Format-List
}
finally {
    if ($probeProcess -and -not $probeProcess.HasExited) {
        Stop-Process -Id $probeProcess.Id -Force -ErrorAction SilentlyContinue
        $probeProcess.WaitForExit(5000) | Out-Null
    }
    if ($probeWindow -ne [IntPtr]::Zero) { [WindowLayerProbe]::DestroyWindow($probeWindow) | Out-Null }
    [WindowLayerProbe]::SetCursorPos($originalCursor.X, $originalCursor.Y) | Out-Null
    Remove-Item Env:TUCKPANE_TEST_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:GLASSFOLDER_TEST_EXPANDED -ErrorAction SilentlyContinue
    Remove-Item Env:GLASSFOLDER_TEST_TRANSITION_CYCLES -ErrorAction SilentlyContinue
    Remove-Item Env:TUCKPANE_TEST_RESIZE_AUTORUN -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
}
