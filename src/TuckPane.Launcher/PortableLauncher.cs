using System.Diagnostics;

namespace TuckPane.Launcher;

internal static class PortableLauncher
{
    internal static int Run(string[] arguments, Action<Exception> reportError)
    {
        try
        {
            // The application must keep its canonical executable name for MRT/WinUI resources.
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TuckPane.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            foreach (string argument in arguments) start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)
                ?? throw new InvalidOperationException("The application process could not be started.");
            // Do not keep the launcher locked while the application installs an update.
            return 0;
        }
        catch (Exception exception)
        {
            reportError(exception);
            return 1;
        }
    }
}
