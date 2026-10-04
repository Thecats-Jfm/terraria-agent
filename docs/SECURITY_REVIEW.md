# TerrariaModder 源码安全审查

本次静态审查没有发现可以确认的后门、其他应用凭据窃取、挖矿、关闭安全软件或开机常驻代码。但是，调试模块权限过大，管理器更新链缺少发行身份验证，而且实际加载器和发布二进制尚未与源码对应。因此结论是可以继续研究固定源码的最小接入方案，不能直接把整套下载包标记为安全或原样安装。

审查日期为 2026 年 10 月 4 日，时区 Asia/Shanghai。当前没有运行、安装、编译或加载被审查程序，也没有访问真实角色或世界的内容。

## 审查对象与可复现范围

- 主仓库：[Inidar1/terraria-modder](https://github.com/Inidar1/terraria-modder)。
- 固定提交：[cf96d7c101917e1b3351dceb07f49bea6a9f1de6](https://github.com/Inidar1/terraria-modder/tree/cf96d7c101917e1b3351dceb07f49bea6a9f1de6)，提交说明为 Vault 0.4.4 源码发布。
- 下载的是该提交源码 ZIP。398 个文件的大小及 Git blob SHA 均与 GitHub 的完整、未截断 tree 对应；源码合计 4,083,753 字节，包含 293 个 C# 文件、84,656 行 C#，没有 DLL 或 EXE。
- ZIP SHA-256：`1D0A4A14B362228A0ABFC18128744D5BD6DD9310FEA5160EF6C81E82ADD45C46`。
- 检查方式为全仓库敏感能力/联网/秘密读取/持久化模式扫描，人工追踪 Core、DebugTools、Vault、构建部署脚本和依赖声明；可选模组做敏感命中上下文检查。没有声称逐行证明所有代码安全。
- 外置加载器另读取了 [ConfuzzedCat/TerrariaInjector](https://github.com/ConfuzzedCat/TerrariaInjector) 的固定提交 `9d6897df36bb7b03f0694def49626bbfb169edb4` 中 Program.cs，核对其 Git blob SHA。该补充不是完整加载器审计，也不证明 Nexus 打包版使用同样字节。

源码 ZIP、tree、哈希记录、搜索结果和三份分项审查保存在工作区 `work/audits/terraria-modder/`，不纳入公开源码仓库。哈希用于固定审查对象和验证传输一致，不能独自证明程序没有恶意。

## 明确未发现的恶意证据

本次审查没有发现扫描其他浏览器密码、钱包或系统凭据并外传的路径，没有发现矿工、陌生控制服务器、禁用防护软件、计划任务、Run/RunOnce 或系统服务常驻代码。发现的敏感操作基本能对应到模组加载、游戏调试、Nexus 登录、下载安装和自更新功能。

这不是对作者意图或所有发行物的保证。任意第三方 DLL 在游戏进程内执行都具有当前用户的权限；仅审查这个仓库，无法证明它未来更新、依赖包或别人提供的模组也安全。

## 影响当前 Agent 的主要风险

| 风险 | 已核实行为与利用条件 | 项目处理 |
| --- | --- | --- |
| DebugTools HTTP 默认无认证 | Enabled/HttpServer 默认开启，端口 7878；请求仅检查 Origin，没有会话 token。可访问该接口的调用者能改游戏状态、反射私有字段、生成物品、传送和删除菜单中的世界 | 不加载 DebugTools；自写有认证、有限权限和停止机制的桥接 |
| 反射可组合到打开程序 | 反射可访问全部已加载程序集，能修改 Core 的私有更新 URL。菜单按钮随后用 ShellExecute 打开该值；结合虚拟点击可形成程序/协议打开链 | 排除 reflect、eval、trace、任意 command/mod-action 与外部打开能力；此为静态条件链，未动态复现 |
| 已有虚拟输入不满足断连与接管要求 | hold/press 虽有 10/30 秒上限，但鼠标位置覆盖没有同样期限；没有我们需要的动作序号、连接租约、死亡清空和人工接管锁 | 所有输入用短 TTL 与控制权状态约束，按 A 的实际结果验收 |
| 超时不等于取消 | 主线程请求超时只是停止等待，旧动作还留在队列里，可能恢复后迟到执行 | 执行前再次校验会话、世界、序号和截止时间，使用有界队列 |
| 插件全信任，启用配置失败时可全启用 | Core 动态载入并初始化 DLL；没有签名/固定哈希允许清单，启用清单缺失或解析失败会回退 | 使用空的隔离模组目录，仅加载审阅后的 Core 和自有桥接；清单失败即停止 |
| 隐藏信息与作弊能力 | DebugTools 可读任意区域 tile 和全图箱子，没有照明、视线、探索历史过滤；另有直接改物品/世界操作 | 从游戏线程生成合法观测，序列化前过滤；主挑战操作遵循正常输入、材料和距离 |
| 部署脚本写入原游戏并装全部模组 | setup 创建到实际游戏目录的 junction；deploy 会复制 Core 和所有已构建模组 | 使用约 0.75 GiB 的隔离游戏副本，自己实现最小部署，不运行原样全量部署 |
| 加载器与默认存档路径边界未闭合 | 主仓库缺少 Injector 源码和二进制；独立加载器源码会递归载入 DLL、设置默认 Documents 存档根，并在有 PrePatch 时临时改名游戏 EXE | 核实实际加载器对应源码与字节，显式实现/验证独立 SavePath，备份完成后才启动 |

主要证据链接均固定到本次提交：

- [DebugTools 默认设置](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/DebugTools/DebugToolsConfig.cs#L9-L16)、[HTTP 请求处理](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/DebugTools/DebugHttpServer.cs#L141-L166)、[删除世界路径](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/DebugTools/DebugHttpServer.cs#L1587-L1637)。
- [运行时反射](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/DebugTools/RuntimeIntrospection.cs#L210-L259)、[Core 的网页打开入口](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/Core/PluginLoader.cs#L1318-L1327)。名为 eval 的接口实际是字段、属性和索引访问，不能仅凭名称声称有任意 C# 编译执行。
- [虚拟输入及期限](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/DebugTools/VirtualInputManager.cs#L74-L148)、[主线程等待与超时](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/DebugTools/MainThreadDispatcher.cs#L34-L53)。
- [DLL 载入](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/Core/PluginLoader.cs#L732-L789)、[全量部署脚本](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/deploy.bat#L59-L108)。
- [外置加载器默认存档路径](https://github.com/ConfuzzedCat/TerrariaInjector/blob/9d6897df36bb7b03f0694def49626bbfb169edb4/Program.cs#L320-L328)、[条件性游戏 EXE 改名](https://github.com/ConfuzzedCat/TerrariaInjector/blob/9d6897df36bb7b03f0694def49626bbfb169edb4/Program.cs#L342-L363)。参数是否会在后续游戏初始化覆盖 SavePath，需要真实启动检查，不能仅凭这段代码断言隔离必然失败或成功。

HTTP 源码只注册了 localhost/127.0.0.1 的 URL 前缀。本次没有启动服务，未测试实际 socket 监听和远程可达性；不能把 URL 前缀当成已经证明只监听回环。我们自己的服务需要严格回环绑定和来源校验，或明确拒绝远程连接的同用户管道。

## Vault 管理器的更新与凭据检查

Vault 是独立的下载管理器，不是 Agent 接入的必要依赖。Windows Nexus API key 使用 DPAPI CurrentUser 保护，正常认证请求发往 Nexus 的 API/SSO。API 客户端与下载客户端分开，未发现给任意下载域发送同一 API key 的实现。应用旁的 .env 是它自身的显式配置入口，没有发现读取其他软件秘密。

`HKCU\Software\Classes\nxm` 的注册表写入是 Nexus 下载协议关联，没有发现开机启动项。自更新会生成隐藏 PowerShell 脚本、替换自己并启动更新程序；这是公开更新功能，不能单凭 PowerShell 或 ExecutionPolicy Bypass 判断为后门。

不过发现两个需要处理的实际风险：

1. 用户先点击更新并处于待下载状态时，内嵌浏览器允许任意站点导航，并把任意来源的下载交给当前请求。更新包只核对含有唯一指定文件名的 EXE，没有验证可信发布者签名或独立预期 hash，然后复制并执行。攻击者能控制被导航页面/下载包时，可能把该文件变成更新程序。没有点击更新或没有待完成请求时，不能把任意下载等同于立即执行。此路径只做静态追踪，未构造恶意包或动态复现。[浏览器下载接收](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/mod-manager/Views/NexusBrowserPanel.axaml.cs#L343-L384)、[更新包处理](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/mod-manager/ViewModels/MainViewModel.cs#L479-L525)。
2. 普通 API 模组下载有大小限制，解压也有条目数、路径逃逸和约 1 GB 展开限制；但自更新流和内嵌浏览器下载缺少下载阶段的总字节上限，可在解压前消耗磁盘。不是发现它主动填盘，而是缺少资源限制。[自更新下载流](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/mod-manager/ViewModels/MainViewModel.cs#L411-L428)。

其他边界包括管理器本机管道没有显式同用户/消息大小/超时配置，以及核心包可替换游戏根目录中的其他 DLL/EXE。普通 Windows 路径穿越有规范化和根目录检查，不能把这些边界问题误写成已经确认的任意文件写入漏洞。

## 多人模式与存档行为

专用服务器根据客户端自报 GUID 匹配持久角色；已知管理员 GUID 的、能够加入服务器的客户端可能冒认该角色。reqop 使用非密码学随机值且未见尝试限速。本机管理 API 默认因没有 key 而关闭，启用后默认允许回环来源免 Bearer。以上是有条件的认证设计风险，不是发现硬编码作者管理员后门；当前单人 Agent 不启用这些能力。

Core 正常保存配置、日志、安装 GUID 与模组伴随存档；自定义内容可挂钩玩家/世界保存流程，不能宣传为“永不影响存档”。即使只使用加载核心，也要核实保存路径、备份和游戏副本行为。

## 依赖与发行物的限制

Core 目标是 .NET Framework 4.8，唯一显式 NuGet 包为 Lib.Harmony 2.3.3；本机已有 4.8 开发引用。Vault 为 net8，有 14 个显式包声明。本次未还原、执行或完整审查所有包与 WebView 引擎，也不是完整 CVE 扫描。

仓库没有 NuGet lock 文件、发行二进制对应证明或发行签名/hash 核对流程。唯一 CI 是文档部署，不能用来证明游戏发布包可复现。TerrariaInjector 上游还说明应使用 Inidar 的附带版本，不能假定上游 Releases 与实际 Nexus 包相同。[Core 项目依赖](https://github.com/Inidar1/terraria-modder/blob/cf96d7c101917e1b3351dceb07f49bea6a9f1de6/src/Core/TerrariaModder.Core.csproj)、[Injector 上游说明](https://github.com/ConfuzzedCat/TerrariaInjector/blob/9d6897df36bb7b03f0694def49626bbfb169edb4/README.md)。

## 当前实施决定

用户已选择最新版原版加轻量桥接。继续这条路线，但先闭合实际加载器源码/构建边界；构建固定版本最小 Core 和自有桥接，排除 Vault、DebugTools 与其他可选模组，在隔离游戏副本和专用新档运行。自有桥接只暴露合法观测与正常输入/交互，并实现认证、短 TTL、序号、有界队列和锁定人工接管。

真实游戏 A 仍未开始。下一次验收从合法位置观测、右移、停、跳和断连释放开始，不以本次源码检查代替实机验证。
