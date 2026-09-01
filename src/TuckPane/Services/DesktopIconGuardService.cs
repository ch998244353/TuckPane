using Microsoft.UI.Dispatching;

namespace TuckPane.Services;

/// <summary>
/// 监听桌面文件夹变化（如外部程序"另存为到桌面"产生的图标），
/// 只针对新出现的那一个文件，若其图标落在收纳盒窗口覆盖范围内，
/// 就把它挪到收纳盒外的格子里，避免收纳盒把桌面图标遮住。
/// 事件驱动，空闲时零开销；绝不移动其他已有图标，绝不触碰收纳盒。
/// </summary>
internal sealed class DesktopIconGuardService : IDisposable
{
    private readonly object _gate = new();
    private readonly List<string> _pending = [];
    private readonly DesktopIconPlacementService _placement = new();
    private readonly Func<IReadOnlyList<NativeMethods.RECT>> _organizerBoundsProvider;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<IDisposable> _suspendOrganizerRelocation;
    private readonly FileSystemWatcher _watcher;
    private CancellationTokenSource? _debounce;
    private IDisposable? _suspension;

    public DesktopIconGuardService(
        Func<IReadOnlyList<NativeMethods.RECT>> organizerBoundsProvider,
        DispatcherQueue dispatcher,
        Func<IDisposable> suspendOrganizerRelocation)
    {
        _organizerBoundsProvider = organizerBoundsProvider;
        _dispatcher = dispatcher;
        _suspendOrganizerRelocation = suspendOrganizerRelocation;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        _watcher = new FileSystemWatcher
        {
            Path = desktop,
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
        };
        _watcher.Created += OnDesktopChanged;
        _watcher.Renamed += OnDesktopChanged;
    }

    public void Start()
    {
        if (!_watcher.EnableRaisingEvents) _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        IDisposable? suspension;
        lock (_gate)
        {
            suspension = _suspension;
            _suspension = null;
        }
        suspension?.Dispose();
        _watcher.Created -= OnDesktopChanged;
        _watcher.Renamed -= OnDesktopChanged;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }

    private void OnDesktopChanged(object sender, FileSystemEventArgs e)
    {
        IDisposable? previous;
        lock (_gate)
        {
            _pending.Add(e.FullPath);
            // 从文件出现的瞬间就开始抑制收纳盒自动重定位，直到本批移动扫描结束。
            previous = _suspension;
            _suspension = _suspendOrganizerRelocation();
        }
        previous?.Dispose();

        _debounce?.Cancel();
        _debounce?.Dispose();
        var cts = new CancellationTokenSource();
        _debounce = cts;
        _ = DebounceAndScanAsync(cts.Token);
    }

    private async Task DebounceAndScanAsync(CancellationToken token)
    {
        try
        {
            // 合并"另存为"过程中的多次文件事件（临时文件/改名），并等待 Explorer 完成图标网格放置。
            await Task.Delay(400, token);
            await Task.Delay(350, token);
            token.ThrowIfCancellationRequested();

            string[] pending;
            lock (_gate)
            {
                pending = [.. _pending];
                _pending.Clear();
            }
            if (pending.Length == 0) return;

            // 本批扫描持有当前抑制 scope；若已被更新的事件接管则不再持有。
            IDisposable? ownedSuspension;
            lock (_gate)
            {
                ownedSuspension = _suspension;
                _suspension = null;
            }
            if (ownedSuspension is null) return;
            if (token.IsCancellationRequested)
            {
                ownedSuspension.Dispose();
                return;
            }

            // Explorer 桌面 shell COM 必须运行在 STA/UI 线程，否则静默失败（文件不会被移动）。
            await EnqueueAsync(_dispatcher, () => RunScanAsync(pending, token, ownedSuspension));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLogger.Error("桌面图标避让扫描失败。", ex);
        }
    }

    private async Task RunScanAsync(string[] pending, CancellationToken token, IDisposable ownedSuspension)
    {
        try
        {
            IReadOnlyList<NativeMethods.RECT> organizerBounds = _organizerBoundsProvider();
            if (organizerBounds.Count == 0) return;

            foreach (string path in pending)
            {
                token.ThrowIfCancellationRequested();
                DesktopIconPlacementResult result = await _placement.MoveAwayIfOverlappedAsync(path, organizerBounds, token);
                if (result.Status == DesktopIconPlacementStatus.Failed)
                {
                    AppLogger.Info($"桌面图标避让：新文件“{path}”暂未定位（{result.Warning}），跳过。");
                }
            }
        }
        finally
        {
            ownedSuspension.Dispose();
        }
    }

    private static Task EnqueueAsync(DispatcherQueue dispatcher, Func<Task> action)
    {
        var completion = new TaskCompletionSource<bool>();
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult(true);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException("无法把桌面图标避让调度到 UI 线程。"));
        }
        return completion.Task;
    }
}
