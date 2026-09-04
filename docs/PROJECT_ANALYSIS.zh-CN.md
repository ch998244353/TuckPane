# TuckPane 项目分析

本文档描述当前源码工作树的运行架构。它是后续修改启动、窗口所有权、状态、存储和拖放行为时的项目级入口；具体实现仍以当前源码和 CodeGraph 为准。

## 1. 本机容器、源码与安装目录

本机把源码和运行版收拢在同一个容器中，但二者仍是不同边界：

```text
D:\app\功能\TuckPane\
├─ source\          Git 源码根目录
├─ app\current\     当前自包含安装版及卸载器
├─ AGENTS.md         指向 source\AGENTS.md
├─ .agents\          指向 source\.agents
└─ .codegraph\       指向 source\.codegraph
```

- 开发、测试和发布只从 `source` 进行；`app/current` 只接收已验证的 Release 部署结果，禁止作为源码修改位置。
- 独立克隆仓库时，克隆目录本身就是源码根，不要求存在外层容器或 `app/current`。
- `.agents` 是源码树中的项目级 WinUI Agent 工具；根目录链接只用于本机发现，不复制第二份内容。
- 正常用户真实文件位于 `C:\Users\ch\GlassFolder`，状态、日志和图标缓存位于 `C:\Users\ch\AppData\Local\GlassFolder`；它们不属于源码、安装输出或清理范围。
- 所有自动化测试都应通过 `TUCKPANE_TEST_ROOT` 隔离状态、存储、日志和桌面目录。
- 当前通用 x64 构建最低支持 Windows 10 22H2 build 19045，并继续支持 Windows 11。编译 API 面保留 `net10.0-windows10.0.22621.0`；运行到 Windows 10 时跳过 Windows 11 专属 DWM 圆角、边框与 HostBackdrop 属性，允许使用方角和较简单的材质降级，但不得出现黑屏、空白、不可读或崩溃。

## 2. 启动与所有权

```text
Program.Main
        ├─ AppInstance.FindOrRegisterForKey
        │       └─ 第二进程激活重定向（普通启动 / --startup / .tucknote / Shell 创建命令）
        ▼
App.OnLaunched
        │
        ▼
AppHost.InitializeAsync
        ├─ 单实例保护、状态加载、语言和开机启动设置
        ├─ ConsoleWindow（隐藏宿主 HWND、托盘入口）
        ├─ TransferQueue（跨窗口文件传输串行化与取消）
        ├─ MainWindow × N（每个 OrganizerDefinition 一个）
        │       └─ StorageService（该窗口真实收纳目录）
        ├─ NoteWindow × 已打开的迁移兼容便签或 .tucknote（关闭只隐藏，不随收纳窗折叠）
        └─ NoteStore（旧正文兼容读取及 .tucknote 严格原子读写）
```

`AppHost` 是应用级协调者：持有状态、托盘/控制窗口、所有收纳窗口、按真实路径索引的 `.tucknote` 窗口、尚未迁移成功的兼容旧便签窗口和传输队列，并负责创建、迁移、删除、显示及退出。`MainWindow` 只负责一个收纳窗口的界面与交互；它通过宿主回调保存状态或请求跨窗口操作，不拥有整个应用生命周期。`NoteWindow` 是进入任务栏和 Alt+Tab 的独立应用窗，支持原生最小化/还原但不允许最大化；每次从收纳窗图标打开时都会还原、激活并抬到普通窗口 Z 序顶部。通用设置中的 `NoteAlwaysOnTop` 默认关闭；开启后由 `AppHost` 立即把所有已打开的普通及外部便签切入持续 topmost，新建窗口初始化时也读取同一设置，关闭后立即恢复普通层级。深色标题区由 WinUI 自定义标题栏的原生 Caption 区负责系统拖动，标题文字区域单独作为输入直通区，单击后切换为内联改名框，按钮和输入框不参与拖动。便签保留原生可缩放边框能力和系统阴影，但复用 `NativeWindowChromeController` 抑制 DWM 可见边线。右上角关闭和 Alt+F4 只保存并隐藏；再次打开同一路径会复用窗口，兼容旧便签按 ID 复用。托盘“隐藏全部/显示全部”统一处理两种运行时索引，普通收纳窗折叠不影响便签。`NoteEditor` 的横线模式由同一个字体指标函数设置字号、`字号 × 1.8` 行高/横线周期和 `字号 × 0.2` 下方空隙，并按当前 `devicePixelRatio` 对齐到物理像素；初始化、字号滚轮和窗口/DPI 变化都会重算。图片在横线模式下按实际显示高度把下边距补齐到横线周期整数倍，插入、加载、缩放和字体/窗口变化时重算，补偿不写入正文 HTML。文字复制由 WebView 取消自身写入并在复制事件结束后只发送一次纯文本消息；校验过来源的 WinUI 宿主作为唯一写入者，使用 `Clipboard.SetContentWithOptions` 明确允许进入已启用的 Win+V 历史，短暂占用时最多重试三次。

Windows App SDK `AppInstance` 在创建 XAML 窗口前完成当前版本的实例注册和激活重定向；`TUCKPANE_TEST_ROOT` 会参与实例键，避免隔离测试撞上正常实例。重定向的 Launch 参数先通过 Windows `CommandLineToArgvW` 规则拆分；仅当首项等于当前 `Environment.ProcessPath` 时才丢弃它，因此带中文、空格和引号的 `.tucknote` 路径会作为独立参数交给主实例。普通二次启动打开总控台，`--startup` 保持托盘启动。原有 `SingleInstanceGuard` mutex/event 继续作为旧版本兼容保护。

安装版还接受 `--create-organizer`、`--create-organizer-in "<绝对目录>"` 和 `--create-note-in "<绝对目录>"`；首次启动和第二实例重定向共用同一参数入口，执行后不打开总控台。前两项在鼠标所在显示器创建 Floating 收纳窗；便签命令由主程序再次验证绝对路径、目录存在和 Fixed/Removable 盘型，拒绝 UNC、映射网络盘及虚拟 Shell 目录，在当前鼠标附近生成空白 `.tucknote` 并立即作为外部便签打开。

正常退出从托盘命令进入 `AppHost.ExitAsync`：先等待总控主题和管理设置保存，再逐个刷新已打开便签；任一保存失败都会保留并激活对应窗口、中止退出，全部成功后才停止继续接收交互、关闭各窗口并释放单实例资源。不要用强制结束进程代替正常退出，除非测试清理已确认使用隔离状态。

## 3. 三种窗口模式

`OrganizerDefinition.PlacementMode` 有三种值，但都由同一个 `MainWindow` 实现：

- `Floating`：可自由摆放的悬浮收纳窗。
- `Positioned`：保存显示器与位置的定位收纳窗。
- `Station`：贴靠屏幕边缘、由热区展开的中转站；边由 `DockEdge` 指定。

三种模式的差异集中在窗口放置、折叠/展开和可见性状态机。文件目录、项目刷新、拖入、拖出、排序和跨窗口传输共用同一套实现。因此拖放缺陷应优先修复共享链路，而不是分别在三种模式增加分支。

`Floating/Positioned` 收起入口可作为引用放入任一未被收纳的根收纳窗，包括普通收纳窗和 `Station`；来源的模式、保存位置、入口缩放与真实目录均不改变。关系严格限制为单层：`Station` 不能作为来源，内部已有直接子窗的来源不能再被收纳，已被收纳的目标也不能继续接收子窗，自引用与悬空引用同样拒绝；已被收纳的叶子窗仍可在不同根容器之间移动。被收纳窗口不显示桌面入口、不占用 Positioned 桌面网格，创建和复制不会继承容器关系。单击任一容器内的收纳窗元素会以元素屏幕矩形为锚点临时展开原窗口；父容器保持展开并位于后方，子窗收起后重新隐藏。互斥展开按直接父子组合处理，切换到无关窗口时按子窗再父容器的顺序收敛。

管理页只允许 `Floating ↔ Positioned`；`Station` 与两者之间的选项直接禁用，`AppHost.ApplyOrganizerRuntime` 仍通过共享模式矩阵拦截其他调用入口。添加页不受此限制，仍可新建 Station。

收纳窗拖动统一经过 `MainWindow.BeginWidgetPress -> CommitWidgetDrag -> FinishCompactPressAsync`。只有“`GlobalSettings.WindowAlignmentEnabled` 开启 + `Floating` + 收起且未处于动画 + 指针不在任一收纳窗的有效投放区”进入 `WindowAlignmentMath`：窗口中心继续决定跨屏归属，X/Y 各自在 12 DIP 内吸附并保持到偏离 20 DIP。屏幕只对齐当前工作区左、右、上、下四边；窗口之间只比较同屏、可见、收起且未处于动画的 `Floating` 收纳窗 `CompactThumbnailHost` 白色圆角外框，并只允许左对左、右对右、上对上、下对下。窗口尺寸、名称区域和另一轴距离不影响同名边对齐，不提供屏幕/窗口中心线或异名相邻边吸附。候选同距时依次优先屏幕、目标收纳窗 ID、左/上边。`Positioned`、`Station`、展开态和便签不进入此分支，也不作为目标。

吸附输入由 `CompactThumbnailHost` 相对 `WindowRoot` 的实际布局、XAML RasterizationScale 和客户区屏幕原点共同换算；开始拖动时缓存白框与 HWND 外框偏移，最终仍只执行一次 `SetWindowPos`。其他窗口候选、工作区限制和保存/恢复位置使用同一白框矩形，因此不同紧凑缩放或名称缩放不会改变对齐边。白框尚未完成布局时本次不进入吸附，不使用整个窗口外框冒充白框。显示器、12/20 DIP 阈值和 1 DIP 指示线都按移动窗当前 DPI 换算。命中时最多创建两个由移动窗拥有的原生局部线窗，使用系统强调色并保持 click-through、no-activate、非 topmost；进入任一根收纳窗的有效投放内容区会立即清空吸附状态并隐藏指示线，离开后重新计算；无命中、松手、取消、捕获丢失、跨屏、关闭窗口或关闭设置时同样立即隐藏。

`GlobalSettings.ExclusiveExpansion` 默认开启并统一约束三种模式：展开新窗口时折叠上一个窗口；关闭后允许多个窗口持续展开；重新开启时立即保留最近操作的窗口并折叠其余窗口。跨窗口 Shell 拖动期间，正在导出的源窗暂不参与折叠，拖动结束后再恢复互斥。

普通 `Floating/Positioned` 窗口的悬浮展开不仅检查矩形范围，还用 `WindowFromPoint` 确认鼠标实际命中当前 HWND、子窗口或画布缩放边窗；被其他应用覆盖时不得展开。`HoverExpandDelayMs` 和 `PointerLeaveCollapseDelayMs` 分别控制悬浮展开与离开收缩，默认 350/400ms，可在 100–2000ms 间按 50ms 步进调整。重新进入会立即取消收缩；菜单、对话框、传输、窗口拖动/缩放、项目换序和系统拖放会清零计时，交互结束后重新等待完整设定时间。Station 的呼出热区覆盖其保存显示器对应方向的整条物理屏幕边缘，包括全屏应用占用但系统工作区排除的任务栏保留区域；热区只位于 Station 所在屏幕一侧，`StationActivationDistanceDip` 默认 16 DIP、范围 4–48 DIP/步进 4，并按该显示器 DPI 换算为像素。`StationHoverExpandDelayMs` 默认 120ms、范围 0–500ms/步进 20；离开收缩仍由独立的 `StationPointerLeaveCollapseDelayMs` 控制，默认 400ms，并使用 100–2000ms/50ms 规则。

`GlobalSettings.PerformanceProfile` 以手动持久化的节能、平衡、高性能三档统一后台策略：指针轮询分别为 100/50/25ms，桌面层修复分别为 8/4/2s；缺失或非法值归一为平衡且不提升 Schema 9。普通窗口仅在启用悬浮展开，或已展开且启用离开收缩时运行指针轮询；Station 仅在逻辑可见时运行，其 25/50/100ms 热路径复用显示器缓存，由初始化、模式变化和桌面修复轮询刷新。节能档禁用自定义动画，平衡和高性能保留现有效果，Windows 辅助功能关闭动画始终优先。

Station 展开时先由 `DesktopLayerService.SetExpanded(..., stayTopmost: true)` 脱离桌面 owner、设置自身 `WS_EX_TOPMOST`/`WS_EX_NOACTIVATE`，再由 `ApplyBounds` 移动并显示自身；owner 切换不携带 `SWP_SHOWWINDOW`。收缩完成后先隐藏、解除 topmost，再恢复桌面 owner，恢复路径同样不显示窗口。普通 `Floating/Positioned` 窗口仍只做一次非持续置顶的抬升，不改变其层级契约。

通用窗口边界更新和紧凑窗口重新附着 Explorer 桌面层都使用 `SWP_NOOWNERZORDER`；owner 未变化的周期修复再加 `SWP_NOZORDER`，避免单个窗口的几何/层级修复改变桌面 owner 组并把其他收纳窗带到全屏应用之上。Station 展开只抬升自身；其他普通/定位收纳窗继续留在桌面层。Station 的展开安全区由整条物理边缘热区、热区到展开窗之间的连接区和展开窗口组成；指针离开这三者后才开始原有 400ms 收缩计时。

`Floating/Positioned` 展开后在玻璃面板外的 56 DIP 透明顶部带居中显示只读收纳窗名称，固定白色、单行超长省略。总控台“设置 → 通用”提供始终生效的“收起名称大小”和“展开后名称大小”两条 60%–100% 全局比例，统一作用于 Floating/Positioned；旧定位比例、统一开关和每窗 `NameScale` 仅保留状态兼容，不再参与渲染。展开标题字号为 `42 × 全局展开比例`；Station 的标题带高度为 0 且不显示顶部名称。展开外框高度包含标题带，网格、手动画布与内容缩放始终以“面板高度 = HWND 高度 - 标题带高度”计算，并在当前显示器工作区内收敛。普通/定位窗口展开完成后由同一个 28 DIP 内侧命中带处理四边、四角 resize 光标、原生命中和按下起始；Station 仍不进入手动 resize。普通/定位窗口始终收缩回展开前保存的紧凑位置，移动展开窗口不会改写该紧凑位置；开启“记住收纳窗展开位置”后，两种模式共用每窗唯一的 `ExpandedPosition`，被其他收纳窗收纳不会禁用或分叉这份位置，移动/缩放后会在重新展开、重启、移出或换容器后继续使用；Station 自身仍只使用边缘锚点。

精简列表的 `CompactListItemScale` 每窗独立保存，默认 100%、范围 50%–165%；展开精简列表左右使用 12 DIP 内容边距，普通图标模式保持 28 DIP，Station 同样为 12 DIP。图标和精简模式普通滚轮均按当前行距进入约 160ms 可中断平滑滚动，支持余量累计、目标合并和硬边界；`Ctrl + 滚轮` 每格调整 5%，只改变对应模式比例，两者互不覆盖。

收起预览、展开窗、总控设置和应用对话框按所属主题挂载局部 `SystemBackdropElement`，由它承载 `ThemeBackdrop` 作为面板的最终背景。`ThemeBackdrop` 负责桌面采样、所选颜色分支、固定 Glass 光学处理、不透明度合成以及可选的桌面分支模糊；`ThemeSurface` 只负责按当前可见效果缩放的内部玻璃高光和圆角裁剪，不再绘制主背景、HostBackdrop、噪点或边缘。`MainWindow` 的收起缩略图和展开内容面板，以及总控设置 `NavigationView` 的右侧页面内容根，分别通过独立的 `ThemeEdgeSurface` overlay 绘制固定中性双层结构边缘；标题栏、左侧导航栏和应用对话框不接入。圆角、位移、缩放、不透明度和 Station clip 随所在 XAML 视觉树生效。窗口级 `SystemBackdrop` 保持透明，因此大 HWND 的留白和名称区域不承载矩形背景。

`GlobalSettings` 分别持久化“设置界面”和“收纳窗”两套颜色、玻璃背景不透明度、0%–200% 模糊强度和独立纯色模式；为兼容现有 JSON，磁盘字段仍名为 `ThemeTransparency` / `SettingsThemeTransparency`。纯色模式只使用完整主题色并隐藏透明度和模糊设置，关闭后恢复该目标原有玻璃参数。两套主题不显示或保存旧材质选择。`ConsoleWindow` 使用设置主题，`MainWindow` 与由它打开的 `OwnedDialogWindow` 使用收纳主题，所有收纳窗共享同一套收纳主题。收纳窗重命名复用独立 HWND 的 `OwnedDialogWindow`：按当前收纳窗所在屏幕居中、以原 HWND 为 owner、禁用原窗直至关闭，并保持 tool-window/no-taskbar；因此不再受展开 XAML 根边界裁切。确认新名称后进入 `ApplyOrganizerRuntime(Name)`，立即刷新当前窗和所属容器收纳窗的 organizer 投影，再刷新总控台并保存状态。单一 `AppHost.ThemeChanged` 仍让全部已打开外壳重读自己所属的主题；便签继续使用独立的 `NoteTheme`。主题页“修改目标”每次进入默认收纳窗，选择不持久化；切换目标只重读对应控件，不复制主题。

玻璃主题把保存值解释为背景不透明度 `o = clamp(value, 0, .99)`，模糊强度为 `b = clamp(blurStrength, 0, 2)`。`o=0` 完全透明；`0<o<=.99, b=0` 使用 alpha 为 `o` 的清晰主题色画刷。仅 `0<o<=.99, b>0` 创建 Glass：桌面分支使用 `10×b` GaussianBlur，饱和度从 1 过渡到 2、明度从 0 过渡到 0.06，后二者在 `b=1` 封顶；内部淡高光为 `4×o×(1-o)×min(b,1)`。独立纯色模式始终输出完整主题色，并旁路 HostBackdrop、GaussianBlur、调色和高光。

总控台通用、添加和管理页的选项行在浅色主题叠加约 9% 黑色、深色主题叠加约 7% 白色，管理页外层大卡保持原层级，输入框略亮。收纳窗紧凑名称、展开标题和项目名称统一使用全局白色/黑色设置，设置窗口内其他文字、图标和按钮前景仍按 WCAG 相对亮度在黑白之间自动选择。设置页只保留颜色、不透明度和模糊度，不再提供材质区域或预览。系统关闭透明效果、DWM opt-in 失败或 HostBackdrop 创建失败时，真实面板回退为 alpha 为 `o` 的主题色，不伪造模糊，透明窗口外区不参与降级绘制。收纳窗 `DesktopLayerService` 在创建以及系统主题、设置和 DWM 合成重建后重新应用无原生边框属性，避免 HWND 白色矩形框恢复。

## 4. 状态与持久化

- 根状态模型是 `AppStateV2`，当前 Schema 为 15；包含全局设置和 `OrganizerDefinition` 列表。Schema 15 为设置界面和收纳窗增加独立纯色模式字段，并统一收敛玻璃不透明度上限；旧配置缺失字段时保持玻璃模式，旧 100% 不透明度归一为新的 99% 玻璃上限。之前的单主题复制、文字颜色、透明度重置和旧材质移除迁移继续按原顺序执行，最终写回 Schema 15。
- 两套主题分别强制为不透明 ARGB；现有 `ThemeTransparency` 字段承载玻璃背景不透明度，非有限值回退 35%，其余值限制为 0%–99%；100% 仅由独立纯色模式提供。非有限模糊强度回退 100%，其余值限制为 0%–200%。纯色模式字段只选择渲染分支，不覆盖保存的玻璃参数。总控设置立即预览当前目标，停止操作约 300ms 后把两套主题一次保存。
- `GlobalSettings.CollapseOnPointerLeave` 缺失时按 `false` 处理，不提升 Schema；总控台“设置 → 通用”负责保存并在失败时回滚开关。
- `GlobalSettings.HoverExpandDelayMs` / `PointerLeaveCollapseDelayMs` / `StationPointerLeaveCollapseDelayMs` 缺失时按 350/400/400ms 处理，加载时收敛到 100–2000ms 的 50ms 步进值；`StationActivationDistanceDip` / `StationHoverExpandDelayMs` 缺失时按 16 DIP/120ms 处理，并分别收敛到 4–48 DIP/4 DIP 与 0–500ms/20ms。以上字段都不提升 Schema；旧状态中的未知 `CollapseToCenter` 字段会被忽略。
- `GlobalSettings.WindowAlignmentEnabled` 缺失时按 `false` 处理，不提升 Schema；总控台“设置 → 通用”的拖动对齐开关保存失败时回滚，关闭后当前对齐锁和指示线立即清除。
- `GlobalSettings.UseUniformFloatingCompactScale` / `UseUniformPositionedCompactScale` 缺失时均按 `false` 处理，不提升 Schema；两种模式分别保存统一目标大小，默认 156%。开启后 `StateStore.Normalize` 会把对应 `OrganizerDefinition.CompactScale` 收敛到目标值，关闭后保留当前大小。创建、复制、Shell 新建、管理编辑和模式切换都通过 `GlobalSettings.ResolveCompactScale` 使用同一规则；Station 不参与。定位模式保存 120%–180% 的统一目标，但实际窗口仍可被当前显示器桌面图标网格上限压小。
- `MoveOrganizerFilesToDesktopOnDelete` 缺失时通过属性初始化器按 `true` 处理；全局收起名称沿用旧 `UniformFloatingCompactNameScale`，缺失时为 100%，新增 `ExpandedNameScale` 缺失时同样为 100%，两者均限制为 60%–100% 且不提升 Schema。Floating/Positioned 共用这两项；旧定位比例、两个统一开关和每窗 `NameScale` 不再参与运行时渲染，Station 不参与。
- `GlobalSettings.ExclusiveExpansion` 缺失时通过属性初始化器按 `true` 处理，不提升 Schema；语言字段缺失或无效时回退中文，显式保存的中文、英文和日文保持不变。
- `StateStore.LoadAsync` 负责读取、迁移和规范化旧状态；无效的窗口数量、网格、位置和模式组合会在这里收敛。
- `StateStore.SaveAsync` 通过临时文件和备份文件完成替换，避免进程中断时直接损坏主状态文件。
- `GlobalSettings.NoteTheme` 是全部便签的持久化全局主题；任意便签选色后由 `AppHost` 保存全局值、刷新全部已打开便签，并逐个原子更新所有已注册收纳目录顶层的有效 `.tucknote`。子目录、未注册目录和已打开路径不参与批量扫描；单文件失败只记录该文件，不破坏其原内容或阻止其他文件。
- 每个收纳窗保存身份、名称、模式、布局、显示器/位置、`CompactListItemScale` 等缩放、目录映射和项目顺序。`Notes` 列表及本地 `notes/<guid>.json` 只保留给尚未迁移成功的旧便签，不再接收新内容。
- `OrganizerDefinition.ContainerOrganizerId` 保存可空的唯一父容器，磁盘 JSON 为兼容旧状态仍使用 `ContainerStationId` 字段且不提升 Schema；每个容器的 `ItemOrder` 使用 `organizer:<guid>` 稳定键参与统一排序。加载归一化按持久化顺序重放单层关系，仅保留“无子窗的 Floating/Positioned 来源 → 未被收纳的现存目标”，清除悬空、自引用、Station 来源、多层关系和错误顺序键，并让无效来源恢复桌面入口。删除任一容器时会原子解除其直接子窗关系并在父窗附近规划桌面位置；Floating 子窗按顺序错位展开，Positioned 子窗逐个占用最近网格。任一 Positioned 子窗无可用网格时，在目录转移前拒绝删除；保存失败时恢复全部归属、顺序和位置。子窗的真实目录始终不移动。
- `AppPaths` 决定正常用户根目录以及 `TUCKPANE_TEST_ROOT` 隔离根目录。`note-staging` 也位于相同本地根；启动仅清理其直接 GUID 暂存子目录。测试不得读写正常用户状态。

## 5. 真实目录、刷新与传输

每个收纳窗对应一个真实文件系统目录。`AppPaths.ResolveStoragePath` 解析窗口的相对或绝对存储位置，`StorageService` 负责枚举、重名处理、导入、复制、导出、快捷方式和目录创建。

新建或粘贴生成的收纳窗便签直接通过 `NoteStore.CreatePortableAsync` 在对应真实目录顶层创建唯一命名的 `.tucknote`，并把文件名写入 `ItemOrder`。启动迁移逐张读取旧 `notes/<guid>.json`：目标文件原子创建并重读验证、顺序键替换和状态保存全部成功后才删除旧正文；单张失败会删除本次目标、保留旧定义供下次重试，不阻止其他便签或应用启动。

可移植便签是无 BOM UTF-8 JSON `.tucknote` v1，固定字段为 `format="TuckPane.Note"`、`version=1`、`theme`、`fontSize`、`showRuledLines`、`placement`、`html`；文件名就是窗口标题，不保存 organizer/note ID。读取边界为 64 MiB，并严格拒绝缺失/未知字段、损坏 JSON、未知版本/主题、非法字号或几何。所有创建和保存沿用同目录临时文件与原子发布/替换，主题批量更新只改 `theme`，保留 HTML、字号、横线和窗口位置。便签内联改名会先保存正文，再校验空名、非法/保留文件名和目标冲突，通过同目录 `File.Move` 改真实文件名，并同步打开窗口路径索引、托盘隐藏集合和所属收纳窗 `ItemOrder`；状态保存失败时回滚这些运行时索引和文件名。

文件操作结束后，窗口重新读取目录，并把真实文件、尚未迁移的兼容 `Notes` 以及该窗口直接包含的收纳窗引用投影按 `ItemOrder` 合并；目录监听仍只管理真实文件。父窗处于图标模式时，收纳窗投影使用白色圆角小窗口轮廓，内部保留子窗前四项的实时 2×2 图标预览并显示底部名称；单元大小只读取父窗格子和 `ItemScale`，不读取子窗 `CompactScale/NameScale`。父窗处于精简模式时，引用改用 TuckPane 软件图标与收纳窗名称，并跟随父窗的 `CompactListItemScale`。不存在的顺序项会被清理，新项目会进入可见列表。`.tucknote` 在枚举时获得运行时 `PortableNote` 分类，会隐藏扩展名、使用便签图标并支持单击打开，拖出时仍是普通真实文件。删除开关开启时，已打开的顶层便签先保存并隐藏，整目录移动成功后把窗口路径索引重绑到桌面新目录并恢复原可见状态；失败保持旧路径。开关关闭时只删除收纳窗状态与窗口，目录和便签路径原地保留。应用级 `TransferQueue` 继续串行化文件传输；批量入口把递归校验、清单、复制和移动 I/O 放到 worker，保持逐项顺序、取消、进度、暂存回滚及跨盘校验，只有可执行文件的 WScript 快捷方式创建保留在调用线程的 STA 链路。

真实图片由 `IconCacheService` 先请求 Windows `SingleItem` 缩略图并按实际宽高写入 PNG 缓存，收起预览和展开网格共用该缓存并以 `Uniform` 保持完整比例。缓存身份由规范路径、项目类型、文件长度和最后修改时间组成；目录监听的 `refresh:true` 只对身份变化项重新提取，冷启动也可直接复用同一身份的磁盘 PNG。Windows 未返回图片缩略图或解码失败时回退 Shell 基础图标；Jumbo 与 fallback 路径都不请求或绘制 overlay，因此目录、快捷方式和其他文件均不显示快捷方式箭头、云同步标记等 Shell 叠加图标。缓存版本变化会让旧图标按需重新生成，不主动删除旧缓存文件。

## 6. 拖入数据流

```text
外部 OLE 拖入
  → MainWindow.WindowRoot / ItemsGrid DragOver、Drop
  → 按来源 AllowedOperations 选择 Move，或在 Copy-only 时选择 Copy
  → 优先读取 StandardDataFormats.StorageItems，失败或为空时回退原生 CF_HDROP
  → 提取真实文件或文件夹路径
  → TransferQueue
  → Move: StorageService.ImportBatchAsync
  → Copy: StorageService.CopyBatchAsync
  → 刷新目录与顺序
```

接收端沿用 Windows 的 StorageItems/FileDrop 数据，并按来源允许的操作协商：来源支持 Move 时保持 Explorer 的物理移动；不支持 Move 但支持 Copy 时执行复制，覆盖 Edge/Chrome 已完成下载项的 Copy/Link 数据对象；只有 Link 或没有有效本地路径时拒绝。两个 Drop 入口都会在第一次异步等待前标记事件已处理，避免同一次拖入被父级重复接收。普通本地文件没有扩展名白名单；拖回来的 `.tucknote` 与其他真实文件一样导入收纳目录，不转换成旧迁移兼容定义；在 TuckPane 网格中单击即由当前主实例打开，在资源管理器或外部应用中仍按正常文件关联处理。文件夹按真实目录处理，不自动压缩。同盘移动若仅因 Windows 共享或锁冲突失败，会退化到现有的暂存复制与长度校验流程；复制完整但源文件无法删除时返回 `CopiedSourceRetained` 并保留源文件，源文件无法读取时仍失败且不保留不完整目标。

## 7. 拖出数据流

所有模式共用下面的源链路：

```text
MainWindow 项目指针拖动
  → 先进入窗口内重排状态
  → 鼠标仍在完整窗口边界内：更新并提交 ItemOrder
  → 鼠标离开完整窗口边界：边界钩子投递外拖升级
  → 普通真实文件/文件夹/顶层 .tucknote：BeginXamlShellDrag → UIElement.StartDragAsync
  → 仅旧迁移失败便签在拖动激活时预热暂存 .tucknote 与 StorageFile
  → .lnk/.url：ShellDragService.DoDragDrop 原生 Shell IDataObject
  → 普通项目：Copy | Move | Link；便签：Copy | Move，默认 Move
  → 根据目标返回效果分类并收尾
```

真实界面在鼠标越过完整窗口边界后才升级为系统拖放。普通文件、文件夹、顶层 `.tucknote` 和兼容旧便签生成的暂存文件都把 `StorageFile/StorageFolder` 放入 WinUI `DataPackage` 并调用 `StartDragAsync`；WinUI 将其桥接为标准系统文件拖放数据。`.lnk/.url` 继续复用 `ShellDragService`，直接创建 Shell `IDataObject` 并同步运行 `DoDragDrop`；快捷方式拖出原文件本身，不解析目标。两条路径最终都向标准接收端提供真实绝对路径。

日常便签已经是顶层真实 `.tucknote`，直接沿用普通 StorageItems 文件拖放。只有旧迁移失败便签继续使用兼容链：超过激活阈值后强制保存、暂时隐藏窗口，并在隔离的 `note-staging/<GUID>` 生成 `.tucknote`；Move 成功才删除旧定义，Copy、取消或失败均保留旧正文并恢复窗口。这段代码只服务迁移兼容项，不再接收新便签。

低级钩子只负责判断是否越过窗口边界并把升级请求送回 UI 线程，不直接承担 OLE 消息循环。普通真实项目、顶层 `.tucknote` 和兼容旧便签暂存文件依赖 WinUI/OLE 桥接保持输入连续性；只有 `.lnk/.url` 使用原生 Shell 数据对象。拖动期间 `AppHost` 暂缓折叠活动源窗，结束后调用共享互斥收敛逻辑，成功转移时保留目标窗，取消时源窗仍可继续交互。

收纳窗引用复用同一项目换序与“越过完整窗口边界”提升点，但在进入文件/Shell 数据对象链之前被单独拦截。容器内换序只更新 `ItemOrder`；越界后原收纳窗作为父容器的临时 owned window 显示紧凑拖动预览，Station 容器仍让预览处于其 topmost 带且位于父窗上方。拖动收纳窗悬浮命中另一普通根收纳窗的紧凑白色内容卡片时，目标立即展开；该行为独立于普通悬停展开开关，且在展开动画完成后继续使用内容区插入索引。落入另一未被收纳的根收纳窗时原子更换容器，落在其他位置时解除归属：Floating 以释放点为中心并限制在工作区，Positioned 选择最近有效桌面网格。保存前失败恢复原容器、顺序和位置；保存成功后立即按最终 `ContainerOrganizerId` 对齐窗口可见性，后续父容器或总控台刷新失败只记录日志，不得再把已脱离容器的窗口隐藏。无网格或取消仍恢复原容器；真实目录和文件始终不移动。桌面入口拖到屏幕边缘仍只复用 Station 的可调展开等待时间；普通文件拖入继续沿用原有悬停规则。精简列表把双击绑定到整行，文件、目录、快捷方式和两类便签均不再单击打开；收纳窗引用在图标和精简模式下均可单击临时展开，且没有普通文件右键菜单。

结果处理边界：

- 窗口内部排序：普通项目以 Link、顶层 `.tucknote` 和兼容旧便签以 Move 作为内部协商结果；`MainWindow` 只更新 `ItemOrder`，不删除源。
- TuckPane 窗口之间：目标通过现有传输路径移动真实文件，源窗与目标窗随后刷新。
- 资源管理器文件夹和桌面目标通过同一条 StorageItems 链完成普通文件、顶层 `.tucknote` 和兼容旧便签暂存文件的移动/复制；目标显式返回 Move，或返回 None 但交接后源路径已不存在时，统一按外部 Move 收尾并刷新/删除源项。
- 桌面特殊转移只用于 `.lnk/.url` 原生 Shell 分支，并且只识别由 `Progman` 或 `WorkerW` 承载的桌面 `SHELLDLL_DefView`；普通资源管理器 `CabinetWClass` 文件夹视图继续走系统目标。
- 外部 Copy：保留收纳目录中的源项目。
- 外部 Move：目标完成移动后刷新收纳目录；WinUI 返回 None 时以源路径是否仍存在补足 Explorer 的结果差异。
- 外部 Link：保留源项目并刷新界面状态。
- 目标取消或不接受：不改动源文件。高权限目标拒绝普通权限拖入属于 Windows UIPI 边界。
- 旧迁移失败便签外部 Move：目标完成接收后删除兼容定义；Copy/取消时保留旧正文并恢复窗口。日常 `.tucknote` 使用普通真实文件链。

真实文件系统项目的应用内右键菜单固定为“重命名、复制、剪切、删除”，文件、文件夹、`.lnk`、`.url` 和顶层 `.tucknote` 共用入口；尚未迁移成功的兼容便签仍保留“重命名、删除”。紧凑收纳窗、展开空白画布、文件和便签四类菜单共用浅色 `MenuFlyoutPresenter`，普通模式背景固定为 `#FFFFFFFF`，不读取系统窗口颜色；高对比模式继续由 WinUI 自动调整。菜单 `Opening` 时临时启用输入激活；若 Station 正处于持续 topmost，会先临时退出 topmost 带，让前台 WinUI Popup 稳定位于宿主之上。`Closed` 或打开异常后立即恢复 no-activate、原 owner 与 Station topmost；展开态菜单仍限制在当前 XAML 根边界内并在靠近边缘时向内重排。复制/剪切通过 `ShellDragService` 向系统剪贴板写入 `CF_HDROP` 和对应的 `Preferred DropEffect=Copy/Move`，Explorer 可按标准语义粘贴；TuckPane 粘贴在 WinRT `StorageItems` 无法实体化快捷方式时回退读取原生 `CF_HDROP`。真实文件改名保留扩展名并同步 `ItemOrder`，失败时不覆盖目标且回滚文件名；`.tucknote` 复用打开窗口路径索引协调。删除调用 .NET 提供的 Windows 回收站操作，不显示应用确认框，失败时保留源文件并报告错误。旧的完整 Shell 菜单辅助进程已移除。

## 8. 发布与本机安装

`scripts/build-release.ps1` 先生成 Release x64 自包含发布目录，再在打包前创建与 `TuckPane.exe` 哈希一致的 `00-启动 TuckPane.exe`，最后由 Inno Setup 生成离线安装器和便携包。本机安装器使用自定义目录 `app/current`；公开安装器的默认目录仍由 Inno Setup 决定，不把本机绝对路径写进源码。自包含发布携带 .NET 与 Windows App SDK；构建脚本另从微软官方 Evergreen 地址缓存 x64 WebView2 Standalone Installer，并在提升为正式缓存前校验微软 Authenticode 签名与 Edge Update 文件身份。Inno 安装时严格解析 HKLM/HKCU 的 WebView2 `pv`，仅在缺失时由非提升安装进程静默调用微软安装器，实际安装范围由系统中的 Edge Updater 配置决定；安装失败则不启动 TuckPane。便携包不修改系统，仍使用电脑上已有的 WebView2 Runtime。

Inno 安装器在当前用户 `HKCU\Software\Classes` 注册 `.tucknote` 的 `TuckPane.Note` ProgID、同源 `Assets\Note.ico` 图标和 `"TuckPane.exe" "%1"` 打开命令，并在卸载时清理自身关联。收纳窗继续使用同源 `Note.png`，应用自身仍使用 `TuckPane.ico`。便携包不主动修改注册表，但相同可执行文件仍接受命令行/“打开方式”传入的 `.tucknote`。

3.0.0 不再构建或注册资源管理器右键菜单，因此发布链不包含 Shell DLL、MSIX 稀疏包或本地签名证书。覆盖安装会删除旧版传统菜单注册表项以及安装目录中的 `Shell`、`ShellPackage` 残留；主程序保留既有创建命令行入口供内部兼容使用，但不会据此创建系统右键入口。

## 3.0.2 收纳窗视觉与滚轮输入

- 项目图标由 `MainWindow` 统一维护图片、异步加载中状态与兜底图标的互斥关系；主题、尺寸、展开和收缩刷新不会在已有图标背后重新显示文档兜底。
- `GlobalSettings.EdgeGlowEnabled` 在 Schema 15 中直接持久化，`AppHost` 保存成功后通过现有主题广播同步所有 `ThemeEdgeSurface`；设置保存失败会恢复原值。
- 图标和精简列表通过顶层 `PointerWheelChanged` 路由解析普通滚动与 Ctrl 缩放；两种模式都关闭 `ScrollView` 默认鼠标滚轮，普通滚轮按当前行距进入约 160ms 可中断平滑滚动，目标合并并硬边界夹紧。
- Station 展开先脱离 `SHELLDLL_DefView` desktop owner，再设置自身 topmost/no-activate 后移动并显示；恢复 owner 不携带 `SWP_SHOWWINDOW`，因此底边只影响匹配显示器上的 Station 自身。

本次唯一专项入口为：

```powershell
dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 --no-restore -- --sep04-bottom-name-wheel
```

该专项只验证底边 Station 层级时序、名称颜色迁移/对比度、图标与精简滚轮状态和接线，不启动窗口或模拟真实鼠标、键盘、UIA、截图操作。

迁移或覆盖安装时必须先从托盘正常退出用户实例。安装完成后从真实安装路径启动，`StartupService.Apply` 会按 `Environment.ProcessPath` 刷新开机启动项；桌面、开始菜单和卸载注册也必须指向 `app/current`。发布文件与安装文件需要逐文件 SHA-256 比对，卸载器只能管理安装目录，不能覆盖或删除 `source`。

## 9. 失败处理与测试入口

- Shell/COM 失败在共享拖放服务边界转为明确结果或异常；文件传输以逐项结果报告，避免半成功被整体吞掉。
- `TuckPane.LogicChecks` 是无测试框架的核心回归入口。
- `TuckPane.LogicChecks --aug26-fixes` 只验证中文默认/显式语言保留、互斥默认值和 Move/Copy 操作选择。
- `TuckPane.LogicChecks --aug27-visual-fixes` 只验证非方形图片缓存保留内容和宽高比，以及缓存身份在元数据不变时稳定、修改时间变化后更新。
- `TuckPane.LogicChecks --shortcut-clipboard-fixes` 只验证 `.lnk` 左下区域不含 Shell overlay，以及便签复制只在事件结束后向宿主投递而不由 WebView 重复写剪贴板；不读写真实剪贴板，也不启动 UI。
- `TuckPane.LogicChecks --theme-targets` 是双目标主题专项入口，只验证 Schema 7 复制迁移、两套主题独立修改/归一化/保存重载，以及 56 DIP 标题带与 Station 排除的纯几何约束；不启动窗口或执行鼠标/UI 自动化。
- `TuckPane.LogicChecks --theme-opacity-blur-arc` 是本次主题端点专项入口，只验证 Schema 15、0%–99% 玻璃不透明度、独立纯色模式、模糊 0%–200%、弧光/纹理端点、唯一 Glass 门槛、透明宿主资源生命周期、局部圆角与 DWM 无边框接线；不启动应用，不执行 UIA、鼠标、键盘、截图或任何真实窗口操控。旧 `--theme-material-depth` 与 `--theme-visual-zero-endpoints` 仅保留兼容别名，不属于本次回归范围。
- `TuckPane.LogicChecks --station-hot-zone` 只验证 Station 触发设置的默认/归一化/保存重载、四边 DPI 热区、展开安全区和双屏拼接线不跨屏。
- `TuckPane.LogicChecks --aug28-organizer-behavior` 只验证四边 Station 展开安全区和指定显示器可用位置。
- `TuckPane.LogicChecks --aug29-shell-hover` 只验证中文/空格目录的 Shell 参数解析、三项悬浮延迟的默认值与归一化/独立保存重载，以及旧 `CollapseToCenter` 字段被忽略并在保存后消失。
- `TuckPane.LogicChecks --aug31-organizer-requirements` 只验证精简缩放、模式矩阵、删除与统一名称状态、顶层便签创建、逐张迁移重试、注册目录顶层主题同步和删除路径映射；不启动窗口或执行鼠标/UIA/Explorer 自动化。
- `TuckPane.LogicChecks --unified-compact-scale` 只验证两个统一入口大小开关的旧状态默认、独立作用范围、大小归一化、Station 排除、关闭后保留、共享有效值规则和保存重载；不启动窗口或执行鼠标/UI 自动化。
- `TuckPane.LogicChecks --organizer-nesting` 只验证单层收纳接受矩阵、跨容器唯一归属、顺序键规范化、事务快照回滚、直接子窗释放与 Floating 布局规划；整行命中、实时预览、窗口层级和真实拖动由安装版人工验收。
- `TuckPane.LogicChecks --organizer-resize-rename-sync` 只验证展开窗 28 DIP 四边/四角命中和原生 hit-test 映射、收纳窗重命名独立 owned-dialog 接线、contained organizer 最新名称投影及父容器/总控台刷新链；不启动窗口或执行鼠标/UIA。
- `TuckPane.LogicChecks --window-alignment` 只验证开关默认/保存重载、不同宽高窗口四条同名边、远距排列、屏幕四边、中心线与异名相邻边禁用、12/20 DIP 滞回、双轴和确定性优先级、负坐标/非 100% DPI、视觉外框偏移及跨屏状态清理所需的纯几何约束。
- `TuckPane.LogicChecks --portable-note-placement` 只验证中文空格目录与隔离桌面目录、本地路径拒绝、同目录原子创建、自动编号/不覆盖/并发重试、严格 v1 重读、无 `.tmp` 残留，以及内部便签 Move/Copy/None 与源路径存在性映射、单个 StorageItem 回读和暂存目录显式清理；不启动 UI 或执行鼠标/Explorer 自动化。
- `--external-file-drop` 启动跨进程 OLE 探针；隐藏子进程参数 `--external-file-drop-target <Copy|Move|Link>` 只供该探针使用。接收端读取 `FileDrop/CF_HDROP`，验证真实路径和协商结果。
- `tests/ExternalMainWindowDrop.ps1` 驱动真实 `MainWindow`，覆盖文件与快捷方式窗口内换序，以及文件、文件夹、`.lnk`、`.url` 的标准 FileDrop；定向入口 `-FileActionsOnly -Modes 0` 只验证五类真实项目的“重命名、复制、剪切、删除”顺序、`CF_HDROP + Copy`、普通文件和 `.tucknote` 改名及 `ItemOrder`。
- `tests/NoteFeatureCheck.ps1` 在独立 `TUCKPANE_TEST_ROOT` 中验证便签功能；定向入口 `-ScrollStationOnly` 只覆盖鼠标滚轮滚动无 F2 提示、右侧 Station 展开图标原生截图和收起隐藏，`-NotePolishOnly` 只覆盖 Schema 5 暖黄迁移、全局主题同步、外部便签保存写回、新建/重启继承、标题单击改名与本次视觉截图，`-ChromeOnly` 只比较便签应用 DWM `COLOR_NONE` 前后的真实边缘，并确认非 topmost、可缩放 app-window 样式不变；`-TitleDragOnly` 覆盖深色标题拖动、按钮隔离、内部内联改名和 Esc，`-PortableNoteOnly` 覆盖 `.tucknote` 跨收纳窗真实移动、单击打开、文件/顺序同步以及重名、保留名和 Esc，`-ActivationOnly` 覆盖带中文空格路径的第二实例重定向，`-RuledLinesOnly` 生成默认/大字号中英文下沉字形截图并核对横线状态。无开关时保留原有空白新建、文字粘贴、图片、颜色、关闭隐藏、传统菜单改名与删除回归。
- `tests/WindowLayerCheck.ps1` 除窗口层级外，还验证普通窗口被覆盖时不悬浮展开、重新暴露后恢复，以及离开收缩和提前返回取消；定向入口 `-HoverCollapseOnly` 只验证两项非默认延迟的阈值、重新进入取消、移动展开窗后回到原紧凑坐标、滑块持久化和中心收缩控件已删除；`-StationCoveredOnly` 只在 `TUCKPANE_TEST_ROOT` 中验证主屏右边缘 Station 连续展开 1.5 秒、desktop owner 脱离、持续 topmost、no-activate、不抢焦点、覆盖普通窗口但不抬升 LayerPeer，以及离开后隐藏且不重新展开。窗口对齐不再提供鼠标自动化入口，由用户在安装版手动验收。
- 托盘启动回归使用 `scripts/check-tray-startup.ps1`；定向入口 `-CreateWindowOnly` 在独立 `TUCKPANE_TEST_ROOT` 中只验证冷启动桌面命令与第二实例文件夹命令。运行前必须确认没有正常 TuckPane 实例。

常用验证命令：

```powershell
dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 --no-restore -- --theme-targets
dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 --no-build -- --station-hot-zone
dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 --no-restore -- --theme-opacity-blur-arc
dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 -- --window-alignment
dotnet run --project .\tests\TuckPane.LogicChecks\TuckPane.LogicChecks.csproj -c Release -p:Platform=x64 -- --aug29-shell-hover
& .\scripts\check-tray-startup.ps1 -ExecutablePath '<TuckPane.exe>' -CreateWindowOnly
& .\tests\WindowLayerCheck.ps1 -ExePath '<TuckPane.exe>' -HoverCollapseOnly
& 'D:\app\功能\TuckPane\.agents\skills\winui-dev-workflow\BuildAndRun.ps1' '.\src\TuckPane\TuckPane.csproj' -SkipRun /p:Configuration=Release
```

## 10. 维护规则

修改前先读本文档并查询当前 CodeGraph。若启动/所有权、窗口模式、状态 Schema、存储目录、传输队列、拖放或发布/安装边界发生变化，必须在同一任务内同步更新本文档，并把逻辑检查、跨进程探针、WinUI 构建、真实 UI 和安装版验收分别报告，不能用其中一项代替另一项。

## 11. 本次主题与文字颜色更新

本次实现覆盖前述旧语义：纯色模式现在使用独立的 0%–100% 主题色 alpha，仅隐藏模糊控件且不混入桌面；玻璃模糊下限为 5%，收纳窗玻璃表面和总控设置右侧页面内容区启用静态中性弧光与拉丝纹理边缘。

- 当前状态 Schema 为 15；Schema 14 及更早配置迁移到双目标纯色字段，缺失字段默认玻璃模式，旧 100% 不透明度归一为 99%，不反转或重置其他主题数值。
- 玻璃背景不透明度范围为 0%–99%；纯色模式提供独立的 0%–100% 主题色 alpha，并隐藏模糊设置。
- 模糊强度为 0% 时不请求 HostBackdrop，GaussianBlur、饱和度、明度和内部高光归零；独立 `ThemeEdgeSurface` 的中性边缘、弧光与拉丝纹理保持极弱非零强度。收纳窗的收起缩略图、展开内容面板和设置右侧页面内容区保留固定的中性多层玻璃边缘；该边缘不读取主题、不透明度、模糊强度或系统高级效果，标题栏、左侧导航栏和内部对话框不启用。背景、内容裁剪与装饰 Visual 显式使用 `CompositionBorderMode.Soft`，并由同一像素对齐半径同步圆角；原生 HWND 继续关闭 DWM 系统边框与系统圆角。仅中间不透明度与非零模糊创建实时 Glass；HostBackdrop 不可用时 fallback 使用 alpha 为 `o` 的主题色且不伪造模糊。
- “设置 → 显示”提供全局收纳窗文字颜色自动/白色/黑色，统一作用于收起名称、展开标题和项目名称；自动模式以主题 Tint 为稳定背景代理并按 WCAG 对比度选择纯黑或纯白。
