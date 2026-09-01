param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$resolvedExe = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $resolvedExe -PathType Leaf)) { throw "Executable not found: $resolvedExe" }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class DeleteMenuProbe
{
    public const uint RightDown = 0x0008;
    public const uint RightUp = 0x0010;
    public const uint LeftDown = 0x0002;
    public const uint LeftUp = 0x0004;
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
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public static IntPtr FindVisibleWindow(int processId)
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
                rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindCompactWindow(int processId)
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
                rect.Right - rect.Left <= 300 && rect.Bottom - rect.Top <= 300)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr FindExpandedWindow(int processId)
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
                rect.Right - rect.Left >= 300 && rect.Bottom - rect.Top >= 250)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    public static IntPtr[] FindVisiblePopupWindows(int processId)
    {
        var results = new List<IntPtr>();
        EnumChildWindows(GetDesktopWindow(), delegate(IntPtr window, IntPtr parameter)
        {
            uint owner;
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId || !IsWindowVisible(window)) return true;
            var className = new StringBuilder(256);
            GetClassName(window, className, className.Capacity);
            Rect rect;
            if (className.ToString() == "Microsoft.UI.Content.PopupWindowSiteBridge" &&
                GetWindowRect(window, out rect) && rect.Right - rect.Left > 20 && rect.Bottom - rect.Top > 20)
            {
                results.Add(window);
            }
            return true;
        }, IntPtr.Zero);
        return results.ToArray();
    }

    public static void BringToFront(IntPtr window)
    {
        if (!SetWindowPos(window, TopMost, 0, 0, 0, 0, NoMove | NoSize | ShowWindow))
            throw new InvalidOperationException("SetWindowPos(HWND_TOPMOST) failed.");
    }

}
'@

function Get-RunValue([string]$Name) {
    $property = (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return [string]$property.Value
}

function Find-WindowElement([IntPtr]$Window, [string]$Name, [int]$TimeoutMilliseconds = 5000) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        try {
            $root = [System.Windows.Automation.AutomationElement]::FromHandle($Window)
            $element = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $element -and -not $element.Current.IsOffscreen) { return $element }
        }
        catch {
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    return $null
}

function Invoke-WindowElement([System.Windows.Automation.AutomationElement]$Element) {
    $pattern = $null
    if (-not $Element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        throw "Element does not support InvokePattern: $($Element.Current.Name)"
    }
    ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
}

function Open-ContextMenu([IntPtr]$Window) {
    $bounds = New-Object DeleteMenuProbe+Rect
    [DeleteMenuProbe]::GetWindowRect($Window, [ref]$bounds) | Out-Null
    $x = [int](($bounds.Left + $bounds.Right) / 2)
    $y = [int](($bounds.Top + $bounds.Bottom) / 2)
    [DeleteMenuProbe]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 60
    [DeleteMenuProbe]::mouse_event([DeleteMenuProbe]::RightDown, 0, 0, 0, [UIntPtr]::Zero)
    [DeleteMenuProbe]::mouse_event([DeleteMenuProbe]::RightUp, 0, 0, 0, [UIntPtr]::Zero)
}

function Invoke-LastContextMenuItem([IntPtr]$Window, [int]$ProcessId) {
    Open-ContextMenu $Window
    Start-Sleep -Milliseconds 300
    $candidates = @([DeleteMenuProbe]::FindVisiblePopupWindows($ProcessId) | ForEach-Object {
        $candidateBounds = New-Object DeleteMenuProbe+Rect
        [DeleteMenuProbe]::GetWindowRect($_, [ref]$candidateBounds) | Out-Null
        $width = $candidateBounds.Right - $candidateBounds.Left
        $height = $candidateBounds.Bottom - $candidateBounds.Top
        if ($width -le 300 -and $height -le 400) {
            [pscustomobject]@{
                Handle = $_
                Area = $width * $height
            }
        }
    })
    if ($candidates.Count -eq 0) { throw 'The real right click did not create a visible WinUI menu popup.' }
    $popup = ($candidates | Sort-Object Area -Descending | Select-Object -First 1).Handle
    $bounds = New-Object DeleteMenuProbe+Rect
    [DeleteMenuProbe]::GetWindowRect($popup, [ref]$bounds) | Out-Null
    $x = [int](($bounds.Left + $bounds.Right) / 2)
    $y = $bounds.Bottom - 40
    [DeleteMenuProbe]::SetCursorPos($x, $y) | Out-Null
    [DeleteMenuProbe]::mouse_event([DeleteMenuProbe]::LeftDown, 0, 0, 0, [UIntPtr]::Zero)
    [DeleteMenuProbe]::mouse_event([DeleteMenuProbe]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-OrganizerCount([string]$StatePath) {
    return @((Get-Content -LiteralPath $StatePath -Raw -Encoding UTF8 | ConvertFrom-Json).Organizers).Count
}

$projectRoot = Split-Path $PSScriptRoot -Parent
$runsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\delete-menu-runs'))
$runRoot = [IO.Path]::GetFullPath((Join-Path $runsRoot ([Guid]::NewGuid().ToString('N'))))
if (-not $runRoot.StartsWith($runsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Unexpected delete-menu test root.'
}
$localRoot = Join-Path $runRoot 'LocalAppData\TuckPane'
$itemsRoot = Join-Path $runRoot 'UserProfile\TuckPane\Windows\DeleteCheck-44444444\Items'
$statePath = Join-Path $localRoot 'state.json'
$desktopRoot = Join-Path $runRoot 'Desktop'
$exportRoot = Join-Path $desktopRoot 'DeleteCheck（已导出）'
$sourceFile = Join-Path $itemsRoot 'keep.txt'
$exportedFile = Join-Path $exportRoot 'keep.txt'
New-Item -ItemType Directory -Path $localRoot, $itemsRoot -Force | Out-Null
[IO.File]::WriteAllText($sourceFile, 'keep this file', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($statePath, @'
{
  "SchemaVersion": 7,
  "GlobalSettings": { "ThemeColorArgb": 4293060073, "Material": 0, "ThemeTransparency": 0.35, "StartWithWindows": false, "Language": 0 },
  "ConsolePlacement": null,
  "Organizers": [
    {
      "Id": "44444444-4444-4444-4444-444444444444",
      "Name": "DeleteCheck",
      "CreatedAtUtc": "2026-08-23T00:00:00+00:00",
      "PlacementMode": 0,
      "Layout": { "Mode": 0, "Rows": 3, "Columns": 3 },
      "CompactScale": 0.8,
      "CanvasScale": 0.8,
      "ItemScale": 1.0,
      "NameScale": 1.0,
      "Position": null,
      "StorageRelativePath": "Windows\\DeleteCheck-44444444\\Items",
      "StorageAbsolutePath": null,
      "ItemOrder": ["keep.txt"]
    }
  ]
}
'@, [Text.UTF8Encoding]::new($false))

$expectedMenu = @('设置', '窗口复制', '模式切换', '重命名', '打开收纳目录', '删除窗口')
$xaml = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src\TuckPane\MainWindow.xaml') -Raw -Encoding UTF8)
$menuNames = @('CompactTileContextMenu', 'ExpandedViewContextMenu', 'ItemContextMenu')
foreach ($menuName in $menuNames) {
    $menu = $xaml.SelectSingleNode("//*[local-name()='MenuFlyout' and @*[local-name()='Name' and .='$menuName']]")
    if ($null -eq $menu) { throw "Context menu was not found in XAML: $menuName" }
    $items = @($menu.ChildNodes | Where-Object LocalName -eq 'MenuFlyoutItem')
    $texts = @($items | ForEach-Object { $_.GetAttribute('Text') })
    if (($texts -join '|') -ne ($expectedMenu -join '|')) {
        throw "Unexpected XAML menu order for ${menuName}: $($texts -join ', ')"
    }
    if (@($items | Where-Object { $_.GetAttribute('Click') -eq 'DeleteWindowMenuItem_Click' }).Count -ne 1) {
        throw "Delete click handler is missing or duplicated in $menuName."
    }
}

$localizedDelete = @{
    'zh-CN' = '删除窗口'
    'en-US' = 'Delete window'
    'ja-JP' = 'ウィンドウを削除'
}
foreach ($language in $localizedDelete.Keys) {
    $resource = [xml](Get-Content -LiteralPath (Join-Path $projectRoot "src\TuckPane\Strings\$language\Resources.resw") -Raw -Encoding UTF8)
    $value = $resource.SelectSingleNode("//data[@name='ContextDeleteWindow']/value")
    if ($null -eq $value -or $value.InnerText -ne $localizedDelete[$language]) {
        throw "ContextDeleteWindow is missing or incorrect for $language."
    }
}
$startupBefore = Get-RunValue 'TuckPane'
$legacyStartupBefore = Get-RunValue 'GlassFolder'
$probeProcess = $null
$originalCursor = New-Object DeleteMenuProbe+Point
[DeleteMenuProbe]::GetCursorPos([ref]$originalCursor) | Out-Null

try {
    $env:TUCKPANE_TEST_ROOT = $runRoot
    Remove-Item Env:GLASSFOLDER_TEST_EXPANDED -ErrorAction SilentlyContinue
    Remove-Item Env:TUCKPANE_TEST_RESIZE_AUTORUN -ErrorAction SilentlyContinue
    $probeProcess = Start-Process -FilePath $resolvedExe -ArgumentList '--startup' -PassThru

    Start-Sleep -Milliseconds 1800
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $window = [IntPtr]::Zero
    do {
        Start-Sleep -Milliseconds 100
        if ($probeProcess.HasExited) { throw 'TuckPane exited before the delete-menu check started.' }
        $window = [DeleteMenuProbe]::FindCompactWindow($probeProcess.Id)
    } while ($window -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($window -eq [IntPtr]::Zero) { throw 'Organizer window was not found.' }

    [DeleteMenuProbe]::BringToFront($window)
    Start-Sleep -Milliseconds 500
    Invoke-LastContextMenuItem $window $probeProcess.Id
    $dialogDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 100
        $expandedWindow = [DeleteMenuProbe]::FindExpandedWindow($probeProcess.Id)
    } while ($expandedWindow -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $dialogDeadline)
    if ($expandedWindow -eq [IntPtr]::Zero) { throw 'Delete confirmation did not expand the organizer window.' }
    $cancel = Find-WindowElement $expandedWindow '取消'
    if ($null -eq $cancel) { throw 'Delete confirmation cancel button was not exposed by the organizer window.' }
    Invoke-WindowElement $cancel
    Start-Sleep -Milliseconds 400
    if ((Get-OrganizerCount $statePath) -ne 1 -or -not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
        throw 'Cancelling deletion changed the organizer or its file.'
    }

    $window = [DeleteMenuProbe]::FindVisibleWindow($probeProcess.Id)
    Invoke-LastContextMenuItem $window $probeProcess.Id
    $confirm = Find-WindowElement $window '导出并删除窗口'
    if ($null -eq $confirm) { throw 'Delete confirmation primary button was not exposed by the organizer window.' }
    Invoke-WindowElement $confirm

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 100
        $deleted = (Get-OrganizerCount $statePath) -eq 0
        $exported = Test-Path -LiteralPath $exportedFile -PathType Leaf
    } while ((-not $deleted -or -not $exported) -and [DateTime]::UtcNow -lt $deadline)

    if (-not $deleted) { throw 'Organizer state was not deleted.' }
    if (Test-Path -LiteralPath $itemsRoot) { throw 'Owned source storage still exists after export.' }
    if (-not $exported) { throw 'Organizer contents were not exported to the isolated Desktop.' }
    if ((Get-Content -LiteralPath $exportedFile -Raw -Encoding UTF8) -ne 'keep this file') {
        throw 'Exported file content changed.'
    }
    if ((Get-RunValue 'TuckPane') -ne $startupBefore -or (Get-RunValue 'GlassFolder') -ne $legacyStartupBefore) {
        throw 'The isolated delete-menu check changed a real startup entry.'
    }

    [pscustomobject]@{
        Passed = $true
        MenuOrder = $expectedMenu -join ' -> '
        Localizations = $localizedDelete.Count
        RealRightClickOpenedMenu = $true
        CancelPreservedOrganizer = $true
        ConfirmRemovedState = $deleted
        SourceStorageRemoved = -not (Test-Path -LiteralPath $itemsRoot)
        ExportedFilePreserved = $exported
        StartupRegistryUnchanged = $true
    } | Format-List
}
finally {
    if ($probeProcess -and -not $probeProcess.HasExited) {
        Stop-Process -Id $probeProcess.Id -Force -ErrorAction SilentlyContinue
        $probeProcess.WaitForExit(5000) | Out-Null
    }
    [DeleteMenuProbe]::SetCursorPos($originalCursor.X, $originalCursor.Y) | Out-Null
    Remove-Item Env:TUCKPANE_TEST_ROOT -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $runRoot) { Remove-Item -LiteralPath $runRoot -Recurse -Force }
}
