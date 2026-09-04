# TuckPane 3.0.2 安装包与 GitHub 发布计划

## 总结

- 项目实际仓库为 `D:\app\功能\TuckPane\source`，是基于 .NET 10、WinUI 3、Windows App SDK 的 Windows x64 桌面文件收纳工具。
- 当前工作树已有一批未提交的 3.0.2 源码、测试、文档和安装器改动；本次将这些现有改动整体作为 3.0.2 发布内容。
- 版本固定为 `3.0.2`，创建新的 GitHub `v3.0.2` Release，不改写 `v3.0.1`。
- 不新增公共 API 或数据接口；本次重点是版本文档同步、构建、桌面交付和 GitHub 发布。

## 执行流程与关键门槛

1. **覆盖计划文档并建立基线**
   - 用本计划完全覆盖 `source/PLAN.md`，旧计划内容不再作为约束。
   - 记录仓库状态、当前提交、远端、版本号、工具链和待发布文件。
   - 保留现有用户改动，不执行 `reset --hard`、回滚或覆盖无关文件。
   - 完成后重新读取 `source/PLAN.md`，确认执行记录与本计划一致。

2. **同步发布文档**
   - 将 `README.md` 和 `README.zh-CN.md` 的当前版本、Latest Release 链接及下载文件名更新为 3.0.2。
   - 保留已有功能说明，只做发布所需的最小修改。
   - 检查 `RELEASE_NOTES.md`、项目版本字段、安装器默认版本和构建脚本默认版本均为 3.0.2。
   - 回读计划文档，确认没有引入额外功能开发。

3. **本地构建最新版**
   - 在 `source` 目录执行 `scripts/build-release.ps1 -Version 3.0.2`。
   - 允许脚本清理并重新生成 `artifacts/publish/win-x64`、`artifacts/release` 和 `artifacts/shell-extension`；不触碰仓库外目录及 `app/current`。
   - 使用当前工作树构建自包含 Win-x64 版本，包含离线 WebView2 安装器、Inno Setup 安装包和便携 ZIP。
   - 构建失败时停在当前门槛，记录具体错误，不绕过版本或签名校验。
   - 构建成功后回读计划文档并更新门槛状态。

4. **发布产物专项验收**
   - 只验证本次发布直接相关内容，不运行旧功能回归套件。
   - 检查 `source/artifacts/release` 只包含：
     - `TuckPane-3.0.2-win-x64-setup.exe`
     - `TuckPane-3.0.2-win-x64-portable.zip`
     - `SHA256SUMS.txt`
   - 校验 setup 的文件版本、产品名、架构和文件大小非零。
   - 校验 portable ZIP 可读取且包含 `TuckPane.exe`、`00-启动 TuckPane.exe`、运行时依赖和许可证文件。
   - 重新计算两个文件的 SHA-256，并与 `SHA256SUMS.txt` 完全匹配。
   - 执行 `git diff --check`。
   - 不执行鼠标、键盘、OLE 拖放、UIA、Explorer 或其他实际电脑操控测试。

5. **复制到桌面**
   - 将上述三件产物复制到 `C:\Users\ch\Desktop`。
   - 若桌面已有同名 3.0.2 文件，覆盖同名文件；其他桌面文件保持不动。
   - 复制后再次计算桌面文件哈希，确保与 `source/artifacts/release` 逐项一致。

6. **提交并触发 GitHub CI 发布**
   - 审查最终 `git status`，确认只包含本次发布所需的当前工作树改动。
   - 将当前完整工作树提交到 `main`，提交信息采用 `Release TuckPane 3.0.2`。
   - 推送 `main` 到 `origin`。
   - 创建并推送不可变的 `v3.0.2` 标签；不强制覆盖已有远端标签。
   - 标签触发现有 `.github/workflows/release.yml`，由 Windows CI 使用同一版本重新构建并创建 GitHub Release。
   - 等待 CI 完成，检查 Release 是否公开、标题是否为 `TuckPane 3.0.2`、三件资产是否齐全，且 Release 说明来自 `RELEASE_NOTES.md`。
   - 若 CI 失败：先读取失败日志；只有在不存在完整 Release 时，才使用已验收的本地三件套创建备用 Release，不强制改写标签。

7. **最终回读与交付**
   - 回读 `source/PLAN.md`，确认所有关键门槛均有结果记录。
   - 最终确认桌面三件套存在且哈希正确、`main` 已推送、`v3.0.2` 标签存在、GitHub Release 资产可见、工作树状态符合预期。
   - 向用户报告桌面路径、GitHub Release 地址、提交/标签信息和验证结果。

## Sub-agent 分工

- 架构审查 sub-agent：已完成项目入口、功能模块、数据存储和运行时依赖分析。
- 发布审查 sub-agent：已完成版本来源、构建脚本、GitHub Actions、远端状态和环境依赖分析。
- 构建完成后安排一个只读验收 sub-agent：仅检查发布目录结构、压缩包内容、版本元数据和 SHA-256，不修改代码、不进行电脑实际操控。

## 测试与验收标准

- 必做：Release 构建脚本自身校验、产物结构检查、ZIP 可读性检查、版本元数据检查、SHA-256 校验、`git diff --check`。
- 可选的唯一新增逻辑专项：`--sep04-bottom-name-wheel`，仅验证本轮相关的 Station 层级、名称颜色和滚轮状态接线，不启动 UI。
- 不做：已经完成的旧功能测试、完整逻辑回归套件、真实鼠标/键盘/窗口/Explorer 操控测试。
- 构建编译本身作为本次代码可编译性门槛；不增加新的测试代码。

## 默认假设

- 发布版本为 `3.0.2`。
- 当前工作树中的已有源码、文档、安装器和测试改动全部纳入本次提交。
- GitHub 使用新建 `v3.0.2` Release，优先由现有 CI 构建和上传。
- 桌面目标固定为当前用户桌面 `C:\Users\ch\Desktop`。
- 不修改 `app/current`，不把构建缓存、调试文件或历史 artifacts 上传到 GitHub。

## 执行记录

- [x] 计划文档覆盖与基线检查
- [x] README 版本链接同步（README.md / README.zh-CN.md 已切换至 v3.0.2）
- [x] 本地 3.0.2 Release 构建（脚本成功生成 setup、portable ZIP、SHA256SUMS）
- [x] 发布产物专项验收（文件清单、setup 元数据、ZIP 条目与 SHA-256 均通过）
- [x] 桌面三件套复制与哈希复核（已复制到 `C:\Users\ch\Desktop`，逐项哈希一致）
- [x] 提交、推送 main、创建并推送 v3.0.2（发布提交 `6888552`，标签已推送）
- [x] GitHub Actions Release 验证（运行 `33860994997` 成功，三件资产已上传）
- [x] 最终计划回读与交付（桌面、远端 Release、标签与哈希均已核对）
