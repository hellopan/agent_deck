# 本地 Relay 传输原型

使用 Go 1.26，运行 `go test -v ./internal/transport`。测试在本机启动 HTTPS Relay，并在同一测试进程启动两个模拟 Agent 和一个模拟手机。外层走 WSS，内层由手机与指定 Agent 进行 TLS 1.3 双向认证。Relay 只处理二进制帧和设备路由。

测试用临时证书和固定测试凭据。两台模拟设备同时连通后，各自返回带设备标识的探测响应。测试还覆盖跨设备路由凭据被拒绝、错误主机证书被拒绝、未配对手机无法获取响应、Relay 实例替换后用原身份重连。

## 用独立进程联调

在仓库根目录构建 Windows 实验工具，再生成一次性实验配置。`init` 要求输出目录尚不存在。正常重启或重连继续使用原文件。

```powershell
New-Item -ItemType Directory -Path bin -Force | Out-Null
go build -o bin/agentdeck-lab.exe ./cmd/agentdeck-lab
.\bin\agentdeck-lab.exe init -out .agentdeck-lab
```

在三个终端分别运行 Relay、Agent A 和 Agent B。

```powershell
.\bin\agentdeck-lab.exe relay -config .agentdeck-lab/relay.json
.\bin\agentdeck-lab.exe agent -config .agentdeck-lab/agent-a.json -device a
.\bin\agentdeck-lab.exe agent -config .agentdeck-lab/agent-b.json -device b
```

从第四个终端发起两次探测，每次应返回指定设备的认证结果。重启 Relay 或单台 Agent 后再探测，保留原有配置。

```powershell
.\bin\agentdeck-lab.exe probe -config .agentdeck-lab/phone.json -device a
.\bin\agentdeck-lab.exe probe -config .agentdeck-lab/phone.json -device b
```

跨机器实验时，生成配置前通过 `init -relay-url` 填入客户端实际可达的 WSS 地址，再通过 `relay -listen` 绑定对应的局域网或实验网卡。TLS 校验会使用该地址中的主机名或 IP。每台机器只拷贝其角色的配置文件。实验配置含私钥和路由凭据，保存在当前用户可控的目录，仓库默认忽略 `.agentdeck-lab/`。生成的证书有效期为七天。

Linux amd64 的同一实验工具可用下面的命令构建。在 Linux 运行时使用同样的子命令和对应配置文件。

```sh
mkdir -p bin
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -o bin/agentdeck-lab-linux-amd64 ./cmd/agentdeck-lab
```

## 当前边界

已配对的探测客户端也可运行 `agentdeck-lab projects -config .agentdeck-lab/phone.json -device a`。Agent 需以 `-projects <本机授权清单绝对路径>` 启动。查询通过内层 TLS，每次读取电脑当前授权清单，只返回启用且目录可用的项目 ID。清单停用、删除或重定向的项目不会出现在下一次查询中。

原型每台设备只允许一个活跃手机通道，路由登记在内存。Relay 重新启动时由 Agent 重建通道。当前范围是传输和身份验证，测试探测消息没有工具调用能力。公网部署前仍需实现配对、持久化授权、心跳和连接限流，并在目标 Android 和 Windows 上验证证书、断线及重启行为。
