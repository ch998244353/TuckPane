# TuckPane 3.0.2

## English

TuckPane 3.0.2 fixes Station bottom-edge layering, adds adaptive organizer label colors, and smooths wheel input in icon and compact-list modes.

### What's new

- Transparent areas in loaded file icons no longer expose the white document fallback after an organizer refresh or collapse; the fallback remains available for definitive icon-load failures.
- Display settings now include a global edge-glow switch for compact organizers, expanded organizers, and the settings content edge.
- Compact-list organizers now scroll by one scaled row per mouse-wheel notch; Ctrl+wheel continues to resize compact rows or grid icons.
- Icon and compact-list ordinary wheel input now uses an interruptible smooth row target with hard bounds; Automatic organizer text color chooses the higher WCAG contrast against the theme tint.
- Station owner/Z-order transitions detach from the Explorer desktop layer before showing and never show peer windows during collapse or repair.
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

- `TuckPane-3.0.2-win-x64-setup.exe`: recommended per-user offline installer.
- `TuckPane-3.0.2-win-x64-portable.zip`: extract and run `00-启动 TuckPane.exe`.
- `SHA256SUMS.txt`: SHA-256 checksums for both downloads.

## 简体中文

TuckPane 3.0.2 修复底边 Station 层级、增加自适应收纳窗名称颜色，并平滑图标/精简模式滚轮输入。

### 最新功能

- 已加载文件图标的透明区域不再在刷新或收缩后露出白色文档兜底；图标明确加载失败时仍显示兜底。
- “显示”设置新增全局边缘弧光开关，同时控制收起、展开和设置内容区三处边缘。
- 精简列表现在每个鼠标滚轮刻度滚动一条当前缩放后的行；Ctrl+滚轮继续调整精简行或图标比例。
- 图标和精简列表普通滚轮均按当前行距平滑滚动；收纳窗名称支持“自动 / 白色 / 黑色”，自动模式根据主题 Tint 的高对比度黑白选择。
- Station 展开先脱离 Explorer 桌面 owner，收缩和修复不会在恢复 owner 时显示或抬升其他收纳窗。
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

- `TuckPane-3.0.2-win-x64-setup.exe`：推荐使用的当前用户离线安装器。
- `TuckPane-3.0.2-win-x64-portable.zip`：解压后运行 `00-启动 TuckPane.exe`。
- `SHA256SUMS.txt`：两个下载文件的 SHA-256 校验值。

安装器尚未代码签名，Windows SmartScreen 可能显示“未知发布者”。升级或卸载不会删除已有收纳文件与设置。

The installer is currently unsigned, so Windows SmartScreen may show an “Unknown publisher” warning. Upgrading or uninstalling does not delete existing organizer files or settings.
