# 启动、控制与验收

隔离 Terraria 1.4.5.8 的基本动作、首次直接 A、活动断连、TTL、人工接管和紧急停止已实机通过；B 两次完成真实砍树→拾取→正常制作→完整工作台放置。正常暂停/恢复、Save&Exit 后重进保留工作台也通过，**死亡、文本输入、切世界等完整安全矩阵仍待**。工作区保持 outputs/terraria-agent 与 work/terraria-runtime 布局，脚本从自身位置解析路径。

## 只开发代码时

桌面仍被使用或只做代码开发时，从项目目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -CodeOnly
```

该模式只编译到 `work/terraria-runtime/build-code-only/`，不启动游戏、不部署、不替换隔离游戏中的桥接，也不替换正常 Controller 输出。已准备的隔离副本和固定依赖仍需存在。此输出不自动成为下一次游戏加载的版本。

## A 的首次直接启动

首次启动或确需更新 Bridge/Host 时，确认隔离游戏关闭，再执行普通 Build：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
```

普通 Build 检查游戏关闭并构建到正常输出。随后双击 `scripts/Start-A-Direct.cmd`，或执行下面的启动型入口。**同一版本的后续短测复用世界，不为每个技能退出重载**；已运行游戏不要再次调用启动型入口。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Direct-Test.ps1 -Mode stage-a -DesktopAvailable
```

入口启动隔离 Host，并连接同一新 run 的规则控制器。**控制器连接后的就绪等待预算为 60 秒**：用鼠标选择已有 Classic 人物 **TA1** 和 Small、Classic、Corruption 世界 **The Annoyed Frontier**，进入世界并保持游戏聚焦、关闭菜单/文本输入/暂停。不要重复创建存档。菜单里出现原用户人物或世界时停止实验，不能加载它们。选档和进入属于人工准备。

入口显式开启 Host 的 `--allow-initial-controller-start`，对应 `Start-Game.ps1 -AllowInitialControllerStart`；Controller 使用 `-Arm -InitialStart`。控制器先等待真实同世界的 sequence、gameTick、monotonicMs 推进及 `CanOperatorArm=true`，然后只发送一次 `operator_arm`。首次授权无需许可热键。启动脚本不模拟按键，也不重连重试授权。

**不要先启动独立 observe 连接再断开，然后使用初始授权。** 断连会永久消耗本 run 的首次机会；直接动作控制器已经自行观察和检查就绪。超时、失败、停止、断连、死亡、世界变化或人工接管不会自动恢复机会。普通键盘输入也可能撤销初始资格，等待时避免用 Enter/Esc 代替鼠标选档。未获授权时没有动作成绩。

现有 `STOP` 会阻止首次直接启动；启动器和 Host 都检查，Bridge 在 `STOP.lock` 内再次检查并授予。`operator_arm` 不删除或绕过 STOP。需要从既有停止状态恢复时，用下文的一次人工许可；脚本不会清除标记或重启游戏来获取机会。

新版直接 A 基本流程已实机通过：65 条动作、右移 100.71094 像素、跳起 78.808105 像素、两次停止均实际输入清空且 VX=0。退出前独立日志确认 neutral/disconnected，正常退出无死亡或接管。这只证明基本流程，不能替代活动断连、TTL 或 B 成绩；结果见 VALIDATION。

## 活动输入的断连与有效期

下面是全新游戏 run 的入口，适用于首次启动或确需重载；它们不能连接已有暂停游戏：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Direct-Test.ps1 -Mode disconnect-test -DesktopAvailable
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Direct-Test.ps1 -Mode expiry-test -DesktopAvailable
```

复用已加载的世界时，使用 Run-Controller.ps1 的对应 Mode、`-Arm`，不带 `-InitialStart`，按下文重新授予一次人工许可。每个控制器有独立连接和结果文件，同一个游戏 run 可以包含多项任务。测试先确认新游戏观测中实际 Right=true 与 X 位移，再制造 TCP 断连或停止续期；用同世界、存活、故障后推进的实际样本与匹配原因验收。leaseInputs 与回执不能证明执行或释放，精确延迟需桥接单调时间事件复核。

活动断连与 TTL 已实机通过，均先有真实右移，再触发 socket 关闭或停止续期，最终对应 disconnected / lease_expired、LatchedStop、actualInputsReleased=true。107ms 与 198ms 是含测试等待及重连采样的观察上界，不能称精确释放延迟。manual-test 和 emergency-test 后续也通过：分别在真实右移后触发 physical_window_key→Manual、emergency_window_hotkey→LatchedStop，实际输入均释放；61ms 与 1ms 仍只是采样上界，尤其1ms不是按键发送至停止的延迟。手柄接管、菜单/文本输入、死亡复活及切世界仍需单独记录。

## B 规则代码预览

B 默认关闭，必须给 Host 显式 `--enable-stage-b`，对应 `Start-Game.ps1 -EnableStageB`。首次切换为 B 需要正常退出并按此标志重新启动；后续 B 短测在同一世界暂停/继续，不为规则控制器的小修改反复加载。新增 `scripts/Start-B-Direct.cmd` 可双击新启动，其下方 PowerShell 入口已实机完成 B，cmd 包装本身尚未独立实机验收：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Direct-Test.ps1 -Mode stage-b -DesktopAvailable
```

此入口同时开启一次初始授权和 B。规则任务依次发现合法可见树、接近、选正常斧头、砍树、拾取、正常制作并放置工作台。接近有 15 秒预算及一次卡住跳跃恢复；砍树最多 30 秒、8 秒无背包木材进展即失败；拾取最多 15 秒；制作只发一次请求，资格和结果分别最多等待 3 秒；放置最多 5 秒、最多两个位置。丢失控制权、死亡、低血量、陈旧观测或不安全上下文时终止，不自动 rearm。

已开启 B 的世界中复测用 `Run-Controller.ps1 -Mode stage-b -Arm -PermissionWaitSeconds 60`，不带 `-InitialStart`。控制器等待时解除暂停并给一次新人工许可；规则动作继续由 Bridge 直接执行。当前一次许可仍使用 Ctrl+Shift+Insert，不是人工逐帧操纵。

砍树必须有新样本中的实际斧头使用及此前观察树根再次可见后的消失确认；候选列表缺项或树进入黑暗/遮挡不算砍倒。拾取看实际木材增加；制作看正常材料减少 10 木材且工作台增加 1；放置看完整世界工作台出现且背包减少 1。桥接走原版工具、拾取和配方流程，不直接改背包、物块、伤害或时间。制作提交受 STOP 文件锁与当前控制权事务保护，同一会话/动作序号只制作一次。

B 仅支持保守的单人普通重力环境，SmartCursor 或手柄输入会拒绝，脚本不改设置。首跑拾取 0→6 后过早结束、制作不足 10 木材失败；修订后等待至少 10 且在释放输入的新鲜样本中确认。run 20261004T154929Z-efb622b8 一键 B 完整通过：Wood0→35→25、工作台0→1→0、完整世界工作台(2101,281)。后续同 Host 重新人工许可第二次 B 也通过：Wood49→52→42、工作台0→1→0、世界(2102,281)，正常 Save&Exit 后新 run 重进确认工作台保留。C/D 未实现。

## 观察、停止与人工恢复

每次短测结束先停止控制器，再**观察确认正常暂停实际生效**。B 成功后短按 Esc 三次及 AltTab 未成功暂停，角色随后死亡 1 次；已有配置开启、发过按键或失焦都不能当暂停成功。**Ctrl+Shift+Home** 打开正常 Settings Menu；run 20261004T160530Z-744acb29 已确认 gamePaused/optionsOpen=true、角色位置稳定、HP100、速度0，实际和租约输入全部清空、LatchedStop/operator_pause_hotkey，CanArm/CanOperatorArm=false。最新版本在原背包、地图或箱子打开时拒绝 Home，应先正常关闭这些界面。暂停后核对返回状态和实际画面，不能让角色长期空站。

**Ctrl+Shift+End** 正常恢复入口已在同 run 实机通过：16:08:03Z normal_options_close、optionsOpen/inventoryOpen/gamePaused=false，仍 LatchedStop/operator_resume_hotkey、输入中立、CanArm=false。恢复、重连或重启控制器不自动启用，初始 operator 票每 run 一次，后续须新的明确许可。该 run 早期 manual-test 在1871ms因 local_movement_bound_exceeded 失败，后续独立 run 已通过，历史失败保留。

同一 Host 用一次新 human_hotkey 许可完成第二次 B：146动作、总耗时28814ms含授权等待。任务后 Home 正常暂停，再从正常菜单 Save&Exit 返回主菜单，16:09:35.8937795Z 应用 run_end 为死亡0、接管0。此过程复用世界，没有重启来重新获取初始票。

只有 Bridge/Host 更新需重载时才退出重启；控制器小修在其停止后刷新输出，不替换运行中 Bridge。普通 Build 拒绝游戏仍运行，CodeOnly 不自动更新运行输出。**应用 run_end、正常 SaveAndQuit、重进持久化分别验收**。历史 Alt+F4 后树和木材未完整保留；仅可能部分自动保存。后来正常 Save&Exit 后，run20261004T161025Z-de709aaf 的过滤观测seq638确认Wood94、完整工作台(2102,281)，截图也有工作台，持久化通过。Alt+F4 只能在正常保存返回菜单后用于关闭程序，不能代替 SaveAndQuit。

最新 run 完成活动人工接管和紧急停止，无死亡，manualTakeovers=3（普通接管1，紧急组合修饰键引发Manual与Emergency共2），目前仍正常暂停、gamePaused/optionsOpen=true、HP45；尚无本 run 应用退出结果。后续继续复用暂停世界，重新显式授予许可。

不使用首次直连机会时，可只观察：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-Controller.ps1 -Mode observe -Seconds 20
```

观察通过要求非空真实样本的 sequence、gameTick、monotonicMs 同时推进，末尾仍新鲜。空数据或旧缓存不能通过。观察模式不接管；连接结束也会消耗尚未使用的初始机会。

人工恢复时运行 `Run-Controller.ps1 -Mode stage-a -Arm`（不带 `-InitialStart`），在正常游戏中按 **Ctrl+Shift+Insert** 并完整释放，授予一次许可。等待默认 15 秒，`-PermissionWaitSeconds` 可设 1–60 秒；到期即停止并断开，不自动重试 arm。消息过滤器仅观察自有游戏 HWND，不消费消息或安装全局键盘钩子；手势核对会话、世界、epoch、500ms 时限和安全上下文。

游戏内 **Ctrl+Shift+Backspace** 紧急停止，普通物理键盘输入或手柄移动/跳跃会交还人工控制。游戏外双击 `scripts/Stop-Agent.cmd`，或执行 Stop-Agent.ps1。脚本在 `STOP.lock` 内写入停止标记，最多等 2 秒；取锁失败会明确报告未送达，不绕过锁。输入在下一次可执行更新释放；暂停期间按真实时间过期，恢复后不应重新生效。

停止保留人工控制，不结束游戏；重连、复活或再次启动控制器不能自动抢回。新的有效人工许可可在 STOP 文件锁下解除自己已经观察的标记，旧/无效手势不能删除它。桥接异常会停控，检查原因后决定是否重启实验游戏。

## 隔离与证据

启动只读复核原存档集合、字节与备份；仅时间戳差异可记录，文件增减或字节变化会阻止。部署只替换哈希匹配的自有文件，原安装不写入。Host 固定独立 savedirectory 并检查 EXE、资源、Steam App ID，Bridge 在菜单前安装云和路径保护。专用档创建与可重进不等于修改已保存；本轮已另行验证正常 SaveAndQuit 后重进保留工作台。Steam 账号授权保留。

每 run 日志位于工作区 `work/terraria-runtime/logs/<run>/`，current-run.txt 指向当前 run。连接配置在本地 ipc 目录，以当前用户权限保护并在正常停止时删除；凭据、原始日志、存档及机器私有路径不加入公开仓库。

近期测试按用户要求不特别录屏。需要录屏时用实际窗口标题运行 Record-Game.ps1，显式给 `-DesktopAvailable`。只录指定游戏窗口，不回退整个桌面；文件在 `outputs/terraria-evidence/`。编码/完整解码之后，还必须在播放器中确认连续游戏画面。截图不称作连续录像。

结果文件记录任务、动作、结果、失败原因、死亡与接管次数。游戏通过、仅编译、协议/几何/技能合成测试分别记录在 [VALIDATION.md](VALIDATION.md)。
