package com.agentdeck.lab.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.agentdeck.lab.ApprovalItem
import com.agentdeck.lab.Desk
import com.agentdeck.lab.QueueItem
import com.agentdeck.lab.deviceLabel
import com.agentdeck.lab.shortId

internal data class Risk(val text: String, val fg: Color, val bg: Color)

internal val RISK_DELETE = Risk("删除", Tok.Red, Tok.RedBg)
internal val RISK_WRITE = Risk("写文件", Tok.Amber, Tok.AmberBg)
internal val RISK_RUN = Risk("执行", Tok.Tx2, Tok.Panel2)

internal fun riskOf(approval: ApprovalItem): Risk {
    val command = approval.command.lowercase()
    val destructive = listOf("rm -rf", "rm -r", "rmdir", "del /", "remove-item", "format ", "drop table", "git push")
    return when {
        destructive.any { command.contains(it) } -> RISK_DELETE
        approval.kind.contains("file", true) -> RISK_WRITE
        else -> RISK_RUN
    }
}

private data class StatusChip(val text: String, val fg: Color, val bg: Color)

private fun statusOf(item: QueueItem): StatusChip = when (item.status) {
    "queued" -> StatusChip("待处理", Tok.Amber, Tok.AmberBg)
    "running" -> StatusChip("运行中", Tok.Green, Tok.GreenBg)
    "completed" -> StatusChip("已完成", Tok.Tx2, Tok.Panel2)
    "failed" -> StatusChip("失败", Tok.Red, Tok.RedBg)
    "interrupted" -> StatusChip("已中断", Tok.Red, Tok.RedBg)
    "canceled" -> StatusChip("已叫停", Tok.Tx3, Tok.Panel2)
    else -> StatusChip(item.status, Tok.Tx3, Tok.Panel2)
}

@Composable
internal fun TaskListScreen(
    desk: Desk,
    onApproval: (String) -> Unit,
    onTrace: (QueueItem) -> Unit,
    onSubmit: () -> Unit,
    onStatus: () -> Unit,
) {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val pending = desk.approvals
    val queued = desk.items.filter { it.status == "queued" }
    val runningItems = desk.items.filter { it.status == "running" }
    val done = desk.items.filter { it.finished }

    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        NavTop("任务", trailing = {
            Row(
                Modifier.clip(RoundedCornerShape(6.dp)).clickable(role = Role.Button, onClick = onStatus)
                    .padding(horizontal = 4.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Dot(if (desk.reachable == true) Tok.Green else Tok.Tx3)
                Spacer(Modifier.width(6.dp))
                Text(
                    desk.latency?.let { it.toString() + "ms" } ?: "状态",
                    color = Tok.Tx3,
                    fontSize = 10.5.sp,
                    fontFamily = Tok.Mono,
                )
                Spacer(Modifier.width(4.dp))
                Chevron(Tok.Tx3, Modifier.size(9.dp).rotate(180f))
            }
        })
        Column(Modifier.fillMaxWidth().padding(horizontal = 18.dp).weight(1f)) {
            DeviceRow(desk)
            Spacer(Modifier.height(12.dp))
            SegTabs(
                listOf(
                    "待处理 " + (pending.size + queued.size),
                    "运行中 " + runningItems.size,
                    "已完成 " + done.size,
                ),
                tab,
            ) { tab = it }
            Spacer(Modifier.height(12.dp))
            Box(Modifier.fillMaxWidth().weight(1f)) {
                when (tab) {
                    0 -> LazyColumn(
                        Modifier.fillMaxSize(),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                        contentPadding = PaddingValues(bottom = 10.dp),
                    ) {
                        items(pending, key = { "a" + it.id }) { approval ->
                            ApprovalRow(approval) { onApproval(approval.id) }
                        }
                        items(queued, key = { "q" + it.taskId }) { item ->
                            QueueRow(item, null) { onTrace(item) }
                        }
                        if (pending.isEmpty() && queued.isEmpty()) {
                            item { EmptyState("没有待处理的任务", "手机提交的任务会排在这里，电脑空闲时依次执行。") }
                        }
                        if (done.isNotEmpty()) {
                            item {
                                Box(
                                    Modifier.fillMaxWidth().padding(top = 6.dp, bottom = 2.dp)
                                        .clickable(role = Role.Button) { tab = 2 },
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Text(
                                        "今天已处理 " + done.size + " 条 · 点按查看",
                                        color = Tok.Tx3,
                                        fontSize = 10.5.sp,
                                    )
                                }
                            }
                        }
                    }
                    1 -> LazyColumn(
                        Modifier.fillMaxSize(),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                        contentPadding = PaddingValues(bottom = 10.dp),
                    ) {
                        items(runningItems, key = { "r" + it.taskId }) { item ->
                            QueueRow(item, desk, { onTrace(item) })
                        }
                        if (runningItems.isEmpty()) {
                            item { EmptyState("现在没有任务在跑", "待处理的任务被电脑取走后会出现在这里。") }
                        }
                    }
                    else -> LazyColumn(
                        Modifier.fillMaxSize(),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                        contentPadding = PaddingValues(bottom = 10.dp),
                    ) {
                        items(done.take(20), key = { "d" + it.taskId }) { item ->
                            QueueRow(item, null) { onTrace(item) }
                        }
                        if (done.isEmpty()) {
                            item { EmptyState("还没有完成的任务", "任务结束后会保留在这里，最多显示 20 条。") }
                        }
                    }
                }
            }
            Column(Modifier.fillMaxWidth().padding(top = 12.dp, bottom = 24.dp)) {
                PButton(
                    "提交新任务",
                    kind = ButtonKind.Primary,
                    enabled = desk.paired && !desk.busy,
                    leading = { PlusGlyph(Color.White, Modifier.size(15.dp)) },
                    onClick = onSubmit,
                )
            }
        }
    }
}

@Composable
private fun DeviceRow(desk: Desk) {
    val devices = desk.config?.optJSONObject("devices")?.keys()?.asSequence()?.toList().orEmpty()
    if (devices.isEmpty()) {
        DeskCard {
            Text("还没有配对的电脑", color = Tok.Tx2, fontSize = 12.sp)
            Spacer(Modifier.height(4.dp))
            Text("用电脑端显示的二维码扫码配对。", color = Tok.Tx3, fontSize = 10.5.sp)
        }
        return
    }
    LazyRow(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
        items(devices) { id ->
            val selected = id == desk.device
            val online = selected && desk.reachable == true
            Column(
                Modifier.width(150.dp).clip(RoundedCornerShape(8.dp))
                    .background(if (selected) Tok.AmberBg else Tok.Panel)
                    .border(1.dp, if (selected) Tok.Amber.copy(alpha = 0.45f) else Tok.Line, RoundedCornerShape(8.dp))
                    .clickable(role = Role.Tab) { desk.switchDevice(id) }
                    .padding(horizontal = 10.dp, vertical = 9.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Dot(if (online) Tok.Green else Tok.Tx3, 5.dp)
                    Spacer(Modifier.width(6.dp))
                    Text(
                        deviceLabel(desk.hosts[id], id),
                        color = Tok.Tx2,
                        fontSize = 10.5.sp,
                        fontFamily = Tok.Mono,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                Spacer(Modifier.height(6.dp))
                Text(
                    when {
                        online -> if (desk.approvals.isEmpty()) "在线" else desk.approvals.size.toString() + " 条待批准"
                        selected -> "连不上"
                        else -> "点按切换"
                    },
                    color = if (selected) Tok.Amber else Tok.Tx3,
                    fontSize = 10.sp,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
private fun ApprovalRow(approval: ApprovalItem, onClick: () -> Unit) {
    val risk = riskOf(approval)
    DeskCard(
        modifier = Modifier.clickable(role = Role.Button, onClick = onClick),
        border = if (risk === RISK_DELETE) Tok.Red.copy(alpha = 0.35f) else Tok.Line,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Tag(risk.text, risk.fg, risk.bg)
            Spacer(Modifier.width(7.dp))
            Text(approval.projectId, color = Tok.Tx3, fontSize = 11.sp, fontFamily = Tok.Mono, maxLines = 1)
            Spacer(Modifier.weight(1f))
            Text(
                fmtTtl(approval.secondsLeft),
                color = Tok.Amber,
                fontSize = 10.sp,
                fontFamily = Tok.Mono,
            )
        }
        Spacer(Modifier.height(7.dp))
        Text(
            approval.command.ifEmpty { approval.reason.ifEmpty { "请求执行一次操作" } },
            color = Tok.Tx,
            fontSize = 12.5.sp,
            fontFamily = Tok.Mono,
            lineHeight = 19.sp,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                "Codex · " + tailPath(approval.cwd),
                color = Tok.Tx3,
                fontSize = 10.sp,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            Spacer(Modifier.weight(1f))
            Text("待批准", color = Tok.Amber, fontSize = 10.sp)
        }
    }
}

@Composable
private fun QueueRow(item: QueueItem, desk: Desk?, onClick: () -> Unit) {
    val chip = statusOf(item)
    DeskCard(
        modifier = Modifier.clickable(role = Role.Button, onClick = onClick),
        border = if (item.running) Tok.Green.copy(alpha = 0.3f) else Tok.Line,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Tag(chip.text, chip.fg, chip.bg)
            Spacer(Modifier.width(7.dp))
            Text(item.projectId, color = Tok.Tx3, fontSize = 11.sp, fontFamily = Tok.Mono, maxLines = 1)
            Spacer(Modifier.weight(1f))
            Text(clock(item.submittedAt), color = Tok.Tx3, fontSize = 10.sp, fontFamily = Tok.Mono)
        }
        Spacer(Modifier.height(7.dp))
        Text(
            item.summary.ifEmpty { "（空提示）" },
            color = Tok.Tx,
            fontSize = 12.5.sp,
            fontFamily = Tok.Mono,
            lineHeight = 19.sp,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(
                if (item.running) "Codex · 只读沙箱 · 运行中" else "Codex · 只读沙箱",
                color = Tok.Tx3,
                fontSize = 10.sp,
                maxLines = 1,
            )
            Spacer(Modifier.weight(1f))
            if (item.running && desk?.running?.waitingFor?.isNotEmpty() == true) {
                Text("等你批准", color = Tok.Amber, fontSize = 10.sp)
            } else if (item.running) {
                Text("查看轨迹", color = Tok.Green, fontSize = 10.sp)
            } else if (item.finished) {
                Text(shortId(item.taskId), color = Tok.Tx3, fontSize = 10.sp, fontFamily = Tok.Mono)
            }
        }
    }
}

internal fun fmtTtl(seconds: Int): String {
    val safe = seconds.coerceAtLeast(0)
    return String.format("%d:%02d", safe / 60, safe % 60)
}

internal fun tailPath(path: String, max: Int = 24): String {
    val trimmed = path.trimEnd('\\', '/')
    if (trimmed.isEmpty()) return "需要你批准"
    return if (trimmed.length <= max) trimmed else "…" + trimmed.takeLast(max)
}

internal fun clock(millis: Long?): String {
    if (millis == null) return "--:--"
    return java.time.Instant.ofEpochMilli(millis)
        .atZone(java.time.ZoneId.systemDefault())
        .format(java.time.format.DateTimeFormatter.ofPattern("HH:mm"))
}

internal fun stamp(millis: Long?): String {
    if (millis == null) return "--:--:--"
    return java.time.Instant.ofEpochMilli(millis)
        .atZone(java.time.ZoneId.systemDefault())
        .format(java.time.format.DateTimeFormatter.ofPattern("HH:mm:ss"))
}
