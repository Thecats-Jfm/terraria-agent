# 隔离准备与构建

本页的准备与构建脚本不会启动游戏、移动鼠标、切换窗口或读取存档地形。独立启动入口已建立，流程见 RUNNING，尚未实机验证；不能把构建命令当成游戏启动成功。

## 路径

脚本根据自身位置找到项目根，再从 `outputs/terraria-agent` 找到工作区。所有准备产物固定写入工作区 `work/terraria-runtime/`，不接受任意运行目标目录，不改原游戏安装。该布局不支持直接把仓库克隆到任意目录后运行；需要保留 `outputs/` 布局。

| 路径 | 内容 |
| --- | --- |
| `work/terraria-runtime/game/` | 原游戏的隔离副本；目标非空时准备脚本拒绝覆盖 |
| `work/terraria-runtime/backups/<UTC时间及随机后缀>/` | 本地与云端存档只读备份、源/副本 SHA-256 与文件元数据清单 |
| `work/terraria-runtime/saves/main/` | 新主挑战专用保存目录，准备脚本只建空目录 |
| `work/terraria-runtime/preparation-status.json` | 稳定备份与游戏复制结果；`SafeToLaunch` 始终为 false |
| `work/dependencies/feed/` | root 提前核实的本地 NuGet 包源，脚本不下载任何包 |
| `work/terraria-runtime/nuget-packages/` | 本地源恢复的构建依赖缓存 |
| `work/terraria-runtime/build/<项目>/` | 构建产物 |
| `work/terraria-runtime/logs/` | 构建日志和机器可读结果 |

运行数据、完整绝对路径、Steam 账号目录和备份只保留在工作区，不能提交公开仓库。

## 准备脚本

在游戏和游戏加载器关闭、Steam 存档同步完成后运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Prepare-Runtime.ps1
```

默认只读源为已检查的 `E:\SteamLibrary\steamapps\common\Terraria`；其他安装可用绝对路径 `-TerrariaInstallPath` 指定。Steam 根从已知注册表与默认安装路径发现；必要时传 `-SteamRoot 'C:\Program Files (x86)\Steam'`。本机默认禁用脚本，示例的 `ExecutionPolicy Bypass` 仅作用于当前 PowerShell 进程，不更改系统或用户持久策略；仍应只执行本项目已审阅脚本。不会要求管理员权限，也不调用上游 setup/build/deploy。

准备先要求能够看见正常桌面进程，再检查已知游戏进程；受限沙箱把桌面进程隐藏时立即停止，不能把“进程列表没有游戏”误当成游戏已关闭。拒绝源或目标路径中的目录链接/junction。备份本地 Documents 的 `My Games/Terraria/Players`、`Worlds`，以及所发现 Steam 用户的 `105600/remote` 和 `remotecache.vdf`。只对文件字节计算 SHA-256，不解析人物、地图或世界内容。每个复制文件校验长度与 SHA-256；复制后重新枚举源目录，对源路径集合、长度、写入时间和哈希作一致性比较。失败保留备份并记录失败，不复制/批准新的游戏环境；已有不完整游戏副本也保留，脚本不会自动删除或覆盖。

成功的 `BackupStable` 只能证明捕获期间源没有可检测的变化，不能证明云端没有排队同步或稍后再变。脚本不会关闭 Steam，也不会修改云同步设置。`CloudSyncConfirmed` 和 `SavePathVerifiedInGame` 留为 false；核实实际运行保存目录、云档不可访问及世界加载前的保护后，才能安排实机运行。原安装不存在模组文件是复制前提；发现 `TerrariaModder`、`Mods` 或 `TerrariaInjector.exe` 时先停止审查。

`Verify-Backup.ps1` 在每次启动前重新读取已成功备份的清单，核对当前原文件、实际备份及复制前后记录。原文件在备份读取前后各采样一次，源集合、长度、修改时间或字节变化均会阻止启动。该检查不写原文件、备份或准备状态，也不解析存档；它仍不能证明 Steam 云队列已清空。发现变化时保留旧备份，另建经过核实的新备份后再继续。

## 本地源构建

构建项目为 `src/AgentHost` 与 `src/AgentBridge`（net48，x86），以及 `src/Controller`（net8.0）。`src/Protocol` 是共享源，由各项目显式编译，不单独执行构建。Host/Bridge 项目使用 MSBuild 属性 `TerrariaInstallPath` 引用隔离副本中的游戏程序。第一版决策方式为规则状态机，游戏运行时没有付费模型调用。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1
```

`Build.ps1` 先要求备份稳定与副本已验证，再使用自己生成的 NuGet 配置：清除所有其他包源，仅使用固定工作区本地 feed；把缓存放到运行目录。缺少已核实的 `Lib.Harmony 2.3.3` 包、包哈希变化、构建 DLL 与固定包不一致、本机 .NET Framework 4.8 或 XNA 4 引用缺失都会停止，不安装依赖。关闭 NuGet 在线审计及自动引用程序集包，用本机 v4.8 与 GAC_32 中的 XNA 引用路径。随后 `dotnet build --no-restore`，所有构建命令与结果留日志。未预置的依赖会失败；失败后由 root 核实并补充本地包源，脚本不会回退公网源。

这里只声明编译，不声称模型、游戏加载、独立存档、动作执行或 A 验收成功。构建成功也不会复制加载器/桥接到游戏，或执行新程序。

原版 Terraria 1.4.5.8 的 x86 EXE 已带 `IMAGE_FILE_LARGE_ADDRESS_AWARE` 标记。构建后脚本只检查并补齐自有 `build/AgentHost/TerrariaAgent.Host.exe` 的同一标记，以对齐原游戏的地址空间能力；校验 MZ、PE、PE32 和 x86 头，只写 COFF 特征字段的两个字节并验证其他标记保留。不会修改 Terraria.exe、DLL、原安装或游戏规则。此项针对纹理加载时地址空间不足的可能原因，是否解决实际加载失败仍须重新进入游戏验证。

## 验证状态

准备脚本已在可见桌面进程的环境实际运行：21 文件存档备份稳定，16,017 文件游戏副本与原安装逐文件 SHA-256 一致。受限环境会因进程不可见而拒绝准备。离线 Build 脚本已实际运行，三个项目编译零警告、零错误。所有新增 PowerShell 脚本语法检查通过。

游戏启动、云拦截、创建与保存、动作控制和录屏播放仍未实测。编译产物位于固定 build 目录；现阶段不标记启动或 A 验收通过。

`Deploy-Bridge.ps1` 已单独运行两次成功，只将四个自有文件放进隔离副本；其清单在 `work/terraria-runtime/bridge-deployment.json`。副本仍保留固定版本游戏 EXE，原安装未改动。真正的 Start-Game 尚未执行。
