# TuckPane v4.1.0：异常诊断包限制与发布

## 目标与约定
- ZIP 硬上限 5 MiB；本地 runtime 日志 4 × 5 MiB，总计 20 MiB。
- 写入和导出只保留失败、超时、意外中断、可获取的崩溃；正常运行、成功、主动取消、正常退出不记录。
- 超限保留最新完整异常，说明时间范围、保留条数、容量省略条数。
- 白名单记录 UTC 时间、会话/操作、版本/构建、模块、耗时、分类、错误码、受控内部代码位置/异常类型；不包含用户正文、文件名、路径、用户名、启动参数、原始异常消息/堆栈。
- 兼容旧诊断记录，不清空历史；配置格式和更新协议不变。
- 发布 v4.1.0 正式 latest；不覆盖或安装 app/current，用户自行手测更新。
- 不增加卡顿监控、正常轨迹、自动上传、内存转储、日志设置页面。

## 流程及检查点
1. 实施前：重读本文件，核对需求、工作区及范围。
2. 诊断改造：主 agent 实现，sub-agent 独立审查异常分类与隐私。
3. 验证前：重读本文件，sub-agent 审查最小测试集，主 agent 执行定向检查。
4. 发布前：重读本文件，核对测试、编译、版本及资产门槛，阻断问题未解决不得发布。
5. 发布后：记录提交、标签、Actions、Release、资产哈希/版本与待手测项。
每节点维护实际证据；通过后不重复执行，只有相关变更、失败或新证据才重跑受影响部分。

## 实现
- 入队前统一异常过滤，导出重新白名单校验与过滤；正常过滤不增加 Dropped。
- 修正退出传输超时被记作 exit-cancelled 的诊断表达，不修改退出业务行为。
- 扩展内部记录格式，兼容旧格式；受控标识未知值省略，旧数据不猜测补全。
- 按时间选择最新异常并顺序输出；runtime.jsonl 最多 4 MiB UTF-8，辅助内容最多 256 KiB；关闭 ZIP 后验证实际 <=5 MiB。
- 保留 runtime.jsonl/environment.json/README.txt；补充导出时间、范围、计数、采集状态，区分无异常与采集不完整。
- Windows 崩溃摘要维持相关应用、七天、有限条目及超时/脱敏；刷新或事件采集失败明确说明。
- 暂存并验证后替换目标；失败/取消保留原目标并清理临时文件。
- 同步中英日提示及诊断/架构文档，移除依赖正常开始/完成记录的旧解释。

## 精准验证
仅运行 --stability diagnostics-limits，隔离数据目录、注入事件读取器，无真实事件采集。
1. 过滤/时间：失败、超时、中断保留；普通通知、成功、主动取消排除；UTC 有效且正常过滤不计丢弃。
2. 兼容/隐私：混合旧记录与隐私注入，只导出异常白名单字段。
3. 容量/裁剪：小预算覆盖最新保留、计数、空包、解压、实际 ZIP 大小；生产上限断言。
4. 轮转/失败：小样本总容量；取消/失败保留目标和清理暂存。
调整旧 Completed 日志 fixture，不运行旧测试组。不执行全量、更新旧测试、应用启动、窗口/鼠标/键盘测试。
定向检查包含必要编译；正式包由 GitHub 工作流构建，不重复本地完整打包。

## 发布
- 主程序/辅助程序/打包默认版本同步 4.1.0，程序集/文件版本 4.1.0.0。
- 更新 RELEASE_NOTES，提交并推送 v4.1.0 标签，沿用 GitHub Actions。
- latest 正式版必须含 TuckPane-4.1.0-win-x64-setup.exe、TuckPane-4.1.0-win-x64-portable.zip、SHA256SUMS.txt。
- 一次核对工作流、资产版本/名称/哈希和便携更新清单。
- 用户手测：旧客户端手动检查、升级版本、已有数据、诊断导出与取消；自动验证不冒充真实体验通过。

## 执行状态与证据
- [x] 需求确认；本计划已覆盖旧 PLAN，旧任务不继承。
- [x] 实施前检查：source 工作区干净；当前版本 4.0.0；本机 app/current 保留。
- [x] 实现及独立审查：异常规则统一；隐私/容量审查完成；修复读取权限故障误判为无日志的问题。
- [x] 验证前检查及 diagnostics-limits 定向检查：一次运行 PASS，含必要 Release x64 编译。
- [x] 发布前检查：已重读计划；版本 4.1.0/4.1.0.0 一致，四组定向检查通过，无阻断审查项。
- [x] GitHub 发布与资产核验：正式 latest v4.1.0，三项资产与 543 个便携程序文件校验通过。
- [ ] 用户实际更新及界面手测（待用户执行）。

### 验证前检查
- 已重读计划，修改仅涉及诊断、版本/说明与专用测试入口。
- sub-agent 完成隐私边界审查、版本/本地化/文档、四组定向测试实现；主 agent 审阅测试，无旧套件及 GUI 调用。
- 本机旧客户端文件版本 3.1.0，SHA256 `7C828C7FB18677C8DB33206E268B64EAF91B5BCDFB74A96F8991EB34AF86A9C1`，不部署覆盖。

### 自动验证证据
- 命令：`dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 --no-restore -- --stability diagnostics-limits`
- 退出码 0；输出：`PASS --stability diagnostics-limits: 4 focused groups (filter/time, history/privacy, capacity/newest retention, rotation/failure/status); isolated files and synthetic events only; no GUI, input automation or legacy suites.`
- 此定向入口只执行一次；本轮未启动 TuckPane、未采集真实 Windows 事件、未执行旧测试或 GUI 测试。
- 版本/资源 sub-agent 完成六份 XML 静态解析与限定 diff 检查。
- 远端发布使用现有 tag 工作流；流程只进行打包与发布，不额外执行旧测试。

### 发布结果
- 发布代码提交：`c7ff08a5fc54da7863d1facdba8e9e0abd9122bc`，标签 `v4.1.0` 已推送。
- GitHub Actions：<https://github.com/ch998244353/TuckPane/actions/runs/34630996717>，成功，release job 用时 3m53s。
- Release：<https://github.com/ch998244353/TuckPane/releases/tag/v4.1.0>；`/releases/latest` 确认正式版 v4.1.0，非 draft/prerelease。
- 三项下载资产大小、SHA256SUMS 与 GitHub SHA-256 digest 全部一致：
  - `TuckPane-4.1.0-win-x64-setup.exe`：301106048 字节；SHA256 `271248b636abcd5ec9cfb32fa6dfff85591a0a198e0ab9115d1416211ffdda4f`。
  - `TuckPane-4.1.0-win-x64-portable.zip`：129776757 字节；SHA256 `fd68b1ddb0476882ddfccbd2a7db029c34179d80ff071e87fc88eea263c82e4d`。
  - `SHA256SUMS.txt`：203 字节；SHA256 `21a477e14cd294574dcfc66101a76c885fe9d37813ba723c912b3a456e9fb11f`。
- 便携 `update-files.json` 版本 4.1.0，543 个程序文件逐项 SHA256 匹配；主程序与辅助程序文件版本均为 4.1.0.0；只读取元数据，没有执行程序。
- 本地核验输出：`artifacts/verified-v4.1.0/verification.json`。GitHub CLI 大文件下载停滞后改公开链接；安装包连接重置后续传成功，完整哈希最终通过。未因此重跑功能测试或构建。
- 发布后重读本计划，全部自动门槛完成。app/current 仍为 3.1.0，EXE 哈希与实施前一致；未部署、安装、重启或操控软件。
- 剩余用户验收：旧客户端手动检查并升级到 4.1.0，确认数据保留、诊断导出和取消；真实更新/界面体验不标记为自动验证通过。
