using System.Runtime.InteropServices;
using TuckPane.Launcher;

return PortableLauncher.Run(args, exception => MessageBox(
    IntPtr.Zero,
    "无法启动 TuckPane。请完整解压便携包，并确认启动器与 TuckPane.exe 位于同一目录。\n\n" +
    $"错误代码：0x{exception.HResult:X8}",
    "TuckPane 启动失败", 0x00000010));

[DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
static extern int MessageBox(IntPtr window, string text, string caption, uint type);
