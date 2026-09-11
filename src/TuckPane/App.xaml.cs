using TuckPane.Services;
using TuckPane.Core;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.ApplicationModel.Activation;
using Windows.Storage;

namespace TuckPane;

public static class Program
{
    internal static string[] InitialArguments { get; private set; } = [];
    internal static AppInstance? PrimaryInstance { get; private set; }
    internal static ActivationInbox<AppActivationArguments> Activations { get; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            AppLogger.Record(DiagnosticArea.Runtime, DiagnosticStage.Failed, exception: eventArgs.ExceptionObject as Exception);
            try { AppLogger.FlushAsync().Wait(TimeSpan.FromMilliseconds(300)); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            AppLogger.Record(DiagnosticArea.Runtime, DiagnosticStage.Failed, exception: eventArgs.Exception);
        try { return Run(args); }
        catch (Exception ex)
        {
            AppLogger.Record(DiagnosticArea.Runtime, DiagnosticStage.Failed, exception: ex);
            try { AppLogger.FlushAsync().Wait(TimeSpan.FromMilliseconds(300)); } catch { }
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        AppLogger.Record(DiagnosticArea.Lifecycle, DiagnosticStage.Started);
        InitialArguments = args;
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance registered = AppInstance.FindOrRegisterForKey(CreateInstanceKey());
        if (!registered.IsCurrent)
        {
            AppLogger.Lifecycle("activation-redirect", "source=secondary-instance");
            RedirectActivation(registered, activation);
            return 0;
        }
        PrimaryInstance = registered;
        registered.Activated += (_, activationArgs) => Activations.Enqueue(activationArgs);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        AppLogger.Lifecycle("message-loop-ended", "source=Application.Start");
        try { AppLogger.FlushAsync().Wait(TimeSpan.FromMilliseconds(300)); } catch { }
        return 0;
    }

    private static string CreateInstanceKey()
    {
        const string name = "TuckPane-019d2f2d-0bfb-7ff0-98f5-d93093bb0b5d";
        string? testRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(testRoot)) return name;
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(testRoot))))[..12];
        return $"{name}-{suffix}";
    }

    private static void RedirectActivation(AppInstance instance, AppActivationArguments activation)
    {
        using var completed = new ManualResetEventSlim();
        Exception? failure = null;
        _ = Task.Run(async () =>
        {
            try { await instance.RedirectActivationToAsync(activation); }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        });
        completed.Wait();
        if (failure is not null) throw failure;
    }
}

public partial class App : Application
{
    private readonly SingleInstanceGuard _singleInstance = CreateSingleInstanceGuard();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private AppHost? _host;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled UI exception", args.Exception);
            AppLogger.Lifecycle("ui-exception", $"type={args.Exception.GetType().FullName} hresult=0x{args.Exception.HResult:X8} handled=true");
            // Keep recoverable async-void/UI callback failures from terminating
            // the process. Native fail-fast crashes are still handled by WER.
            args.Handled = true;
        };
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (!_singleInstance.IsPrimary)
        {
            if (!_singleInstance.SignalPrimary()) SingleInstanceGuard.ShowLegacyInstanceMessage();
            AppLogger.Lifecycle("exit-request", "source=OnLaunched reason=legacy-instance");
            Exit();
            return;
        }

        try
        {
            AppLogger.Lifecycle("startup");
            _host = new AppHost();
            await _host.InitializeAsync();
            _singleInstance.Listen(() => _host.OpenConsole());
            await HandleArgumentsAsync(Program.InitialArguments, redirected: false);
            Program.Activations.Start(HandleActivationAsync,
                work => _dispatcher.TryEnqueue(async () => await work()),
                ex => AppLogger.Error("处理应用激活失败。", ex));
        }
        catch (Exception ex)
        {
            AppLogger.Error("TuckPane 初始化失败。", ex);
            AppLogger.Lifecycle("exit-request", $"source=OnLaunched reason=initialization-failed type={ex.GetType().FullName} hresult=0x{ex.HResult:X8}");
            Exit();
        }
    }

    private async Task HandleActivationAsync(AppActivationArguments activation)
    {
        if (activation.Data is IFileActivatedEventArgs fileArgs)
        {
            string[] paths = fileArgs.Files.OfType<StorageFile>().Select(file => file.Path).ToArray();
            if (paths.Length > 0)
            {
                await HandleArgumentsAsync(paths, redirected: true);
                return;
            }
        }

        string arguments = (activation.Data as ILaunchActivatedEventArgs)?.Arguments ?? string.Empty;
        await HandleArgumentsAsync(ParseRedirectedArguments(arguments), redirected: true);
    }

    internal static string[] ParseRedirectedArguments(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        IntPtr argv = NativeMethods.CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var values = new string[count];
            for (int index = 0; index < count; index++)
                values[index] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, index * IntPtr.Size)) ?? string.Empty;
            int start = values.Length > 0 && IsActivationExecutable(values[0]) ? 1 : 0;
            return values[start..];
        }
        finally
        {
            _ = NativeMethods.LocalFree(argv);
        }
    }

    private static bool IsActivationExecutable(string candidate)
    {
        try
        {
            string fullPath = Path.GetFullPath(candidate);
            if (Environment.ProcessPath is string executable &&
                fullPath.Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return true;
            // All copies share one instance key. A redirected launch may originate in
            // a different installation/portable directory, including its launcher alias.
            string name = Path.GetFileName(fullPath);
            return Path.IsPathFullyQualified(candidate) &&
                (name.Equals("TuckPane.exe", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("00-启动 TuckPane.exe", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private async Task HandleArgumentsAsync(IEnumerable<string> arguments, bool redirected)
    {
        if (_host?.IsPreparingUpdate == true) return;
        string[] rawArguments = arguments.ToArray();
        if (FolderOrganizerCreation.IsRequest(rawArguments))
        {
            await _host!.CreateFolderOrganizerFromArgumentsAsync(rawArguments);
            return;
        }
        string[] values = rawArguments.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        int noteFolderIndex = Array.FindIndex(values,
            value => value.Equals("--create-note-in", StringComparison.OrdinalIgnoreCase));
        if (noteFolderIndex >= 0)
        {
            string folderPath = noteFolderIndex + 1 < values.Length ? values[noteFolderIndex + 1] : string.Empty;
            await _host!.CreateExternalNoteAsync(folderPath);
            return;
        }
        string[] notePaths = values
            .Where(value => value.EndsWith(".tucknote", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] todoPaths = values
            .Where(value => value.EndsWith(".tucktodo", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (notePaths.Length > 0 || todoPaths.Length > 0)
        {
            foreach (string path in notePaths) await _host!.OpenExternalNoteAsync(path);
            foreach (string path in todoPaths) await _host!.OpenExternalTodoAsync(path);
            return;
        }
        if (values.Contains("--create-organizer", StringComparer.OrdinalIgnoreCase))
        {
            await _host!.CreateDesktopOrganizerAsync();
            return;
        }
        if (redirected && !values.Contains("--startup", StringComparer.OrdinalIgnoreCase)) _host!.OpenConsole();
    }

    private static SingleInstanceGuard CreateSingleInstanceGuard()
    {
        const string name = "TuckPane-019d2f2d-0bfb-7ff0-98f5-d93093bb0b5d";
        const string legacyName = "GlassFolder-019d2f2d-0bfb-7ff0-98f5-d93093bb0b5d";
        string? testRoot = Environment.GetEnvironmentVariable("TUCKPANE_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(testRoot)) return new SingleInstanceGuard(name, legacyName);
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(testRoot))))[..12];
        return new SingleInstanceGuard($"{name}-{suffix}", $"{legacyName}-{suffix}");
    }
}
