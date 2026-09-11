# TuckPane 4.1.0

## 简体中文

本版限制本地诊断的记录范围与导出大小，方便分享和定位异常。

- 仅记录失败、超时、意外中断及可获取的崩溃信息，包含 UTC 时间；正常运行、成功操作、主动取消和正常退出不再记录。
- 诊断 ZIP 最大 5 MiB，本地日志最多 4 个文件、每个 5 MiB，总计 20 MiB。导出超限时保留最新完整异常，并说明保留时间范围、条数和因容量省略的条数。
- 导出兼容已有日志并重新过滤、脱敏；只保留允许的诊断字段。受控代码位置和异常类型帮助定位，不包含用户正文、文件名、完整路径、用户名、启动参数、原始异常消息或堆栈。
- 包内说明日志刷新与 Windows 应用错误事件的采集状态；没有收集到异常不代表程序一定正常。失败或取消不会替换原目标文件。
- 更新中、英、日诊断提示。日志留在本机，分享由用户主动决定，不自动上传。

下载：[安装包](https://github.com/ch998244353/TuckPane/releases/download/v4.1.0/TuckPane-4.1.0-win-x64-setup.exe)、[便携包](https://github.com/ch998244353/TuckPane/releases/download/v4.1.0/TuckPane-4.1.0-win-x64-portable.zip)、[SHA256SUMS.txt](https://github.com/ch998244353/TuckPane/releases/download/v4.1.0/SHA256SUMS.txt)。

支持应用内更新的旧客户端可在设置的“更新”页面手动检查本版。没有更新入口的版本请使用安装包覆盖原安装，或将便携包解压到独立程序目录，保留现有数据根。最低支持 Windows 10 22H2 x64 / Windows 11 x64。

本轮自动验证限定为异常诊断专项与必要编译，不重复运行旧功能全套测试。实际应用内更新、重启、已有数据保留及诊断导出界面由用户手测确认，操作说明见 [本地诊断与用户手测](https://github.com/ch998244353/TuckPane/blob/v4.1.0/docs/LIFECYCLE_DIAGNOSTICS.zh-CN.md)。

## English

This release limits local diagnostic content and archive size to make failures easier to locate and share.

- Records only failures, timeouts, unexpected interruptions and available crash information, with UTC timestamps. Normal activity, successful operations, intentional cancellations and normal exits are excluded.
- Diagnostic ZIPs are capped at 5 MiB. Local logs use at most four 5 MiB files, totaling 20 MiB. Exports retain the newest complete exceptions and report their time range, count and records omitted for capacity.
- Existing logs are filtered and redacted again on export. Controlled code locations and exception types aid investigation; user content, file names, full paths, user names, launch arguments, raw exception messages and stack traces are excluded.
- Archives report log-flush and Windows application-error collection status. No collected exceptions does not prove normal operation. Failed or cancelled exports preserve an existing destination file.
- Updated Chinese, English and Japanese descriptions. Logs stay local; sharing is voluntary and nothing is uploaded automatically.

Downloads: [installer](https://github.com/ch998244353/TuckPane/releases/download/v4.1.0/TuckPane-4.1.0-win-x64-setup.exe), [portable ZIP](https://github.com/ch998244353/TuckPane/releases/download/v4.1.0/TuckPane-4.1.0-win-x64-portable.zip), and [SHA256SUMS.txt](https://github.com/ch998244353/TuckPane/releases/download/v4.1.0/SHA256SUMS.txt).

Existing clients with in-app updates can check manually in Settings → Updates. Versions without an updater can use the installer over their existing installation or extract the portable package into a separate program directory, retaining existing data roots. Requires Windows 10 22H2 x64 or Windows 11 x64.

Automated verification for this release is limited to focused diagnostics checks and the required build; existing full feature suites are not rerun. Actual in-app updates, restart, retained user data and the diagnostic export UI require user testing.
