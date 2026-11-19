package com.agentdeck.lab.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
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

internal data class Risk(val text: String, val fg: Color, val bg: Color, val line: Color)

internal val RISK_DELETE = Risk("命令", Tok.Red, Tok.RedBg, Tok.RedLine)
internal val RISK_WRITE = Risk("写文件", Tok.Amber, Tok.AmberBg, Tok.AmberLine)
internal val RISK_RUN = Risk("执行", Tok.Tx2, Tok.TagDone, Tok.Line)

internal fun riskOf(approval: ApprovalItem): Risk {
    val command = approval.command.lowercase()
    val destructive = listOf("rm -rf", "rm -r", "rmdir", "del /", "remove-item", "format ", "drop table", "git push")
    return when {
        destructive.any { command.contains(it) } -> RISK_DELETE
        approval.kind.contains("file", true) -> RISK_WRITE
        else -> RISK_RUN
    }
}

private data class StatusChip(val text: String, val fg: Color, val bg: Color, val line: Color)

private fun statusOf(item: QueueItem): StatusChip = when (item.status) {
    "queued" -> StatusChip("待处理", Tok.Amber, Tok.AmberBg, Tok.AmberLine)
    "running" -> StatusChip("运行中", Tok.Green, Tok.GreenBg, Tok.GreenLine)
    "completed" -> StatusChip("已完成", Tok.Tx2, Tok.TagDone, Tok.Line)
    "failed" -> StatusChip("失败", Tok.Red, Tok.RedBg, Tok.RedLine)
    "interrupted" -> StatusChip("已中断", Tok.Red, Tok.RedBg, Tok.RedLine)
    "canceled" -> StatusChip("已叫停", Tok.Tx3, Tok.TagDone, Tok.Line)
    else -> StatusChip(item.status, Tok.Tx3, Tok.TagDone, Tok.Line)
}

/**
 * Screen 1. The feed is a live one: approvals on top, then queued and running
 * work, then a link to the finished list. A separate tab for each would make
 * "what needs me right now" a thing you have to assemble from three screens.
 */
@Composable
internal fun TaskListScreen(
    desk: Desk,
    onApproval: (String) -> Unit,
    onTrace: (QueueItem) -> Unit,
    onSubmit: () -> Unit,
    onDevices: () -> Unit,
    onStatus: () -> Unit,
) {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) {
        while (true) {
            now = System.currentTimeMillis()
            kotlinx.coroutines.delay(1_000)
        }
    }

    val pending = desk.approvals
    val queued = desk.items.filter { it.status == "queued" }
    val running = desk.items.filter { it.status == "running" }
    val done = desk.items.filter { it.finished }
    val attention = pending.size + queued.size

    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        Row(
            Modifier.fillMaxWidth().padding(start = 18.dp, end = 18.dp, top = 6.dp, bottom = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(
                Modifier.weight(1f).clip(RoundedCornerShape(8.dp))
                    .clickable(role = Role.Button, onClick = onDevices).padding(vertical = 2.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        desk.label + " · " + (desk.project.ifEmpty { "未选项目" }),
                        color = Tok.Tx,
                        fontSize = 17.sp,
                        fontWeight = FontWeight.SemiBold,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f, fill = false),
                    )
                    Spacer(Modifier.width(5.dp))
                    Chevron(Tok.Tx3, Modifier.size(12.dp), down = true)
                }
                Spacer(Modifier.height(3.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Dot(if (desk.reachable == true) Tok.Green else Tok.Tx3)
                    Spacer(Modifier.width(6.dp))
                    Text(
                        buildString {
                            append(if (desk.reachable == true) "已连接" else "连不上")
                            desk.latency?.let { append(" · ").append(it).append("ms") }
                            if (pending.isNotEmpty()) append(" · ").append(pending.size).append(" 个待审批")
                        },
                        color = Tok.Tx3,
                        fontSize = 12.5.sp,
                        maxLines = 1,
                    )
                }
            }
            Box(
                Modifier.size(36.dp).clip(RoundedCornerShape(10.dp))
                    .clickable(role = Role.Button, onClick = onStatus),
                contentAlignment = Alignment.Center,
            ) { MenuGlyph(Tok.Tx2, Modifier.size(20.dp)) }
        }

        Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp).weight(1f)) {
            SegTabs(
                listOf("$attention 待处理", "${running.size} 运行中", "${done.size} 已完成"),
                tab,
            ) { tab = it }
            Spacer(Modifier.height(12.dp))
            Box(Modifier.fillMaxWidth().weight(1f)) {
                LazyColumn(
                    Modifier.fillMaxSize(),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                    contentPadding = PaddingValues(bottom = 12.dp),
                ) {
                    if (tab == 0) {
                        items(pending, key = { "a" + it.id }) { approval ->
                            ApprovalRow(approval) { onApproval(approval.id) }
                        }
                        items(queued, key = { "q" + it.taskId }) { item ->
                            QueueRow(item) { onTrace(item) }
                        }
                        items(running, key = { "r" + it.taskId }) { item ->
                            RunningRow(item, now) { onTrace(item) }
                        }
                        if (attention == 0 && running.isEmpty()) {
                            item {
                                EmptyState("现在没有待处理的任务", "手机提交的任务会排在这里，电脑空闲时依次执行。")
                            }
                        }
                        if (done.isNotEmpty()) {
                            item {
                                Box(
                                    Modifier.fillMaxWidth().padding(top = 2.dp, bottom = 4.dp)
                                        .clickable(role = Role.Button) { tab = 2 },
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Text("今天已完成 ${done.size} 条 · 点按查看", color = Tok.Tx3, fontSize = 12.sp)
                                }
                            }
                        }
                    }
                    if (tab == 1) {
                        items(running, key = { "r" + it.taskId }) { item ->
                            RunningRow(item, now) { onTrace(item) }
                        }
                        if (running.isEmpty()) {
                            item { EmptyState("现在没有任务在跑", "待处理的任务被电脑取走后会出现在这里。") }
                        }
                    }
                    if (tab == 2) {
                        items(done.take(20), key = { "d" + it.taskId }) { item ->
                            QueueRow(item) { onTrace(item) }
                        }
                        if (done.isEmpty()) {
                            item { EmptyState("还没有完成的任务", "任务结束后会保留在这里，最多显示 20 条。") }
                        }
                    }
                }
            }
        }
        Dock(
            placeholder = "描述你要做的改动",
            hint = "提交后带 requestId，断线重试不会重复执行",
            enabled = desk.paired && !desk.busy,
            onClick = onSubmit,
        )
        Spacer(Modifier.height(10.dp))
    }
}

/** Card with the 3px status bar on its left edge. */
@Composable
private fun EdgeCard(edge: Color, border: Color = Tok.Line, onClick: (() -> Unit)? = null,
                     content: @Composable androidx.compose.foundation.layout.ColumnScope.() -> Unit) {
    val base = Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
        .border(1.dp, border, RoundedCornerShape(16.dp))
    Box(if (onClick != null) base.clickable(role = Role.Button, onClick = onClick) else base) {
        Row(Modifier.height(IntrinsicSize.Min)) {
            Box(
                Modifier.padding(vertical = 12.dp).padding(start = 0.dp).width(3.dp)
                    .fillMaxHeight().clip(RoundedCornerShape(2.dp)).background(edge),
            )
            Column(Modifier.padding(start = 13.dp, end = 14.dp, top = 12.dp, bottom = 12.dp),
                content = content)
        }
    }
}

@Composable
internal fun ApprovalRow(approval: ApprovalItem, onClick: () -> Unit) {
    val risk = riskOf(approval)
    EdgeCard(risk.fg, if (risk === RISK_DELETE) Tok.RedLine else Tok.Line, onClick) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Tag(risk.text, risk.fg, risk.bg, risk.line)
            Spacer(Modifier.width(7.dp))
            Text(approval.projectId, color = Tok.Tx3, fontSize = 12.sp, fontFamily = Tok.Mono,
                maxLines = 1, modifier = Modifier.weight(1f))
            Text(fmtTtl(approval.secondsLeft), color = Tok.Tx3, fontSize = 11.5.sp, fontFamily = Tok.Mono)
        }
        Spacer(Modifier.height(7.dp))
        Text(
            approval.command.ifEmpty { approval.reason.ifEmpty { "请求执行一次操作" } },
            color = Tok.Tx,
            fontSize = 13.5.sp,
            fontFamily = Tok.Mono,
            lineHeight = 20.sp,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(6.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Codex · " + tailPath(approval.cwd), color = Tok.Tx3, fontSize = 11.5.sp,
                maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            Text("待批准", color = Tok.Red, fontSize = 11.5.sp)
        }
    }
}

@Composable
private fun RunningRow(item: QueueItem, now: Long, onClick: () -> Unit) {
    EdgeCard(Tok.Green, Tok.Line, onClick) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Tag("运行中", Tok.Green, Tok.GreenBg, Tok.GreenLine)
            Spacer(Modifier.width(7.dp))
            Text(item.projectId, color = Tok.Tx3, fontSize = 12.sp, fontFamily = Tok.Mono,
                maxLines = 1, modifier = Modifier.weight(1f))
            Text(clock(item.submittedAt), color = Tok.Tx3, fontSize = 11.5.sp, fontFamily = Tok.Mono)
        }
        Spacer(Modifier.height(7.dp))
        Text(item.summary.ifEmpty { "（空提示）" }, color = Tok.Tx, fontSize = 14.5.sp,
            lineHeight = 21.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
        Spacer(Modifier.height(8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Codex · 只读沙箱 · 运行中", color = Tok.Tx3, fontSize = 12.sp,
                maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            Text("查看轨迹", color = Tok.Amber, fontSize = 12.5.sp)
        }
    }
}

@Composable
private fun QueueRow(item: QueueItem, onClick: () -> Unit) {
    val chip = statusOf(item)
    EdgeCard(chip.fg, Tok.Line, onClick) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Tag(chip.text, chip.fg, chip.bg, chip.line)
            Spacer(Modifier.width(7.dp))
            Text(item.projectId, color = Tok.Tx3, fontSize = 12.sp, fontFamily = Tok.Mono,
                maxLines = 1, modifier = Modifier.weight(1f))
            Text(clock(item.submittedAt), color = Tok.Tx3, fontSize = 11.5.sp, fontFamily = Tok.Mono)
        }
        Spacer(Modifier.height(7.dp))
        Text(item.summary.ifEmpty { "（空提示）" }, color = Tok.Tx, fontSize = 13.5.sp,
            lineHeight = 20.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
        Spacer(Modifier.height(8.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Codex · 只读沙箱", color = Tok.Tx3, fontSize = 11.5.sp, maxLines = 1)
            Spacer(Modifier.weight(1f))
            Text(shortId(item.taskId), color = Tok.Tx3, fontSize = 11.sp, fontFamily = Tok.Mono)
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

internal fun deviceTitle(desk: Desk, id: String) = deviceLabel(desk.hosts[id], id)
