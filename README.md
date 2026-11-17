# AgentDeck

AgentDeck 是一个可直接安装到手机的原生 Android APK。它通过自托管公网 Relay，远程管理自己多台电脑上的 Codex、Cursor 和腾讯 WorkBuddy。任务在选定电脑的工作区运行，手机负责下发任务、查看日志与状态、切换会话和处理审批。

项目已进入原型阶段。首批电脑以 Windows 为目标，Relay 计划部署在已有的阿里云 Linux 实例上。

- [使用说明：从装到用](docs/user-guide.md)
- [首版范围、原型验证与验收清单](docs/mvp-acceptance.md)
- [跨端协议与权限设计](docs/protocol-and-permissions.md)
- [Android 与 Windows 界面流程](docs/client-flows.md)
- [Android 单机联调原型](docs/android-lab.md)
- [本地 Relay 传输原型](docs/transport-prototype.md)
- [阿里云私有联调包](docs/private-lab-deploy.md)
- [Windows 服务和 Codex 启动验证](docs/windows-service-lab.md)

## 产品需求

- 使用 Kotlin 原生界面和独立签名 APK。手机端覆盖设备列表、工作区和会话切换、任务详情、日志及跨设备待审批列表。
- 至少同时管理两台 NAT 后的电脑。显示设备在线状态、最后在线时间及适配器是否就绪。各电脑任务可并行运行，切换界面时仍能查看其他设备的状态和待审批数。
- 每台被控电脑安装 Windows 客户端，提供系统托盘图标和设置窗口。由电脑端用户指定允许手机远程控制的项目，并可在本机查看运行日志和操作审计。
- 在指定设备和工作区内创建或继续会话、发送任务、查看增量输出、取消运行，并按工具支持程度进行 CLI 交互。工具会话输入优先，通用 PTY 作为独立权限，默认关闭。
- 手机切换 Wi-Fi 和移动网络、Relay 重启、电脑重启后自动重连。持久保存配对身份，正常重启无需重新扫码或手输 Token。支持查看历史与继续会话，因重启结束的运行标记为已中断。
- 远程操作以任务、审批和文本交互为范围。屏幕捕获、画面串流和浏览器控制台均在项目范围之外。智能插座负责给电脑通电，Agent 从操作系统启动后接手。

## 技术栈

| 组件 | 选型 | 职责 |
| --- | --- | --- |
| Android | Kotlin、Jetpack Compose、AndroidX ViewModel、Coroutines / Flow、Room、OkHttp、Gradle Kotlin DSL | 原生 UI、设备与会话状态、事件缓存和 WSS 连接。最低支持 Android 10（API 29），设备私钥用 Android Keystore 保存 |
| 公网 Relay | Go、`net/http`、`github.com/coder/websocket`、SQLite（`modernc.org/sqlite`） | 接入鉴权、设备注册及撤销、心跳和字节转发。SQLite 保存设备注册等必要元数据 |
| Windows 客户端 | Go 后台服务、Windows Service Control Manager、同一套 WebSocket 和 SQLite 依赖、独立 .NET 10 WinForms 托盘进程 | 后台服务以低权限账户开机自启，管理适配器、项目授权、任务、审批和本机日志。托盘原型以当前用户启动本机审核命令，后续通过受权限控制的本机 IPC 与服务通信 |
| Cursor 适配器 | TypeScript、Node.js 22.13+、官方 `@cursor/sdk` | 由 Agent 启动本机子进程，通过 stdio 交换结构化消息 |
| 部署 | 阿里云 Linux、Caddy、systemd | Caddy 提供公网 HTTPS/WSS，Go Relay 监听回环地址。由 systemd 管理进程 |
| 应用协议 | 版本化 JSON、WSS、端到端 TLS 1.3 双向认证 | Go 使用 `crypto/tls`，Android 使用 `SSLEngine`。Relay 转发内层 TLS 字节流。会话以 `deviceId + adapterId + sessionId` 定位 |

Go 模块、Gradle Wrapper、Android 依赖和 npm 依赖在初始化代码时锁定可复现版本。首版采用单节点部署，Relay 暂时不可用时，已启动的本机任务由电脑 Agent 继续管理。

[Android 应用架构](https://developer.android.com/topic/architecture/recommendations) · [Android Keystore](https://developer.android.com/privacy-and-security/keystore) · [Caddy 反向代理](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy)

## Windows 客户端

- **托盘和设置。** 托盘显示服务和 Relay 的连接状态，可打开设置窗口或日志。设置窗口支持修改设备名称和 Relay 地址、发起配对和撤销配对、查看工具就绪状态，以及管理授权项目。关闭窗口或退出托盘后，后台服务继续运行。电脑未登录时，后台服务仍可自动上线。
- **本地指定项目。** 在设置窗口通过本地目录选择器新增项目，支持重命名、停用和移除。授权清单默认为空，保存在电脑端，只能由已授权的本机用户修改。手机只能看到已启用项目的名称和 ID，任务请求使用 `projectId`，由 Agent 解析到本机目录。手机不能浏览电脑目录、提交任意工作目录或自行添加项目。
- **由服务执行项目边界。** Agent 对新建或恢复会话、日志读取、审批和 CLI 交互执行项目授权检查。启动工具前校验实际目录，防止通过 `..`、符号链接或 Windows 目录联接访问未授权位置。远程文件和命令操作还须由工具沙箱或操作系统权限约束到项目，单靠设置工作目录无法限制进程访问范围。无法落实项目边界的适配器能力不向手机开放。停用或移除项目后立即拒绝新的远程操作，并取消该项目正在运行的远程任务。
- **本机日志。** 持久记录连接与重连、适配器错误、任务状态和输出、审批及项目授权变更。设置窗口可按时间、级别、项目或任务查看日志。审计记录包含时间、发起方、项目、操作及结果。日志按大小或保留期限轮转，敏感凭据不写入日志。手机只可读取当前获授权项目的任务日志，Relay 不保存日志明文。

## 网络与部署

```text
Android APK ── WSS ── 阿里云 Relay ── WSS ── 电脑 A 的 Agent ── 本机工具
                            └──────── WSS ── 电脑 B 的 Agent ── 本机工具
```

手机和电脑都主动连接 Relay 的公网域名。阿里云实例需要固定公网 IP 或 EIP，安全组和 Linux 防火墙允许 `443/TCP`。电脑无需公网 IP 或路由器端口映射。中国内地实例上线前向阿里云确认个人 APK/WSS Relay 场景的备案要求。工具接口只在电脑本机使用。

自托管应用层 Relay 是正式方案，手机只需安装 AgentDeck。Tailscale 可用于快速验证 Agent 和 APK 的业务协议，具备 NAT 穿透和中继回退。自建 WireGuard 需要公网 Hub 并自行维护密钥、路由和 UDP 可达性。两者作为可选联调传输，复用应用层权限与会话协议。

[阿里云 ECS 安全组](https://help.aliyun.com/zh/ecs/user-guide/security-group-rules) · [阿里云备案说明](https://help.aliyun.com/zh/icp-filing/basic-icp-service/user-guide/icp-filing-server-access-information-check) · [Tailscale 连接类型](https://tailscale.com/docs/reference/connection-types) · [WireGuard 快速入门](https://www.wireguard.com/quickstart/)

## 工具适配

适配器向 Agent 声明可用能力，包括会话列表、恢复、增量输出、取消、审批和交互输入。APK 据此显示操作入口，并统一展示工具事件和审批。设备在线时，单个适配器仍可能因为未登录或启动失败而不可用。

| 工具 | 接入方式 | 首版范围与待验证项 |
| --- | --- | --- |
| Codex | Agent 启动 `codex app-server`，经 stdio 使用 JSON-RPC | 先实现任务、会话、实时事件和原生审批。桌面已运行会话的接管单独验证。 [官方文档](https://learn.chatgpt.com/docs/app-server) |
| Cursor | 使用官方 SDK 本机模式，保存本机 SDK 会话 ID | 先支持 APK 管理的只读会话、流式输出和恢复。SDK 无界面模式默认自动允许工具调用，写入和命令能力需通过文件式 Hook 将审批转交本机 Agent，并验证拒绝、超时或 Hook 异常时操作被阻止。Cursor IDE 已打开会话的接管单独验证。 [SDK](https://cursor.com/docs/sdk/typescript) · [Hooks](https://cursor.com/docs/hooks) |
| WorkBuddy | 验证腾讯 WorkBuddy Open API 的本地助理接口 | 公开接口可查状态、发消息、读历史及回应权限请求，文档未给出按物理设备 ID 路由的方法。双机实测确认后才开放按电脑下发。若接受安装 CodeBuddy Code，单独评估其本机 SDK 或 HTTP API Beta。 [WorkBuddy Open API](https://open.workbuddy.cn/docs/openapi) · [CodeBuddy Code HTTP API](https://cloud.tencent.com/document/product/1831/137064) |

## 配对、权限与恢复

- **身份。** 首次配对使用短时、一次性二维码，校验电脑身份并在电脑确认。手机私钥进入 Android Keystore，电脑身份由操作系统凭据保护。工具凭据留在对应电脑。支持撤销手机身份并断开现有会话。
- **传输。** 外层 WSS 校验公网证书，内层 TLS 校验已配对的两端身份。Relay 只接触路由等元数据及加密载荷。任务、日志及审批明文由端点处理。工具调用厂商模型服务的数据按各厂商规则处理。
- **授权。** Agent 按设备、工作区和操作校验权限。审批展示原始操作范围，绑定会话、运行实例和请求 ID。超时、重放或旧运行实例的审批被拒绝，工具自身的沙箱和权限策略继续生效。
- **恢复。** 任务带幂等 ID，断线后先查提交结果。Agent 持久保存任务状态及事件，APK 按设备游标补拉，游标失效时同步快照。Windows 服务需在停留于登录界面时上线，依赖桌面登录的适配器显示等待状态。Android 以回到前台后可靠同步为基线。

## 开发顺序与验收

1. 安装 Windows 客户端并让 APK 经阿里云 Relay 同时连接两台电脑。验证托盘状态、设置窗口、首次配对、手机网络切换、Relay 重启、退出托盘后服务继续运行、电脑停在 Windows 登录界面时自动上线，以及手机身份撤销。
2. 接入 Codex。两台电脑各运行一个任务，在 A 处理审批时仍能看到 B 的状态。验证重连后日志可补齐、任务不重复、过期审批无效、重启中断可辨识。
3. 接入 Cursor。验证使用 Windows 服务账户启动 SDK、恢复 APK 管理的会话，以及可写操作的 Hook 审批。再验证 WorkBuddy 是否能稳定指定实际执行任务的电脑。

各适配器开放给手机前，验证手机只能选择已授权项目，伪造项目 ID、传入任意路径或通过目录联接越界时均被拒绝。停用项目后，验证旧会话和审批无法继续、远程任务被取消、手机无法再读取该项目日志，并能在本机查到授权变更和操作结果。
