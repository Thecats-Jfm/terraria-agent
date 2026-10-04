# 已安装原版的 A 阶段接口核查

核查日期：2026-10-04。对象是本机 Windows Steam 原版 Terraria **1.4.5.8**，不使用 tModLoader 的 `ModPlayer.SetControls` 或 `ModSystem.PostUpdateEverything`。

接口核查只读取安装程序集的 PE、CLI 元数据和 IL，用自有工具分析；核查工具没有加载或执行 Terraria 程序集、入口点、类型初始化器或游戏方法，没有读取人物/世界存档内容，也没有操作界面或录屏。下面的更新顺序是静态证据，不能当作实机验证。另在“启动就绪事件与首次启动失败”中单独记录主执行者实际启动隔离 Host 时的日志结果；首次启动失败，A 尚未验收。

## 版本与证据

| 项目 | 值 |
| --- | --- |
| 程序集路径 | `E:\SteamLibrary\steamapps\common\Terraria\Terraria.exe` |
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

当前 Bridge 在入口前安装存档保护，然后订阅 `OnEnginePreload`。一次性回调先取消订阅、重新断言隔离并确认 `Main.instance` 与 `GraphicsDevice` 非空，再安装 `DoUpdate` / `CopyInto` 控制补丁。隔离错误终止进程；控制钩子失败进入 faulted/停控状态，禁止通过热键再次取得控制。事件时序来自静态 IL；此修改是否解决启动失败，仍需新进程实机验证。

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

### 局部窗口热键事件修复（待实机验证）

本机 net48 的 `System.Windows.Forms.Control.FromHandle(IntPtr)` 返回 Control；Control 提供公开 `KeyDown` / `KeyUp` 事件，委托签名为 `void KeyEventHandler(object sender, KeyEventArgs e)`。`KeyEventArgs.KeyCode`、`Control`、`Shift` 均为真实公开属性。XNA 的 `Game.Window` 返回 GameWindow，`GameWindow.Handle` 返回 IntPtr。Bridge 在图形就绪后仅订阅 `Control.FromHandle(Main.instance.Window.Handle)` 对应的真实 Form，不安装全局键盘钩子，不修改 Handled/SuppressKeyPress；订阅失败停控并禁止 Arm，Shutdown 解除订阅。

事件回调不接触 Terraria 玩家、Main 或世界对象：Ctrl+Shift+Insert 的首次 KeyDown 只把单调时间和 Gate 世界会话号放入不可变信号，通过 Interlocked 交换引用，避免 x86 上时间/世界字段不同步。只有真实 KeyUp 复位 Insert 按住标志；过期、不安全上下文、死亡、菜单和世界切换清空信号与待发许可，但不复位按住标志，因此 autorepeat 不能取得新的许可。原 XNA 轮询作为 fallback，不能重新接受已经由窗口捕获的同一按住手势。

游戏线程消费信号时再次核对不超过 500ms、同世界、焦点、普通玩家状态、无桥接故障及全键释放，最多授予一次许可；窗口和 fallback 均受时限约束。Ctrl+Shift+Backspace 及 Agent 期间普通 KeyDown 立即通过线程安全 Gate 停控，玩家输入字段仍在下一次游戏更新释放；不记录普通按键内容。未捕获的 KeyUp 会保守阻止许可，不通过猜测复位按住状态。本修复仍需新进程验证真实 Form 事件、许可、停止与接管；此前没有 human_arm_permission 的精确原因尚未确认，不能把源码或编译成功算作 A 通过。

## Host 入口与资源根

真实入口 `0x0600019C` 是 `private static void Terraria.WindowsLaunch.Main(string[] args)`，返回 void，只有一个 string[] 参数。静态方法反射调用的参数包装应为单元素 object[]，该元素为完整 string[]；入口先注册原版嵌入 DLL 的 AssemblyResolve，再调用 `Program.LaunchGame(args, false)`。注册的资源解析器和其他原版 `GetExecutingAssembly()` 调用仍位于 Terraria 程序集中，不会因 Host 调用了入口而自动变成 Host 程序集。

但 **XNA 内容根确实依赖 Host 可执行文件的位置**。本机 XNA4 的 `Microsoft.Xna.Framework.TitleLocation.get_Path()`（`0x06000392`）调用 `Assembly.GetEntryAssembly()`，再取该程序集 Location 的父目录并缓存。原版 Main 构造器将 `Content.RootDirectory` 设为相对路径 `Content`，XNA 的相对资源读取随后基于 TitleLocation。因此只把当前目录设为游戏副本，仍可能使 XNA 去找工作区 build/Content。

启动流程应把真正运行的 Host、Bridge 和 Harmony 副本放进隔离游戏目录，或在任何资源加载前明确设置绝对 Content 根并核查所有其他相对资源读取。前者能同时覆盖更多 XNA 相对路径。此结论来自本机 XNA 程序集的静态 IL；实际资源/音频/图形加载仍未验证。

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

编译状态：SaveIsolation 对本机原版、官方 Harmony 2.3.3 与 net48/x86 **独立编译通过，0 警告、0 错误**。首次实际启动也已报告保护补丁安装与 Bridge 加载，但之后图形初始化失败，仍没有证明在实际人物菜单、创建新档、进入/退出世界和保存过程中兼容。进入存档前必须核实实际根、空云列表、所有人物/世界均在隔离目录及实际落盘位置；任何保护安装失败或路径不符都中止进入存档。

此保护针对普通单人 A 流程，不是恶意本机程序的沙箱；未穷尽地图伴随文件、迁移/恢复、Workshop 或所有可选保存路径，也不覆盖恶意程序制造硬链接或并发替换路径的攻击。A 不启用这些额外路径、联机和第三方模组。原有存档备份与同步等待仍须由启动流程单独完成。
