# AgentDeck 使用说明

适用版本：电脑端与手机端 `0.2.0-lab`。这份说明从零写到能用：起服务器、装电脑、配项目、配对手机、跑第一个任务、设开机自启、撤销配对。

手机不直连电脑。两端都主动连你自己的一台云服务器（Relay），由它转发加密流量，所以电脑不需要公网 IP，也不用在路由器上开端口。

```text
手机 ──WSS── 阿里云 Relay ──WSS── 电脑 A
                        └────WSS── 电脑 B
```

## 0. 开始之前

需要准备的东西：

| 项目 | 要求 |
|---|---|
| 云服务器 | 阿里云 Linux，`uname -m` 为 `x86_64`，有公网地址，安全组放行 `8443/TCP` |
| 电脑 | Windows 10 / 11，已安装并登录 Codex（`codex` 命令可用） |
| 手机 | Android 10（API 29）及以上 |
| 安装包 | `AgentDeck-Setup.exe`、`AgentDeck-Lab.apk`、`agentdeck-relay-linux-amd64` |

先做完第 1 步拿到三个值：**Relay 地址**、**证书指纹**、**注册令牌**。后面电脑端要填这三项。

## 1. 在云服务器上起 Relay（只做一次）

用 SSH 登录云服务器，在独立目录里初始化。把 `你的云机地址` 换成手机和电脑都能访问的地址——用 IP 就写 IP，用域名就写域名，证书按这里填的内容签发。

```sh
mkdir -p "$HOME/agentdeck-enroll/routes"
chmod 700 "$HOME/agentdeck-enroll" "$HOME/agentdeck-enroll/routes"
cd "$HOME/agentdeck-enroll"

# 上传 agentdeck-relay-linux-amd64 到本目录后执行
chmod 700 ./agentdeck-relay-linux-amd64
./agentdeck-relay-linux-amd64 relay-init -config ./relay.local.json \
  -relay-url 'wss://你的云机地址:8443'
```

`relay-init` 只跑一次，会打印证书指纹。生成注册令牌并启动：

```sh
umask 077
openssl rand -hex 32 > enrollment-token
cat enrollment-token          # 这就是「注册令牌」，复制到电脑端

export AGENTDECK_ENROLLMENT_TOKEN="$(cat enrollment-token)"
./agentdeck-relay-linux-amd64 relay -config ./relay.local.json \
  -enrollment-dir ./routes -listen 0.0.0.0:8443
```

启动后会打印监听地址。另开一个终端确认指纹（和 `relay-init` 打印的一致）：

```sh
./agentdeck-relay-linux-amd64 relay-fingerprint -config ./relay.local.json
```

**要让 Relay 常驻**，用 systemd 托管，避免 SSH 断开就掉线：

```sh
cat > "$HOME/agentdeck-enroll/relay.env" <<'EOF'
AGENTDECK_ENROLLMENT_TOKEN=把令牌粘在这里
EOF
chmod 600 "$HOME/agentdeck-enroll/relay.env"
```

```ini
# /etc/systemd/system/agentdeck-relay.service（命令路径按你的实际用户目录改）
[Unit]
Description=AgentDeck Relay
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
EnvironmentFile=/home/你的用户名/agentdeck-enroll/relay.env
WorkingDirectory=/home/你的用户名/agentdeck-enroll
ExecStart=/home/你的用户名/agentdeck-enroll/agentdeck-relay-linux-amd64 relay -config ./relay.local.json -enrollment-dir ./routes -listen 0.0.0.0:8443
Restart=always
RestartSec=3

[Install]
WantedBy=multi-user.target
```

```sh
sudo systemctl daemon-reload
sudo systemctl enable --now agentdeck-relay
systemctl status agentdeck-relay --no-pager
```

> 当前实验证书有效期七天。到期后手机和电脑都会连不上，需要重新 `relay-init` 并重填三项。

## 2. 装电脑端

双击 `AgentDeck-Setup.exe`。它不需要安装向导，直接把程序解压到当前用户目录并启动：

```text
%LOCALAPPDATA%\AgentDeck\app\AgentDeck.exe            ← 主程序
%LOCALAPPDATA%\AgentDeck\app\agentdeck-task-lab.exe   ← 后台 Agent
```

启动后窗口打开，同时系统托盘出现图标。

**下次怎么打开**：安装包不创建桌面和开始菜单快捷方式，需要手动打开上面那个 `AgentDeck.exe`。建议右键它选「发送到 → 桌面快捷方式」，或者右键托盘图标固定到任务栏。若程序已在运行，再双击安装包只会提示「AgentDeck 已运行，请从系统托盘打开」。

### 填云服务器三项

状态页点「配置云服务器」，填：

| 字段 | 填什么 |
|---|---|
| Relay 地址 | `wss://你的云机地址:8443`，必须 `wss://` 开头 |
| 证书 SHA-256 指纹 | 第 1 步 `relay-fingerprint` 打印的 64 位十六进制 |
| 注册令牌 | 第 1 步 `cat enrollment-token` 的输出，64 位十六进制 |

确定后电脑会生成并保存自己的设备身份，向 Relay 注册。注册成功状态页顶部变「已就绪」，`本机身份` 卡片出现设备 ID。

状态页三种状态的含义：

- **已就绪** — Agent 在跑且已注册，手机此刻可以提交任务。
- **已停止** — 进程退出了，重启托盘再检查 Codex 是否可用。
- **未注册** — 还没有 Relay 身份，配置云服务器后手机才能连。

注册成功或失败都会记进状态页的「最近连接事件」，最新一条在最上面，失败原因会写明是哪里对不上。底部的单行状态只是即时提示，会淡出。

## 3. 配项目（决定手机能碰什么）

手机看不到电脑上的目录，只能操作你在这里授权的项目。所以先配白名单，再做别的。

设置页点「管理项目…」：

1. 选一个目录，点新增。新项目**默认停用、默认只读**——这是有意的，防止误开写权限。
2. 在表格「权限」列点一下，可在 `只读` / `可写` 之间切换。
3. 在「启用」列打开它。

几点说明：

- 手机只显示**已启用**的项目，停用的项目对手机不可见，正在跑的任务也会被取消。
- `只读` 下 Codex 能读文件和跑只读命令，写文件和改代码会被拒绝；要让它改代码就切 `可写`。
- 越界（`..`、符号链接、目录联接跳到项目外）由 Agent 在执行前拦下，不是靠工作目录约束。
- 改动对下一次任务生效，正在跑的任务沿用启动时的权限。

## 4. 配对手机

1. 电脑状态页点「显示配对二维码」。
2. 手机打开 AgentDeck，点「扫描电脑配对码」，扫屏幕上的码。
3. **两分钟内**回到电脑，点「核对手机指纹」，把它显示的完整指纹和手机上的对照，一致才点确认。

配对码一次性、两分钟失效，同一时间只允许一个配对请求。配对成功后手机自动进入任务页，项目列表跟着刷新。

手机用 Keystore 保存私钥，之后打开软件沿用同一身份，不用每次扫码。

## 5. 跑第一个任务

手机任务页：

1. 顶部切换到目标电脑，选项目。
2. 输入提示词提交。提交带幂等 ID，网络抖动重试不会起两个任务。
3. Codex 一旦要执行命令或写文件，审批请求会推到手机——**你不需要回到电脑上点**。
4. 进任务看执行轨迹：消息、命令、文件、审批、结束五类分开着色，可按游标补拉。
5. 想停就点叫停，二次确认后取消，工具进程一起退出。
6. 一轮结束后底部变成输入条，打完字「接着说」会续在同一个会话里，模型记得上一轮。

审批规则：

- 有效期 **5 分钟**，超时按拒绝处理，过期或旧运行实例的审批一律无效。
- 「允许一次」先要求系统锁屏验证（指纹/密码）；设备没设锁屏会先弹风险提示。
- 会话级的目录、网络授权请求不推给手机，Agent 直接拒绝。

## 6. 开机自动运行（推荐）

不装服务时，Agent 由托盘启动，**必须先登录 Windows** 才能连上手机。装了服务就能停在登录界面也上线。

设置页「常驻与自启」卡片：

- 现在显示「开机自动运行 · 未安装」→ 点「安装服务」，Windows 会弹 UAC，同意。
- 成功后卡片变「已安装」，`当前方式` 从「托盘进程」变成「Windows 服务」，服务名 `AgentDeckLab`。
- 装好后关掉托盘窗口不影响连接；托盘只用来改配置和看状态。
- 要取消就点「卸载服务」，卸载后回到托盘启动方式。

> 电脑已装服务时，托盘不会再自己起一个 Agent，避免抢同一个设备身份。

## 7. 撤销一台手机

状态页「已配对手机」列出每台手机：指纹、`已信任`、配对时间与证书到期。

点「撤销」并二次确认后，信任文件被删除，**这台手机的现有连接立即断开**，重连必须重新扫码。电脑「最近连接事件」会记一条。

## 8. 常见问题

| 现象 | 原因 | 处理 |
|---|---|---|
| 状态页一直「未注册」 | 地址 / 指纹 / 令牌填错，或 8443 不通 | 先在云机 `curl -k https://127.0.0.1:8443` 看端口；再核对安全组和防火墙；指纹必须与 `relay-fingerprint` 完全一致 |
| 注册报 `fingerprint mismatch: server sent …, configured …` | 填的指纹不是这台 Relay 现在用的那份 | 用报错里 `server sent` 那串；重新生成过 Relay 就一定换了指纹 |
| 注册报 `names …, which does not cover "…"` | 填的地址和证书里的名字不是同一个 | 改成报错里 `names` 后面的那个名字，或在云机按新地址重新 `relay-init` |
| 注册报 `expired at …` | 七天有效期到了 | 云机重新 `relay-init`，然后电脑和手机全部重配 |
| 注册报 `existing device …`（`pinned` / `another Relay`） | 本机还留着旧身份 | 退出托盘，删 `%LOCALAPPDATA%\AgentDeck` 下 `agent.local.json`、`registered-device`、`pairing\`，再重新配置 |
| 手机连不上，电脑显示已就绪 | 手机填的地址和电脑不同，或手机没网 | 手机连接页看延迟；确认手机能打开 `https://云机地址:8443` |
| 配对扫完没反应 | 超过两分钟，或已有别的配对请求 | 重新点「显示配对二维码」，一次只配一台 |
| 手机看不到项目 | 项目未启用 | 设置页把「启用」打开，手机下拉刷新 |
| 任务里写文件被拒 | 项目是只读 | 「权限」列切成 `可写`，重新提交任务 |
| 审批点了允许没执行 | 超过 5 分钟或任务已换轮次 | 重新发起；过期审批按拒绝处理，这是设计行为 |
| 电脑重启后手机连不上 | 没装服务，停在登录界面 | 装开机自启服务（第 6 步） |
| 七天左右突然全断 | 实验证书过期 | 云机重新 `relay-init`，电脑和手机重填三项 |
| Codex 显示「未运行」 | 没装或没登录 | 电脑上跑 `codex` 确认可用；设置页点「重新检测」 |

## 9. 边界：手机能做什么、不能做什么

| 能做 | 不能做 |
|---|---|
| 看已启用项目的任务、轨迹、审批 | 浏览电脑目录、提交任意工作目录 |
| 提交任务、叫停、续话 | 自行添加或启用项目 |
| 批准或拒绝单次操作 | 改电脑上的全局设置、改权限 |
| 看连接状态与延迟 | 截屏、看画面、控制浏览器 |

权限清单只存在电脑上，只能由本机用户改。手机发的请求用 `projectId`，由 Agent 解析成真实目录——手机既拿不到路径，也没法伪造。

## 10. 出问题时给开发者什么

- 电脑：设置页底部「任务日志」目录里的文件（默认 `%LOCALAPPDATA%\AgentDeck\logs`）。
- 电脑：状态页「最近连接事件」那四条（只保留最近 4 条）。
- 手机：连接页显示的延迟、设备 ID、指纹短码。
- 云机：`journalctl -u agentdeck-relay -n 100 --no-pager`。
