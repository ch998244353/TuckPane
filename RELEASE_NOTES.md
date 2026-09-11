# TuckPane 4.0.0

## 简体中文

新增设置左侧“更新”页面，安装版和便携版均可检查并升级 GitHub 正式版。

- 启动后每 24 小时至多自动检查一次，新版仅在侧栏提示；也可手动检查。
- 用户点击后下载并核对 SHA-256；先保存设置、便签和待办，再备份配置、退出、安装并重启。
- 升级保留窗口布局、收纳目录关联和真实文件，兼容原有 GlassFolder 数据目录。便携版按程序清单替换文件，保留额外文件，并在替换失败时尝试恢复旧程序。
- 更新准备期间暂停新文档激活和输入；保存失败或退出取消时不会开始安装。更新页显示失败详情和配置备份位置。
- 同步此前本地已完成的 Dock 栏及悬浮效果、永久展开、收纳窗管理与外观、窗口名称编辑、便签/待办及存储稳定性改进。

本版功能汇总：

- 悬浮与桌面定位收纳窗、屏幕边缘中转站、Dock 栏，以及普通收纳窗的单层嵌套。
- 图标与精简列表显示、独立内容缩放、永久展开、悬浮展开及窗口名称编辑。
- 真实文件的拖放、排序、剪切、复制、移动；桌面或文件夹右键创建收纳窗，目录关联与显示名称相互独立。
- 支持图片的 `.tucknote` 便签与 `.tucktodo` 待办，包含主题、排序、编辑和窗口位置保存。
- 多主题及中英日界面、分组设置、托盘驻留，以及删除收纳窗时保留文件或移到桌面的选择。

下载：[安装包](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/TuckPane-4.0.0-win-x64-setup.exe)、[便携包](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/TuckPane-4.0.0-win-x64-portable.zip)、[SHA256SUMS.txt](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/SHA256SUMS.txt)。

最低支持 Windows 10 22H2 x64 / Windows 11 x64。安装包包含 .NET、Windows App SDK 和按需安装的 WebView2 Runtime。3.0.2 没有更新入口，首次请用 4.0.0 安装包覆盖原安装，或将便携包解压到独立程序目录；继续沿用现有数据根。4.0.0 提供后续版本的应用内更新。

验证状态：自动更新专项检查已通过；实际 GUI 安装、完整升级和重启体验尚未经用户手测确认。本次没有重复运行旧功能全套测试。

## English

Settings now includes **Updates** for both installed and portable editions.

- Checks GitHub stable releases at most once per 24 hours after startup, with a quiet sidebar badge and a manual check button.
- Downloads only after a click, verifies SHA-256, saves open documents and settings, backs up configuration, exits safely, installs and restarts.
- Preserves layouts, storage associations and real files, including legacy GlassFolder data roots. Portable updates use a program manifest, preserve extra files and attempt to restore previous program files on replacement failure.
- Pauses new document activation and input while preparing an update. Failed saves or cancelled exits stop installation; recovery details appear in the Updates page.
- Includes the accumulated improvements to Dock and hover effects, permanent expansion, settings, organizer names, notes/to-dos and storage stability.

Included features:

- Floating and desktop-positioned panes, edge Stations, Dock, and one-level nesting of ordinary panes.
- Icon and compact-list views, independent content scaling, permanent expansion, hover expansion, and pane title editing.
- Dragging, sorting, cutting, copying and moving real files; desktop/folder context-menu creation with independent display names and storage associations.
- Rich `.tucknote` notes with images and `.tucktodo` lists, including themes, ordering, editing and saved window positions.
- Multiple themes, English/Chinese/Japanese interfaces, grouped settings, tray operation, and pane deletion with a choice to retain files or move them to Desktop.

Downloads: [installer](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/TuckPane-4.0.0-win-x64-setup.exe), [portable ZIP](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/TuckPane-4.0.0-win-x64-portable.zip), and [SHA256SUMS.txt](https://github.com/ch998244353/TuckPane/releases/download/v4.0.0/SHA256SUMS.txt).

Requires Windows 10 22H2 x64 or Windows 11 x64. The installer bundles .NET, the Windows App SDK and the WebView2 Runtime installer. Version 3.0.2 has no updater: install 4.0.0 over the existing installation or extract the portable package into a separate program directory, retaining the existing data roots. Use 4.0.0's Updates page for future releases.

Verification: focused automated update checks have passed. Actual GUI installation, full upgrade and restart behavior has not yet been confirmed by user testing. The existing full feature test suite was not rerun for this release.
