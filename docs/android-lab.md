# AgentDeck 单机扫码联调

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

`relay-init` 只执行一次。服务器指纹可用 `./agentdeck-relay-linux-amd64 relay-fingerprint -config ./relay.local.json` 重新查看。注册令牌只由电脑管理员从云机本地复制到 Windows 设置窗口。阿里云安全组和系统防火墙需放行实际监听端口。若仍使用原先只绑定云机回环的 Relay，则需为手机和电脑分别配置可达路径。证书目前沿用实验工具的七天有效期，请在有效期内联调。

## 新电脑和手机

1. 双击 Windows 单文件安装程序，在「连接」页点「配置云服务器」，填入上述三项。注册后窗口会启动本机 Agent。需要任务功能时，在「项目」页启用测试项目，并在「设置」页填写已安装的 Codex 路径。
2. 点「配对手机」。手机 App 点「扫描电脑配对码」。两分钟内在 Windows 点「核对手机指纹」，与手机完整指纹一致后确认。
3. 手机点「刷新项目」。后续打开软件可以使用同一设备身份。此实验版退出托盘会停止由托盘启动的 Agent，Windows 服务自启仍需单独部署和验收。

任务启动仍在 Windows 本机审核。APK 和单文件安装程序的构建与服务器本地注册测试，与真机扫码和公网连接分别验收。

## 构建 APK

准备 Java 17、Gradle 8.13、Android SDK API 36 和 Build Tools 35.0.0。配置 `ANDROID_HOME` 后运行 `gradle -p android :app:assembleDebug`。APK 输出位于 `android/app/build/outputs/apk/debug/app-debug.apk`。
