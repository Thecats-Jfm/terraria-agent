# 已安装原版的 A 阶段接口核查

核查日期：2026-10-04。对象是本机 Windows Steam 原版 Terraria **1.4.5.8**，不使用 tModLoader 的 `ModPlayer.SetControls` 或 `ModSystem.PostUpdateEverything`。

接口核查只读取安装程序集的 PE、CLI 元数据和 IL，用自有工具分析；核查工具没有加载或执行 Terraria 程序集、入口点、类型初始化器或游戏方法，没有读取人物/世界存档内容，也没有操作界面或录屏。下面的更新顺序是静态证据，不能当作实机验证。后续隔离 Host 已实际进入自有新档，完成 A 基本动作、一次性直接授权、活动输入断连与过期停控，以及 B 的工作台完整任务。完整证据与尚未通过的安全场景见 [VALIDATION](VALIDATION.md)。

## 版本与证据

| 项目 | 值 |
| --- | --- |
| 程序集位置 | 本机 Steam 原版安装目录下的 `Terraria.exe`；私有绝对路径仅保留在本地核查记录 |
| 程序集、文件、产品版本 | `1.4.5.8` |
| MVID | `2c29f6c3-4bd9-4add-9c58-da159804e083` |
| SHA-256 | `960A03BFF6050CF7BE16DFC1A7B19E10FC2C4F8F835A6A3B135A50DD9E6BA2F3` |
| 入口点 token | `0x0600019C`，静态核查工具未调用；首次实际启动结果另记 |
| 分析工具 | 工作区 `work/game-api-inspector/`，.NET 9.0.201 的 `System.Reflection.Metadata` / `PEReader` |
| 结构化证据 | 工作区 `work/game-api-inspection.json` |
| 原始 IL 证据 | 工作区 `work/game-api-*.il.txt`，保留在本机，不提交原版游戏代码或二进制 |

环境初查时没有找到可直接使用的 Mono.Cecil、Harmony、ILSpy/ildasm 工具。静态分析没有下载依赖；桥接编译使用另行准备并固定的官方 NuGet `Lib.Harmony 2.3.3`。程序集还引用 .NET Framework 4.0、XNA 4.0、ReLogic、Steamworks.NET 等；这里只核对 A 所需接口，不能声称已经验证游戏运行依赖全部可用。

## 更新和输入

| 准确声明 | token | 用途和限制 |
| --- | --- | --- |
| `protected override void Main.Update(GameTime gameTime)` | `0x06000D16` | 调用 `DoUpdate(ref gameTime)`，之后处理 CinematicManager 和主线程队列；桥接异常清理不能依赖正常返回 |
| `protected void Main.DoUpdate(ref GameTime gameTime)` | `0x06000D19` | 可在 prefix/postfix 检查会话、期限与生命周期，观察自身状态；并非每次都有世界更新 |
| `private void Main.DoUpdate_HandleInput()` | `0x06000D40` | 先调用 `PlayerInput.UpdateInput()`，随后维护原版键盘状态 |
| `private void Main.DoUpdateInWorld()` | `0x06000D29` | 经原版是否更新实体的条件后进入世界更新 |
| `private void Main.DoUpdateInWorld_Inner()` | `0x06000D2A` | 在 `IL_004C` 调用 `UpdateWorld_Players()` |
| `private void Main.UpdateWorld_Players()` | `0x06000D2B` | 在 `IL_003E` 调用各玩家 `Update(int)` |
| `public void Player.Update(int i)` | `0x06000990` | 本地玩家在方法内部重建控制状态；不适合在其 prefix 直接写持续输入 |
| `public void TriggersSet.CopyInto(Player p)` | `0x0600170B` | 推荐 A 的输入 postfix 点，必须重新检查是否本地、存活及允许 Agent 控制 |
| `public void Player.UpdateDead()` | `0x06000911` | 死亡分支也调用 `CopyInto`；输入钩子必须拒绝死亡/幽灵状态 |

命名空间：`Main` / `Player` 在 `Terraria`，`TriggersSet` / `PlayerInput` 在 `Terraria.GameInput`，`GameTime` 在 `Microsoft.Xna.Framework`。

`Player.Update(int)` 的本地普通控制分支在 `IL_0BB1` 调 `ResetControls()`，然后检查 `Main.drawingPlayerChat`、`editSign`、`editChest`、`blockInput`；正常分支在 `IL_0BE9` 调 `TriggersSet.CopyInto(Player)`。因此在 `Player.Update` prefix 写入的 `controlLeft/controlRight/controlJump` 会被原版清掉。

`CopyInto` 在 `IL_0034`、`IL_0040`、`IL_004C` 分别写左、右、跳。三字段均为 `public bool` 实例字段；`position`、`velocity` 是继承自 `Terraria.Entity` 的 `public Vector2` 字段。后续原版仍可能因全屏地图、旁观、操控其他实体、反向控制等状态清空或调整输入；桥接保留这些正常机制。水平移动和跳跃随后在玩家更新内执行，最终验收必须看位置/速度变化，不能只看写入 flags。

安全判断至少使用本地玩家身份、`active/dead/ghost`、`Main.gameMenu/gamePaused/netMode`、世界加载状态以及上述文本输入状态。`CurrentInputTextTakerOverride`、`PlayerInput.WritingText` 也可作为保守停控信号，但 `WritingText` 每次 `PlayerInput.UpdateInput()` 的 `IL_0103` 会重置为 false，不能单独判断聊天/编辑是否开启。

`DoUpdate` 包含启动画面、游戏内截图、帧时间累积、失去焦点、菜单、不能更新玩法及暂停等提前返回路径。租期用单调真实时间，执行动作时再次检查；恢复更新时旧动作必须已经失效。`Main.Update` 可作外层异常清理点，单纯 postfix 无法保证处理抛异常情况。

## 启动就绪事件与首次启动失败

准确事件为 `public static event System.Action Terraria.Main.OnEnginePreload`；添加方法 `public static void Main.add_OnEnginePreload(Action value)` 的 token 是 `0x06000C69`，移除方法 token 是 `0x06000C6A`。事件添加/移除仅维护委托，不读取 `CaptureManager`。

本机更新顺序：

1. XNA `private void Microsoft.Xna.Framework.Game.RunGame(bool useBlockingRun)`（`0x06000056`）在 `IL_002E` 调用图形管理器 `CreateDevice()`，在 `IL_0034` 调用 `Main.Initialize()`，在 `IL_007A` 首次调用 `Main.Update(GameTime)`。
2. `Main.Update`（`0x06000D16`，108 字节）在 `IL_0000` 检查 `IsEnginePreloaded`，首次在 `IL_0019` 调用 `OnEnginePreload`，然后在 `IL_0036` 调用 `DoUpdate(ref gameTime)`。此方法没有直接读取 `CaptureManager` 静态字段。
3. `Main` 构造器在 `IL_0267` 设置 `Main.instance`，在 `IL_02C3` 创建 `GraphicsDeviceManager`；构造器结束时尚不能假定设备已经由 XNA `CreateDevice()` 创建。因此选择首次更新的事件比构造器 postfix 更稳妥。

当前 Bridge 在入口前安装存档保护，然后订阅 `OnEnginePreload`。一次性回调先取消订阅、重新断言隔离并确认 `Main.instance` 与 `GraphicsDevice` 非空，再安装 `DoUpdate` / `CopyInto` 控制补丁。隔离错误终止进程；控制钩子失败进入 faulted/停控状态，禁止通过热键再次取得控制。事件时序来自静态 IL；延迟安装及后续自有 Host LAA 修复后，真实运行已到达菜单并进入自有新档，记录了 `graphics_ready=true`。这不证明首次异常的唯一触发原因或 A 输入验收通过。

首次实际启动已报告 SaveIsolation 与 Bridge 加载，随后在图形初始化阶段失败。游戏副本的 `client-crashlog.txt` 记录时间为 `10/4/2026 12:35:05 PM`，异常为 `TypeInitializationException: Terraria.Graphics.Capture.CaptureManager`，内层是其构造器的 `NullReferenceException`；调用链经过 `LegacyLighting.Rebuild → Lighting.Initialize → Main.SetDisplayMode → Main.LoadSettings → Main.ClientInitialize → Main.Initialize → XNA Game.RunGame → Program.RunGame`。没有据此声称到达人物菜单、进入新档或完成 A 动作验收。

与故障有关的静态证据：`CaptureManager` 标记为 `BeforeFieldInit`，其静态构造器（`0x060021C8`）直接创建单例；实例构造器（`0x060021BC`）在 `IL_0019` 读取 `Main.instance`，在 `IL_001E` 对其调用 `Game.get_GraphicsDevice()`。原版 `DoUpdate` 在 `IL_016B` 读取 `CaptureManager.Instance`。提前安装补丁会准备该方法；固定 Harmony 内的 `MonoMod.Core.Platforms.Runtimes.FxCoreBaseRuntime.Compile` 在 `IL_0009` 明确调用 `RuntimeHelpers.PrepareMethod`。

由此推断：控制补丁预编译可能在 Main/图形就绪前触发放宽时序的静态初始化，并使失败被 CLR 缓存。支持此推断的是：Windows 的 `SetDisplayMode` 在 `IL_000B` / `IL_0010` 已通过 `Main.instance.get_Window()`，之后才在 `IL_046F` 调用 `Lighting.Initialize()`；本次错误出现在后者，说明不能简单认定该时刻的 Main.instance 仍为空。**尚未动态追踪首次触发 CaptureManager 初始化的精确位置，也未证明某一次 Harmony 调用就是唯一原因**，不得把此推断写成已经确认的根因。失败的类型初始化不能在同一进程内恢复，必须退出该隔离 Host 后以修复版本新启动。

新增本机静态证据：`work/game-api-capture-manager.il.txt`、`work/game-api-capture-references.il.txt`、`work/game-api-main-client-init.il.txt`、`work/game-api-xna-game-init.il.txt`、`work/game-api-harmony-init-references.il.txt`。原始 IL 保留在工作区，不提交游戏代码或二进制。

## 焦点、生命周期和热键

- 焦点真实接口：`public static bool FocusHelper.IsSelectedApplication`；`public static void FocusHelper.UpdateFocus(out bool wantsToPause)`，token `0x060000CD`。不要猜测 `Main.hasFocus` 字段。该方法综合 XNA IsActive 和 Windows 窗口状态；焦点仍需要后续实机测试。
- `Main.DoUpdate` 的 `IL_0823` 更新焦点，失焦停更分支在 `IL_0845` 设置 `gamePaused=true` 后返回。`private static bool Main.CanPauseGame()` 根据单人模式、选项窗口和 autoPause/UI 状态判断暂停；`IL_098A` 设置暂停，恢复正常更新在 `IL_099C` 清除。
- 退出入口包括 `public static void WorldGen.SaveAndQuit(Action callback)`、`public static void WorldGen.JustQuit()`。进入菜单、世界加载/退出或 `Main.ActiveWorldFileData`/世界会话变化应清空租约；复活和进入新世界不能恢复控制权。
- 世界会话可使用合法元数据 `Main.ActiveWorldFileData.UniqueId` 和专用会话号；不因此暴露地图、种子、矿物或世界文件内容。`UniqueId` 是 `public Guid` 实例字段。
- **F8 已被原版网络诊断使用**：`Main.DoUpdate` 调用 `DoUpdate_F8_ToggleNetDiagnostics()`。停控应选择核实无冲突的组合键或外部停止入口，实机再验证。
- `Player.spectating` 的普通初值是 **-1**：构造器 `IL_0260` 加载 -1 并在 `IL_0261` 写入；正常 Spawn 也写入 -1。A 可要求 `spectating < 0` 才允许接管。
- Ctrl+Shift+Insert / Ctrl+Shift+Backspace：静态搜索直接检查 Insert/Back 键的游戏代码，仅找到 `Main.GetInputText` 的复制、粘贴与退格处理，没有发现这些组合的常规玩法专门强功能。Ctrl/Shift 本身有原版默认 SmartCursor/SmartSelect 功能，用户自定义绑定与实机冲突尚未核对；聊天、编辑或任何 UI 文本输入时禁止启用 Agent。

### 游戏窗口消息过滤器修复（基本动作已实测）

此前真实 Form 的 `KeyDown` / `KeyUp` 订阅已在实际运行中安装，但 A 等待期间仍没有 `arm_chord_seen`。这只能证明没有收到可记录的手势，不能证明操作工具的按键已送达游戏窗口。针对本机 XNA 4.0 与 .NET Framework WinForms 程序集，静态 IL 进一步确认以下路径：

| 已核实接口或方法 | token 与顺序证据 | 含义 |
| --- | --- | --- |
| `void WindowsGameHost.Run()`，XNA Game 程序集内的内部 override | `0x06000199`，`IL_002F` 调用 `Application.Run(gameForm)` | 游戏使用真实 Form 的 WinForms 消息泵 |
| `bool Application.ThreadContext.PreTranslateMessage(ref NativeMethods.MSG)`，WinForms 内部方法 | `0x06006216`，`IL_0006` 调用 `ProcessFilters`，之后 `IL_00B9` 才调用 `Control.PreProcessControlMessageInternal` | 消息过滤器先于控件键盘预处理 |
| `bool Control.PreProcessMessage(ref Message)`，public virtual | `0x060011B8`，`IL_0077` 调用 `ProcessDialogKey` | 被分类为 dialog key 的消息可能在 Form.KeyDown 前消费 |
| `protected override bool WindowsGameForm.ProcessDialogKey(Keys keyData)` | `0x06000186`，普通路径在 `IL_0043` 返回 true；Alt+F4 和部分 Guide 状态例外交回基类 | 不能假定真实 Form 的所有短按都会触发 KeyDown；此处是已核实的结构性风险，未证明全部实际失败都由它造成 |
| `public static KeyboardState Keyboard.GetState()` / `GetState(PlayerIndex playerIndex)` | `0x0600027F` / `0x06000280`，后者 `IL_0002` 调用 Win32 `GetKeyboardState` | 读取当前键盘状态，不保存按下/释放事件；两次采样间完成的短按可能漏检 |

本机准确公开接口为 `static void Application.AddMessageFilter(IMessageFilter value)`（`0x06000923`）、`static void Application.RemoveMessageFilter(IMessageFilter value)`（`0x06000947`）及 `bool IMessageFilter.PreFilterMessage(ref Message m)`（`0x06002A04`）。添加和移除均使用 `ThreadContext.FromCurrent()`，因此在游戏线程注册并在同一线程解除。`Message.HWnd/Msg/WParam/LParam` 和 `static Keys Control.ModifierKeys` 均已核实；`Control.FromHandle(IntPtr)` 与 `Game.Window.Handle` 用于获取真实游戏 Form 和 HWND。

当前源码用 `IMessageFilter` 代替 Form.KeyDown/KeyUp 订阅：在图形就绪后缓存真实游戏 HWND，仅观察该 HWND 的 `WM_KEYDOWN`、`WM_KEYUP`、`WM_SYSKEYDOWN`、`WM_SYSKEYUP`；过滤器始终返回 false，保留原版消息处理，不安装全局钩子、不拦截其他窗口。安装失败停控并禁止 Arm，Shutdown 在游戏线程移除过滤器。`window_hotkeys_installed` 应记录 `messageFilter=True;ownHwndOnly=True;consume=False`；该安装记录仍不等于手势收到或控制成功。

消息回调不接触 Terraria 玩家、Main 或世界对象。Ctrl+Shift+Insert 的首次按下把单调时间、已连接控制器会话、世界、control epoch 与已观察的停止标记版本放入不可变信号，通过 Interlocked 交换引用，避免 x86 上字段不同步。只有真实释放消息复位 Insert 按住标志；过期、不安全上下文、死亡、菜单和世界切换清空信号与待发许可，但不复位按住标志，因此 autorepeat 不能取得新的许可。原 XNA 轮询仍作为 fallback，不能重新接受已经由窗口消息捕获的同一按住手势。

游戏线程消费信号时再次核对不超过 500ms、同世界、同控制器会话与 epoch、焦点、普通玩家状态、无桥接故障及全键释放，最多授予一次许可。Ctrl+Shift+Backspace 与普通键消息立即通过线程安全 Gate 停控并撤销待消费许可；玩家输入字段在下一次游戏更新释放，不记录普通按键内容。未捕获的释放消息会保守阻止许可，不猜测复位按住状态。`STOP.lock` 与已观察版本检查继续保护授权和 STOP 清除的竞态。

MessageFilter 已在第七次实际运行收到手势并完成右移、停止、跳跃、停止。后续 `153146` 运行通过一次性 `operator_arm` 基本动作；`153501` 与 `153759` 分别通过活动移动中的断连、租期过期后释放输入；`161025` 运行通过活动移动中的普通按键人工接管与紧急热键停控。较早的热键许可等待仍有失败，完整根因未确认。死亡、活动控制中离开/切换世界等矩阵项尚未逐项实机验收。逐次证据见 VALIDATION。

### 正常暂停与保存

以下签名来自同一安装版本的元数据与 IL，实际暂停、恢复和保存重进结果另见 VALIDATION。

| 准确声明 | token | 用途 |
| --- | --- | --- |
| `public static void IngameOptions.Open()` | `0x060001C3` | 打开普通选项菜单，由原版 `CanPauseGame` / `DoUpdate` 决定暂停 |
| `public static void IngameOptions.Close()`；`Close(bool quiet = false)` | `0x060001C4` / `0x060001C5` | 正常关闭选项菜单并打开背包 |
| `public void Player.ToggleInv()` | `0x06000872` | 正常关闭恢复时新打开的背包，否则 AutoPause 会继续暂停 |
| `public static void WorldGen.SaveAndQuit(Action callback = null)` | `0x0600112F` | 正常排队保存角色/世界，完成后把回调排回主线程 |

Ctrl+Shift+Home / End 只来自真实游戏 HWND 的首次按下消息，使用不可变信号与 Interlocked；500ms 内在游戏线程再次核对同世界、单人、存活、焦点及无文本输入。Home 先停控、释放注入，再调用正常 Open；原背包、全屏地图或箱子已打开时拒绝 UI 操作。End 只关闭本入口在同世界打开的选项菜单，保持 Gate 停控，不授予 Agent 许可。源码从不直接赋值 `Main.gamePaused` 或改变游戏时间。

`160530` 和 `161025` 已实测 Home 暂停；`160530` 已实测 End 恢复后仍停控，以及同一 Host 内新人工授权后再次完成 B。随后点击普通 Save & Exit 回主菜单，再次启动 `161025` 时，经过可见过滤的观测与画面均确认工作台 `(2102,281)` 保留。Alt+F4 / `Main.QuitGame()` 只代表程序退出，不能替代正常保存成功证据。

## B 的原版正常动作接口（最小任务已实测）

以下来自同一本机 1.4.5.8 PE/CLI 元数据与 IL，只读检查未加载或执行目标程序集。Source 通过实际游戏引用编译仍不证明玩法成功。

| 接口 | 本机签名/证据 | 使用边界 |
| --- | --- | --- |
| 选取自身物品 | `Player.selectedItem` 是公开只读 getter `0x06000831`；`selectedItemState.Select(int)` 是公开 struct 方法 `0x06003F06` | 不写 getter、不改物品 stack；初版观测只返回热栏斧头/工作台槽 |
| 工具、放置 | `controlUseItem`、Item `axe/type/stack/createTile/placeStyle`；CopyInto 后 Player.Update 正常计算 tileTarget 并进入 ItemCheck | 普通斧头沿原版工具、距离、动画、伤害累计和掉落流程；工作台由原版放置与消耗处理，不独立调用 KillTile/PlaceTile |
| 工作台形状与放置原点 | `TileObjectData.GetTileData(int,int,int)` `0x060014E8`；公开 Width、Height、Origin(Point16)、CoordinateWidth/Padding/FullWidth/FullHeight getter | 观测用左格为规范坐标，执行按当前物品样式的正常 Origin 换算；完整两格 frame 连续才确认世界工作台 |
| 资格刷新 | `Player.AdjTiles()` `0x06000A04`，`Recipe.UpdateRecipeList()` `0x060004FB` | 游戏线程，取当前可制作列表；主任务限定自身普通 Wood 足够且非打开箱子 |
| 配方前置 | `Recipe.PlayerMeetsEnvironmentConditions(Player,List<string>)` `0x06000503`；`CollectedEnoughItemsToCraft(Recipe)` `0x060004FE`；`Main.CursorHasSpaceToCraftRecipe(Recipe)` `0x06000E09` | 每次制作前再次验证；requiredTile 为 int，不能使用旧版 requiredTile 数组假设 |
| 正常制作 | `CraftingRequests.CraftItem(Recipe,int,bool)` `0x060023CC` → CraftLocally `0x060023CE` → Consume → CreateResult → Main.CraftItem_GrantItem `0x06000E14` | 第一轮 CraftItem 不自行完整检查全部条件，所以调用方先检查；只调用普通 10 Wood→1 WorkBench、qty=1、quickCraft=true，并核对真实背包增减。不得单独调用 GrantItem/GetItem 制造物品 |
| 正常拾取 | 原版 Player.Update → GrabItems → PickupItem → GetItem | 控制器接近掉落处，等待自身 Wood 增加；不调用拾取函数伪造掉落物 |
| 镜头、可见过滤 | Camera.ScaledPosition/ScaledSize、Lighting.Brightness(int,int)、Tile 状态公开接口 | 先实际 viewport 与光照，再有界遮挡射线；不把 TileDrawing.IsVisible（只含隐形涂料等判定）当完整视野，也不使用渲染 culling 的屏幕外扩展区 |

B 默认关闭。支持范围保守限定单人、普通重力、关闭 SmartCursor 且非手柄输入；不改变这些用户设置。仅记住最多 16 个此前实际返回的可见树根，换世界/退出清空；当前重新可见且该格不再为树，才发布 GoneTreeTargets，候选缺失不能证明砍倒。NPC、矿物、墙、液体和未探索地图均不提供。

制作按会话/动作序号先消费去重；实际提交在 `STOP.lock` → Gate 的固定锁顺序内核对当前控制、动作序号和租期。先取得停止权的请求阻止提交，已开始的普通制作事务完成后才处理停止。配方查询后再次检查租期，避免慢资格查询刷新旧动作。工具/选择/瞄准的意图随停止清空，最终工作台正常耗尽不导致残留 UseItem 或重复消费。`154929` 运行已通过普通可见树木的发现、接近、斧头砍伐、拾取、制作与放置：Wood 0→35→25，WorkBench 0→1→0，当前可见世界出现完整两格工作台。`160530` 复用窗口再次通过任务，正常保存后重进 `161025` 已确认其工作台保留。其他群系和可见性边界场景尚未穷尽实测。

本机只读原始证据保存在 `work/terraria-runtime/logs/game-api-b-contract-members.txt` 与 `game-api-b-craft.il.txt`，不发布原始本机文件。代码阶段之后已按用户重新授权进行了上述实机测试。

## Host 入口与资源根

真实入口 `0x0600019C` 是 `private static void Terraria.WindowsLaunch.Main(string[] args)`，返回 void，只有一个 string[] 参数。静态方法反射调用的参数包装应为单元素 object[]，该元素为完整 string[]；入口先注册原版嵌入 DLL 的 AssemblyResolve，再调用 `Program.LaunchGame(args, false)`。注册的资源解析器和其他原版 `GetExecutingAssembly()` 调用仍位于 Terraria 程序集中，不会因 Host 调用了入口而自动变成 Host 程序集。

但 **XNA 内容根确实依赖 Host 可执行文件的位置**。本机 XNA4 的 `Microsoft.Xna.Framework.TitleLocation.get_Path()`（`0x06000392`）调用 `Assembly.GetEntryAssembly()`，再取该程序集 Location 的父目录并缓存。原版 Main 构造器将 `Content.RootDirectory` 设为相对路径 `Content`，XNA 的相对资源读取随后基于 TitleLocation。因此只把当前目录设为游戏副本，仍可能使 XNA 去找工作区 build/Content。

启动流程把真正运行的 Host、Bridge 和 Harmony 副本放进隔离游戏目录，覆盖 XNA 的相对资源读取。此依据来自本机 XNA 程序集的静态 IL；修复后的隔离 Host 已实际到达菜单并进入自有新档，但完整资源、音频和图形兼容性未穷尽验证。

`Steam.CoreSocialModule.Initialize()`（`0x06001E48`）会调用 `SteamAPI.RestartAppIfNecessary(AppId_t(105600))`；返回 true 时退出当前进程，SteamAPI.Init 失败时弹出错误框并退出。原生函数的实际返回值和重启对象没有在本次静态分析中验证。首次实机前应避免静默绕过隔离 Host 重新启动原游戏；不能只凭调用 Terraria EntryPoint 就宣称启动成功。

相关工作区证据：`game-api-windows-launch.il.txt`、`game-api-entry-input-init.il.txt`、`game-api-player-spawn.il.txt`、`game-api-xna-title-location.il.txt`、`game-api-xna-content-path.il.txt`、`game-api-insert-back-key-uses.il.txt`。

## 存档与 Steam Cloud

`Program.SavePath` 为 `public static string`。`public static void Program.LaunchGame(string[] args, bool monoArgs)`，token `0x06000C36`，在 `IL_002F` 读取 **`-savedirectory`**，并在 `IL_005B` 设置 SavePath 后进入 `RunGame()`。`Main` 静态初始化分别将 `PlayerPath`、`WorldPath` 设为该根下的 `Players`、`Worlds`，不会随后自动跟着根字段变化；Host 必须在任何可能触发 Main 初始化之前设好根。

云路径是另一套命名：`CloudPlayerPath="players"`、`CloudWorldPath="worlds"`，与本地 SavePath 无关。实际模块字段是 **`Terraria.Social.SocialAPI.Cloud`**，类型为 `Terraria.Social.Base.CloudSocialModule`，不是 `Platform.SocialAPI.Cloud`。

`Main.LoadPlayers()`（`0x06000CAC`）/`Main.LoadWorlds()`（`0x06000CAA`）先扫描本地目录，再在 Cloud 非空时调 `Cloud.GetFiles()`，之后调用玩家文件读取/世界元数据读取。只在人物/世界显示出来后过滤已经太迟，必须在列表入口前验证根，提前拦截云枚举和 I/O。仅把 Cloud 设为 null 也不足够：`FileUtilities` 对 `isCloud=true && Cloud==null` 的一些操作会回退本地相对路径。

`LaunchInitializer` 中只发现 `-cloudworld`（选择云世界），没有可当作禁云开关的 `-cloud` / `-nosteam` 客户端参数。`SocialAPI.Initialize(Nullable<SocialMode>)` 会给普通客户端选择 Steam；本实现保留 Steam 的正常模块，不改用户的 Steam 全局设置。

`Steam.CloudSocialModule` 的准确可拦截实例签名如下，均为 public virtual：

```csharp
IEnumerable<string> GetFiles();
bool HasFile(string path);
bool Write(string path, byte[] data, int length);
int GetFileSize(string path);
void Read(string path, byte[] buffer, int size);
Stream OpenRead(string path);
bool Delete(string path);
bool Forget(string path);
```

`Base.CloudSocialModule.EnabledByDefault` 是 public bool 实例字段；其 `private void Configuration_OnLoad(Preferences preferences)` 可在配置加载后保持 false。基础类的便利 Read/Write 重载调用上述虚方法。

## 当前存档保护实现及边界

`src/AgentBridge/SaveIsolation.cs` 实现：

- `Install(Harmony, string saveRoot, Action<string,string> diagnostic)` 要求程序集版本 1.4.5.8；安装前与每个保存入口核对 Program/Main 根与 Players/Worlds；精确核对每个待 patch 方法的参数、返回类型与 static/instance，缺少签名立即失败。
- 云 GetFiles 返回空、存在/写入/删除/遗忘返回 false、长度返回零；意外 Read/OpenRead 抛异常禁止真实读取。关闭云默认值并跳过 MoveToCloud/MoveToLocal，不清空正常 Steam 模块。
- 核对玩家/世界列表、LoadPlayer/GetFileData、GetAllMetadata、SetAsActive、SavePlayer/InternalSavePlayerFile、WorldFile.LoadWorld/SaveWorld/_SaveWorld/InternalSaveWorld 的明确文件路径与云标识。
- 仅接受完全限定的本地盘路径，拒绝目录外路径和保存根内 reparse points。不会拦截游戏 Content 或整个系统文件 I/O，不读取存档内容。

被核实的关键保存签名：

```csharp
PlayerFileData Player.LoadPlayer(string playerPath, bool cloudSave); // static
PlayerFileData Player.GetFileData(string file, bool cloudSave);      // static
void Player.SavePlayer(PlayerFileData playerFile, bool skipMapSave, bool canBeSkipped); // static
void Player.InternalSavePlayerFile(PlayerFileData playerFile);       // private static
WorldFileData WorldFile.GetAllMetadata(string file, bool cloudSave); // static
void WorldFile.LoadWorld();                                         // static
void WorldFile.SaveWorld(bool resetTime, bool useTemps, bool canBeSkipped); // static
void WorldFile._SaveWorld(bool useCloudSaving, bool resetTime, bool useTemps, bool canBeSkipped); // private static
void WorldFile.InternalSaveWorld(bool useCloudSaving, bool resetTime, bool useTemps); // private static
```

编译状态：SaveIsolation 对本机原版、官方 Harmony 2.3.3 与 net48/x86 **独立编译通过，0 警告、0 错误**。首次实际启动在保护安装后因图形初始化失败；后续运行已核实 Main/Program 隔离根一致、云拦截日志、自有新档创建和落盘，以及退出后重进。完整保存与切世界流程仍未全部验收，不能把这些单项或本次输入修改算作 A 通过。每次进入存档前仍须核实实际根、空云列表、所有人物/世界均在隔离目录及实际落盘位置；任何保护安装失败或路径不符都中止进入存档。

此保护针对普通单人 A 流程，不是恶意本机程序的沙箱；未穷尽地图伴随文件、迁移/恢复、Workshop 或所有可选保存路径，也不覆盖恶意程序制造硬链接或并发替换路径的攻击。A 不启用这些额外路径、联机和第三方模组。原有存档备份与同步等待仍须由启动流程单独完成。
