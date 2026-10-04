# A 阶段启动与验收

已实机启动隔离 Terraria、创建并进入自有新人物与世界，自身观测已通过；完整的移动、停止、跳跃动作闭环仍未通过。以下流程继续用于复测，不能把脚本存在、编译成功或单项成功视为整个 A 已验收。保持工作区 `outputs/terraria-agent` 与 `work/terraria-runtime` 的布局。

## 启动游戏

先运行 Prepare 和 Build，步骤见 BUILD。确认桌面已经交给游戏测试后，双击 `scripts/Start-Game.cmd`，或从项目目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-Game.ps1 -DesktopAvailable
```

启动先只读复核原存档当前集合、字节及实际备份是否仍一致，并记录修改时间变化。相对历史基线仅时间戳变化可记录而不拒绝（本轮 1 条）；本次读取前后仍须稳定，任何文件集新增/缺失或字节变化都会阻止启动，需要保留旧备份并另建经核实的新备份。随后把自有 Host、桥接、Host 配置与固定 Harmony 四个文件部署到隔离游戏目录，再打开游戏，输入默认交给人类。XNA 使用入口程序集目录定位 Content，所以不能只改变工作目录。部署只替换清单内且哈希匹配的上一版自有文件，不触碰原安装。

Host 固定 `-savedirectory`、校验游戏 EXE 哈希；桥接在进入菜单前安装云读写拦截与本地保存路径保护。任何签名或路径不符都停止启动；运行中保存根异常会终止实验游戏进程，避免继续用错误路径保存。新档创建与实际落盘必须单独检查。

Host 还检查隔离副本内现有 `steam_appid.txt` 内容为 `105600`，并在该目录运行，避免 Steam 的正常重启分支转而打开原安装。此行为依据 [Steam 官方 API 文档](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_RestartAppIfNecessary)，原游戏的 Steam 初始化和账号授权检查仍保留，没有绕过授权。Steam 必须在同一用户和权限级别下运行；隔离游戏已经实际到达菜单并进入自有新档。

隔离目录已创建 Classic 人物 **TA1** 与小型 Classic、Corruption 世界 **The Annoyed Frontier**（生成时种子框留空）。下一次选择这两个现有自有新档，勿重复创建。原用户角色和世界不应出现在列表；出现时停止实验，不能选中它们。创建、进入和退出世界的人工操作需记录为人工准备。

## 观察和测试动作

仅观察，不接管输入：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-Controller.ps1 -Mode observe -Seconds 20
```

观察完成要求真实非空观测的 sequence、gameTick、monotonicMs 至少同时推进一次，末尾样本仍有效且最近 1 秒内有推进。空数据、旧缓存或停滞不能通过；观察模式不证明角色动作成功。

进行一次基础流程：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Run-Controller.ps1 -Mode stage-a -Arm
```

当前控制器为规则模式，连接后人工许可等待默认 **15 秒**，可用 `-PermissionWaitSeconds 10` 调整（范围 1–60 秒）；直接调用 Controller 时对应 `--permission-wait-ms`，范围 1000–60000ms、默认 15000ms。到期仍发送 Stop 并断开，不自动 Arm；通信 I/O 另有独立超时。在游戏内按 **Ctrl+Shift+Insert**，完整释放组合键后才授予一次许可；控制器只发一次 arm 请求，随后连续处理右移、停止和跳跃。暂停、失焦、聊天、死亡、世界变化和物理移动输入会停止控制。没有自动重新授权或无限重试。

把 `-Mode` 换成 `disconnect-test` 或 `expiry-test` 可分别测试断连、停止续期。每次都需要新的人工热键许可。测试先确认实际右移，再制造故障，最后只观察停止；只有同一世界、存活状态、触发后的新游戏样本与对应停止原因满足，才记该项成功。精确释放延迟以桥接的单调时间事件复核，观察端计时含采样和重连开销。

这三个模式只覆盖验收矩阵的一部分。紧急停止、键盘与手柄接管、暂停恢复、菜单/文本输入、死亡/复活和世界切换还需逐项实测，未测项保留为未验证。

## 紧急停止与重新接管

游戏内 **Ctrl+Shift+Backspace** 紧急停止；物理键盘输入或手柄移动/跳跃也会交还控制权。游戏外双击 `scripts/Stop-Agent.cmd`，或运行 Stop-Agent.ps1，在本地创建 STOP 标记。角色输入在下次可执行游戏更新释放；完全暂停时旧输入在恢复前过期。

停止不会结束游戏，可以人工操作。重新启动控制器不会自行抢回控制；再次在正常游戏中按完整授权组合键，才可重新许可。该人工热键会清除 STOP 标记。桥接自身发生异常后保持禁用，需检查日志并重新启动实验游戏。

## 证据

每次游戏运行的日志在 `work/terraria-runtime/logs/<run>/`；`current-run.txt` 指向当前日志目录。短期 token 在 `work/terraria-runtime/ipc/`，使用当前用户文件权限并在正常停止时删除，不放入日志或公开证据。

录屏时使用已观察到的游戏窗口标题：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Record-Game.ps1 -WindowTitle '<实际游戏窗口标题>' -Seconds 15 -DesktopAvailable
```

只录指定游戏窗口，失败不回退为整个桌面。文件在 `outputs/terraria-evidence/`，无音轨。脚本检查编码和完整解码；还必须在播放器中确认可播放、确有游戏画面，再把播放结果写入验收摘要。截图不能代替连续录像。

控制器结果文件记录自身运行的成功/失败与原因；不宣称全部 A 通过，不计 Boss、采集或制作成绩。真实游戏结果、编译和离线检查分别记在 VALIDATION。
