using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using TuckPane.Core;
using static TuckPane.Services.ShellMenuNative;

namespace TuckPane.Services;

// COM, HMENU and its message window stay on one STA. A blocked extension must
// not block WinUI or cause each retry to allocate another apartment.
internal sealed class ShellMenuSession : IDisposable
{
    private const uint WorkMessage = 0x8000 + 181, CancelMenuMessage = WorkMessage + 1;
    private static readonly FileContextMenuGate ApartmentGate = new();
    private readonly IDisposable _apartmentLease;
    private readonly IntPtr _commandOwner;
    private readonly ConcurrentQueue<(Action Run, Action<Exception> Fail)> _work = new();
    private readonly object _queueLock = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly NativeMethods.WindowProc _windowProc;
    private IContextMenu? _menuObject;
    private IContextMenu2? _menu2;
    private IContextMenu3? _menu3;
    private IntPtr _window, _menu;
    private uint _commandCount, _threadId;
    private bool _working, _tracking, _exiting;
    private int _disposed;

    private ShellMenuSession(IDisposable apartmentLease, IntPtr commandOwner)
    {
        _apartmentLease = apartmentLease;
        _commandOwner = commandOwner;
        _windowProc = WindowProc;
        var thread = new Thread(Work) { IsBackground = true, Name = "TuckPane Shell menu" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    internal static async Task<ShellMenuSession> OpenAsync(string path, IntPtr commandOwner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IDisposable lease = ApartmentGate.TryEnter()
            ?? throw new InvalidOperationException(AppStrings.Get("FileMenuSessionBusy"));
        ShellMenuSession session;
        try { session = new ShellMenuSession(lease, commandOwner); }
        catch { lease.Dispose(); throw; }
        await FileMenuInitialization.RunAsync(async initializationToken =>
        {
            await session._ready.Task.WaitAsync(initializationToken).ConfigureAwait(false);
            await session.Queue(() =>
            {
                session.CheckActive(initializationToken);
                session.Initialize(path, initializationToken);
                session.CheckActive(initializationToken);
                return true;
            }).WaitAsync(initializationToken).ConfigureAwait(false);
        }, session.Dispose, token).ConfigureAwait(false);
        return session;
    }

    internal Task<FileMenuSelection?> ShowClassicAsync(FileMenuPopup popup, CancellationToken token) => Queue<FileMenuSelection?>(() =>
    {
        CheckActive(token);
        using var cancel = token.Register(RequestMenuCancellation);
        _tracking = true;
        try
        {
            ShowWindow(_window, 4); // SW_SHOWNOACTIVATE; the host itself has no content.
            NativeMethods.SetForegroundWindow(_window);
            CheckActive(token); // Foreground/activation messages may have pumped cancellation.
            uint flags = NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | popup.PositionFlags;
            int x = checked((int)popup.Position.X), y = checked((int)popup.Position.Y);
            uint id;
            if (popup.Exclusion is { } exclusion)
            {
                PopupParameters parameters = CreatePopupParameters(exclusion);
                id = TrackPopupMenuEx(_menu, flags, x, y, _window, in parameters);
            }
            else id = TrackPopupMenuEx(_menu, flags, x, y, _window, IntPtr.Zero);
            CheckActive(token);
            if (id == 0) return null;
            if (!ValidId(id)) throw new InvalidOperationException("Shell returned an invalid command identifier.");
            return new FileMenuSelection(id, Verb(id));
        }
        finally
        {
            _tracking = false;
            NativeMethods.PostMessage(_window, 0, UIntPtr.Zero, IntPtr.Zero); // WM_NULL after native menu tracking.
            ShowWindow(_window, 0);
        }
    }).WaitAsync(token);

    internal Task InvokeAsync(FileMenuSelection selection, CancellationToken token) => Queue(() =>
    {
        CheckActive(token);
        if (!ValidId(selection.CommandId)) throw new InvalidOperationException("Invalid Shell command.");
        IntPtr offset = (IntPtr)(selection.CommandId - FirstCommand);
        var info = new InvokeInfo
        {
            Size = (uint)Marshal.SizeOf<InvokeInfo>(), Mask = 0x4000, // CMIC_MASK_UNICODE
            // Command dialogs may outlive InvokeCommand. Keep their existing application owner.
            Owner = _commandOwner, Verb = offset, VerbW = offset, Show = 1
        };
        CheckActive(token);
        Marshal.ThrowExceptionForHR(_menuObject!.InvokeCommand(ref info));
        return true;
    }).WaitAsync(token);

    private void CheckActive(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private bool ValidId(uint id) => id >= FirstCommand && id - FirstCommand < _commandCount;

    private void Initialize(string path, CancellationToken token)
    {
        IntPtr absolute = IntPtr.Zero;
        IShellFolder? folder = null;
        try
        {
            CheckActive(token);
            Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out absolute, 0, out _));
            CheckActive(token);
            Marshal.ThrowExceptionForHR(SHBindToParent(absolute, ShellFolderId, out folder, out IntPtr child));
            CheckActive(token);
            Marshal.ThrowExceptionForHR(folder.GetUIObjectOf(_window, 1, [child], ContextMenuId, IntPtr.Zero, out var context));
            _menuObject = context;
            CheckActive(token);
            _menu3 = context as IContextMenu3;
            _menu2 = context as IContextMenu2;
            _menu = NativeMethods.CreatePopupMenu();
            if (_menu == IntPtr.Zero) throw new Win32Exception();
            CheckActive(token);
            int result = context.QueryContextMenu(_menu, 0, FirstCommand, LastCommand, 0x10 | 0x80); // CANRENAME | ITEMMENU
            Marshal.ThrowExceptionForHR(result);
            _commandCount = (uint)(result & 0xffff);
        }
        finally
        {
            if (folder is not null) Cleanup(() => Marshal.FinalReleaseComObject(folder));
            if (absolute != IntPtr.Zero) Marshal.FreeCoTaskMem(absolute);
        }
    }

    private string? Verb(uint id)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt16(buffer, 0);
            int hr = _menuObject!.GetCommandString((UIntPtr)(id - FirstCommand), 4, IntPtr.Zero, buffer, 512);
            return hr < 0 ? null : Marshal.PtrToStringUni(buffer)?.ToLowerInvariant();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private Task<T> Queue<T>(Func<T> action)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_queueLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _work.Enqueue((() =>
            {
                try { CheckActive(CancellationToken.None); done.TrySetResult(action()); }
                catch (Exception ex) { done.TrySetException(ex); }
            }, ex => done.TrySetException(ex)));
            SignalWork();
        }
        return done.Task;
    }

    private void SignalWork()
    {
        if (_exiting) return; // Caller holds _queueLock; never post to recycled native identifiers.
        if (NativeMethods.PostMessage(_window, WorkMessage, UIntPtr.Zero, IntPtr.Zero)) return;
        // Never leave the caller waiting for a message that was not delivered.
        _disposed = 1;
        var error = new InvalidOperationException("The Shell menu message window is unavailable.");
        while (_work.TryDequeue(out var pending)) pending.Fail(error);
        if (!NativeMethods.PostThreadMessage(_threadId, 0x12, UIntPtr.Zero, IntPtr.Zero)) // WM_QUIT
            AppLogger.Error("Unable to stop Shell menu apartment", error);
    }

    private void Work()
    {
        string className = "TuckPane.ShellMenu." + Guid.NewGuid().ToString("N");
        bool ole = false, registered = false;
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            Marshal.ThrowExceptionForHR(OleInitialize(IntPtr.Zero)); ole = true;
            var windowClass = new NativeMethods.WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(), ClassName = className,
                WindowProc = Marshal.GetFunctionPointerForDelegate(_windowProc)
            };
            if (NativeMethods.RegisterClassEx(ref windowClass) == 0) throw new Win32Exception();
            registered = true;
            // Independent tool window: do not couple the menu's input queue or lifetime to WinUI.
            _window = NativeMethods.CreateWindowEx(0x80, className, null, 0x80000000,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_window == IntPtr.Zero) throw new Win32Exception();
            _ready.TrySetResult();
            int result;
            while ((result = NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(message);
                DispatchMessage(message);
            }
            if (result < 0) throw new Win32Exception();
        }
        catch (Exception ex) { _ready.TrySetException(ex); AppLogger.Error("Shell menu apartment failed", ex); }
        finally
        {
            IntPtr window;
            lock (_queueLock)
            {
                _exiting = true;
                window = _window;
                _window = IntPtr.Zero;
                _threadId = 0;
                _disposed = 1;
                while (_work.TryDequeue(out var pending)) pending.Fail(new ObjectDisposedException(nameof(ShellMenuSession)));
            }
            if (_menu != IntPtr.Zero) Cleanup(() => NativeMethods.DestroyMenu(_menu));
            _menu2 = null; _menu3 = null;
            if (_menuObject is not null) Cleanup(() => Marshal.FinalReleaseComObject(_menuObject));
            if (window != IntPtr.Zero) Cleanup(() => NativeMethods.DestroyWindow(window));
            if (registered) Cleanup(() => UnregisterClass(className, IntPtr.Zero));
            if (ole) Cleanup(OleUninitialize);
            GC.KeepAlive(_windowProc);
            // Release only after native work and cleanup actually stop, not at timeout/Dispose.
            _apartmentLease.Dispose();
        }
    }

    private static void Cleanup(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLogger.Error("Shell menu resource cleanup failed", ex); }
    }

    private IntPtr WindowProc(IntPtr window, uint message, UIntPtr wparam, IntPtr lparam)
    {
        if (message == 0x2) // WM_DESTROY: invalidate before the HWND can be recycled.
        {
            lock (_queueLock) { _exiting = true; _disposed = 1; _window = IntPtr.Zero; }
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        if (message == CancelMenuMessage) { if (_tracking) EndMenu(); return IntPtr.Zero; }
        if (message == WorkMessage)
        {
            // A Shell call may pump a nested modal loop. Never dispose resources reentrantly.
            if (_working) return IntPtr.Zero;
            _working = true;
            try { if (_work.TryDequeue(out var work)) work.Run(); }
            finally
            {
                _working = false;
                lock (_queueLock) { if (!_work.IsEmpty) SignalWork(); }
            }
            return IntPtr.Zero;
        }
        if (message is 0x117 or 0x125 or 0x2b or 0x2c or 0x120 && Forward(message, wparam, lparam, out var result)) return result;
        return NativeMethods.DefWindowProc(window, message, wparam, lparam);
    }

    private bool Forward(uint message, UIntPtr wparam, IntPtr lparam, out IntPtr result)
    {
        result = IntPtr.Zero;
        try
        {
            if (_menu3 is not null && _menu3.HandleMenuMsg2(message, wparam, lparam, out result) == 0) return true;
            if (_menu2 is not null && message != 0x120 && _menu2.HandleMenuMsg(message, wparam, lparam) == 0) return true;
        }
        catch (Exception ex) { AppLogger.Error("Shell menu message failed", ex); }
        return false;
    }

    private void RequestMenuCancellation()
    {
        lock (_queueLock)
        {
            if (_exiting) return;
            if (!NativeMethods.PostMessage(_window, CancelMenuMessage, UIntPtr.Zero, IntPtr.Zero))
                SignalWork(); // Fails pending tasks and requests thread exit if HWND is gone.
        }
    }

    public void Dispose()
    {
        lock (_queueLock)
        {
            if (_disposed != 0) return;
            _disposed = 1;
        }
        // Even an initialization timeout can precede HWND creation. Retain the
        // apartment lease until the eventual owner-thread cleanup has completed.
        _ = _ready.Task.ContinueWith(task =>
        {
            _ = task.Exception; // Observe startup failure, including after a timeout.
            if (!task.IsCompletedSuccessfully) return;
            lock (_queueLock)
            {
                if (_exiting) return;
                RequestMenuCancellation();
                _work.Enqueue((() => PostQuitMessage(0), _ => { }));
                SignalWork();
            }
        }, TaskScheduler.Default);
    }
}
