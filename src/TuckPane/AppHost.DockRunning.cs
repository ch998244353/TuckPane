using Microsoft.UI.Dispatching;
using TuckPane.Core;
using TuckPane.Models;
using TuckPane.Services;

namespace TuckPane;

public sealed partial class AppHost
{
    private readonly DockRunningService _dockRunningService = new();
    private readonly DockSnapshotVersion _dockSnapshotVersion = new();
    private DispatcherQueueTimer? _dockRunningTimer;
    private string[] _dockRunningPaths = [];
    private Dictionary<string, bool> _dockOpenPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _dockSampling, _dockStateDisposed, _dockNotificationQueued;

    internal void RefreshDockRunningSubscription(bool invalidate = false)
    {
        if (_dockStateDisposed) return;
        string[] paths = _windows.Values.Where(window => window.WantsDockRunningState)
            .SelectMany(window => window.ItemSnapshot)
            .Where(item => item.Kind is WidgetItemKind.File or WidgetItemKind.Folder or WidgetItemKind.Shortcut)
            .Select(item => item.FullPath).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (invalidate || !_dockRunningPaths.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase))
        {
            _dockRunningPaths = paths;
            _dockSnapshotVersion.Invalidate();
            _dockRunningService.Invalidate();
            _dockOpenPaths.Clear();
        }
        if (paths.Length == 0)
        {
            _dockRunningTimer?.Stop();
        }
        else
        {
            if (_dockRunningTimer is null)
            {
                _dockRunningTimer = _dispatcher.CreateTimer();
                _dockRunningTimer.Interval = TimeSpan.FromSeconds(1);
                _dockRunningTimer.Tick += (_, _) => SampleDockRunningState();
            }
            if (!_dockRunningTimer.IsRunning)
            {
                _dockRunningTimer.Start();
                SampleDockRunningState();
            }
        }
        NotifyDockOpenStateChanged();
    }

    private async void SampleDockRunningState()
    {
        if (_dockSampling || _dockStateDisposed || _dockRunningPaths.Length == 0) return;
        _dockSampling = true;
        long version = _dockSnapshotVersion.Current;
        try
        {
            Task<Dictionary<string, bool>> sampling = _dockRunningService.SampleAsync(_dockRunningPaths.ToArray());
            try
            {
                var snapshot = await sampling.WaitAsync(TimeSpan.FromSeconds(2));
                if (!_dockStateDisposed && _dockSnapshotVersion.Accepts(version)) _dockOpenPaths = snapshot;
            }
            catch (TimeoutException)
            {
                if (_dockSnapshotVersion.Accepts(version)) _dockOpenPaths.Clear();
                NotifyDockOpenStateChanged();
                // Keep the single-worker gate until COM returns; discard late data.
                // A stuck shell must not spawn another worker every second.
                try { await sampling; } catch { }
            }
        }
        catch (Exception ex)
        {
            if (_dockSnapshotVersion.Accepts(version)) _dockOpenPaths.Clear();
            AppLogger.Error("Dock 打开状态采集失败，本次不确认外部项目。", ex);
        }
        finally
        {
            _dockSampling = false;
            NotifyDockOpenStateChanged();
        }
    }

    internal bool IsDockItemOpen(WidgetItem item) => item.Kind switch
    {
        WidgetItemKind.Note => item.NoteId is Guid id && _noteWindows.TryGetValue(id, out var note) && note.IsVisible,
        WidgetItemKind.PortableNote => _externalNoteWindows.TryGetValue(item.FullPath, out var note) && note.IsVisible,
        WidgetItemKind.PortableTodo => _externalTodoWindows.TryGetValue(item.FullPath, out var todo) && todo.IsVisible,
        WidgetItemKind.Organizer => item.OrganizerId is Guid id && _windows.TryGetValue(id, out var window) && window.IsOpenForDock,
        _ => _dockOpenPaths.TryGetValue(item.FullPath, out bool open) && open
    };

    internal void NotifyDockOpenStateChanged()
    {
        if (_dockNotificationQueued || _dockStateDisposed) return;
        _dockNotificationQueued = _dispatcher.TryEnqueue(() =>
        {
            _dockNotificationQueued = false;
            if (_dockStateDisposed) return;
            foreach (MainWindow window in _windows.Values) window.UpdateDockRunningVisuals();
        });
    }

    private void DisposeDockRunningState()
    {
        _dockStateDisposed = true;
        _dockSnapshotVersion.Invalidate();
        _dockRunningTimer?.Stop();
        _dockOpenPaths.Clear();
    }
}
