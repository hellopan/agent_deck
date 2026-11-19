package com.agentdeck.lab

import android.graphics.Color as AndroidColor
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.MaterialTheme
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Modifier
import com.agentdeck.lab.ui.AboutScreen
import com.agentdeck.lab.ui.ApprovalListScreen
import com.agentdeck.lab.ui.ApprovalScreen
import com.agentdeck.lab.ui.CancelDialog
import com.agentdeck.lab.ui.ConnectScreen
import com.agentdeck.lab.ui.DeckTabBar
import com.agentdeck.lab.ui.DeviceDetailScreen
import com.agentdeck.lab.ui.DeviceListScreen
import com.agentdeck.lab.ui.SubmitDialog
import com.agentdeck.lab.ui.TaskListScreen
import com.agentdeck.lab.ui.Tok
import com.agentdeck.lab.ui.TraceScreen
import com.agentdeck.lab.ui.deskColors
import com.agentdeck.lab.ui.deskTypography

/**
 * Debug-only screen gallery. Nothing here ships in a release build; it exists so
 * each of the seven v3 screens can be opened and compared against
 * design/v3-phone.html without a paired desktop.
 *
 *   adb shell am start -n com.agentdeck.lab/.GalleryActivity \
 *       --es screen approval --ez dark true
 *
 * Screen names: tasks | approvals | approval | trace | follow | devices |
 * device | cancel | submit | connect | about
 */
class GalleryActivity : ComponentActivity() {
    // Kept in a state holder so `am start` on a live instance switches screens
    // instead of silently showing the previous one (onCreate does not re-run).
    private val screenState = mutableStateOf("")
    private val darkState = mutableStateOf(false)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        readIntent(intent)
        val screen = screenState.value
        val dark = darkState.value
        Tok.dark = dark
        enableEdgeToEdge(
            statusBarStyle = if (dark) SystemBarStyle.dark(AndroidColor.TRANSPARENT)
            else SystemBarStyle.light(AndroidColor.TRANSPARENT, AndroidColor.TRANSPARENT),
            navigationBarStyle = if (dark) SystemBarStyle.dark(AndroidColor.TRANSPARENT)
            else SystemBarStyle.light(AndroidColor.TRANSPARENT, AndroidColor.TRANSPARENT),
        )
        setContent {
            val dark = darkState.value
            Tok.dark = dark
            MaterialTheme(colorScheme = deskColors(dark), typography = deskTypography()) {
                val scope = rememberCoroutineScope()
                val desk = remember {
                    Desk(LabState(this), scope).apply {
                        seedPreview(sampleQueue, sampleApprovals, sampleRunning)
                    }
                }
                Box(Modifier.fillMaxSize().background(Tok.Bg)) {
                    when (screenState.value) {
                        // Composed exactly the way MainActivity composes the home
                        // screen, tab bar included — that is what a user sees.
                        "tasks" -> Column(Modifier.fillMaxSize()) {
                            Box(Modifier.fillMaxWidth().weight(1f)) {
                                TaskListScreen(desk, {}, {}, {}, {}, {})
                            }
                            DeckTabBar(selected = 0, approvals = desk.approvals.size, onSelect = {})
                        }
                        "approvals" -> ApprovalListScreen(desk, {}, {})
                        "approval" -> ApprovalScreen(
                            desk = desk,
                            approval = sampleApprovals.first(),
                            device = "DESKTOP-8F2A",
                            busy = false,
                            onAllow = {},
                            onDecline = {},
                            onBack = {},
                            contextPreview = sampleTrace.filter { it.text.isNotBlank() }.takeLast(2),
                        )
                        "trace" -> TraceScreen(
                            desk = desk,
                            item = sampleQueue.first { it.status == "running" },
                            taskId = "7c1e04b26a5d4f8e9b0c1d2e3f405162",
                            projectId = "api-gateway",
                            onBack = {},
                            onCancel = {},
                            preview = sampleTrace,
                        )
                        "follow" -> TraceScreen(
                            desk = desk,
                            item = sampleQueue.first { it.status == "completed" },
                            taskId = "1a2b3c4d5e6f708192a3b4c5d6e7f809",
                            projectId = "web-app",
                            onBack = {},
                            onCancel = {},
                            preview = sampleTrace + TraceEvent(
                                "task/finished", "", "completed", System.currentTimeMillis(),
                            ),
                        )
                        "devices" -> DeviceListScreen(
                            desk = desk,
                            notificationsOn = true,
                            onOpen = {},
                            onScan = {},
                            onAbout = {},
                        )
                        "device" -> DeviceDetailScreen(
                            desk = desk,
                            deviceId = "8f2a1c4d",
                            notificationsOn = true,
                            onBack = {},
                            onPickProject = {},
                        )
                        "cancel" -> {
                            TraceScreen(
                                desk = desk,
                                item = sampleQueue.first { it.status == "running" },
                                taskId = "7c1e04b26a5d4f8e9b0c1d2e3f405162",
                                projectId = "api-gateway",
                                onBack = {},
                                onCancel = {},
                                preview = sampleTrace,
                            )
                            CancelDialog({}, {})
                        }
                        "submit" -> {
                            TaskListScreen(desk, {}, {}, {}, {}, {})
                            SubmitDialog(desk) {}
                        }
                        "connect" -> ConnectScreen(desk, {}, null, {}, {})
                        "about" -> AboutScreen(onBack = {})
                        // The tab bar is part of every home screen; this entry
                        // renders it where it actually sits — at the bottom of a
                        // full-height column — so the badge can be judged.
                        else -> Column(Modifier.fillMaxSize()) {
                            Spacer(Modifier.weight(1f))
                            DeckTabBar(selected = 2, approvals = 2, onSelect = {})
                        }
                    }
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        readIntent(intent)
    }

    private fun readIntent(intent: Intent?) {
        if (intent == null) return
        val dark = intent.getBooleanExtra("dark", darkState.value)
        Tok.dark = dark
        darkState.value = dark
        screenState.value = intent.getStringExtra("screen").orEmpty()
    }
}

private const val MINUTE = 60_000L

private val sampleApprovals = listOf(
    ApprovalItem(
        id = "3f2a91d0c7b64e58a19d0f3c2b5e7a41",
        taskId = "7c1e04b26a5d4f8e9b0c1d2e3f405162",
        projectId = "web-app",
        adapterId = "codex",
        kind = "command",
        command = "rm -rf dist/ && pnpm build",
        cwd = "C:\\work\\web-app",
        reason = "需要删除旧的构建产物再重新打包",
        secondsLeft = 252,
    ),
    ApprovalItem(
        id = "9b8c7d6e5f40312233445566778899aa",
        taskId = "5a4b3c2d1e0f90817263544534231201",
        projectId = "web-app",
        adapterId = "codex",
        kind = "file",
        command = "",
        cwd = "C:\\work\\web-app",
        reason = "写 test/rate-limit.spec.ts · 追加 12 行",
        secondsLeft = 228,
    ),
)

private val sampleQueue = listOf(
    QueueItem(
        taskId = "5a4b3c2d1e0f90817263544534231201",
        projectId = "web-app",
        status = "queued",
        submittedAt = System.currentTimeMillis() - 8 * MINUTE,
        summary = "补全 src/user 下 5 个模块的单元测试",
        prompt = "补全 src/user 下 5 个模块的单元测试",
        threadId = "",
    ),
    QueueItem(
        taskId = "7c1e04b26a5d4f8e9b0c1d2e3f405162",
        projectId = "api-gateway",
        status = "running",
        submittedAt = System.currentTimeMillis() - 12 * MINUTE,
        summary = "排查限流中间件的并发问题",
        prompt = "排查限流中间件的并发问题，复现后给出最小修复",
        threadId = "01a1e02e-b9a6-74c3-9462-b22f77b5cee0",
    ),
    QueueItem(
        taskId = "1a2b3c4d5e6f708192a3b4c5d6e7f809",
        projectId = "web-app",
        status = "completed",
        submittedAt = System.currentTimeMillis() - 53 * MINUTE,
        summary = "pnpm lint --fix",
        prompt = "pnpm lint --fix",
        threadId = "01a1e02e-b9a6-74c3-9462-b22f77b5cee0",
    ),
    QueueItem(
        taskId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0",
        projectId = "web-app",
        status = "interrupted",
        submittedAt = System.currentTimeMillis() - 131 * MINUTE,
        summary = "git push origin feat/ui",
        prompt = "git push origin feat/ui",
        threadId = "",
    ),
)

private val sampleRunning = RunningTask(
    taskId = "7c1e04b26a5d4f8e9b0c1d2e3f405162",
    projectId = "api-gateway",
    startedAt = System.currentTimeMillis() - 12 * MINUTE,
    waitingFor = "item/commandExecution/requestApproval",
)

private val sampleTrace = listOf(
    TraceEvent("turn/started", "", "", System.currentTimeMillis() - 12 * MINUTE),
    TraceEvent("item/agentMessage/delta", "先读限流中间件与它的调用方，确认是否存在共享状态。", "", System.currentTimeMillis() - 12 * MINUTE + 7_000),
    TraceEvent("item/commandExecution/outputDelta", "rate-limit.ts  218 lines\nrate-limit.spec.ts  94 lines", "", System.currentTimeMillis() - 11 * MINUTE),
    TraceEvent("approval/requested", "命令 npm test -- --runInBand", "", System.currentTimeMillis() - 10 * MINUTE),
    TraceEvent("approval/approved", "命令 npm test -- --runInBand", "accept", System.currentTimeMillis() - 10 * MINUTE + 4_000),
    TraceEvent("item/commandExecution/outputDelta", "PASS  test/rate-limit.spec.ts\n  ✓ rejects bursts above the window limit (312 ms)", "", System.currentTimeMillis() - 2 * MINUTE),
    TraceEvent("item/agentMessage/delta", "并发窗口用的是进程内 Map，多实例部署时会各算一份。建议改成 Redis 计数，或把窗口收敛到单实例。", "", System.currentTimeMillis() - MINUTE),
    TraceEvent("turn/completed", "", "completed", System.currentTimeMillis() - 30_000),
)
