# TuckPane 3.0.1

## English

TuckPane 3.0.1 brings the current note, to-do, organizer, theme, and window-interaction work into one release, with official Windows 10 22H2 x64 support.

### What's new

- Added portable `.tucktodo` lists with editing, drag reordering, completion undo, themes, font scaling, inline renaming, and saved window placement.
- Notes now live as visible top-level `.tucknote` files inside organizer directories, with safe one-by-one migration of legacy notes and global theme synchronization.
- Ordinary organizers can be placed inside a Station, opened temporarily from it, moved between Stations, or dragged back out without moving their real directories.
- Added optional window-edge alignment with accent-color guides and an option to remember expanded organizer positions.
- Settings and organizers now have separate color, acrylic/glass/matte material, and transparency controls, plus hover timing, Station timing, performance, always-on-top, and unified size settings.
- Expanded real-file operations and storage choices, including a configurable default directory and a safe choice between moving organizer data to the Desktop or leaving it in place when deleting a pane.

### Windows 10 and installer

- Minimum supported system is Windows 10 22H2 x64 build 19045; Windows 11 x64 remains supported.
- The offline setup includes .NET, the Windows App SDK, and a Microsoft-signed WebView2 Runtime installer. WebView2 runs in the normal installation progress stage only when missing and is verified before TuckPane starts.
- Fixed the old setup appearing frozen while synchronously waiting for WebView2 during `PrepareToInstall`.
- The portable package does not modify the system and continues to use an existing WebView2 Runtime.

### Downloads

- `TuckPane-3.0.1-win-x64-setup.exe`: recommended per-user offline installer.
- `TuckPane-3.0.1-win-x64-portable.zip`: extract and run `00-启动 TuckPane.exe`.
- `SHA256SUMS.txt`: SHA-256 checksums for both downloads.

## 简体中文

TuckPane 3.0.1 汇总发布目前最新的便签、待办、收纳、主题和窗口交互功能，并正式支持 Windows 10 22H2 x64。

### 最新功能

- 新增便携 `.tucktodo` 待办，支持编辑、拖动排序、完成撤销、主题、字号缩放、标题内联改名和窗口位置保存。
- 便签改为收纳目录顶层可见的 `.tucknote` 文件，支持旧便签逐张安全迁移和全局主题同步。
- 普通收纳窗可以放入中转站，从中临时展开、转移到另一中转站或拖回桌面，而不会移动其真实目录。
- 新增可选的窗口边缘自动对齐、强调色辅助线和展开位置记忆。
- 设置界面与收纳窗可分别调整颜色、亚克力/玻璃/磨砂材质和透明度，并新增悬浮、中转站延迟、性能、置顶和统一大小设置。
- 完善真实文件操作和存储选择：可配置新收纳窗默认目录，删除收纳窗时可选择把数据移到桌面或留在原位置。

### Windows 10 与安装器

- 最低支持 Windows 10 22H2 x64 build 19045，并继续支持 Windows 11 x64。
- 离线 setup 自带 .NET、Windows App SDK 和微软签名的 WebView2 Runtime 安装程序；仅在缺失时于正常安装进度阶段执行，并在启动 TuckPane 前复查结果。
- 修复旧安装器在 `PrepareToInstall` 阶段同步等待 WebView2、导致安装页面像卡死的问题。
- 便携版不会修改系统，继续使用电脑上已有的 WebView2 Runtime。

### 下载

- `TuckPane-3.0.1-win-x64-setup.exe`：推荐使用的当前用户离线安装器。
- `TuckPane-3.0.1-win-x64-portable.zip`：解压后运行 `00-启动 TuckPane.exe`。
- `SHA256SUMS.txt`：两个下载文件的 SHA-256 校验值。

安装器尚未代码签名，Windows SmartScreen 可能显示“未知发布者”。升级或卸载不会删除已有收纳文件与设置。

The installer is currently unsigned, so Windows SmartScreen may show an “Unknown publisher” warning. Upgrading or uninstalling does not delete existing organizer files or settings.
