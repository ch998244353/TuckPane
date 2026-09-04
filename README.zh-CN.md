# TuckPane

[English](README.md) | [简体中文](README.zh-CN.md)

TuckPane 是一款适用于 Windows 10 22H2 与 Windows 11 x64 的桌面文件收纳工具。它把真实文件和文件夹集中到可收起的桌面窗口中，需要时展开，平时尽量少占用桌面空间。

## 动态演示

<p align="center">
  <img src="docs/images/demo-expand-collapse.gif" alt="TuckPane 收纳窗展开和收缩演示" width="420">
  <img src="docs/images/demo-file-reorder.gif" alt="在 TuckPane 收纳窗内调整文件位置" width="420">
  <br><sub>左：展开和收缩收纳窗。右：拖动文件，在收纳窗内调整位置。</sub>
</p>

## 界面展示

<p align="center">
  <img src="docs/images/organizer-expanded.png" alt="展开后的 TuckPane 收纳窗，其中显示文件和文件夹" width="720">
  <br><sub>需要查看内容时再展开收纳窗。</sub>
</p>

<p align="center">
  <img src="docs/images/context-menu.png" alt="包含收纳窗快捷操作的 TuckPane 右键菜单" width="344">
  <br><sub>右键即可进入设置、复制窗口、切换模式、重命名、打开保存目录或安全删除。</sub>
</p>

<p align="center">
  <img src="docs/images/manage-settings.png" alt="TuckPane 收纳窗管理设置" width="900">
  <br><sub>分别调整每个收纳窗的网格、模式、主题、入口大小、画布大小和内容比例。</sub>
</p>

<p align="center">
  <img src="docs/images/themes.png" alt="TuckPane 浅色毛玻璃、深色毛玻璃、纯浅色和纯深色主题" width="800">
  <br><sub>可选浅色毛玻璃、深色毛玻璃、纯浅色和纯深色主题。</sub>
</p>

### 快捷功能

可以直接把文件和文件夹拖入收纳窗，从显示器边缘呼出中转站，在真实文件旁创建便签和待办，按住 `Ctrl` 并滚动鼠标调整内容大小，并让 TuckPane 安静驻留在系统托盘中。

## 功能

- 最多创建 12 个普通收纳窗，可使用悬浮或桌面定位模式；还可创建贴靠屏幕边缘、呼出时不抢键盘焦点的中转站。已有中转站不能与前两种模式互相转换。
- 普通收纳窗可单层放入任一未被收纳的根收纳窗，双方的真实保存目录都不会移动；图标模式把内部收纳窗显示为实时小窗口，精简模式则显示 TuckPane 图标与收纳窗名称。中转站始终留在根层；已被收纳的窗口、或内部已有收纳窗的窗口不能继续互嵌。
- 可在收纳目录顶层直接创建包含文字和粘贴图片的 `.tucknote` 便签，支持七种主题、正文横线、标题内联改名和窗口位置保存。
- 可创建便携 `.tucktodo` 待办，支持编辑、拖动排序、完成撤销、主题、字号缩放、标题内联改名和窗口位置保存。
- 文件、文件夹、应用快捷方式、Steam `.url` 快捷方式和便携便签可在收纳窗或标准 Windows 目标之间拖动，并协商复制、移动或创建链接。
- 展开画布可从四边和四角等比例拉伸，尺寸和内容布局会自动保存。
- 图标模式可用 `Ctrl + 滚轮` 调整图标、文件名和间距；精简模式则独立调整整行元素比例，普通滚轮仍用于滚动列表。
- 图标模式和精简模式的普通滚轮都按当前行距平滑滚动；收纳窗文字颜色支持“自动 / 白色 / 黑色”，自动模式依据主题 Tint 对比度选择黑白。
- Station 只在其配置显示器的对应边缘热区展开自身，不会因其他显示器底边操作显示或抬升其他收纳窗。
- 普通收纳窗可选择悬浮后展开、鼠标离开后收缩，也可设置是否只允许一个窗口保持展开。
- 支持粘贴文件、新建文件夹、通过 Windows 剪贴板剪切项目，以及把真实文件移入回收站。
- 右键可打开设置、复制空白窗口、切换模式、重命名、打开收纳目录或安全删除窗口；通用设置还可分别统一悬浮/定位模式的底部名称大小。
- 支持浅色、灰色、纯浅色、纯深色、白色磨砂和深色磨砂主题，以及 English、简体中文、日本語界面。
- 启动后只驻留系统托盘。关闭设置窗口只会隐藏；只有托盘菜单中的“退出”会结束 TuckPane。

## 下载

当前版本：**3.0.2**。完整发布说明请查看 [Latest Release](https://github.com/ch998244353/TuckPane/releases/latest)。

- [TuckPane-3.0.2-win-x64-setup.exe](https://github.com/ch998244353/TuckPane/releases/download/v3.0.2/TuckPane-3.0.2-win-x64-setup.exe)：推荐使用的当前用户离线安装器，创建开始菜单和桌面快捷方式。
- [TuckPane-3.0.2-win-x64-portable.zip](https://github.com/ch998244353/TuckPane/releases/download/v3.0.2/TuckPane-3.0.2-win-x64-portable.zip)：解压后双击 `00-启动 TuckPane.exe`。
- [SHA256SUMS.txt](https://github.com/ch998244353/TuckPane/releases/download/v3.0.2/SHA256SUMS.txt)：两个下载文件的 SHA-256 校验值。

两种版本均自带 .NET 与 Windows App SDK。离线安装器还携带 Microsoft Edge WebView2 Runtime，并仅在系统缺失时自动安装；便携版不修改系统，使用电脑上已有的 WebView2 Runtime。当前 TuckPane 安装器未进行代码签名，Windows SmartScreen 可能显示“未知发布者”；需要时可使用 `SHA256SUMS.txt` 校验下载文件。

系统要求：Windows 10 22H2 x64，版本 19045 或更高。Windows 10 上的圆角、边框和透明效果可能采用较简单的平台降级样式，但内容与交互仍受支持。

## 存储与数据

新安装默认把收纳数据保存到 `%USERPROFILE%\TuckPane`，把设置和缓存保存到 `%LOCALAPPDATA%\TuckPane`。如果电脑上只有旧版 GlassFolder 数据，TuckPane 会原地继续使用，不复制或移动已有收纳文件。

每个新收纳窗使用一个类似 `%USERPROFILE%\TuckPane\Windows\名称-ID` 的单层目录，文件直接保存在其中。也可以选择一个已有的专用目录作为最终保存位置，该目录现有的顶层内容会立即显示。为了避免误操作无关数据，TuckPane 会拒绝范围过大或与其他收纳目录重叠的位置。

收纳窗内新建的便签就是目录顶层可见的 `.tucknote`；旧版内部便签会在启动时逐张安全迁移，单张失败不影响其他便签并会在下次启动重试。

修改全局便签主题时，已注册收纳目录顶层的有效 `.tucknote` 即使未打开也会更新；子目录和未注册目录不会被扫描。

“删除收纳窗时将文件移到桌面”默认开启：TuckPane 只在整个保存目录成功移动后删除窗口，失败或取消时保留原目录和窗口；被删除窗口直接包含的收纳窗会自动返回桌面，它们各自的真实保存目录不会移动。如果桌面没有足够网格位置来释放其中的定位收纳窗，删除会在移动文件前停止。已打开便签会保持打开并改为保存到桌面新路径。关闭该设置后只删除窗口，目录与文件留在原路径。卸载 TuckPane 不会删除收纳文件或设置。

## 构建

安装 .NET SDK 10.0.400 和 Inno Setup 6，然后运行：

```powershell
.\scripts\build-release.ps1
```

运行精简的核心逻辑回归检查：

```powershell
dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64
```

## 许可

TuckPane 使用 [MIT](LICENSE) 许可证。第三方运行库条款见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
