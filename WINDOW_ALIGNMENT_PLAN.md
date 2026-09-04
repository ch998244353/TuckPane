# TuckPane 拉伸、重命名与名称同步执行计划

更新时间：2026-09-01

## 固定范围

- 展开态 Floating/Positioned 收纳窗的四边与四角 resize 命中带由 18 DIP 扩大为 28 DIP；Station 继续禁止手动拉伸。
- 收纳窗重命名改用独立 HWND 的模态 owned dialog：当前屏幕居中、不进入任务栏/Alt+Tab、保持 40 字符和现有主题/按键语义。
- 改名后立即同步当前收纳窗、所属 Station 条目和总控台，再沿用现有状态保存。
- 不改变文件、便签、待办的重命名流程；不新增窗口类、依赖、公开 API 或状态 Schema。
- 最终只发布 Release x64 自包含目录并同步到 `D:\app\功能\TuckPane\app\current`；不构建安装器/便携 ZIP，不提交、打标签或发布远端。

## 执行纪律

1. 每个门槛开始和结束时回读 `PLAN.md` 与 `WINDOW_ALIGNMENT_PLAN.md`，两份内容及状态必须一致。
2. 保留开始时的脏工作树；禁止 reset、stash、整文件还原或覆盖无关修改。
3. 只运行 `--organizer-resize-rename-sync` 和一次 Release publish；禁止完整 LogicChecks、旧 selector、鼠标、UIA、WinApp、拖放及托盘启动测试。
4. sub-agent 只承担 focused diff 只读审查和单一专项检查；主 agent 负责实现、整合和门槛判定。
5. 部署前按可执行文件真实路径检查正式实例；运行中则等待用户从托盘正常退出，禁止强杀或带进程覆盖。
6. 门槛失败立即停止下游，只修复并复测失败的本次专项门槛。

## 实现

- 复用 `MainWindow` 已有统一 resize 链，仅将共享命中常量改为 28 DIP。
- 为 `OwnedDialogWindow.ShowTextInputAsync` 增加可选最大长度和占位文本；`ShowRenameDialogAsync` 改用该窗口并按当前 HWND 所在屏幕定位。
- 接受非空名称后调用 `AppHost.ApplyOrganizerRuntime(..., OrganizerVisualChange.Name)`、刷新总控台并保存，使既有父 Station 刷新链生效。
- 在 `TuckPane.LogicChecks` 增加唯一 selector `--organizer-resize-rename-sync`，只验证本次命中边界、独立弹窗接线和 Station 名称重新投影/刷新链。
- 同步 `docs/PROJECT_ANALYSIS.zh-CN.md` 中的 resize、对话框所有权、名称刷新与专项测试说明。

## 门槛状态

### G0：Plan 与脏工作树基线 — PASS

- [x] 已记录执行前 `git status --short` 和六个相关文件的 SHA-256/长度。
- [x] 已确认相关产品、测试、文档文件原本均含用户修改，后续只应用局部补丁。
- [x] 两份计划文件已覆盖、回读且 SHA-256 一致。

### G1：聚焦实现与只读审查 — PASS

- [x] 28 DIP、独立重命名窗口、三处名称同步和架构说明完成。
- [x] 已回读计划并核对 focused 代码；Station resize 边窗排除已补齐。
- [x] sub-agent 初审问题已修复，二次只读复核无阻塞问题。

### G2：单一专项验证 — PASS

- [x] 只运行一次：
  `dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 -- --organizer-resize-rename-sync`
- [x] 退出码 0，输出 `PASS: organizer resize rename sync`；未运行任何旧 selector 或 UI/鼠标测试。

### G3：Release publish 与安装目录同步 — PASS

- [x] 一次 Release x64 自包含 publish 到 `artifacts\publish\plan-20260901-resize-rename-sync-win-x64`，退出码 0，共 531 个文件。
- [x] `TuckPane.exe` / `00-启动 TuckPane.exe` 版本为 3.0.0.0、哈希一致，必要运行文件齐全。
- [x] 正式实例已由用户从托盘正常退出，真实路径进程门槛通过；未强制结束进程。
- [x] 按相对路径覆盖 531 个 publish 文件；保留 318 个 extras 和卸载器，最终 `Missing=0`、`Different=0`。
- [x] 安装版 `TuckPane.exe` 与启动副本版本均为 3.0.0.0，SHA-256 均为 `F17893C5C49395230C6AFAE9DF57E72CDCD9A70956FBA9E549F9F0DC316021F1`。
- [x] 未自动启动或操控安装版。

### G4：收尾与用户人工验收 — AUTOMATED PASS / WAITING USER MANUAL UI

- [x] 回读计划，核对 focused diff、实际命令、专项测试、发布与部署证据；无范围外构建、测试或发布动作。
- [ ] 用户手动验证四边/四角光标、独立模态重命名窗口、Station/总控台即时名称同步和重启持久化。
