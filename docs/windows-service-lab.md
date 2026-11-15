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

## 本地 Codex 任务与输出

实验工具的 `codex-task` 从本机授权清单解析项目 ID，使用已配置的 Codex 账户运行一轮任务。先使用测试项目和专用 Codex home 准备一份 UTF-8 提示文件，例如内容为「只回复 AgentDeck OK」。从仓库重新编译实验工具后运行：

```powershell
.\bin\agentdeck-lab.exe codex-task -codex 'C:\Tools\Codex\codex.exe' -projects 'C:\AgentDeckTest\projects.json' -project-id test-project -codex-home 'C:\AgentDeckTest\CodexHome' -prompt-file 'C:\AgentDeckTest\prompt.txt'
```

输出为逐行 JSON，包括当前轮次的回复增量、工具输出增量和结束状态。实验会话使用 Codex 的 `read-only` 沙箱和 `never` 审批策略。此处的只读模式用于本机测试，跨目录读取边界须按下文单独验证。若工具仍发来当前轮次的命令或文件变更审批，客户端以原请求 ID 回复拒绝或取消，并记录 `approval/declined`。未识别或不匹配当前轮次的交互请求会中止本轮。用户提交的单次批准仍待接入。 [OpenAI Codex App Server 文档](https://learn.chatgpt.com/docs/app-server)

### 补拉任务输出

在 Windows Agent 所在电脑创建一个独立于项目的本机目录，赋予任务进程写入权限和 Agent 读取权限。运行 `codex-task` 时加上 `-task-logs 'C:\AgentDeckTest\TaskLogs'`，保存输出中的 `taskId`。启动同一台电脑的 Agent 或实验服务时，传入同样的 `-task-logs` 和 `-projects`。

```powershell
.\bin\agentdeck-task-lab.exe task-log -config .\phone.json -device a -project-id test-project -task-id '<taskId>' -after 0
```

保持同一设备、项目及任务 ID，下一次查询使用上一页的 `next` 值。末页可返回空数组，收到 `task/finished` 代表任务已经正常记录了最终状态。任务日志保存在电脑上。查询时重新检查项目授权，并验证项目 ID 与本机目录仍与此任务匹配。当前查询支持已知任务 ID，本机任务启动仍由 Windows 命令行发起。

运行中的本机任务可通过托盘右键菜单「取消运行任务」，从本机日志目录选择对应任务日志并确认。菜单在审核窗口等待任务执行期间也可使用。也可以在 Agent 所在电脑运行 `agentdeck-task-lab.exe cancel-task -task-logs <日志绝对路径> -task-id <任务ID>`。请求由任务进程读取并取消 App Server，日志最终状态为 `interrupted`。如果任务在处理请求前已结束，以原结束状态为准。

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

查询返回 `awaiting_local_review`、`claimed` 或 `declined`。Agent 已配置任务日志且任务写入了最终事件时，也可直接返回 `completed`、`failed` 或 `interrupted`。`claimed` 表示已领取，目前尚无可用最终记录。回执不可用时，可用同一 ID 和原提示重试提交。回执中的 `taskId` 同时用于本机审核和日志补拉。

```powershell
.\bin\agentdeck-task-lab.exe review-task -task-inbox 'C:\AgentDeckTest\Inbox' -receipt-id '<taskId>'
.\bin\agentdeck-task-lab.exe review-task -task-inbox 'C:\AgentDeckTest\Inbox' -receipt-id '<taskId>' -accept-once -codex 'C:\Tools\Codex\codex.exe' -codex-home 'C:\AgentDeckTest\CodexHome' -projects 'C:\AgentDeckTest\projects.json' -project-id test-project -task-logs 'C:\AgentDeckTest\TaskLogs'
```

第一条命令展示任务供本机审核。第二条在重新核对项目权限后占用一次启动名额，并执行一轮只读任务。连接断开只影响客户端查询。若本机审核进程在占用名额后中断，重发同一请求仍返回 `claimed`，需先查看本机日志确认结果。这个确认针对任务启动，Codex 工具审批仍由当前只读策略处理。

在 Agent 所在电脑列出待审核任务，或拒绝指定请求：

```powershell
.\bin\agentdeck-task-lab.exe inbox -task-inbox 'C:\AgentDeckTest\Inbox'
.\bin\agentdeck-task-lab.exe review-task -task-inbox 'C:\AgentDeckTest\Inbox' -receipt-id '<taskId>' -decline
```

批准和拒绝使用同一个一次性标记。拒绝后任务从待办移除，客户端用原请求重试时收到 `declined`。

### Windows 托盘实验界面

解压 `agentdeck-tray-windows.zip`，安装相应架构的 .NET 10 Desktop Runtime 后运行 `AgentDeck.Tray.exe`。保留包内文件在同一目录。窗口内填写本机收件箱、项目授权清单、日志目录、Codex 可执行文件和配置目录。刷新后可预览待办、批准启动一次只读任务或拒绝任务。「查看任务日志」可在窗口内读取所选任务日志末尾最多 256 KiB 的完整行。关闭窗口会隐藏到系统托盘，退出托盘不会停止单独运行的 Agent。

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
