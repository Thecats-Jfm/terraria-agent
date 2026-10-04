# Stage A 协议与输入租约

当前决策模式是本地规则控制。协议共享源码在 `src/Protocol/`，使用 C# 7.3 和系统 DataContractJsonSerializer，由 net48 游戏 bridge 与 net8 controller 直接 link；不依赖第三方协议包。协议检查是离线验证，不能认定游戏观察/动作通过。

## 连接与报文

本机通信传输由 Host/Bridge 实现；协议层不监听端口。每帧先写 4 字节无符号网络序长度，再写该长度的 UTF-8 JSON，JSON 至多 4096 字节。`Wire.ReadFrame` 在分配 payload 前拒绝零长/超过 4096 的长度，截断抛错，单帧读取总预算 3 秒；不能先无限制读入再交给 JsonCodec。

LocalBridgeServer 仅绑定 IPv4 `127.0.0.1` 随机端口，核查对端回环地址。初包必须是 `hello` 与 token，常量时间比较验证凭据；服务端生成全新 GUID sessionId，返回 hello/ok。后续报文支持 observe、arm、action、stop。仅一个认证控制 owner；第二连接返回 session_busy，不能替换首个连接。最多 8 个待处理连接、读写超时 3 秒。连接关闭/解析错误/响应写失败/服务器 Dispose 都释放 owner。token 不出现在日志/观测/错误回显中；认证失败也不返回自身观测。

请求 `AgentRequest` 的 type 使用 `hello`、`observe`、`arm`、`action`、`stop`。arm/action 带 sessionId、worldId、正数单调递增 sequence、引用最近合法观测的 observationSequence。action 包含 ttlMs（1–250）、left/right/jump 三个布尔值。left 和 right 不得同时为 true。stop 是认证当前 session 的优先安全操作，可忽略动作序号/观测引用。格式错误、未知 type、认证失败的连接由 transport 拒绝并关闭；如果该连接已是控制 owner，则调用 Disconnect 释放。认证连接里的错 sessionId 报文只拒绝，不允许旧会话身份干扰新 owner。

`AgentReply` 含 type、status、reason、sessionId、worldId、sequence、protocolVersion，必要时包含 `OwnObservation`。观测仅包括自身位置/速度/血量/dead、menu/textInput、安全状态/reason、gameTick、bridge 单调时间戳。`inputs` 保留游戏线程采样的实际 player controls；`leaseInputs` 是 Gate 当前希望执行的输入，两者必须区分，leaseInputs/命令接受不能当成实际停止或行动验收。Stage A 不提供任何世界 tile、隐藏矿物、NPC 或背包数据；后续合法范围扩展必须由 bridge 游戏线程过滤后再序列化。

## LeaseGate 接口

```csharp
LeaseGate(Func<long> nowMs = null); // 默认 Stopwatch 单调毫秒，测试可注入时钟
bool OpenSession(string sessionId, out string reason);
void Disconnect(string sessionId, string reason = "disconnected");
void UpdateContext(string worldId, bool dead, bool menu, bool textInput);
void RecordObservation(long sequence, long observedAtMs);
bool PermitNextArm(string sessionId, string worldId, long controlEpoch, long observedAtMs);
// 仅由本机人工手势调用；原子验证 owner、世界、撤销版本及 500ms 时限
bool ExplicitArm(AgentRequest request, out string reason);
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

transport 在收到完整 action frame 后、反序列化前记录 `MonotonicClock.NowMs`，原值传给 TryApplyAction。当前后台通信线程直接调用线程安全 Gate 保存合法输入，不读取游戏对象、不需要主线程任务队列。过期时间始终是 `receivedAtMs + ttlMs`；未来若增加队列，也不能在出队时刷新收到时间。bridge 每次游戏输入更新前调用 PollInputs，deadline 当刻释放所有 Agent 输入并进入 LatchedStop。Snapshot 也检查超时。游戏暂停/不更新时无法立即运行代码；恢复第一帧必须先检查租约，再执行输入。

断连、租约过期、死亡、切换世界、菜单、文本输入、紧急 stop、异常进入 LatchedStop；普通 ManualTakeover 进入 Manual 并清空 Agent 输入。两者均撤销 arm 许可并推进 epoch，重连/恢复/任意 action 都不能自动重启；即使手势尚未变成许可，也不能在停止或恢复后复用。只有新的 Ctrl+Shift+Insert 人工手势与有效 arm 请求才能接管。Ctrl+Shift+Backspace 为紧急停止；窗口人工键会立即撤销 Agent 或尚未消费的许可，非许可键也取消待释放的手势。游戏线程的键盘与实际移动/跳跃 flags 检查覆盖仍待消费的许可，包括游戏手柄的移动/跳跃输入。异常和失焦按同样规则停止。

工作区 `STOP` 文件由 watchdog 处理；解除只发生在原子校验通过的人工许可之后，无效/陈旧手势不删除标记。`STOP.lock` 使用本机 FileShare.None 串行化 watchdog 的读取/撤销、热键校验/解除和 `Stop-Agent.ps1` 的写入，避免写入落在比较与删除之间。bridge 一次取锁失败即撤销，不在游戏线程等待；脚本最多等待 2 秒，失败明确报告未送达且不绕过锁写入。同一站立 STOP 不每 50ms 重复推进 epoch；检测到新的写入或活动许可/Agent 状态会再次撤销。有效手势只能解除自己此前观察到的标记；解除之后的写入会留下新的 STOP。若 watchdog 与授予并发撤销，保持停止，不自动补发许可。协议/文件锁检查仍不证明真实角色的停止延迟。

当前 owner 的重复/乱序 sequence、错误 world、过期/未知 observation、非法 TTL、相反移动同时为 true 都拒绝并安全停止。不同/旧 sessionId 的 action、stop、disconnect 只拒绝/忽略，不因旧会话反复报文而停止新会话；新会话不会继承旧输入。身份绑定依赖 transport 先验证其 connection owner，不能将报文内的任意 sessionId 直接当成 OpenSession 依据。

Gate 所有状态方法有锁，返回 input/snapshot 是副本。bridge 写入实际 player controls 的代码仍需确保发生在游戏线程，手动状态不覆盖真人输入；退出 Agent 的一帧应释放此前由 Agent 设置的布尔输入。状态机返回接受与拒绝只是接口结果，必须用真实位置变化/跳跃轨迹验收 A。

## 离线验证

`dotnet run --project tests/ProtocolChecks/ProtocolChecks.csproj` 运行无测试框架/NuGet 的控制台检查：精确超时边界、续租/neutral、重复乱序、安全上下文、世界/会话隔离、断连/重新许可、真人接管、排队过期、非法输入、过期观测、JSON 大小限制、人工许可可见/消费/撤销及并发晚到动作；新增手势 owner/world/500ms/单次绑定、停止和重连后旧手势拒绝、许可授出前不安全上下文撤销、未消费许可的人工撤销、epoch 在普通更新/续租时稳定、并发停止不可被旧手势恢复及真实临时文件共享锁检查。另有网络序分帧/截断，以及真实 localhost socket 的凭据、唯一 owner、断连/重连、畸形帧和 Dispose 检查。网络观测专门验证实际输入不会被拟执行输入覆盖。共 29 项已运行通过；受限沙箱网络失败后，已编译程序在受审环境完成回环测试。Bridge Release 与 ProtocolChecks 均编译为零警告、零错误；Stop-Agent 通过 Windows PowerShell 5 语法解析。没有访问公网、没有启动 Terraria；新增修复尚未部署实机。测试使用合成自身观测与临时 STOP 文件，只验证协议、传输和文件共享语义；真实游戏验收单独记录。
