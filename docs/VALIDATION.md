# Terraria Agent 验收记录

本文件只填写已有证据的结果。计划中的任务、工具可用或命令返回成功都不能直接算作真实游戏完成。

历史“正常退出”或 run_end 表示应用退出链完成，**不表示执行了正常 SaveAndQuit，也不表示本次背包/世界进度已保存**。专用档已创建且可加载，与修改后保存并重进保留进度是不同验收项；此前 Alt+F4 记录不能替代后一项。

## 当前状态

本轮测试时间为 2026 年 10 月 4 日 UTC；本地 Asia/Shanghai 已跨至 10 月 5 日。规则模式，无付费模型 API，隔离 Terraria 1.4.5.8。A 基本动作、首次直接授权、活动断连、TTL、人工接管与紧急停止分别通过；B 的正常树木采集和工作台制作/放置两次通过。Home/End 正常暂停、恢复保持停控，以及正常 Save & Exit 后重进保留工作台也已验证。死亡、文本输入、活动控制中离开/切换世界等完整安全矩阵仍待。

TA1 / The Annoyed Frontier 现仅作 technical_validation 开发验收档：早期 Alt+F4 造成角色与世界部分保存不一致。单次真实技能结果有效，正式 Boss 主挑战须另建干净 Classic 人物与世界，不使用本档保留材料，也不直接修改背包伪装修正。没有 Boss 或战斗准备档成绩。

已知累计死亡 6；最新 run `20261004T161025Z-de709aaf` 无新死亡、仍正常暂停、HP45，未关闭。当前累计日志 manualTakeovers=4：历史菜单紧停1，本轮普通接管1、紧急组合的两次状态转换2；不等同于4个独立活动接管测试。B 后暂停失败、首跑材料不足、旧 manual-test 刹车边界失败均保留。近期按用户要求不特别录屏。

代码轮没有桌面操作、启动或部署，协议 50、几何/DTO 16、最初技能 3 项通过。修复收集必须足够材料后技能 4/4 通过；15:49 前普通 Host/Bridge/Controller 整体 Build 零警告、零错误，随后一键 B 实机成功。取消或仅菜单/Credits退出不冒充新玩法失败，C/D 尚未实现。

| 项目 | 真实游戏结果 | 编译或测试结果 | 证据和下一步 |
| --- | --- | --- | --- |
| 环境检查 | 隔离原版 1.4.5.8 已实际进入新建人物与世界 | 文件版本、工具与磁盘查询已完成；启动修复后重建成功 | 自有 Host/Bridge，仅固定 Harmony；不加载上游 Core/Injector |
| TerrariaModder 源码审查 | 未运行第三方程序 | 固定提交下载、398 文件 Git blob 核对、关键路径静态审查完成 | 见 SECURITY_REVIEW；不代表实际发行二进制已验证 |
| 存档备份与隔离 | 保存根、云拦截、专用档创建通过；正常 Save & Exit 后重进确认工作台 (2102,281) 和 Wood94 保留 | 稳定备份与隔离副本校验通过，正常保存入口签名已核对 | 早期部分保存异常保留；现档只作开发验收，正式主挑战另建干净档 |
| 录屏 | 12 秒短片已在 PotPlayer 实际播放并确认连续变帧，播放验收通过；第五至第九次运行按用户要求不录屏 | 四段历史视频编码、元数据与完整解码通过 | 后续 20、30、45 秒视频尚未做播放器验收；本轮动作只有日志与结果文件，没有新增连续录像 |
| A 观察、移动、停止、跳跃 | 第七次 63 动作基本流程通过；新版首次直接授权 65 动作通过，实际 Right=true、X 增加 100.71094 像素、跳起 78.808105 像素、两次停止标志清空且 VX=0 | 新版 Host/Bridge/Controller 已用于实际游戏加载 | 基本及首次直接 A 通过；不标记完整安全矩阵通过 |
| A 断连、有效期、紧急停止、人工接管 | 活动断连/TTL 锁停；普通键进入 Manual，紧急组合进入 LatchedStop，均有运动前置及实际输入释放证据 | 50 项协议/本机回环/共享锁与制作事务通过 | 四项分别通过；死亡/文本/切世界等仍待，上界不能当精确延迟 |
| B 砍树、拾取、制作和放置工作台 | 两次完整 PASS，148/146 动作；首次 Wood0→35→25，第二次 Wood49→52→42，产物均0→1→0且完整世界工作台出现 | Collect 足够材料释放后确认，技能4/4；实际加载的整体构建零警告零错误 | 正常保存重进保留第二次工作台通过；首跑材料不足失败保留 |
| 正常暂停与恢复 | Home gamePaused/optionsOpen=true，位置/HP稳定；End 恢复二字段false且仍锁停，无授权/旧输入；同窗口重新授权再次完成B | 自有 HWND、500ms同世界、正常UI接口；拒绝人原先已开背包/地图/箱子 | 普通视图暂停恢复通过，其他UI边界尚未逐项实测 |
| C 资源、装备、照明、避险、平台 | 未实现、未实测 | 未测试 | B 通过后扩展 |
| D 独立克苏鲁之眼测试 | 未实现、未实测 | 未测试 | 与主档独立记录 |
| D 主挑战正常准备和 Boss 尝试 | 未实现、未实测 | 未测试 | 正常材料、时间和召唤条件 |

前两次启动失败；第三次新档运行正常退出并记录死亡 4 次、人工接管 0 次。第四次菜单运行无新死亡或 Agent 输入，其强制终止不记正常退出。第五次正常退出，记录新增死亡 1 次；第六次只到菜单且无动作，已核对正常 `run_end`；第七次正常退出并完成基本动作，死亡 0 次、人工接管 0 次。第八次正常退出，死亡 0 次、manualTakeovers 计数 1 次（菜单无 Agent 时紧停）；第九次正常退出，死亡 0 次、manualTakeovers 0 次。截至第九次累计死亡 5；完整 B 后暂停失败新增死亡 1，已知累计为 6。正常 stop 的成功不代替活动输入中的断连、到期或紧急停控验收。

## A 阶段后台准备历史

以下保留启动前的准备证据。其中“尚未启动”等描述仅对应当时状态；当前实际启动结果见后续独立记录。准备状态文件中的 `prepared_not_launched` / `SafeToLaunch=false` 等是准备阶段字段，不应单独用于判断当前是否已有游戏 run。

2026-10-04 01:43 至 01:58（Asia/Shanghai）的准备流程在可见桌面进程的受审环境实际运行。复制前后原存档的文件集合、长度、写入时间与 SHA-256 一致；21 文件约 57.65 MiB 的备份保留在工作区。16,017 文件约 766.72 MiB 的游戏副本与原安装逐文件一致，源与副本 EXE 均匹配 GAME_API 的固定哈希。原安装和原存档未写入。此前受限环境中的准备尝试已停止并标记失败，保留其备份；不把这次失败当成确认游戏关闭。

成功准备状态保存在 `work/terraria-runtime/preparation-status.json`，`BackupStable`、`GameCopyVerified`、`ProcessVisibilityVerified` 为 true；`SafeToLaunch`、`CloudSyncConfirmed`、`SavePathVerifiedInGame` 仍为 false。捕获窗口一致不能证明 Steam 云同步队列已清空，实际隔离仍需首次启动核实。

离线 Build 脚本已实际运行，使用固定本地 NuGet 源和本机 net48/XNA 引用。Host、Bridge 与 Controller 均编译成功，零警告、零错误。ProtocolChecks 的 22 项检查包括租期、陈旧动作、会话、人工许可、实际/目标输入分离、真实 127.0.0.1 通信认证、断连重连和畸形帧释放；没有启动 Terraria。

2026-10-04 02:10（Asia/Shanghai），最终 Build 和独立 Deploy 脚本实际执行成功。四个自有文件部署到隔离游戏目录，后续再次部署也成功；修复了 Windows PowerShell 5 下单条记录的数组解包问题。部署字节与构建输出一致，固定 Harmony 包和 DLL 哈希检查通过。全部 PowerShell 脚本可被 Windows PowerShell 5 解析。启动程序检查资源目录及现有 Steam App ID 文件的代码已编译；这些检查尚未通过真实游戏启动执行。

截至上述后台准备结束时，尚未执行 Start-Game、Run-Controller 的游戏连接、Stop-Agent 的游戏效果或 Record-Game；当时没有游戏 run、人物或世界创建、截图或连续录像。该历史交付状态是“后台实现与准备完成，实机待验证”，不是当前最新状态。

后续启动准备中，再次只读复核原存档与实际备份：21 文件的集合、长度、修改时间及 SHA-256 一致，新增、缺失与变化均为零；没有解析存档内容。用户随后要求暂时保留桌面使用，游戏启动、创建新档、输入测试和录屏继续等待桌面交接。已获取的桌面工具状态不作为游戏截图或录像证据。

启动脚本现已接入 `Verify-Backup.ps1`。其 Windows PowerShell 5 实际调用于 2026-10-04T01:21:18Z 返回 `verified_read_only`：4 组源、21 文件、变化零，原文件和备份均未写入；调用的数组结果也满足 Start-Game 的单结果检查。此次只运行复核脚本，没有执行游戏启动入口；`CloudSyncConfirmed` 仍为 false。

## 2026-10-04 实际启动与修复

启动前使用 Windows PowerShell 5 实际完成新备份 `20261004T043320Z-4d74a0bf`，21 文件的本轮捕获与复核稳定；旧备份仍保留。相对早期基线，8 个源文件的字节哈希发生变化，记录为用户/session 期间的存档或配置变化，本轮重新建立备份基线。此记录只使用文件清单、时间、长度和哈希，不解析或发布原人物/世界的私密内容。

| 启动 run id | 实际结果 | 修复及证据边界 |
| --- | --- | --- |
| `20261004T043452Z-aa95a9b2` | SaveIsolation 与 Bridge 加载后，图形初始化因 `CaptureManager` 的类型初始化异常失败，内层为 NullReferenceException；未到达可验收玩法状态 | 将控制补丁安装延迟至准确核实的 `Main.OnEnginePreload`。静态证据与提前初始化/缓存失败的推断吻合，尚未动态追踪唯一触发位置；详见 GAME_API |
| `20261004T044538Z-df32bcd1` | 延迟补丁修改后再次启动，在纹理加载阶段出现 OutOfMemoryException；未验收游戏动作 | 原版 EXE 已启用 Large Address Aware，自有 x86 Host 原先未启用；仅为自有 Host 的 PE 编译输出启用该标志，未修改原版游戏 EXE |
| `20261004T045508Z-9a8f03cc` | 修复后实际到达 1.4.5.8 主菜单；启动时 Host PID 为 14736；随后创建并进入新人物和世界 | Main/Program 根一致，隔离与控制钩子安装有实际日志；自身观测通过，三次 A 等待人工许可超时，未执行 Agent 动作；05:21:31Z 正常退出 |
| `20261004T054003Z-531d9365` | 实际到达菜单，窗口 KeyDown/KeyUp 钩子安装；菜单只读观测通过 | 桌面操作工具连续报错，未进入世界；没有 Agent 输入或新死亡。核对自有 Host PID 26340 后仅终止该进程；强制终止不记为正常退出，没有正常 `run_end` 验收 |
| `20261004T140050Z-47ec59b6` | 成功重进已有 TA1 与 The Annoyed Frontier；游戏设置页确认三项音量 0%；A 等待许可 15 秒失败、动作零 | 按用户要求未录屏；本轮死亡 1 次、人工接管 0 次，14:09:34.736Z 正常退出；人工许可入口与 A 动作仍未通过 |
| `20261004T141401Z-7a2858f7` | 仅到菜单，未进入世界或执行动作；许可等待预算 60 秒，未获得许可，最终结果为连接中止错误、actions=0 | 实际日志确认 14:16:02.019Z 正常 run_end，deaths=0、manualTakeovers=0；不算世界内动作或释放输入验证 |
| `20261004T141822Z-591bd65a` | 第一次 60 秒许可等待失败、动作零；第二次实际获准并完成右移、停止、跳跃、停止，success=true、63 条动作 | 14:24:44.846Z 正常退出，deaths=0、manualTakeovers=0；无视频，基本动作通过，完整安全矩阵仍待验证 |
| `20261004T142633Z-b2dcc99c` | 实际发起 disconnect-test；许可等待 30 秒后超时、actions=0，没有活动断连验收 | 14:34:02.193Z 正常 run_end，deaths=0、manualTakeovers=1；该计数来自菜单无 Agent 时紧停，不算活动接管通过 |
| `20261004T143826Z-1d1c792e` | 仅菜单、重进及正常退出，没有 Agent 动作或任务结果文件 | 只读核对 run_end 为 14:40:47.740Z，deaths=0、manualTakeovers=0；没有新增动作或安全项成绩 |
| `20261004T153146Z-cc9dd5a0` | 显式初始 operator_arm 成功，stage-a success=true，65 动作完成右移/停止/跳跃/停止 | 15:33:39.9453892Z 正常 run_end，deaths=0、manualTakeovers=0；独立日志确认 neutral/disconnected，活动安全项仍待 |
| `20261004T153501Z-8a5e4cd0` | 显式初始授权后完成真实右移，再关闭连接；disconnect-test success=true，9 动作 | 停止原因 disconnected、LatchedStop、实际输入释放；15:37:15.5622980Z 正常 run_end，deaths=0、manualTakeovers=0 |
| `20261004T153759Z-704e6aa8` | 显式初始授权后真实右移再停止续期；expiry-test success=true，8 动作 | lease_expired / LatchedStop、实际输入释放；15:39:23.6897886Z 正常 run_end，deaths=0、manualTakeovers=0 |
| `20261004T154141Z-64bd8b8e` | B 106 动作、约 6.82 秒，完成发现/接近/砍树/拾取；制作前置木材不足，整体部分成功 | 没有制作或工作台；15:42:57.5459977Z 正常 run_end，deaths=0、manualTakeovers=0，累计计数不变 |
| `20261004T154929Z-efb622b8` | B 一键完整 PASS，148 动作；材料、产物与世界放置变化都满足 | 任务后 Esc/AltTab 暂停失败，死亡 1；15:54:17.2621743Z 正常应用 run_end，deaths=1、manualTakeovers=0，不称保存退出 |
| `20261004T160530Z-744acb29` | Home/End、同窗口重新授权后第二次B、正常Save&Exit通过；旧manual-test刹车边界失败 | B146动作，世界工作台(2102,281)；run_end16:09:35.8937795Z，死亡0/接管0 |
| `20261004T161025Z-de709aaf` | 保存重进保留工作台；活动普通接管与紧急停止通过；当前正常暂停 | 两项323/308动作，实际输入释放；无新死亡，日志接管3，尚无run_end |

第三次运行的 `save-isolation.txt` 显示 `actualMainSavePath` 与 `actualProgramSavePath` 都等于工作区专用 `work/terraria-runtime/saves/main`。`bridge-00.jsonl` 实际记录 `graphics_ready=true` 的控制钩子安装，并记录云 `HasFile`、`Write`、`GetFiles` 被拦截。菜单观测为 `menu=true`、`worldId=null`、`controlState=Manual`，三个输入 flags 均为 false；菜单中的位置/生命值是接口约定的零值，不能作为角色位置观测或角色停止的证明。

本轮没有接入付费模型。启动、观察和修复使用自有固定用途 Host/Bridge 与固定 Harmony；没有加载上游 TerrariaModder Core、Injector、Vault、DebugTools。运行日志保留在 `work/terraria-runtime/logs/<run id>/`，不把受限本机连接令牌或原存档私密内容加入文档。

## 新档与 A 实机尝试

以下结果属于 `main_challenge`，运行标识为 `20261004T045508Z-9a8f03cc`。人物名 `TA1`，Classic，初始生命值 100、魔力值 20。世界名 `The Annoyed Frontier`，Small、Classic、Corruption；种子输入框留空，由游戏随机生成，没有输入特殊种子。首次实际位置观测为 X=33566、Y=4486；桥接中的世界标识是以 `910eece` 开头的不透明标识，不向 Agent 暴露世界地形或种子内容。

连续 8 秒的自身观测实际完成，记录来自游戏更新后的自身状态。三次 `stage-a` 尝试都在等待人工许可 60 秒后超时；动作数量为零，日志没有 `human_arm_permission` 事件。不能把“等待许可”“连接成功”或角色自然位移当成 Agent 控制成功。短促按键可能没有被当前轮询方式采到只是待核实的解释，尚未证实；已检查的焦点判断依据游戏焦点状态，不依赖 EXE 名称或窗口标题。

等待期间角色被 Green Slime 击杀累计 4 次，Agent 未获得控制，因此这些死亡没有验证“正在行动时死亡释放输入”。`Stop-Agent` 已产生 `stop_file` 日志，但触发前没有活动 Agent 输入，主动停止与释放延迟仍未验收。05:21:31Z 使用正常 Alt+F4 退出，`run_end` 为 `deaths=4`、`takeovers=0`。隔离目录中的自有新人物和世界文件及其 `.bak` 文件存在；退出后重进尚未测试。

Controller 的 `MoveRight` 成功判据已强化：必须在 sequence、gameTick、monotonicMs 均晚于初始观测的新采样中看到实际 `Inputs.Right=true`，同时 X 位移至少 16 像素；命令确认或惯性本身不能通过验收。该 Controller 单独 Release 编译通过，零警告、零错误，运行输出已更新；尚未在获准控制后执行这一判据。Bridge 的诊断与窗口输入事件修改已编译，并在最新菜单 run 实际安装 KeyDown/KeyUp 钩子；没有确认手势接收、人工许可或接管成功。

其后独立复核发现并修复两个控制权边界：人工键立即撤销未消费许可；手势绑定已连接会话、世界和单调 control epoch，Gate 原子验证 500ms 时限，停止/断连/重连/不安全上下文后旧手势不能恢复控制。同一正常上下文和动作续租不推进 epoch。`STOP.lock` 串行化停止写入、watchdog 检查和有效许可后的解除，取锁失败保守停机；无效手势不删除 STOP。新增修复后的 Bridge Release 与 ProtocolChecks 均编译为零警告、零错误；29 项离线/回环/临时文件共享锁检查通过，Stop-Agent 通过 Windows PowerShell 5 语法解析。随后最终源码的 Host、Bridge、Controller 整体 Release Build 通过，全部零警告、零错误，自有 Host 的 LAA 标志 `0x0122` 已核实。修复尚未部署或在真实游戏执行，文件锁检查不等于实际输入释放或停控延迟验收。

以上为第七次成功之前的失败与修复记录；当时优先解决人工许可事件，A 未通过，B、C、D 未实现。随后基本动作的实际成功和仍未通过的安全项见下文第七次运行。

## 第五次真实运行：重进、静音与许可超时

运行标识为 `20261004T140050Z-47ec59b6`，仍属于规则模式的 `main_challenge`。已有 Classic 人物 `TA1` 和 Small、Classic、Corruption 世界 `The Annoyed Frontier` 已实际重入成功。游戏设置页实机确认三项音量均为 0%；此次只修改隔离目录的自有配置并保留配置备份，没有修改系统音量或播放器设置。

按用户最新要求，本轮没有录屏。日志和任务结果文件保留在本地运行目录，公开文档不包含原始日志、连接令牌、存档内容或 Windows 用户名；本轮材料不称为连续录像，旧短片的播放验证仍仅属于此前运行。

本次 `stage-a` 在 14:06:07.848Z 建立控制器连接，等待人工许可 15 秒后返回 `human_arm_or_ready_world_timeout`；14:06:22.998Z 断连。结果为 `success=false`、`permissionWaitMs=15000`、`actions=0`，桥接日志没有 `arm_chord` 事件。因此本轮没有成功的 Agent 移动、停止或跳跃，也没有活动持续输入的释放验收。

本轮新增死亡 1 次，累计已知死亡 5 次，人工接管累计 0 次。正常 Alt+F4 退出产生 14:09:34.736Z 的 `run_end`，记录 `deaths=1;manualTakeovers=0`。连接与断连、自然死亡、正常退出均发生在 Agent 未获得控制的情况下，不能据此宣称主动释放、TTL 延迟或人工接管通过。截至第五次，当时仍需修许可入口，A 未通过，B/C/D 尚未实现；后续成功见下文。

## 第六次运行：仅菜单，无动作

`20261004T141401Z-7a2858f7` 只进入菜单，没有世界内动作。`result-stage-a-141602.json` 记录 `permissionWaitMs=60000`、`success=false`、`actions=0`，最终原因是传输连接被中止；60 秒是许可等待预算，不能把该结果写为已在世界内等满 60 秒或执行了动作。日志确认 14:16:02.019Z 正常 `run_end`，`deaths=0;manualTakeovers=0`。本轮没有视频，也没有活动输入释放验收。

## 第七次运行：基本动作闭环通过

运行标识为 `20261004T141822Z-591bd65a`。第一次流程 `result-stage-a-142029.json` 仍等待人工许可 60 秒后返回 `human_arm_or_ready_world_timeout`，动作数为零。第二次先于 14:24:23Z 连接控制器，再在游戏窗口内于 14:24:24.002Z 至 14:24:24.167Z 输入并释放许可组合键。

本游戏窗口的 MessageFilter 于 14:24:24.176Z 捕获手势及已释放状态，许可前置条件全部为 true；14:24:24.177Z 记录人工许可，14:24:24.207Z 实际启用 Agent。这里的成绩包含实际许可与后续角色结果，不把按键发送或连接成功单独当成动作成功。

`result-stage-a-142428.json` 返回 `success=true`、`actions=63`、`reason=basic_actions_complete_safety_matrix_still_separate`，各技能的实际结果如下：

| 技能 | 实际验收结果 |
| --- | --- |
| 向右走 | 新观测中的实际 `Right=true`，X 增加 100.71094 像素；满足输入与位置变化的双重判据 |
| 停止 | 实际输入标志全部清空，水平速度 `VX=0` |
| 跳跃 | 实际向上速度与位置变化，跳起 70.21826 像素 |
| 再次停止 | 实际输入标志全部清空，水平速度 `VX=0` |

14:24:44.846Z 的正常 `run_end` 记录 `deaths=0;manualTakeovers=0`。早期累计 5 次死亡和所有失败尝试继续保留。本轮按用户要求没有视频，证据为私有本地运行日志和任务结果；公开文档不写连接凭据或本机身份路径。

**结论仅为 A 基本动作通过。** 正常 stop 验证明确停止指令；活动断连、TTL、紧急停止、人工接管、暂停恢复、死亡及菜单/切世界仍未通过或未测。本 run 不包含 B/C/D 成绩；其后 B 代码预览见代码开发轮，仍不能称实机通过。

## 第八次运行：断连测试未获许可

运行标识为 `20261004T142633Z-b2dcc99c`。`result-disconnect-test-143339.json` 记录 `permissionWaitMs=30000`、`success=false`、`reason=human_arm_or_ready_world_timeout`、`actions=0`。没有取得许可，也没有产生可以用于断连释放验收的活动 Agent 输入。

本轮许可按键输入窗口为 14:33:09.840Z 至 14:33:10.004Z；桥接于 14:33:10.011Z 捕获手势，诊断 `eligible=true`，但没有完成已释放状态、许可或 arm。具体原因尚未确定，不把符合初步资格或捕获按键当成许可成功。

14:34:02.193Z 的正常 `run_end` 为 `deaths=0;manualTakeovers=1`。该计数对应约 14:28:11Z 处于菜单、没有活动 Agent 输入时的 Emergency 紧停，不能当成“正在行动时人工接管并释放输入”的测试成绩。累计已知死亡仍为 5 次；断连、TTL 与完整安全矩阵仍未通过。本轮没有录像。

## 第九次运行：无动作，正常退出

运行标识为 `20261004T143826Z-1d1c792e`。本轮仅完成菜单、专用新档重进和退出，没有 Agent 动作、任务结果文件或人工许可事件。只读核对日志确认正常 `run_end` 为 14:40:47.740Z，记录 `deaths=0;manualTakeovers=0`；该时间采用游戏日志的退出完成时间。本轮没有录像，不增加基本动作或安全验收成绩。

## 新版代码开发轮：v2 首次授权与 B 预览

本段代码轮未用桌面、未启动/部署，也未替换运行中程序。CodeOnly 输出独立 build-code-only，先前两次三项目构建均零警告零错误。重新授权桌面后，B 成功前的普通整体 Build 也已零警告零错误；之后新增暂停/保存 UI 修复尚不在这个构建成绩内。

首次授权默认关闭；显式 Host `--allow-initial-controller-start` 才开放一次 `operator_arm`。控制器要求新鲜且推进的同世界观测，仅发一次请求，失败也消耗机会。停止、断连、死亡、切换已有世界或人工接管后不能从这个入口恢复；启动和 Bridge 的 STOP 检查均保留，授权在 STOP.lock 内进行，绝不删除或绕过 STOP。首次菜单/首次进世界及尚未启用时的临时失焦/暂停只影响就绪，不能误记为实机授权成功。

B 仅在 Host 显式 `--enable-stage-b` 时开放，默认关闭。已实现过滤后的可见树/此前合法树根消失确认、背包摘要、正常斧头输入、正常拾取、配方制作和工作台放置，以及规则控制器的前置条件、超时、有限恢复和结果判据。制作同一会话/动作序号先消费再调用正常配方流程；STOP.lock 与 Gate 当前动作事务串行化制作提交和停止，避免旧租约在停止后开始制作。合法同步事务若已经开始，则结束后停止生效；执行时间不会续租。没有直接修改背包、地形、伤害或时间。

| 检查 | 实际代码结果 | 范围与证据边界 |
| --- | --- | --- |
| ProtocolChecks | 50/50 PASS，退出码 0；项目编译零警告、零错误 | 租约/身份/授权/工具参数/清空输入、真实 127.0.0.1 TCP、有限帧、共享文件锁、并发制作事务；玩家和世界样本均为合成数据 |
| VisibilityChecks | 16/16 PASS | 有界射线、黑暗/遮挡/未知/镜头边界先拒绝、角落保护、DTO 有限拷贝和最大帧；纯几何与合成网格，没有加载游戏 |
| SkillChecks | 最新 4/4 PASS，先前 3/3 保留 | 冻结观测/失控/不可见目标失败边界，以及首批 6 木不制作、足够材料释放后再确认；合成环境 |
| B 成功前普通整体构建 | Host/Bridge/Controller 零警告、零错误（15:49 前） | 这一版已实际完成 B；后续暂停/保存修复需另行构建验证 |
| 新版首次直接入口 | 基本 A 实机通过 | run 20261004T153146Z-cc9dd5a0 的首次显式授权、65 动作与实际结果；活动安全项仍待 |
| B 完整任务 | 新版实机通过 | 一键 148 动作，实际木材/产物/世界放置变化通过；暂停与保存持久化不并入任务成绩 |

协议日志索引为 `work/terraria-runtime/logs/b-transaction-protocol-20261004T151620Z.log`，最初 3 项技能日志为 skill-checks-20261004T152155Z.log；最新 4 项回归单独记录。原始内容只本地保留，不发布凭据、原存档或私有绝对路径。50/16/4 合计 70 项属于代码检查，不称 70 项游戏成绩。

此代码轮之后已经实测 Home/End、正常保存重进、人工接管与紧急停止，见后文。短测后暂停复用世界是用户操作要求；配置已开、短按 Esc/AltTab 不能代替暂停证明。暂停锁停，继续须新明确许可，不自动 rearm。首次入口须 60 秒内进专用档，不先独立 observe 消费机会。

## 新版首次直接 A：基本闭环实机通过

run `20261004T153146Z-cc9dd5a0` 的 `result-stage-a-153330.json` 为 success=true，controlAuthorization=initial_explicit_start，actions=65，elapsedMs=4345。游戏与人物/世界保持专用隔离新档，首次授权没有 OS 许可热键；进入存档仍属于人工准备。

| 技能 | 新版实际结果 |
| --- | --- |
| 向右走 | actualRightObserved=true、X 增加 100.71094 像素、elapsedMs=1487 |
| 停止 | 实际 flags 清空、VX=0 |
| 跳跃 | 实际跳起 78.808105 像素 |
| 再次停止 | 实际 flags 清空、VX=0 |

桥接独立尾部确认 neutral/disconnected；15:33:39.9453892Z 正常 run_end，deaths=0、manualTakeovers=0。正常中立后断连不代替活动输入断连验收。该轮没有录屏，证据为本地日志和任务结果；累计已知死亡仍 5 次、菜单无活动 Agent 的接管计数仍 1 次。

此前约 15:24:40 的启动仅到菜单/Credits并退出、无动作，不新增玩法失败。后续活动断连、TTL 和 B 结果如下。

## 新版活动断连：实机通过

run `20261004T153501Z-8a5e4cd0` 的 disconnect-test 结果为 success=true、controlAuthorization=initial_explicit_start、actions=9、elapsedMs=953。先用实际游戏样本确认右移，再关闭控制 socket，触发原因 disconnected，controlState=LatchedStop，actualInputsReleased=true。故障前确有运动，因此不同于历史 actions=0 的许可超时或中立后正常断连。

observedDelayUpperBoundMs=107，包含测试设计中的 100ms 等待以及重连采样开销，只能作为观察上界，不能称实际精确释放时间。15:37:15.5622980Z 正常 run_end，deaths=0、manualTakeovers=0；没有录屏，已知累计死亡与接管计数不变。后续 TTL 结果如下，其余安全项仍待。

## 新版活动 TTL：实机通过

run `20261004T153759Z-704e6aa8` 的 expiry-test 为 success=true、controlAuthorization=initial_explicit_start、actions=8、elapsedMs=1029。实际先右移，再停止续期，最终 reason=lease_expired、controlState=LatchedStop、actualInputsReleased=true。

observedDelayUpperBoundMs=198 是观察上界，包含测试等待和重连采样，不称精确输入释放延迟。15:39:23.6897886Z 正常 run_end，deaths=0、manualTakeovers=0；没有录屏，累计已知计数不变。人工接管和紧急停止新增模式仍待实机；暂停恢复、死亡、菜单/文本输入与切世界等也未全部验收，不能据此宣布完整 A 安全通过。

## B 首跑：砍树和拾取成功，制作材料不足

run `20261004T154141Z-64bd8b8e` 采用正常玩法规则，现档改列开发验收，106 条动作，约 6.82 秒。observe、discover_visible_tree、approach_tree、chop_tree、collect_wood 返回 success；合法树目标为 tile (2101,281)。砍树耗时 5.659 秒，真实斧头使用与 GoneTree 确认通过；拾取 691ms，以实际背包 Wood 从 0 增至 6 通过数量增加判据。

紧接着 craft 以 insufficient_materials_wood_10 失败，**没有调用正常制作、没有工作台产物或放置成绩**。采集过早结束未等到所需 10 木材。稍后截图热栏已有 35 木材和 Timber 成就，不倒推失败时满足材料。随后修订为木材增加且总数至少 10、释放输入后新鲜确认，新增离线回归 4/4，通过下一 run 完整任务。

15:42:57.5459977Z 正常应用 run_end，deaths=0、manualTakeovers=0，没有录像。该次只部分成功；后续加载又见相同树、Wood=0，树和木材变化未完整保留。Alt+F4 没有正常 SaveAndQuit 验收，不能把 run_end 或专用存档文件存在写成修改已保存；也不能由此排除游戏可能做过部分自动保存。

## B 完整任务：实机通过，暂停与保存未通过

run `20261004T154929Z-efb622b8` 使用 Start-Direct-Test.ps1 -Mode stage-b -DesktopAvailable 一键入口，任务 success=true、actions=148、totalElapsedMs=63329。总时间含约 54 秒启动/菜单就绪，技能执行时间单独如下：

| 技能 | 实际耗时与结果 |
| --- | --- |
| observe / discover / approach | 60 / 1 / 61ms；新鲜观测、合法树发现与接近通过 |
| chop | 6242ms；正常斧头使用后重新可见树根消失确认 |
| collect | 2415ms；Wood 从 0 增至 35，满足材料且释放后结果保留 |
| craft | 125ms；正常材料 Wood 35→25、工作台 0→1 |
| place | 182ms；工作台物品 1→0，世界完整工作台 tile (2101,281) |

截图出现世界工作台与 Benched 成就。正常斧头、拾取、正常配方材料消耗和完整放置均获游戏变化实证，最低交付的一条完整任务通过；没有直接刷物品或修改地形。该任务没有连续录像。

任务后短按 Esc 三次及 AltTab 等未成功暂停，角色继续受攻击、HP 曾降至 3，随后死亡 1 次。日志已核对正常应用 run_end=2026-10-04T15:54:17.2621743Z，deaths=1、manualTakeovers=0；Alt+F4 仅结束程序，**不称正常保存退出，也不声称修改已完整持久化**。下一 run 重进 Wood49 而原树 (2101,281) 仍在，仅可能部分自动保存，没有工作台世界持久化成绩。C/D 不推进。

## 正常暂停首次通过，人工接管尝试未通过

run `20261004T160530Z-744acb29` 中 Ctrl+Shift+Home 打开原版正常 Settings Menu，gamePaused=true、optionsOpen=true。连续日志确认 X=33739.2266、Y=4470、HP100、速度0、实际 Inputs 与 LeaseInputs 全部中立，ControlState=LatchedStop、reason=operator_pause_hotkey，CanArm/CanOperatorArm=false。正常暂停入口首次实机通过；这不替代暂停恢复后旧输入不复生验收。

本轮旧 manual-test 在1871ms因 local_movement_bound_exceeded 提前失败，故障前dx=13.675781像素、513ms；不算人工接管通过。反向阈值从±144改±64，硬界仍±160、30秒期限和禁止自动授权不变，留出正常减速空间，失败记录保留。

16:08:03.0960299Z，End 调正常 Close/ToggleInv 后 optionsOpen=false、inventoryOpen=false；实际 gamePaused=false、ControlState=LatchedStop、reason=operator_resume_hotkey、全部输入中立、canArm/canOperatorArm=false。恢复游戏不恢复授权。

同一 Host 新建控制连接并给出 Ctrl+Shift+Insert 新手势，第二次B为success=true、human_hotkey、146动作、28814ms（含等待授权）。接近1787ms、砍树6451ms、拾取366ms、制作122ms、放置122ms；Wood49→52→42，工作台0→1→0，世界完整工作台(2102,281)。任务后Home正常暂停；点击正常Save&Exit回主菜单，再结束程序加载修订版本。run_end=16:09:35.8937795Z，死亡0、接管0。

## 正常保存重进与活动人工/紧急停控通过

run `20261004T161025Z-de709aaf` 重进同专用档，合法过滤观测sequence638确认Wood94、完整工作台(2102,281)，画面也有工作台。数量包括上次任务后陆续落下并正常拾取的木材；不把任务中的52与重进94混作制作判据。该项证明正常保存后的世界修改保留，不为早期Alt+F4补造保存成绩。

manual-test：success=true、initial_explicit_start、323动作、20328ms；前置实际right、dx20.238281像素/514ms。普通移动键触发physical_window_key，状态Manual，actualInputsReleased=true；316样本、315续租、10次有界转向，停止后未重发授权。观测停止后的释放上界61ms包含采样，不是精确按键延迟。

同一Host恢复后给出新的人工授权，emergency-test：success=true、human_hotkey、308动作、44146ms（含等待授权）；前置实际right、dx71.74219像素/548ms。Ctrl+Shift+Backspace触发emergency_window_hotkey、LatchedStop、actualInputsReleased=true；299样本/续租、9次转向。1ms是收到停止样本后的观察上界，不能解释成按键到角色释放只用1ms。

随后Home正常暂停，gamePaused/optionsOpen=true、HP45、锁停，保持窗口供后续测试。此run无新死亡；日志manualTakeovers=3（普通接管1；紧急组合修饰键先引起Manual、再紧停，两次转换），尚未关闭、没有run_end。原UI边界只做代码核查；死亡、文本输入、活动切世界等仍未逐项实测。

## 连续录屏短片验证

连续 12 秒、1920×1080、30 fps、H.264 的无声窗口视频为 `outputs/terraria-evidence/game-20261004T045746Z-d1a83d70.mp4`。FFmpeg 编码、ffprobe 时长/流信息与整段解码通过；随后在 PotPlayer 实际播放并确认画面连续变帧，同名 `.verification.json` 的 `PlaybackVerified=true`。这段短片验证了录制和播放能力，没有验证 A 的游戏动作。

首次按窗口名 `Terraria` 录制失败，实际随机完整标题是 `Terraria: Red Dev Redemption`；改用完整标题后录制成功。首次打开播放器触发更新，后续播放验收已完成。

以下后续连续录像均已通过编码、元数据和完整解码检查，**尚未通过播放器播放验收**，也没有成功的 A 动作成绩：

| 视频 | 时长 | 内容与验收边界 |
| --- | --- | --- |
| `outputs/terraria-evidence/game-20261004T050846Z-0f359ed7.mp4` | 20 秒 | 世界列表 |
| `outputs/terraria-evidence/game-20261004T051007Z-16e7eca6.mp4` | 30 秒 | A 等待人工许可 |
| `outputs/terraria-evidence/game-20261004T051625Z-1b804301.mp4` | 45 秒 | 再次等待人工许可 |

下一次先验证正常暂停后的恢复与正常保存，并重进确认背包/工作台保留，再补紧急停止、人工接管、死亡、菜单/切世界等安全项；有限等待失败就结束该次尝试并实际暂停，不能让角色长期空站。

## 每次运行的记录模板

- 日期、run id、代码提交：
- 决策方式及模型费用情况：
- 档案类型：`main_challenge` 或 `combat_test`。
- 游戏版本、加载器/Core/桥接版本、启用模组：
- 人物与世界配置、备份清单位置：
- 任务与前置状态：
- 关键动作及参考观测：
- 背包、装备或世界的真实结果变化：
- 成功、失败、取消及原因：
- 超时、重试与恢复：
- 死亡次数、人工接管次数、停控延迟：
- 连续录像或截图路径、日志时间范围：
- 仅编译/协议测试的部分：
- 当前限制及下一步：
