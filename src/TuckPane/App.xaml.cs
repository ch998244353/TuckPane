using TuckPane.Services;
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

    [STAThread]
    public static int Main(string[] args)
    {
        InitialArguments = args;
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance registered = AppInstance.FindOrRegisterForKey(CreateInstanceKey());
        if (!registered.IsCurrent)
        {
            RedirectActivation(registered, activation);
            return 0;
        }
        PrimaryInstance = registered;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
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
    private readonly Queue<AppActivationArguments> _pendingActivations = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher =
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private AppHost? _host;
    private bool _hostReady;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled UI exception", args.Exception);
        };
        if (Program.PrimaryInstance is not null) Program.PrimaryInstance.Activated += AppInstance_Activated;
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (!_singleInstance.IsPrimary)
        {
            if (!_singleInstance.SignalPrimary()) SingleInstanceGuard.ShowLegacyInstanceMessage();
            Exit();
            return;
        }

        try
        {
            _host = new AppHost();
            bool startup = Environment.GetCommandLineArgs().Any(argument => argument.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            await _host.InitializeAsync(startup);
            _singleInstance.Listen(() => _host.OpenConsole());
            await HandleArgumentsAsync(Program.InitialArguments, redirected: false);
            _hostReady = true;
            while (_pendingActivations.Count > 0) await HandleActivationAsync(_pendingActivations.Dequeue());
        }
        catch (Exception ex)
        {
            AppLogger.Error("TuckPane 初始化失败。", ex);
            Exit();
        }
    }

    private void AppInstance_Activated(object? sender, AppActivationArguments args)
    {
        _ = _dispatcher.TryEnqueue(async () =>
        {
            if (!_hostReady)
            {
                _pendingActivations.Enqueue(args);
                return;
            }
            await HandleActivationAsync(args);
        });
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
            int start = values.Length > 0 && IsCurrentExecutable(values[0]) ? 1 : 0;
            return values[start..];
        }
        finally
        {
            _ = NativeMethods.LocalFree(argv);
        }
    }

    private static bool IsCurrentExecutable(string candidate)
    {
        try
        {
            return Environment.ProcessPath is string executable &&
                Path.GetFullPath(candidate).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task HandleArgumentsAsync(IEnumerable<string> arguments, bool redirected)
    {
        string[] values = arguments.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        string[] notePaths = values
            .Where(value => value.EndsWith(".tucknote", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (notePaths.Length > 0)
        {
            foreach (string path in notePaths) await _host!.OpenExternalNoteAsync(path);
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
