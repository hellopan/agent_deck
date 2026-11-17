# AgentDeck 单机扫码联调

> 要找面向使用者的分步操作（含云服务器、配对、审批、开机自启、排障），看 [使用说明](user-guide.md)。本文记的是联调过程中的细节与约定。

新电脑只需收到 `AgentDeck-Setup.exe`，手机安装 `AgentDeck-Lab.apk`。Windows 程序首次运行把应用和 Go Agent 安装到当前用户目录。在「配置云服务器」里填写 WSS 地址、服务器证书指纹和注册令牌。PC 会生成并保存自己的设备身份，手机扫码后在 Windows 核对指纹并确认。

## 云服务器一次性设置

当前实验版 Relay 需要更新为支持注册的 Linux 程序。下面使用独立目录，原联调配置可以保留。上传 `agentdeck-relay-linux-amd64`，按实例实际地址替换示例 URL。证书包含该地址的主机名或 IP，手机和电脑需要能够访问这个地址。

```sh
mkdir -p "$HOME/agentdeck-enroll/routes"
chmod 700 "$HOME/agentdeck-enroll" "$HOME/agentdeck-enroll/routes"
cd "$HOME/agentdeck-enroll"
chmod 700 ./agentdeck-relay-linux-amd64
./agentdeck-relay-linux-amd64 relay-init -config ./relay.local.json -relay-url 'wss://你的云机地址:8443'
umask 077
openssl rand -hex 32 > enrollment-token
export AGENTDECK_ENROLLMENT_TOKEN="$(cat enrollment-token)"
./agentdeck-relay-linux-amd64 relay -config ./relay.local.json -enrollment-dir ./routes -listen 0.0.0.0:8443
```

`relay-init` 只执行一次。服务器指纹可用 `./agentdeck-relay-linux-amd64 relay-fingerprint -config ./relay.local.json` 重新查看，同时会把证书到期时间打到 stderr。注册令牌只由电脑管理员从云机本地复制到 Windows 设置窗口。阿里云安全组和系统防火墙需放行实际监听端口。若仍使用原先只绑定云机回环的 Relay，则需为手机和电脑分别配置可达路径。`relay-init` 默认签七天证书，长期使用请加 `-validity 365`。

## 新电脑和手机

1. 双击 Windows 单文件安装程序，在「状态」页点「配置云服务器」，填入上述三项。注册后窗口会启动本机 Agent。需要任务功能时，在「设置」页用「管理项目…」添加并启用测试项目；Codex 由托盘自动发现，不用填路径。
2. 点「配对手机」。手机 App 点「扫描电脑配对码」。两分钟内在 Windows 点「核对手机指纹」，与手机完整指纹一致后确认。
3. 手机确认后自动进入「任务」页，项目列表随队列一起刷新，不需要手动点刷新。后续打开软件使用同一设备身份。未装开机自启服务时，退出托盘会停止由托盘启动的 Agent；要在登录前就上线，到「设置」页点「安装服务」（第 6 节）。

审批不再需要回到电脑上点。Codex 每次请求执行命令或写文件，Agent 会把请求挂起、推给手机，手机在 5 分钟内决定，超时按拒绝处理。会话级的目录或网络授权请求不推给手机，Agent 直接拒绝。

## 手机界面

四块，对应 `design/ui-v2.html` 的 `pt` / `pa` / `pr` 三屏加一个连接页：

| 界面 | 内容 | 数据来源 |
|---|---|---|
| 任务 | 设备切换、待处理 / 运行中 / 已完成分段、审批与队列卡片 | `agentdeck-approvals-v1`、`agentdeck-queue-v1` |
| 审批 | 风险条、完整命令与工作目录、2×3 事实、有效期条、影响范围、拒绝 / 允许一次 | `agentdeck-approve-v1` |
| 执行轨迹 | 实时事件流（消息 / 命令 / 审批 / 结束四类着色）、跟随开关、叫停；结束后底部变成「接着说」输入条 | `agentdeck-task-log-v1`、`agentdeck-task-submit-v1` |
| 连接 | 连通状态与延迟、计算机名、设备 ID、手机指纹、切换与重新配对 | `agentdeck-pair-v1`、`agentdeck-projects-v1` |

约定：

- 列表与审批每 2~3 秒轮询一次（有未决审批时用 2 秒），轨迹页按游标每 2 秒补拉，断线重连后从 `after` 继续。
- 「允许一次」先要求系统锁屏验证（`createConfirmDeviceCredentialIntent`）。设备没设锁屏时弹窗说明风险，要求显式确认后才放行。
- 调试构建不加 `FLAG_SECURE`，可以直接截图；release 构建加，防止录屏。
- 一轮结束后，轨迹页底部的按钮换成输入条。发出去的这句带上这一轮的 `threadId`，电脑走 `thread/resume` 续在同一个会话里；「接着说」是新一轮、新任务卡片，但共用同一段对话。没有 `threadId`（首轮根本没跑起来）时不给输入框，只说明这轮接不下去。

`design/phone/` 下是这四块的真机分辨率截图（1080×2400，模拟器 Android 15）。

## 调试画廊

`android/app/src/debug/` 里有一个只在 debug 构建存在的 `GalleryActivity`，用固定样本数据渲染上面四屏，不需要电脑在线：

```sh
adb shell am start -n com.agentdeck.lab/.GalleryActivity --es screen tasks   # tasks | approval | trace | follow | submit | connect
```

修改界面后先在这里对一遍 `design/ui-v2.html`，再上真机联调。样本数据在 `GalleryActivity.kt` 底部，改文案或补字段时同步改它。

## 构建 APK

准备 Java 17、Gradle 8.13、Android SDK Platform 36 与 Build Tools 36.0.0，并在 `android/local.properties` 写明 SDK 路径：

```properties
sdk.dir=D:/path/to/android/sdk
```

然后 `gradle -p android :app:assembleDebug`，APK 位于 `android/app/build/outputs/apk/debug/app-debug.apk`。

