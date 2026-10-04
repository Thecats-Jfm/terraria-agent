# 本机协议与输入租约

当前决策模式是本地规则控制。协议共享源码在 `src/Protocol/`，使用 C# 7.3 和系统 DataContractJsonSerializer，由 net48 游戏 bridge 与 net8 controller 直接 link；不依赖第三方协议包。协议检查是离线验证，不能认定游戏观察/动作通过。

当前代码协议为 v2。`operator_arm` 已实际完成 A 动作，B 已完成普通树木采集、正常制作与放置工作台。断连、过期、普通键接管和活动紧急热键已分别实测；其余安全矩阵与后续玩法仍须验收，详见 [VALIDATION](VALIDATION.md)。A 默认仅发布自身状态且拒绝全部 B 动作字段。

## 一次性直接启动与 B 最小任务

Host 只有显式传入 `--allow-initial-controller-start` 才允许 `operator_arm`。Controller 同时要求 `--arm --initial-start`，等待同世界、存活且持续推进的新鲜观测之后只发一次授权请求。首次认证 owner 请求无论成功或失败均消费资格；断连、停止、人工接管、死亡或离开世界永久撤销，重连不恢复。单纯初始菜单等待或未取得控制前失焦只影响就绪状态，不会自行启用。

Startup 在 `STOP.lock` 文件独占锁内检查当前 STOP 并调用 Gate 授权，和停止脚本使用同一把锁。存在 STOP 或锁不可获得就拒绝；直接入口不删除 STOP。传输回调只使用线程安全 Gate 和停止文件，绝不访问游戏对象。旧热键授权保留为人工重新启用方式。

Host 另有显式 `--enable-stage-b`，默认关闭。B action 扩展 `useItem`、`selectedSlot`、`aimTileX/Y`、`craftWorkBench`；选择槽范围 0–49，-1 表示不选择，瞄准两坐标必须同时提供。缺省反序列化设为 -1，避免省略字段误选第 0 槽。使用物品须提供正常槽和瞄准；制作请求不能同时移动、跳跃或使用物品。所有字段仍受同一序号、新鲜观测和最长 250ms 租约控制；停止会清空工具使用、选择、瞄准与制作意图。

启用 B 才发布 `gameplay`：仅自身木材/工作台计数、相关槽、空槽和当前可见候选，每种候选至多 4 个。候选先限定实际 camera viewport、亮度和保守遮挡检查，再检查普通树根、完整工作台或两格合法放置空间/支撑；不会发布全图、矿物、NPC、墙或液体。传输复制 DTO，不能保留 Tile、Item 或可变数组别名。帧上限仍为 4096 字节。

执行 B 在游戏线程内进行：`selectedItemState.Select` 选槽，内部 mouse 坐标只用于正常瞄准，`controlUseItem` 走原版 ItemCheck 的斧头和放置流程。制作只允许当前可制作列表内的普通 10 Wood → 1 WorkBench 配方，重新核对自身材料、正常环境与空间后调用 `CraftingRequests.CraftItem(recipe, 1, true)`；按 session/动作序号去重。不调用 KillTile、PlaceTile 或独立物品授予，也不直接写背包 stack。控制器必须观察木材与工作台的实际增减、目标树变化和完整世界工作台，不能把 action/ok 当成功。

后文保留 A 的原有协议说明；新增 `operator_arm` 与可选 B 字段以本节及源码为准。

## 连接与报文

本机通信传输由 Host/Bridge 实现；协议层不监听端口。每帧先写 4 字节无符号网络序长度，再写该长度的 UTF-8 JSON，JSON 至多 4096 字节。`Wire.ReadFrame` 在分配 payload 前拒绝零长/超过 4096 的长度，截断抛错，单帧读取总预算 3 秒；不能先无限制读入再交给 JsonCodec。

LocalBridgeServer 仅绑定 IPv4 `127.0.0.1` 随机端口，核查对端回环地址。初包必须是 `hello` 与 token，常量时间比较验证凭据；服务端生成全新 GUID sessionId，返回 hello/ok。后续报文支持 observe、arm、action、stop。仅一个认证控制 owner；第二连接返回 session_busy，不能替换首个连接。最多 8 个待处理连接、读写超时 3 秒。连接关闭/解析错误/响应写失败/服务器 Dispose 都释放 owner。token 不出现在日志/观测/错误回显中；认证失败也不返回自身观测。

请求 `AgentRequest` 的 type 使用 `hello`、`observe`、`arm`、`action`、`stop`。arm/action 带 sessionId、worldId、正数单调递增 sequence、引用最近合法观测的 observationSequence。action 包含 ttlMs（1–250）、left/right/jump 三个布尔值。left 和 right 不得同时为 true。stop 是认证当前 session 的优先安全操作，可忽略动作序号/观测引用。格式错误、未知 type、认证失败的连接由 transport 拒绝并关闭；如果该连接已是控制 owner，则调用 Disconnect 释放。认证连接里的错 sessionId 报文只拒绝，不允许旧会话身份干扰新 owner。

`AgentReply` 含 type、status、reason、sessionId、worldId、sequence、protocolVersion，必要时包含 `OwnObservation`。观测仅包括自身位置/速度/血量/dead、menu/textInput、安全状态/reason、gameTick、bridge 单调时间戳，以及自身界面 `gamePaused/optionsOpen`。`inputs` 保留游戏线程采样的实际 player controls；`leaseInputs` 是 Gate 当前希望执行的输入，两者必须区分，leaseInputs/命令接受不能当成实际停止或行动验收。Stage A 不提供任何世界 tile、隐藏矿物、NPC 或背包数据；后续合法范围扩展必须由 bridge 游戏线程过滤后再序列化。

## LeaseGate 接口

```csharp
LeaseGate(Func<long> nowMs = null, bool allowInitialOperatorArm = false, bool allowGameplayActions = false);
bool OpenSession(string sessionId, out string reason);
void Disconnect(string sessionId, string reason = "disconnected");
void UpdateContext(string worldId, bool dead, bool menu, bool textInput);
void RecordObservation(long sequence, long observedAtMs);
bool PermitNextArm(string sessionId, string worldId, long controlEpoch, long observedAtMs);
// 仅由本机人工手势调用；原子验证 owner、世界、撤销版本及 500ms 时限
bool ExplicitArm(AgentRequest request, out string reason);
bool ExplicitOperatorArm(AgentRequest request, out string reason);
bool TryApplyAction(AgentRequest request, long receivedAtMs, out string reason);
void Stop(string sessionId, string reason = "explicit_stop");
void EmergencyStop(string reason = "emergency_stop");
void ManualTakeover(string reason = "manual_takeover");
InputState PollInputs();
LeaseSnapshot Snapshot();
```

游戏线程先调用 UpdateContext，再发布合法 own-player observation 并 RecordObservation。arm/action 所引用的 observation 必须由 bridge 发布且年龄不超过 1000 ms；至多保留 64 个引用。切换世界清空观测历史。网络线程只接触过滤后的复制数据与 LeaseGate，不读取 Terraria 对象。

默认状态 Manual，输入全 false。OpenSession 只建立已认证 owner，不能抢占已有 session、不能复用上次 identity，不能接管控制。Ctrl+Shift+Insert 在正常世界/存活/不输入文本、有 session 时经游戏线程条件检查调用 PermitNextArm；`LeaseSnapshot.ArmPermitted`/观测 `canArm` 反映当前一次性许可。规则模式 Controller 仍要求显式 `--arm`，人工许可等待默认 15000ms，`--permission-wait-ms` 可设 1000–60000ms；`Run-Controller.ps1 -PermissionWaitSeconds` 对应 1–60 秒、默认 15 秒。到期仍调用 Stop 并关闭连接，不重试或自动 Arm；每次网络 I/O 另有 3 秒超时。ExplicitArm 消费许可并进入 Agent，同时启动 250 ms 初始空输入租约；stop/手动接管/断连等撤销许可。动作持续控制应以低于 250 ms 的频率续租。

真实游戏窗口的 KeyDown 只记录不可变信号；只有实际 KeyUp 才重置 Insert/Backspace 的按住状态，丢失 KeyUp 时保守拒绝。游戏线程在整组按键释放、500ms 内且正常存活/同世界/有焦点时申请许可；信号绑定按下时的已连接 sessionId、worldId 和 `LeaseSnapshot.ControlEpoch`。没有 session 的手势不得用于稍后建立的连接。Gate 在同一把锁下再次校验 owner、世界、epoch 和时间再授予，一次手势不能重复授权。epoch 仅因连接建立、许可或控制权变更、撤销以及首次进入不安全上下文/切世界推进；同一安全上下文的观测、动作续租和 neutral 动作不推进。

观察模式必须收到非 null、有实际正数 sequence/gameTick 的观测，并至少看到一次 sequence、gameTick、monotonicMs 同时推进；结束时距离最后推进的本地时间不得超过 1000ms。空数据或始终重复的缓存不能得到 `observation_complete`，已经推进后停滞则记录失败。该判据只确认合法自身观测流推进，不能证明人物移动、停止或跳跃成功。

transport 在收到完整 action frame 后、反序列化前记录 `MonotonicClock.NowMs`，原值传给 TryApplyAction。当前后台通信线程直接调用线程安全 Gate 保存合法输入，不读取游戏对象、不需要主线程任务队列。过期时间始终是 `receivedAtMs + ttlMs`；未来若增加队列，也不能在出队时刷新收到时间。bridge 每次游戏输入更新前调用 PollInputs，deadline 当刻释放所有 Agent 输入并进入 LatchedStop。Snapshot 也检查超时。游戏完全停止执行更新时无法立即写玩家字段；恢复第一帧必须先检查租约，再执行输入。普通选项菜单暂停期间，外层更新与桥接观测仍可继续，但玩家位置、血量和世界实体由原版暂停机制停止更新。

断连、租约过期、死亡、切换世界、菜单、文本输入、紧急 stop、异常进入 LatchedStop；普通 ManualTakeover 进入 Manual 并清空 Agent 输入。两者均撤销 arm 许可并推进 epoch，重连/恢复/任意 action 都不能自动重启；即使手势尚未变成许可，也不能在停止或恢复后复用。只有新的 Ctrl+Shift+Insert 人工手势与有效 arm 请求才能接管。Ctrl+Shift+Backspace 为紧急停止；窗口人工键会立即撤销 Agent 或尚未消费的许可，非许可键也取消待释放的手势。游戏线程的键盘与实际移动/跳跃 flags 检查覆盖仍待消费的许可，包括游戏手柄的移动/跳跃输入。异常和失焦按同样规则停止。

工作区 `STOP` 文件由 watchdog 处理；解除只发生在原子校验通过的人工许可之后，无效/陈旧手势不删除标记。`STOP.lock` 使用本机 FileShare.None 串行化 watchdog 的读取/撤销、热键校验/解除和 `Stop-Agent.ps1` 的写入，避免写入落在比较与删除之间。bridge 一次取锁失败即撤销，不在游戏线程等待；脚本最多等待 2 秒，失败明确报告未送达且不绕过锁写入。同一站立 STOP 不每 50ms 重复推进 epoch；检测到新的写入或活动许可/Agent 状态会再次撤销。有效手势只能解除自己此前观察到的标记；解除之后的写入会留下新的 STOP。若 watchdog 与授予并发撤销，保持停止，不自动补发许可。协议/文件锁检查仍不证明真实角色的停止延迟。

当前 owner 的重复/乱序 sequence、错误 world、过期/未知 observation、非法 TTL、相反移动同时为 true 都拒绝并安全停止。不同/旧 sessionId 的 action、stop、disconnect 只拒绝/忽略，不因旧会话反复报文而停止新会话；新会话不会继承旧输入。身份绑定依赖 transport 先验证其 connection owner，不能将报文内的任意 sessionId 直接当成 OpenSession 依据。

Gate 所有状态方法有锁，返回 input/snapshot 是副本。bridge 写入实际 player controls 的代码仍需确保发生在游戏线程，手动状态不覆盖真人输入；退出 Agent 的一帧应释放此前由 Agent 设置的布尔输入。状态机返回接受与拒绝只是接口结果，必须用真实位置变化/跳跃轨迹验收 A。

## 离线验证

`dotnet run --project tests/ProtocolChecks/ProtocolChecks.csproj` 已通过 50 项无测试框架/NuGet 的离线检查，包含租约边界、序号、身份、观测新鲜度、并发停止、单次人工/初始授权、真实 localhost 传输、工具与制作事务、停止文件锁及 DTO 深复制。最新 JSON 回环同时确认暂停字段保留；四种最多各 4 个候选的合成观测为 1719 字节，小于 4096 字节上限。另有 VisibilityChecks 16 项和 SkillChecks 4 项通过。合成检查不启动 Terraria，也不证明真实游戏行为；实机结果在 VALIDATION 中单独记录。

Ctrl+Shift+Home 先撤销授权并释放 Agent 输入，再在游戏线程打开正常选项菜单暂停。信号仅来自自身 HWND，500ms 期限内重新核对同世界和安全 UI；原背包/地图/箱子已打开时保留人类界面并拒绝打开。Ctrl+Shift+End 仅关闭本入口在同世界打开的菜单并正常关闭它新打开的背包，仍保持停控。恢复游戏不代表授权，下一轮必须建立控制连接并给出新的 Ctrl+Shift+Insert 手势。该入口不在网络 action 中提供，也不直接写游戏暂停或时间字段。
