using System.Runtime.InteropServices;

namespace TuckPane.Services;

/// <summary>File operations own their COM objects for the entire lifetime of a dedicated STA.</summary>
internal static class ShellFileOperations
{
    internal static Task RenameAsync(string path, string newName, IntPtr owner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (newName is "." or ".." || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            newName.EndsWith(' ') || newName.EndsWith('.'))
        {
            throw new ArgumentException("文件名包含无效字符，或以空格、句点结尾。", nameof(newName));
        }

        var source = Path.GetFullPath(path);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                RenameOnSta(source, newName, owner);
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }) { IsBackground = true, Name = "TuckPane Shell rename" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RenameOnSta(string path, string newName, IntPtr owner)
    {
        var destination = Path.Combine(Path.GetDirectoryName(path) ?? throw new IOException("无法确定文件所在目录。"), newName);
        if (string.Equals(path, destination, StringComparison.Ordinal)) return;
        if (!string.Equals(path, destination, StringComparison.OrdinalIgnoreCase) &&
            (File.Exists(destination) || Directory.Exists(destination)))
            throw new IOException("此位置已存在同名文件或文件夹，请使用其他名称。");

        // NOERRORUI + EARLYFAILURE returns failures to the inline editor; never auto-rename
        // collisions or answer Yes to replacement prompts. Keep Explorer's undo record.
        const uint flags = 0x20000000 | 0x0004 | 0x0400 | 0x00100000 | 0x00040000;
        IFileOperation? operation = null;
        IShellItem? item = null;
        var initialized = false;
        try
        {
            Check(CoInitializeEx(IntPtr.Zero, 2));
            initialized = true;
            var itemId = typeof(IShellItem).GUID;
            Check(SHCreateItemFromParsingName(path, IntPtr.Zero, ref itemId, out item));
            operation = (IFileOperation)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), throwOnError: true)!)!;
            Check(operation.SetOwnerWindow(owner));
            Check(operation.SetOperationFlags(flags));
            var sink = new RenameProgressSink();
            Check(operation.RenameItem(item, newName, sink));
            var result = operation.PerformOperations();
            // Per-item failure is more precise than the aggregate failure/cancel status.
            if (sink.RenameResult is int renameResult)
                Check(renameResult);
            Check(result);
            Check(operation.GetAnyOperationsAborted(out var aborted));
            if (aborted)
                throw new OperationCanceledException("重命名已取消，文件名未更改。");
            if (sink.RenameResult is null)
                throw new IOException("系统未确认重命名完成，请刷新后检查文件名。");
            GC.KeepAlive(sink);
        }
        finally
        {
            if (operation is not null) Marshal.FinalReleaseComObject(operation);
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (initialized) CoUninitialize();
        }
    }

    private static void Check(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);
    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        ref Guid interfaceId, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handlerId, ref Guid interfaceId, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint displayName, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    // Keep all methods in native vtable order, including the unused operations.
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        [PreserveSig] int CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink? sink);
        [PreserveSig] int DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }

    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IntPtr item, [MarshalAs(UnmanagedType.LPWStr)] string name, int result, IntPtr created);
        [PreserveSig] int PreMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostMoveItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IntPtr created);
        [PreserveSig] int PreCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name);
        [PreserveSig] int PostCopyItem(uint flags, IntPtr item, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, int result, IntPtr created);
        [PreserveSig] int PreDeleteItem(uint flags, IntPtr item);
        [PreserveSig] int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created);
        [PreserveSig] int PreNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IntPtr destination, [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? template, uint attributes, int result, IntPtr created);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RenameProgressSink : IFileOperationProgressSink
    {
        public int? RenameResult { get; private set; }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, string name) => 0;
        public int PostRenameItem(uint flags, IntPtr item, string name, int result, IntPtr created)
        {
            RenameResult = result;
            // Borrowed native pointers avoid creating callback RCWs on the short-lived STA.
            return 0;
        }
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, string? name) => 0;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, string? name, int result, IntPtr created) => 0;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, string? name) => 0;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, string? name, int result, IntPtr created) => 0;
        public int PreDeleteItem(uint flags, IntPtr item) => 0;
        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created) => 0;
        public int PreNewItem(uint flags, IntPtr destination, string name) => 0;
        public int PostNewItem(uint flags, IntPtr destination, string name, string? template, uint attributes, int result, IntPtr created) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }
}
