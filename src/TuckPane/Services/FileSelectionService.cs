using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal static class FileSelectionService
{
    private const uint ForceFileSystem = 0x40;
    private const uint AllowMultiSelect = 0x200;
    private const uint PathMustExist = 0x800;
    private const uint FileMustExist = 0x1000;
    private const uint NoDereferenceLinks = 0x100000;
    private const int Cancelled = unchecked((int)0x800704C7);

    // Called on the owner window's STA. Show runs the standard Windows modal loop.
    internal static string? PickSingleFile(IntPtr owner)
    {
        object instance = new FileOpenDialog();
        IShellItem? result = null;
        try
        {
            var dialog = (IFileOpenDialog)instance;
            dialog.GetOptions(out uint options);
            dialog.SetOptions((options & ~AllowMultiSelect) | ForceFileSystem |
                PathMustExist | FileMustExist | NoDereferenceLinks);
            dialog.SetTitle(AppStrings.Get("ContextAddItem"));
            dialog.SetOkButtonLabel(AppStrings.Get("ContextAddItem"));
            int hr = dialog.Show(owner);
            if (hr == Cancelled) return null;
            Marshal.ThrowExceptionForHR(hr);
            dialog.GetResult(out result);
            result.GetDisplayName(0x80058000, out IntPtr path); // SIGDN_FILESYSPATH
            try { return Marshal.PtrToStringUni(path); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        finally
        {
            if (result is not null) Marshal.FinalReleaseComObject(result);
            Marshal.FinalReleaseComObject(instance);
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // The inherited IModalWindow/IFileDialog vtable prefix, through GetResult.
    [ComImport, Guid("D57C7288-D4AD-4768-BE02-9D969532D960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes(uint count, IntPtr types);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem folder);
        void SetFolder(IShellItem folder);
        void GetFolder(out IShellItem folder);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName(out IntPtr name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint type, out IntPtr name);
    }
}
