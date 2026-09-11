# 收纳窗动画基线采集

本轮诊断版标识为 `animation-baseline-20260907.1`。它保留原有动画行为，尚未修复卡顿；默认启动不启用诊断。`PLAN.md` 中的修复阶段需要本轮手测基线。

## 手动启动与采样

1. 先通过托盘菜单正常退出 TuckPane。诊断版由 agent 编译检查、备份部署后再启动；不能把第二实例的环境变量传给仍在运行的旧进程。
2. 部署完成后，在 PowerShell 执行以下命令。脚本仅为这次启动启用 `TUCKPANE_PERF_TRACE=1`，不永久修改系统环境变量，也不操作鼠标、窗口或文件内容：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File "D:\app\功能\TuckPane\source\scripts\start-animation-diagnostics.ps1"
   ```

3. 使用你容易复现卡顿的高性能档、原主题、原内容和原显示器。选择一个普通收纳窗及一个中转站，分别展开、收起三次。等每次动画完成后再进行下一次，第一轮单独观察。
4. 告诉 agent 是否仍然卡顿、显示器刷新率，以及哪一次/哪类窗口最明显。当前机器的日志路径是 `C:\Users\ch\AppData\Local\GlassFolder\TuckPane.log`，agent 可直接只读提取新诊断记录，不需要你手动复制全部日志。

本机沿用旧版 `GlassFolder` 数据根。不要创建新的用户 `TuckPane` 同名目录，不设置 `TUCKPANE_TEST_ROOT`；否则会切换配置或单实例身份。正常退出诊断版后，从原快捷方式启动即可恢复默认关闭诊断。

## 日志含义与限制

每个接受的开合请求结束后产生一行 `[PERF] organizer-animation` JSON，不逐帧写日志，不包含文件名、路径或正文。构建标识、程序集 MVID 和进程 ID 区分二进制与运行批次；窗口 ID 和 sequence 区分单窗样本。中断请求可能标记 superseded、cancelled、window-closed 或 incomplete，应与 completed 样本分开分析。

- `stagesMs`：准备、布局/首帧等待、动画、结束交接的墙钟耗时。收起没有单独布局准备时，Layout 为零。准备可能包含等待互斥窗口收起、内容加载和已有状态保存。
- `uiWaiterCallbacks`：现有逐帧等待器实际收到的 XAML Rendering 回调及间隔。无新增常驻 Rendering 订阅。间隔包含 UI 阻塞和等待器重订阅间隙，**不是显示器或 compositor 的实际呈现 FPS**；33/50ms 只是诊断阈值，不是帧率目标。
- `waits`：已结束的 Rendering/Rendered 等待数量及超时数量；取消中的等待不算超时。
- `work`：ApplyBounds 和 CollapseMove 分别统计主窗口边界提交与收起逐帧位移；不包含桌面层服务和缩放边窗内的原生调用。ConfigureLayout 统计配置请求，UpdateLayout 统计挂点内显式强制布局调用；不是 XAML 引擎全部布局次数。
- SurfaceGeometry、AnimatedCorner、ThemeApplication 分别统计完整表面几何刷新、动画圆角更新组和主题应用调用；圆角更新组包含背景、内容、高光和边缘操作，**不等于 GPU 材质重建次数或 GPU 时间**。
- VisualFrame 是应用提交视觉帧的次数，包括初始化和终点，不是实际显示帧数。项目与已实现元素数为开始/结束时的快照。

并发等待器可能在同一帧收到多个回调，拉低平均间隔；快速触发新请求时，准备阶段也可能混入尚未取消的旧动画工作。因此基线应等待每次开合完成，排除重叠或 superseded 样本。advancedEffects 记录系统高级视觉效果开关，以区别保存的玻璃设置与系统允许的效果。

汇总序列化和日志提交发生在计时结束之后，仍在 UI 线程有短暂开销。诊断仍有少量计数开销，比较时应使用同样的诊断开关。若数据不足以区分 UI 与 GPU 瓶颈，再仅针对手动开合做被动性能跟踪。

## 自动检查边界

仅运行 `--organizer-animation` 的三组诊断纯逻辑检查，不运行旧功能全量检查，不创建或操控窗口。检查证明计数与汇总正确，不能证明动画流畅度。

## 部署与回退

诊断构建输出位于 `source/artifacts/animation/20260907-baseline/publish`。只将其构建清单中的文件复制到 `app/current`；现有 WebView2 缓存、卸载器及其他文件保留。备份和部署清单在同一轮 artifacts 目录记录。

如需回退，由用户退出程序后，agent 按清单恢复原文件，仅移除本次清单中新增的构建文件，再由用户启动。不会删除或回退用户真实文件及配置。
