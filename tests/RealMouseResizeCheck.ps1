param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,
    [ValidateSet('Left', 'Right', 'Top', 'Bottom', 'TopLeft', 'TopRight', 'BottomLeft', 'BottomRight')]
    [string]$Direction = 'Right',
    [switch]$SummaryOnly
)

$ErrorActionPreference = 'Stop'
$resolvedExe = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $resolvedExe)) { throw "Executable not found: $resolvedExe" }

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class RealMouseResizeProbe
{
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
    public const uint CursorShowing = 0x00000001;
    public const uint SizeWestEast = 32644;
    public const uint SizeNorthSouth = 32645;
    public const uint SizeNorthWestSouthEast = 32642;
    public const uint SizeNorthEastSouthWest = 32643;
    public const uint NonClientHitTest = 0x0084;
    private static readonly IntPtr TopMost = new IntPtr(-1);
    private const uint NoMove = 0x0002;
    private const uint NoSize = 0x0001;
    private const uint ShowWindow = 0x0040;

    public delegate bool EnumProc(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CursorInfo
    {
        public int Size;
        public uint Flags;
        public IntPtr Cursor;
        public Point ScreenPosition;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point point);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    public static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static IntPtr FindExpandedWindow(int processId)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate(IntPtr window, IntPtr parameter)
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            Rect rect;
            if (owner == processId && IsWindowVisible(window) && GetWindowRect(window, out rect) &&
                rect.Width >= 300 && rect.Height >= 250)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static void BringToFront(IntPtr window)
    {
        if (!SetWindowPos(window, TopMost, 0, 0, 0, 0, NoMove | NoSize | ShowWindow))
            throw new InvalidOperationException("SetWindowPos(HWND_TOPMOST) failed.");
    }

    public static bool CursorIs(uint cursorId)
    {
        CursorInfo info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) && (info.Flags & CursorShowing) != 0 &&
            info.Cursor == LoadCursor(IntPtr.Zero, new IntPtr(cursorId));
    }

    public static IntPtr CurrentCursor()
    {
        CursorInfo info = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        return GetCursorInfo(ref info) ? info.Cursor : IntPtr.Zero;
    }

    public static int HitTest(IntPtr window, int x, int y)
    {
        long packed = (ushort)x | ((long)(ushort)y << 16);
        return unchecked((int)SendMessage(window, NonClientHitTest, IntPtr.Zero, new IntPtr(packed)).ToInt64());
    }

    public static int HitTestAt(int x, int y)
    {
        return HitTest(WindowAt(x, y), x, y);
    }

    public static IntPtr WindowAt(int x, int y)
    {
        return WindowFromPoint(new Point { X = x, Y = y });
    }

    public static int ProcessAt(int x, int y)
    {
        uint processId;
        GetWindowThreadProcessId(WindowAt(x, y), out processId);
        return unchecked((int)processId);
    }

    public static int ThreadAt(int x, int y)
    {
        uint processId;
        return unchecked((int)GetWindowThreadProcessId(WindowAt(x, y), out processId));
    }

    public static int ThreadFor(IntPtr window)
    {
        uint processId;
        return unchecked((int)GetWindowThreadProcessId(window, out processId));
    }

    public static string ClassAt(int x, int y)
    {
        StringBuilder text = new StringBuilder(256);
        GetClassName(WindowAt(x, y), text, text.Capacity);
        return text.ToString();
    }

    public static IntPtr ParentAt(int x, int y)
    {
        return GetParent(WindowAt(x, y));
    }

    public static string ParentChainAt(int x, int y)
    {
        IntPtr window = WindowAt(x, y);
        var parts = new List<string>();
        for (int depth = 0; window != IntPtr.Zero && depth < 8; depth++)
        {
            var text = new StringBuilder(256);
            GetClassName(window, text, text.Capacity);
            parts.Add(string.Format("0x{0:X}:{1}", window.ToInt64(), text));
            window = GetParent(window);
        }
        return string.Join(" -> ", parts);
    }

    public static double WaitForEdges(
        IntPtr window,
        int expectedLeft,
        int expectedTop,
        int expectedRight,
        int expectedBottom,
        int timeoutMilliseconds,
        out Rect rect)
    {
        Stopwatch clock = Stopwatch.StartNew();
        do
        {
            GetWindowRect(window, out rect);
            bool matched =
                (expectedLeft == int.MinValue || Math.Abs(rect.Left - expectedLeft) <= 1) &&
                (expectedTop == int.MinValue || Math.Abs(rect.Top - expectedTop) <= 1) &&
                (expectedRight == int.MinValue || Math.Abs(rect.Right - expectedRight) <= 1) &&
                (expectedBottom == int.MinValue || Math.Abs(rect.Bottom - expectedBottom) <= 1);
            if (matched) return clock.Elapsed.TotalMilliseconds;
            Thread.Sleep(1);
        }
        while (clock.ElapsedMilliseconds < timeoutMilliseconds);
        GetWindowRect(window, out rect);
        return clock.Elapsed.TotalMilliseconds;
    }
}
'@

function Get-RunValue([string]$Name) {
    $property = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return [string]$property.Value
}

function Move-And-CheckCursor([int]$X, [int]$Y, [uint32]$CursorId) {
    [RealMouseResizeProbe]::SetCursorPos($X - 50, $Y - 50) | Out-Null
    Start-Sleep -Milliseconds 20
    [RealMouseResizeProbe]::SetCursorPos($X, $Y) | Out-Null
    Start-Sleep -Milliseconds 60
    return [RealMouseResizeProbe]::CursorIs($CursorId)
}

$runRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts\real-mouse-runs\$([Guid]::NewGuid().ToString('N'))"
$localRoot = Join-Path $runRoot 'LocalAppData\TuckPane'
$itemsRoot = Join-Path $runRoot 'UserProfile\TuckPane\Windows\RealMouse-33333333\Items'
New-Item -ItemType Directory -Path $localRoot, $itemsRoot -Force | Out-Null

@'
{
  "SchemaVersion": 7,
  "GlobalSettings": { "ThemeColorArgb": 4293060073, "Material": 0, "ThemeTransparency": 0.35, "StartWithWindows": false, "Language": 0 },
  "ConsolePlacement": null,
  "Organizers": [
    {
      "Id": "33333333-3333-3333-3333-333333333333",
      "Name": "RealMouse",
      "CreatedAtUtc": "2026-08-23T00:00:00+00:00",
      "PlacementMode": 0,
      "Layout": { "Mode": 0, "Rows": 3, "Columns": 3 },
      "CompactScale": 0.8,
      "CanvasScale": 0.8,
      "ItemScale": 1.0,
      "NameScale": 1.0,
      "Position": null,
      "StorageRelativePath": "Windows\\RealMouse-33333333\\Items",
      "StorageAbsolutePath": null,
      "ItemOrder": []
    }
  ]
}
'@ | Set-Content -LiteralPath (Join-Path $localRoot 'state.json') -Encoding UTF8

$startupBefore = Get-RunValue 'TuckPane'
$legacyStartupBefore = Get-RunValue 'GlassFolder'
$probeProcess = $null
$mouseDown = $false

try {
    $env:TUCKPANE_TEST_ROOT = $runRoot
    $env:GLASSFOLDER_TEST_EXPANDED = '1'
    Remove-Item Env:TUCKPANE_TEST_RESIZE_AUTORUN -ErrorAction SilentlyContinue
    $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru

    Start-Sleep -Milliseconds 1800
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $window = [IntPtr]::Zero
    do {
        Start-Sleep -Milliseconds 100
        if ($probeProcess.HasExited) { throw 'TuckPane exited before the real-mouse probe started.' }
        $window = [RealMouseResizeProbe]::FindExpandedWindow($probeProcess.Id)
    } while ($window -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($window -eq [IntPtr]::Zero) { throw 'Expanded organizer window was not found.' }

    [RealMouseResizeProbe]::BringToFront($window)
    Start-Sleep -Milliseconds 1200
    $bounds = New-Object RealMouseResizeProbe+Rect
    [RealMouseResizeProbe]::GetWindowRect($window, [ref]$bounds) | Out-Null
    $centerX = [int](($bounds.Left + $bounds.Right) / 2)
    $centerY = [int](($bounds.Top + $bounds.Bottom) / 2)

    $hoverChecks = @(
        [pscustomobject]@{ Name = 'Left-2'; X = $bounds.Left + 2; Y = $centerY; Cursor = [RealMouseResizeProbe]::SizeWestEast },
        [pscustomobject]@{ Name = 'Left-8'; X = $bounds.Left + 8; Y = $centerY; Cursor = [RealMouseResizeProbe]::SizeWestEast },
        [pscustomobject]@{ Name = 'Left-16'; X = $bounds.Left + 16; Y = $centerY; Cursor = [RealMouseResizeProbe]::SizeWestEast },
        [pscustomobject]@{ Name = 'Right-2'; X = $bounds.Right - 2; Y = $centerY; Cursor = [RealMouseResizeProbe]::SizeWestEast },
        [pscustomobject]@{ Name = 'Right-8'; X = $bounds.Right - 8; Y = $centerY; Cursor = [RealMouseResizeProbe]::SizeWestEast },
        [pscustomobject]@{ Name = 'Right-16'; X = $bounds.Right - 16; Y = $centerY; Cursor = [RealMouseResizeProbe]::SizeWestEast },
        [pscustomobject]@{ Name = 'Top-8'; X = $centerX; Y = $bounds.Top + 8; Cursor = [RealMouseResizeProbe]::SizeNorthSouth },
        [pscustomobject]@{ Name = 'Bottom-8'; X = $centerX; Y = $bounds.Bottom - 8; Cursor = [RealMouseResizeProbe]::SizeNorthSouth },
        [pscustomobject]@{ Name = 'TopLeft-8'; X = $bounds.Left + 8; Y = $bounds.Top + 8; Cursor = [RealMouseResizeProbe]::SizeNorthWestSouthEast },
        [pscustomobject]@{ Name = 'TopRight-8'; X = $bounds.Right - 8; Y = $bounds.Top + 8; Cursor = [RealMouseResizeProbe]::SizeNorthEastSouthWest },
        [pscustomobject]@{ Name = 'BottomLeft-8'; X = $bounds.Left + 8; Y = $bounds.Bottom - 8; Cursor = [RealMouseResizeProbe]::SizeNorthEastSouthWest },
        [pscustomobject]@{ Name = 'BottomRight-8'; X = $bounds.Right - 8; Y = $bounds.Bottom - 8; Cursor = [RealMouseResizeProbe]::SizeNorthWestSouthEast }
    )
    $hoverResults = @($hoverChecks | ForEach-Object {
        [pscustomobject]@{
            Name = $_.Name
            Passed = Move-And-CheckCursor $_.X $_.Y $_.Cursor
            ActualCursor = '0x{0:X}' -f ([RealMouseResizeProbe]::CurrentCursor().ToInt64())
            ExpectedCursor = '0x{0:X}' -f ([RealMouseResizeProbe]::LoadCursor([IntPtr]::Zero, [IntPtr]([int64]$_.Cursor)).ToInt64())
            HitTest = [RealMouseResizeProbe]::HitTest($window, $_.X, $_.Y)
            HitTestAtPoint = [RealMouseResizeProbe]::HitTestAt($_.X, $_.Y)
            WindowAtPoint = '0x{0:X}' -f ([RealMouseResizeProbe]::WindowAt($_.X, $_.Y).ToInt64())
            ProcessAtPoint = [RealMouseResizeProbe]::ProcessAt($_.X, $_.Y)
            ThreadAtPoint = [RealMouseResizeProbe]::ThreadAt($_.X, $_.Y)
            ClassAtPoint = [RealMouseResizeProbe]::ClassAt($_.X, $_.Y)
            ParentAtPoint = '0x{0:X}' -f ([RealMouseResizeProbe]::ParentAt($_.X, $_.Y).ToInt64())
            ParentChain = [RealMouseResizeProbe]::ParentChainAt($_.X, $_.Y)
        }
    })

    [RealMouseResizeProbe]::GetWindowRect($window, [ref]$bounds) | Out-Null
    $dragLeft = $Direction -in @('Left', 'TopLeft', 'BottomLeft')
    $dragRight = $Direction -in @('Right', 'TopRight', 'BottomRight')
    $dragTop = $Direction -in @('Top', 'TopLeft', 'TopRight')
    $dragBottom = $Direction -in @('Bottom', 'BottomLeft', 'BottomRight')
    $startX = if ($dragLeft) { $bounds.Left + 4 } elseif ($dragRight) { $bounds.Right - 4 } else { $centerX }
    $startY = if ($dragTop) { $bounds.Top + 4 } elseif ($dragBottom) { $bounds.Bottom - 4 } else { $centerY }
    $dragCursorId = if (($dragLeft -or $dragRight) -and ($dragTop -or $dragBottom)) {
        if ($Direction -in @('TopLeft', 'BottomRight')) {
            [RealMouseResizeProbe]::SizeNorthWestSouthEast
        } else {
            [RealMouseResizeProbe]::SizeNorthEastSouthWest
        }
    } elseif ($dragLeft -or $dragRight) {
        [RealMouseResizeProbe]::SizeWestEast
    } else {
        [RealMouseResizeProbe]::SizeNorthSouth
    }
    $dragCursorReady = Move-And-CheckCursor $startX $startY $dragCursorId
    [RealMouseResizeProbe]::mouse_event([RealMouseResizeProbe]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    $mouseDown = $true
    Start-Sleep -Milliseconds 25

    $samples = for ($step = 1; $step -le 12; $step++) {
        $increment = if (($dragLeft -or $dragRight) -and ($dragTop -or $dragBottom)) { 5 } else { 6 }
        $deltaX = if ($dragLeft) { $step * $increment } elseif ($dragRight) { -$step * $increment } else { 0 }
        $deltaY = if ($dragTop) { $step * $increment } elseif ($dragBottom) { -$step * $increment } else { 0 }
        $cursorX = $startX + $deltaX
        $cursorY = $startY + $deltaY
        [RealMouseResizeProbe]::SetCursorPos($cursorX, $cursorY) | Out-Null
        $ignoredEdge = [int]::MinValue
        $expectedLeft = if ($dragLeft) { $bounds.Left + $deltaX } else { $ignoredEdge }
        $expectedTop = if ($dragTop) { $bounds.Top + $deltaY } else { $ignoredEdge }
        $expectedRight = if ($dragRight) { $bounds.Right + $deltaX } else { $ignoredEdge }
        $expectedBottom = if ($dragBottom) { $bounds.Bottom + $deltaY } else { $ignoredEdge }
        $sampleBounds = New-Object RealMouseResizeProbe+Rect
        $settledMs = [RealMouseResizeProbe]::WaitForEdges(
            $window,
            $expectedLeft,
            $expectedTop,
            $expectedRight,
            $expectedBottom,
            80,
            [ref]$sampleBounds)
        $edgeErrors = @()
        if ($dragLeft) { $edgeErrors += [Math]::Abs($sampleBounds.Left - $expectedLeft) }
        if ($dragTop) { $edgeErrors += [Math]::Abs($sampleBounds.Top - $expectedTop) }
        if ($dragRight) { $edgeErrors += [Math]::Abs($sampleBounds.Right - $expectedRight) }
        if ($dragBottom) { $edgeErrors += [Math]::Abs($sampleBounds.Bottom - $expectedBottom) }
        $centerError = [Math]::Max(
            [Math]::Abs(($sampleBounds.Left + $sampleBounds.Right) - ($bounds.Left + $bounds.Right)) / 2,
            [Math]::Abs(($sampleBounds.Top + $sampleBounds.Bottom) - ($bounds.Top + $bounds.Bottom)) / 2)
        $aspectError = [Math]::Abs($sampleBounds.Height - [Math]::Round($sampleBounds.Width * $bounds.Height / $bounds.Width))
        [pscustomobject]@{
            Step = $step
            CursorX = $cursorX
            CursorY = $cursorY
            TrackingErrorPx = ($edgeErrors | Measure-Object -Maximum).Maximum
            CenterErrorPx = $centerError
            AspectErrorPx = $aspectError
            SettledMs = [Math]::Round($settledMs, 2)
            Width = $sampleBounds.Width
            Height = $sampleBounds.Height
            BoundsKey = "$($sampleBounds.Left),$($sampleBounds.Top),$($sampleBounds.Right),$($sampleBounds.Bottom)"
        }
    }

    $beforeRelease = New-Object RealMouseResizeProbe+Rect
    [RealMouseResizeProbe]::GetWindowRect($window, [ref]$beforeRelease) | Out-Null
    [RealMouseResizeProbe]::mouse_event([RealMouseResizeProbe]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
    $mouseDown = $false
    Start-Sleep -Milliseconds 120
    $afterRelease = New-Object RealMouseResizeProbe+Rect
    [RealMouseResizeProbe]::GetWindowRect($window, [ref]$afterRelease) | Out-Null
    $releaseJumpPx = [Math]::Max(
        [Math]::Max([Math]::Abs($afterRelease.Left - $beforeRelease.Left), [Math]::Abs($afterRelease.Top - $beforeRelease.Top)),
        [Math]::Max([Math]::Abs($afterRelease.Right - $beforeRelease.Right), [Math]::Abs($afterRelease.Bottom - $beforeRelease.Bottom)))

    [RealMouseResizeProbe]::SetCursorPos($centerX, $centerY) | Out-Null
    Start-Sleep -Milliseconds 120
    $afterFreeMove = New-Object RealMouseResizeProbe+Rect
    [RealMouseResizeProbe]::GetWindowRect($window, [ref]$afterFreeMove) | Out-Null
    $followedAfterRelease = $afterFreeMove.Left -ne $afterRelease.Left -or
        $afterFreeMove.Top -ne $afterRelease.Top -or
        $afterFreeMove.Right -ne $afterRelease.Right -or
        $afterFreeMove.Bottom -ne $afterRelease.Bottom

    $hoverFailures = @($hoverResults | Where-Object { -not $_.Passed })
    $maxErrorPx = ($samples | Measure-Object TrackingErrorPx -Maximum).Maximum
    $maxCenterErrorPx = ($samples | Measure-Object CenterErrorPx -Maximum).Maximum
    $maxAspectErrorPx = ($samples | Measure-Object AspectErrorPx -Maximum).Maximum
    $maxSettleMs = ($samples | Measure-Object SettledMs -Maximum).Maximum
    $distinctBounds = @($samples.BoundsKey | Select-Object -Unique).Count
    $passed = $hoverFailures.Count -eq 0 -and $dragCursorReady -and
        $maxErrorPx -le 1 -and $maxCenterErrorPx -le 1 -and $maxAspectErrorPx -le 1 -and
        $maxSettleMs -le 60 -and $distinctBounds -eq 12 -and
        $releaseJumpPx -le 1 -and -not $followedAfterRelease

    $summary = [pscustomobject]@{
        Passed = $passed
        Direction = $Direction
        ProbeProcessId = $probeProcess.Id
        TargetWindow = '0x{0:X}' -f $window.ToInt64()
        TargetThreadId = [RealMouseResizeProbe]::ThreadFor($window)
        HoverChecks = $hoverResults.Count
        HoverFailures = ($hoverFailures.Name -join ', ')
        DragCursorReady = $dragCursorReady
        DistinctLiveBounds = $distinctBounds
        MaxTrackingErrorPx = $maxErrorPx
        MaxCenterErrorPx = $maxCenterErrorPx
        MaxAspectErrorPx = $maxAspectErrorPx
        MaxSettleMs = $maxSettleMs
        ReleaseJumpPx = $releaseJumpPx
        FollowedAfterRelease = $followedAfterRelease
    }
    $summary | Format-List
    if (-not $SummaryOnly) {
        $hoverResults | Format-List
        $samples | Select-Object -ExcludeProperty BoundsKey | Format-Table -AutoSize
    }

    if (-not $passed) { throw 'Real mouse resize regression check failed.' }
}
finally {
    if ($mouseDown) {
        [RealMouseResizeProbe]::mouse_event([RealMouseResizeProbe]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
    }
    if ($null -ne $probeProcess -and -not $probeProcess.HasExited) {
        Stop-Process -Id $probeProcess.Id -ErrorAction SilentlyContinue
        Wait-Process -Id $probeProcess.Id -ErrorAction SilentlyContinue
    }
    Remove-Item Env:TUCKPANE_TEST_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:GLASSFOLDER_TEST_EXPANDED -ErrorAction SilentlyContinue
    if ((Get-RunValue 'TuckPane') -ne $startupBefore -or
        (Get-RunValue 'GlassFolder') -ne $legacyStartupBefore) {
        throw 'The isolated real-mouse probe changed the real startup registry values.'
    }
}
