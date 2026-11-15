# 阿里云私有联调包

联调包用于测试现有 Relay 和 Windows Agent 的传输、重连及授权项目查询。Windows 版是命令行实验工具，当前 Android 客户端由 Windows 探测命令代替。正式 APK、扫码配对和远程工具任务各有独立验收。

`agentdeck-lab-linux-amd64.zip` 是给阿里云 Linux x86-64 实例使用的实验包。压缩包内有 Linux 可执行文件 `agentdeck-lab` 和说明 `DEPLOY.md`。在阿里云只运行其中的 `relay` 子命令，由它转发实验流量。

## 1. 在 Windows 生成实验配置

解压 Windows 包，在解压目录打开 PowerShell。

```powershell
.\agentdeck-lab.exe init -out .agentdeck-lab -relay-url wss://127.0.0.1:8443
```

把 `.agentdeck-lab/relay.json` 上传到阿里云。将 `agent-a.json` 和 `agent-b.json` 分别交给两台 Windows 电脑。`phone.json` 留在执行探测的 Windows 电脑上。配置包含测试私钥，证书有效期为七天。重启测试沿用原配置。

## 2. 在阿里云启动 Relay

使用已登录的阿里云终端操作。Linux 包适用于 `uname -m` 返回 `x86_64` 的实例。先创建当前 SSH 登录账户的私有目录。

```sh
mkdir -p "$HOME/agentdeck-lab"
chmod 700 "$HOME/agentdeck-lab"
```

上传以下两个文件到该目录：

| 上传文件 | 来源 |
| --- | --- |
| `agentdeck-lab-linux-amd64.zip` | 项目 `bin/` 内的 Linux 包 |
| `relay.json` | Windows 执行 `init` 后生成的 `.agentdeck-lab/relay.json` |

在阿里云执行以下命令解压和启动。

```sh
cd "$HOME/agentdeck-lab"
unzip agentdeck-lab-linux-amd64.zip
chmod 700 ./agentdeck-lab
chmod 600 ./relay.json
./agentdeck-lab relay -config ./relay.json -listen 127.0.0.1:8443
```

解压后的目录如下。Relay 启动时使用 `agentdeck-lab` 和 `relay.json` 两个文件。

```text
~/agentdeck-lab/
├── agentdeck-lab-linux-amd64.zip
├── agentdeck-lab
├── DEPLOY.md
└── relay.json
```

启动成功会显示监听地址。此实验让 Relay 监听服务器回环地址，通过 SSH 转发联调，无需对公网开放 8443。

## 3. 在参与联调的 Windows 电脑建立转发

下面的服务器用户名及地址只在你自己的终端填写。每台 Windows 开一个 PowerShell 窗口运行；同一台电脑上的探测客户端可以复用这条转发。

```powershell
$relaySshTarget = Read-Host '输入可登录阿里云的 SSH 用户和主机'
ssh -N -o ExitOnForwardFailure=yes -L 127.0.0.1:8443:127.0.0.1:8443 $relaySshTarget
```

需要该实例允许 SSH TCP 转发。如果只具备控制台终端访问权限，仍可完成 Linux 启动检查，跨机转发要等可用的 SSH 路径。请通过你已信任的渠道核对 SSH 主机指纹。

在两台电脑各开一个终端启动 Agent：

```powershell
# 电脑 A
.\agentdeck-lab.exe agent -config .\agent-a.json -device a

# 电脑 B
.\agentdeck-lab.exe agent -config .\agent-b.json -device b
```

## 4. 从 Windows 探测并记录结果

```powershell
.\agentdeck-lab.exe probe -config .\phone.json -device a
.\agentdeck-lab.exe probe -config .\phone.json -device b
```

两次应各返回指定设备的 TLS 1.3 认证成功。停止并重新启动 Relay，然后重复两次探测。再单独停止并启动 A，验证 B 仍可探测、A 启动后沿用原身份。此流程使用前台 SSH 和 Agent，Windows 无人登录自启要用正式公网入口另行验收。

需要检查项目授权时，按仓库内的 `docs/windows-service-lab.md` 准备本机授权清单。启动 Agent 时加入 `-projects` 和清单的绝对路径，再把探测命令中的 `probe` 改为 `projects`。结果只显示允许访问的项目 ID。
