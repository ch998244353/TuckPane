using System.Runtime.InteropServices;
using Windows.Foundation;

namespace TuckPane.Services;

internal static class ShellMenuNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct CursorInfo
    {
        internal uint Size, Flags;
        internal IntPtr Cursor;
        internal NativeMethods.POINT Position;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IconInfo
    {
        internal int IsIcon;
        internal uint HotspotX, HotspotY;
        internal IntPtr Mask, Color;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct PopupParameters
    {
        internal uint Size;
        internal NativeMethods.RECT Exclude;
    }

    internal static PopupParameters CreatePopupParameters(Rect exclusion) => new()
    {
        Size = (uint)Marshal.SizeOf<PopupParameters>(),
        Exclude = new NativeMethods.RECT
        {
            Left = checked((int)Math.Floor(exclusion.Left)), Top = checked((int)Math.Floor(exclusion.Top)),
            Right = checked((int)Math.Ceiling(exclusion.Right)), Bottom = checked((int)Math.Ceiling(exclusion.Bottom))
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuInfo
    {
        internal uint Size, Mask, Style, MaximumHeight;
        internal IntPtr Background;
        internal uint HelpId;
        internal UIntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MenuLogFont
    {
        internal int Height, Width, Escapement, Orientation, Weight;
        internal byte Italic, Underline, StrikeOut, CharacterSet, OutPrecision, ClipPrecision, Quality, PitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string FaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NonClientMetrics
    {
        internal uint Size;
        internal int BorderWidth, ScrollWidth, ScrollHeight, CaptionWidth, CaptionHeight;
        internal MenuLogFont CaptionFont;
        internal int SmallCaptionWidth, SmallCaptionHeight;
        internal MenuLogFont SmallCaptionFont;
        internal int MenuWidth, MenuHeight;
        internal MenuLogFont MenuFont, StatusFont, MessageFont;
        internal int PaddedBorderWidth;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, in PopupParameters parameters);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetMenuInfo(IntPtr menu, ref MenuInfo info);
    [DllImport("user32.dll")] internal static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoForDpi")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNonClientMetrics(uint action, uint size, ref NonClientMetrics metrics, uint flags, uint dpi);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateFontIndirectW(in MenuLogFont font);
    [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int DrawTextW(IntPtr dc, string text, int length, ref NativeMethods.RECT rect, uint format);

    internal const uint FirstCommand = 1, LastCommand = 0x7fff;
    internal static readonly Guid ShellFolderId = new("000214E6-0000-0000-C000-000000000046");
    internal static readonly Guid ContextMenuId = new("000214E4-0000-0000-C000-000000000046");

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr bind, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumerator);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr bind, in Guid iid, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr bind, in Guid iid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr parameter, IntPtr first, IntPtr second);
        [PreserveSig] int CreateViewObject(IntPtr hwnd, in Guid iid, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls, in Guid iid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out IContextMenu result);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr offset, uint flags, IntPtr reserved, IntPtr text, uint length);
    }

    // Repeat the base slots explicitly so COM vtable layout is unambiguous.
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr offset, uint flags, IntPtr reserved, IntPtr text, uint length);
        [PreserveSig] int HandleMenuMsg(uint message, UIntPtr wparam, IntPtr lparam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
        [PreserveSig] int InvokeCommand(ref InvokeInfo info);
        [PreserveSig] int GetCommandString(UIntPtr offset, uint flags, IntPtr reserved, IntPtr text, uint length);
        [PreserveSig] int HandleMenuMsg(uint message, UIntPtr wparam, IntPtr lparam);
        [PreserveSig] int HandleMenuMsg2(uint message, UIntPtr wparam, IntPtr lparam, out IntPtr result);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct InvokeInfo
    {
        internal uint Size, Mask;
        internal IntPtr Owner, Verb, Parameters, Directory;
        internal int Show;
        internal uint HotKey;
        internal IntPtr Icon, Title, VerbW, ParametersW, DirectoryW, TitleW;
        internal NativeMethods.POINT Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MenuItemInfo
    {
        internal uint Size, Mask, Type, State, Id;
        internal IntPtr SubMenu, CheckedBitmap, UncheckedBitmap;
        internal UIntPtr Data;
        internal IntPtr Text;
        internal uint TextLength;
        internal IntPtr Bitmap;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int SHParseDisplayName(string name, IntPtr bind, out IntPtr pidl, uint requested, out uint attributes);
    [DllImport("shell32.dll", ExactSpelling = true)]
    internal static extern int SHBindToParent(IntPtr pidl, in Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellFolder folder, out IntPtr child);
    [DllImport("ole32.dll")] internal static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] internal static extern void OleUninitialize();
    [DllImport("user32.dll")] internal static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMenuItemInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMenuItemInfo(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MenuItemInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(in NativeMethods.MSG message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] internal static extern IntPtr DispatchMessage(in NativeMethods.MSG message);
    [DllImport("user32.dll")] internal static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EndMenu();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterClass(string name, IntPtr instance);
}
