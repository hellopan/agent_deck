# Windows 服务和 Codex 启动验证

实验工具的 `service` 模式由 Windows 服务控制管理器启动。它使用既有 Agent 实验配置连接 Relay，并在本机追加运行日志。`codex-check` 也可单独运行，便于区分 Codex 自身启动问题和服务账户的权限问题。

## 本机检查 Codex

构建 `agentdeck-lab.exe` 后，为检查创建一个测试项目目录、专用的 Codex home，以及只允许本机管理员编辑的 `projects.json`。使用项目真实的绝对目录，按文件系统的目录名填写，且路径不经过符号链接或目录联接。

```json
{
  "projects": [
    {
      "id": "test-project",
      "directory": "C:\\AgentDeckTest\\Project",
      "enabled": true
    }
  ]
}
```

启动检查以 `-project-id` 查找本地授权清单。不存在、停用或目录被重定向的项目无法启动检查。设为 `"enabled": false` 后重新运行同一命令，应看到授权失败。

```powershell
.\bin\agentdeck-lab.exe codex-check -codex 'C:\Tools\Codex\codex.exe' -projects 'C:\AgentDeckTest\projects.json' -project-id test-project -codex-home 'C:\AgentDeckTest\CodexHome'
```

检查启动 `codex app-server` 的 stdio 接口，完成 `initialize` 和 `initialized`，然后用 `account/read` 读取本机账号状态，关闭进程。输出只包含 `account_configured` 和 `openai_auth_required` 两个布尔值。全新 Codex home 可以通过握手，但需要 OpenAI 登录时会显示 `account_configured=false`。这一步不刷新凭据，也不创建会话或发起模型任务。 [Codex App Server 文档](https://learn.chatgpt.com/docs/app-server)

## 自动检测本机工具

`-codex` 与 `-codex-home` 都可以省略，省略时由 Agent 自己找；`detect` 单独输出三款软件的结果，托盘「设置」页的「检测」按钮读的就是它。

```powershell
.\bin\agentdeck-lab.exe detect
```

每条结果包含 `name`、`path`、`source`、`usable`，不可用时多一个 `problem`。查找顺序是：显式配置、Codex 的每构建安装目录（`%LOCALAPPDATA%\OpenAI\Codex\bin\*`）、注册表卸载项、开始菜单快捷方式、各固定磁盘的 `Program Files`、`PATH`。装在 `D:\00-Softwares` 这类非默认目录的软件靠注册表和快捷方式找到，不需要手填。

一条硬性判据：Codex 必须与 `codex-code-mode-host.exe` 同目录。`%USERPROFILE%\.codex\.sandbox-bin\codex.exe` 是 Codex 自用的裁剪副本，缺这个宿主程序，任务里的 shell 工具会报 `failed to spawn code-mode host ... (os error 2)`，而且这一轮仍然正常结束——表现出来是手机永远收不到审批请求。检测会把这类副本标成 `usable: false` 并写明原因，不会静默选中它。

托盘启动 Agent 时，只有填了才传 `-codex` / `-codex-home`；留空即由 Agent 自动检测。

## 本地 Codex 任务与输出

任务由 Agent 自己消费，不需要在本机点批准。启动 Agent 时传入 `-task-inbox`、`-task-logs`、`-projects` 和 `-codex`，后台单线程按提交时间取出最旧的一条，写任务日志、跑一轮 Codex，结束时记录最终状态。手机提交后即开始执行，这正是「人离开电脑，Codex 继续干活」的前提。

实验会话使用 Codex 的 `read-only` 沙箱和 `on-request` 审批策略：读操作在沙箱内直接放行，需要越界的命令执行与文件写入会挂起并等待已配对手机批准。批准只对那一次操作有效，不缓存、不继承；5 分钟无人处理按拒绝处理，本轮继续往下走。请求额外目录或网络权限会被直接拒绝，不会推到手机，因为那会把边界从「一次命令」放大成「整个会话」。 [OpenAI Codex App Server 文档](https://learn.chatgpt.com/docs/app-server)

### 补拉任务输出

在 Windows Agent 所在电脑创建一个独立于项目的本机目录，赋予任务进程写入权限和 Agent 读取权限。启动 Agent 时用 `-task-logs 'C:\AgentDeckTest\TaskLogs'` 指定它。日志文件名就是回执里的 `taskId`，Agent 未配置日志目录时不消费队列。

```powershell
.\bin\agentdeck-task-lab.exe task-log -config .\phone.json -device a -project-id test-project -task-id '<taskId>' -after 0
```

保持同一设备、项目及任务 ID，下一次查询使用上一页的 `next` 值。末页可返回空数组，收到 `task/finished` 代表任务已经正常记录了最终状态。任务日志保存在电脑上。查询时重新检查项目授权，并验证项目 ID 与本机目录仍与此任务匹配。任务由 Agent 队列启动，客户端只负责查询和决定。

运行中的任务可通过托盘右键菜单「取消运行任务」，或在本机运行 `agentdeck-task-lab.exe cancel-task -task-inbox <收件箱绝对路径> -projects <清单> -project-id <项目> -task-id <任务ID> -task-logs <日志绝对路径>`。请求会同时写入收件箱与任务日志，Agent 读取后取消 App Server，日志最终状态为 `interrupted`；尚未开始的任务直接标记为 `canceled`，不会被执行。

已配对的实验客户端可通过 Relay 发送取消请求。Windows Agent 需要使用新版程序，并配置原任务使用的 `-task-logs` 和 `-projects`。

```powershell
.\bin\agentdeck-task-lab.exe remote-cancel -config .\phone.json -device a -project-id test-project -task-id '<taskId>'
```

`{"requested":true}` 表示 Agent 已记录请求。重复发送同一取消请求安全，最终状态从 `task-log` 查询。

### 提交任务到本机收件箱

在 Agent 所在电脑创建私有目录存放收件箱和日志。启动 Agent 或实验服务时传入 `-task-inbox`、`-task-logs` 和 `-projects` 的绝对路径。收件箱包含原始提示，只向本机 Agent 和负责审核的账号开放。

```powershell
$requestId = [Guid]::NewGuid().ToString('N')
.\bin\agentdeck-task-lab.exe submit -config .\phone.json -device a -project-id test-project -request-id $requestId -prompt-file 'C:\AgentDeckTest\prompt.txt'
```

客户端应保存原请求 ID。连接中断时，可以先按原请求 ID 查回执。若已收到回执，也可用 `-task-id` 代替 `-request-id`。

```powershell
.\bin\agentdeck-task-lab.exe task-receipt -config .\phone.json -device a -project-id test-project -request-id $requestId
```

查询返回 `queued`、`running`、`canceled`、`completed`、`failed` 或 `interrupted`。`queued` 表示已落盘等待 Agent 取走，`running` 表示正在执行。回执不可用时，可用同一 ID 和原提示重试提交。

### 队列与审批

已配对客户端可以读取任务队列，队列项包含项目、状态、提交时间、提示摘要与完整提示：

```powershell
.\bin\agentdeck-task-lab.exe queue -config .\phone.json -device a
```

Agent 侧也可以直接列出同一份队列，用于托盘与排障：

```powershell
.\bin\agentdeck-task-lab.exe inbox -task-inbox 'C:\AgentDeckTest\Inbox' -projects 'C:\AgentDeckTest\projects.json' -task-logs 'C:\AgentDeckTest\TaskLogs'
```

当 Codex 需要一个越界操作时，Agent 会挂起并把请求推到手机。读取待批列表，然后决定：

```powershell
.\bin\agentdeck-task-lab.exe approvals -config .\phone.json -device a
.\bin\agentdeck-task-lab.exe approve -config .\phone.json -device a -approval-id '<approvalId>' -decision accept
```

`approvals` 返回 `approvalId`、所属 `taskId`、`kind`（`command` 或 `file`）、`command`、`cwd`、`reason` 与 `secondsLeft`，以及当前运行中的任务。`approve` 只接受 `accept` 与 `decline`；同一条审批只能用一次，换一台手机、换一台电脑都无法决定同一条。

### 会话与续轮

一个任务是一轮对话，不是一次独立请求。首轮由 Agent 执行 `thread/start` 拿到 thread id，落盘成收件箱里的 `<taskId>.thread.json`，`queue` 会把 `threadId` 一并返回。想接着说，就用同一个 `threadId` 再提交一次：

```powershell
.\bin\agentdeck-task-lab.exe submit -config .\phone.json -device a -project-id test-project -request-id $requestId -thread-id '01a1e02e-b9a6-74c3-9462-b22f77b5cee0' -prompt-file 'C:\AgentDeckTest\prompt.txt'
```

带 `threadId` 的提交走 `thread/resume`，模型保留前面每一轮的内容。三条硬约束，都来自对 Codex 0.167.0-alpha.3.2 的实测：

- 首轮必须 `ephemeral: false`。临时线程不写 rollout，之后 `thread/resume` 返回 `no rollout found`，这轮对话就断了。
- rollout 在第一个 turn 真正运行时才创建。只有 `thread/start`、没跑过 turn 的线程续不上。
- 续接只能指向本 Agent 在同一项目里建过的会话，提交时按收件箱里的 `*.thread.json` 核对，对不上直接拒绝，避免手机要求电脑去读任意线程。

续不上时任务状态是 `failed`，轨迹里会多一条 `task/notice` 说明原因。Agent 不会悄悄开一个新会话替你把话说完——那样模型不知道上文，答非所问还看不出来。

### Windows 托盘实验界面

解压 `agentdeck-tray-windows.zip`，安装相应架构的 .NET 10 Desktop Runtime 后运行 `AgentDeck.Tray.exe`。保留包内文件在同一目录。窗口内填写本机收件箱、项目授权清单、日志目录、Codex 可执行文件和配置目录。刷新后可查看任务队列、预览提示与状态、叫停任务。审批只在手机上完成，托盘不提供放行入口。「查看任务日志」可在窗口内读取所选任务日志末尾最多 256 KiB 的完整行。关闭窗口会隐藏到系统托盘，退出托盘不会停止单独运行的 Agent。

这版界面用当前 Windows 用户调用同目录的实验工具。点击「保存路径」后，下次启动会从 `%LOCALAPPDATA%\AgentDeck\tray-settings.json` 加载本机路径。

「管理项目」编辑所填路径指向的本机授权清单。请与 Agent 的 `-projects` 使用同一文件。可以在已有的本机目录下指定一个新 JSON 文件，或打开现有清单。用目录选择器添加项目，填写唯一 ID，勾选「启用」并保存。新增项目默认停用。取消勾选或移除后，Agent 在后续项目查询、任务提交和日志读取时重新校验授权。运行中的实验任务每 500 毫秒及输出事件前复查授权。项目停用、移除或目录变更会请求取消 Codex App Server 进程，并在本机任务日志记录 `interrupted`。编辑器需要当前 Windows 用户对清单目录有写权限。

## 在 Windows 测试机注册服务

1. 先按 [Relay 原型文档](transport-prototype.md) 生成实验配置并启动 Relay。将 Windows 可执行文件放到由管理员维护的安装目录，仅复制分配给测试电脑的 Agent 配置。
2. 为实验服务账户授予可执行文件、Agent 配置和项目授权清单的读取权限，并为独立日志目录授予写入权限。若同时跑 Codex 检查，确保服务账户有权执行安装的 Codex、访问测试项目并使用其专属的 Codex home。Codex home 权限应限制为该账户和管理员。使用 `LocalService` 可验证低权限启动与传输，正式接入使用独立的低权限账户和凭据。 [Microsoft LocalService 说明](https://learn.microsoft.com/en-us/windows/win32/services/localservice-account)
3. 在测试机的管理员 PowerShell 7 中，替换下面的绝对路径后注册服务。只验证 Relay 时，去掉 `-codex`、`-projects`、`-project-id` 和 `-codex-home` 参数。

```powershell
$serviceName = 'AgentDeckLab'
$serviceCommand = '"C:\Program Files\AgentDeckLab\agentdeck-lab.exe" service -name AgentDeckLab -config "C:\ProgramData\AgentDeckLab\agent-a.json" -log "C:\ProgramData\AgentDeckLab\logs\service.log" -device a -codex "C:\Tools\Codex\codex.exe" -projects "C:\AgentDeckTest\projects.json" -project-id test-project -codex-home "C:\AgentDeckTest\CodexHome"'
sc.exe create $serviceName 'binPath=' $serviceCommand 'start=' 'auto' 'obj=' 'NT AUTHORITY\LocalService'
sc.exe start $serviceName
sc.exe query $serviceName
```

4. 从另一台机器用原 `phone.json` 配置探测设备 A。重启测试机并停在登录界面，再次探测 A。登录后核对服务日志中的启动时间、已认证探测和 Codex 就绪结果。即使 Codex 启动检查失败，传输 Agent 仍可独立上线。

实验结束后，先用 `sc.exe stop AgentDeckLab` 停止服务，确认已停止，再用 `sc.exe delete AgentDeckLab` 移除实验注册。 [Microsoft sc.exe 文档](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-create)

## 验证 Codex 命令的项目边界

项目授权负责选择工作目录，命令执行还需要独立的进程级边界。Windows 回归测试使用临时 `CODEX_HOME` 和显式 MXC 后端。测试配置只授予项目写入和系统最小运行路径读取权限，关闭命令网络访问。该配置只用于实验，未写入用户现有 Codex 配置。 [Windows MXC 说明](https://learn.chatgpt.com/docs/windows/windows-sandbox#mxc-compatibility) · [Codex 权限配置](https://learn.chatgpt.com/docs/permissions#file-access-limited-to-workspace)

在准备验证的 Windows 账号下，从仓库根目录运行。`bin` 用于存放测试期间生成的合成文件和临时配置，测试结束后自动清理。

```powershell
New-Item -ItemType Directory -Path bin -Force | Out-Null
$env:AGENTDECK_TEST_CODEX = (Get-Command codex.exe).Source
$env:AGENTDECK_TEST_CODEX_SANDBOX_ROOT = (Resolve-Path -LiteralPath bin).Path
go test -run '^TestWindowsCodexProjectBoundary$' -v ./internal/codexprobe
```

测试先确认合成文件在基线进程中确实可以访问，再分别通过 Codex CLI 沙箱和 App Server `command/exec` 执行同一个探针。两条链路都必须能读写项目内文件，拒绝读写项目外的相邻测试目录。测试还从宿主侧确认项目外写入未发生。启动或配置失败会让测试失败。

本机的两条链路均已通过。服务账户停在登录界面时的运行、模型发起的真实任务及审批仍需分别验收。若目标电脑的 MXC 不可用，具备拒绝读取能力的替代执行环境也需通过同样的项目边界验收，才能开放远程任务。
