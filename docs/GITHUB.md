# Terraria Agent GitHub 配置

公开仓库 [Thecats-Jfm/terraria-agent](https://github.com/Thecats-Jfm/terraria-agent) 已存在，默认分支 `main`。GitHub connector 的实际 `get_repo` 查询确认仓库为 public、`size=0`，当前连接账号拥有 `push=true`、`admin=true` 权限；插件实际登录账号为 `Thecats-Jfm`。本地仓库已初始化并配置目标 origin，尚未进行首次推送。

本地项目在工作区新目录 `outputs\terraria-agent`。为公开提交使用账号的 GitHub noreply 邮箱，配置只作用于这个仓库，不改全局 Git 身份。源码和文档纳入 Git；存档、备份、游戏二进制、凭据、本机配置、原始日志和录像由 `.gitignore` 排除。

CLI 已实际执行 `git ls-remote`，退出码为 0，返回空 refs，确认远程仓库可访问且当前为空。该读取结果和插件的写权限均不能证明原生 Git 已具备推送认证；原生 Git 认证状态仍未知，后续先做非交互验证。

早期准备阶段曾遇到浏览器读取及 GitHub 连接超时，Git Credential Manager 的设备登录尝试也未完成。上述内容属于历史尝试；当前实际查询已确认仓库存在且可访问，不再将网页创建或网络不可访问记为当前阻碍。未根据插件登录结果假定原生 Git 凭据已建立。

首次公开同步前核对源码快照及提交范围，排除原存档、版权游戏文件、凭据、私人路径、截图和原始日志。本地旧审查历史计划保留在本地，公开首推使用经过核查的干净源码快照。完成原生 Git 的非交互认证核验后再推送，并核对远程分支和内容；尚未推送的状态不记为同步成功。

后续按 A、B、C、D 分别提交代码和验收摘要。只有真实通过的阶段才创建通过标签。自动测试与 CI 必须注明验证范围，不能把构建成功显示为 Boss 或采集任务通过。
