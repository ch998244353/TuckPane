using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using TuckPane.Updates;

namespace TuckPane.Services;

internal sealed class AppUpdateService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _root = Path.Combine(AppPaths.LocalRoot, "updates");
    private readonly bool _installed = FolderContextMenuService.IsInstalledCopy();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private UpdateCheckCache? _cache;
    private bool _busy;
    public event Action? Changed;
    public string CurrentVersion { get; } = typeof(AppUpdateService).Assembly.GetName().Version!.ToString(3);
    public UpdateRelease? Available { get; private set; }
    public string StatusKey { get; private set; } = "UpdateReady";
    public string? Error { get; private set; }
    public string? PreviousResult { get; private set; }
    public string? BackupPath { get; private set; }
    public double Progress { get; private set; }
    public bool Busy => _busy;
    public bool Downloading { get; private set; }
    public bool Installed => _installed;
    public DateTimeOffset? LastCheck => _cache?.LastAttemptUtc;

    public AppUpdateService()
    {
#if TUCKPANE_UPDATE_VALIDATION
        if (!AppPaths.IsTestMode) throw new InvalidOperationException("Start this validation build using the provided validation launcher.");
        _installed = File.Exists(Path.Combine(AppContext.BaseDirectory, "validation-installed"));
        _http.Dispose();
        _http = new HttpClient(new ValidationUpdateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
#endif
        try
        {
            string path = Path.Combine(_root, "check.json");
            if (File.Exists(path)) _cache = JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(path));
            if (_cache?.ReleaseJson is { } json) Available = ReleaseRules.Parse(json, CurrentVersion, _installed);
            path = Path.Combine(_root, "result.json");
            if (File.Exists(path))
            {
                UpdateResult? result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path));
                if (result is not null)
                {
                    PreviousResult = result.Status == "launched" && result.Version == CurrentVersion
                        ? AppStrings.Get("UpdatePreviousSuccess") : $"{result.Status}: {result.Message}";
                    BackupPath = result.BackupDirectory;
                }
            }
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    public async Task CheckAsync(bool manual)
    {
        if (_busy || (!manual && !ReleaseRules.AutoCheckDue(_cache?.LastAttemptUtc, DateTimeOffset.UtcNow))) return;
        _busy = true;
        StatusKey = "UpdateChecking";
        Error = null;
        Changed?.Invoke();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            _cache = new(DateTimeOffset.UtcNow, _cache?.ReleaseJson);
            UpdateFiles.WriteJson(Path.Combine(_root, "check.json"), _cache);
            string json = await new ReleaseClient(_http).GetLatestJsonAsync(timeout.Token);
            Available = ReleaseRules.Parse(json, CurrentVersion, _installed);
            _cache = _cache with { ReleaseJson = json };
            UpdateFiles.WriteJson(Path.Combine(_root, "check.json"), _cache);
            StatusKey = Available is null ? "UpdateLatest" : "UpdateAvailable";
        }
        catch (Exception ex)
        {
            StatusKey = "UpdateCheckFailed";
            Error = ex is OperationCanceledException ? AppStrings.Get("UpdateTimeout") : ex.Message;
        }
        finally { _busy = false; Changed?.Invoke(); }
    }

    public async Task DownloadAndInstallAsync(Func<Func<Task>, Task<bool>> exitWithHandoff, Func<string[]> protectedPaths)
    {
        if (_busy || Available is not { } release) return;
        _busy = true;
        Downloading = true;
        Progress = 0;
        Error = null;
        StatusKey = "UpdateDownloading";
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation.CancelAfter(TimeSpan.FromMinutes(30));
        Changed?.Invoke();
        try
        {
            string session = Path.Combine(_root, "sessions", Guid.NewGuid().ToString("N"));
            UpdateFiles.NoLinks(session);
            string package = await new ReleaseClient(_http).DownloadAsync(release, session,
                new Progress<double>(value => { Progress = value; Changed?.Invoke(); }), _operation.Token);
            Downloading = false;
            StatusKey = "UpdatePreparing";
            Changed?.Invoke();
            bool exited = await exitWithHandoff(async () =>
            {
                BackupPath = UpdateFiles.BackupConfiguration(AppPaths.StatePath, Path.Combine(_root, "config-backups"), CurrentVersion);
                string helper = Path.Combine(session, "TuckPane.Updater.exe");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "TuckPane.Updater.exe"), helper);
                using Process parent = Process.GetCurrentProcess();
                var request = new UpdateRequest(parent.Id, parent.StartTime.ToUniversalTime().Ticks,
                    Path.GetFullPath(AppContext.BaseDirectory), package, UpdateFiles.Hash(package), release.Version,
                    _installed, Path.Combine(_root, "result.json"), protectedPaths(), AppPaths.IsTestMode ? Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT") : null);
                UpdateFiles.WriteJson(Path.Combine(session, "request.json"), request);
                UpdateFiles.WriteJson(request.ResultPath, new UpdateResult("preparing", release.Version, AppStrings.Get("UpdatePreparing"), BackupPath));
                var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = session };
                start.ArgumentList.Add(Path.Combine(session, "request.json"));
                using Process process = Process.Start(start) ?? throw new IOException("Updater did not start.");
                using var readyTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                try
                {
                    while (!File.Exists(Path.Combine(session, "ready")))
                    {
                        if (process.HasExited) throw new IOException("Updater preparation failed. " + ReadResultMessage(request.ResultPath));
                        await Task.Delay(100, readyTimeout.Token);
                    }
                    File.WriteAllText(Path.Combine(session, "commit"), "ready-to-exit");
                }
                catch
                {
                    File.WriteAllText(Path.Combine(session, "cancel"), "cancelled");
                    throw;
                }
            });
            if (!exited) StatusKey = "UpdateExitCancelled";
        }
        catch (OperationCanceledException) { StatusKey = "UpdateCancelled"; }
        catch (Exception ex) { StatusKey = "UpdateFailed"; Error = ex.Message; }
        finally
        {
            Downloading = false;
            _busy = false;
            _operation?.Dispose();
            _operation = null;
            Changed?.Invoke();
        }
    }

    private static string ReadResultMessage(string path)
    {
        try { return JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path))?.Message ?? ""; }
        catch { return ""; }
    }

    public void CancelDownload() { if (Downloading) _operation?.Cancel(); }
    public void Dispose() { _lifetime.Cancel(); _http.Dispose(); }
}
