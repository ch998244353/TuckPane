using System.Runtime.InteropServices;

namespace TuckPane.Services;

internal static class StartupFailure
{
    internal static void Show(Exception exception)
    {
        Environment.ExitCode = 1;
        // XAML and localized resources may be the cause of the failure; use a native dialog.
        MessageBox(IntPtr.Zero,
            "TuckPane 初始化失败。请重新完整解压或安装程序；便携版可尝试运行同目录的 TuckPane.exe。\n\n" +
            $"错误代码：0x{exception.HResult:X8}\n诊断记录位于应用数据目录的 diagnostics 文件夹。",
            "TuckPane 启动失败", 0x00000010);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
}
