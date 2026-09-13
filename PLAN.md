# TuckPane 4.2.0 执行计划

## 范围与已确认决策
- 本文覆盖旧 PLAN，只维护本轮工作。保留工作区已有 Dock、便签保存等未提交改动并一并发布；本次不重测无关旧功能。
- 顺序：待办布局 → 主题修复 → 紧凑标题栏 → 定向验证 → CW 本机安装 → 用户手测 → GitHub v4.2.0。
- 待办新增入口为 ListView 页脚，跟随最后一条滚动；不是任务、不参与拖动排序。空列表时位于内容区顶部。
- 默认居中加号，圆角 8，宽度对齐条目内容，高度取当前字号的普通单行条目。专用背景文字色 alpha 12/255（原 24/255），弱边框；不改条目编辑框配色。
- 点击原位输入；字号与正文相同并同步缩放。Enter 非空提交一次后恢复加号；空白不创建；非空失焦保留草稿；空白失焦恢复加号；Esc 清空恢复加号。草稿不入任务文件。
- 任意便签/待办切换主题，统一已打开窗口、受管理目录顶层有效文件及新建默认值。保持正文、任务、字号、横线偏好不变；便签横线和待办横线独立。
- 定位 COM 实际抛错步骤后修复；主题应用串行，窗口快照，单目标失败不截断其余目标，明确报告失败。保持文件格式，不迁移、不递归扫描。
- 两类标题栏 44→26.4 DIP，按钮 34→20.4 DIP，图标视觉尺寸乘 0.6；标题/改名字号保留 15，正文不缩。修正控件最小尺寸及命中区。

## 执行约束与分工
- 主 agent：架构、根因、实现、审查及验收。sub-agent：本次定向测试范围/证据复核。CW：确定性安装命令、备份/部署/哈希核对；不给其架构任务。
- 每次开始实现、每项功能完成、验证前、安装前、发布前回读本文并记录门槛。失败或未验证不得标完成。
- 验证时停止相关目录编辑。仅新增一个 --note-todo-42 定向入口，最多三组：新增状态；主题传播/失败/序列/横线独立；隔离文件往返保留数据。
- 布局静态检查一次，Release x64 编译一次；仅修改或失败才补跑相关项，不运行旧完整测试集。
- 禁止所有 GUI、鼠标键盘、桌面操作自动化；不启动应用做验证、不强杀当前应用。实际外观/焦点/COM 修复结果交用户手测。

## 安装与发布门槛
- 统一版本 4.2.0，更新发布说明；记录源码提交、构建命令、安装包/便携包/校验文件。
- 用户正常退出 TuckPane 后，CW 备份 app/current，部署候选产物，保留数据和附加文件，检查版本和 SHA-256，失败回退；不自动启动。
- 用户手测：入口位置/状态/字号；标题栏/改名；两类入口换主题、横线独立；关闭重开保存。
- 必须用户确认手测通过才推送 v4.2.0 发布标签。使用现有 GitHub Release 工作流，核验三附件、哈希、更新清单、版本。无用户手测确认不得正式发布。

## 当前状态与证据
- [x] 需求确认；工作区基线记录于 artifacts/v4.2-work/baseline.patch（不含旧计划）。已有未跟踪文件保留。
- [x] 开始实现前回读：本轮只涉及上述功能，无 GUI 测试。
- [x] 待办布局完成与回读（视觉/焦点待用户）
- [ ] 主题原因证据、修复完成与回读
- [x] 标题栏完成与回读（44→26.4，34→20.4，文字15不变）
- [x] 定向检查及 Release 编译通过（2026-09-13；三组 PASS；无 GUI）
- [x] 4.2.0 候选产物与源码提交对应：800d59f
- [ ] CW 本机部署与校验
- [ ] 用户手测通过
- [ ] 正式发布并校验

诊断基线：2026-09-13 本机诊断日志在 ThemeItem_Click 记录 COMException（0x800F0902、0x80004005）；尚无原生抛错栈，不将其视作磁盘保存失败的证据。旧 note-save-failure 用例覆盖改名/保存交错，不证明主题修复成功。

## 主题调查与验证前回读
- 已确认代码缺陷：全局串行遍历被首个异常截断；打开文档 Flush 的 false 结果被忽略；目录失败列表被丢弃。已修复传播隔离、返回结果及队列顺序，并保留每个便签原主题用于失败回滚。
- 原生 COM 根因尚未确认。官方源码允许 Brush 多关联和 ResourceDictionary 已有 key 替换，不能将“共享 Brush 非法”作为根因。候选修复改为每个资源键只注册一次、后续只改 Color，减少运行中资源移除/插入并保持模板引用有效；失败记录保留资源键与阶段。
- 原生现场不能用现有隔离逻辑测试复现；按用户禁止 GUI 自动化的要求，COM 修复确认保留给手测，不将候选方案标作已证实。
- 验证前回读：只运行 --note-todo-42 三组及一次布局静态门槛；本轮未改 NoteSaveSequence/NoteRenameTransaction，因此不重跑旧 note-save-failure。

## 验证证据
- 命令：`dotnet run --project tests/TuckPane.LogicChecks/TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 --no-restore -- --note-todo-42`，退出 0；包含 Release 编译。
- PASS 1：composer drafts, blur, cancel and single submission。
- PASS 2：theme queue ordering, failure isolation and settings rollback。
- PASS 3：real portable note/todo theme persistence and exclusions；真实临时文件，无 GUI。
- XML 静态检查通过：页脚归属、双标题栏26.4、按钮20.4、标题15、输入默认隐藏及圆角8。git diff --check 通过。
- sub-agent 独立只读审查未发现可证实现缺陷；不包含 footer 真正宽度/高字号渲染确认。
- 资源参考：[WinUI ResourceDictionary Insert](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/dxaml/lib/ResourceDictionary_partial.cpp#L143)、[官方共享 Brush 文档](https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/brushes)。源码契约支持稳定资源引用策略，但不证明本机 COM 根因。

## 候选产物与安装前回读
- 源码候选提交：`800d59f`（包含用户确认保留的既有未提交改动）；尚未推送发布标签。
- 构建：`scripts/build-release.ps1 -Version 4.2.0 -OutputName v4.2.0-candidate`，退出 0。日志 `artifacts/v4.2-work/build-release.log`。
- 产物目录：`artifacts/release/v4.2.0-candidate`；setup.exe 346857125 bytes，portable.zip 129782375 bytes，SHA256SUMS.txt 203 bytes。
- 打包校验 PASS：两附件 SHA-256；ZIP 内 543 个清单文件与本地 publish 全部哈希一致；EXE 和 Updater 版本均 4.2.0.0。证据 `artifacts/v4.2-work/package-check.json`。
- 安装前回读完成：只能用户正常退出后部署，不能强杀、不能自动启动。当前仍检测到 `app/current/TuckPane.exe` PID 26940，因此未安装、未调用 CW 部署、未触及 app/current。
- 已准备并通过 PowerShell 语法解析的确定性安装脚本：`artifacts/v4.2-work/install-candidate.ps1`。待退出后交 CW 的 command 模式执行；脚本验证候选清单→备份 app/current→复制并核验→保留额外文件；失败恢复备份。
- 下一步：确认进程已退出后调用 CW 执行脚本、验收 install-result.json，更新本计划，再交用户手测。原生 COM 候选修复门槛仍待手测，不能据逻辑 PASS 发布正式版。
