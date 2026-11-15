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
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
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
import com.agentdeck.lab.Desk
import com.agentdeck.lab.QueueItem
import com.agentdeck.lab.TraceEvent
import com.agentdeck.lab.shortId
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

@Composable
internal fun TraceScreen(
    desk: Desk,
    item: QueueItem?,
    taskId: String,
    projectId: String,
    onBack: () -> Unit,
    onCancel: () -> Unit,
    preview: List<TraceEvent> = emptyList(),
) {
    var events by remember { mutableStateOf<List<TraceEvent>>(emptyList()) }
    var following by remember { mutableStateOf(true) }
    var ended by remember { mutableStateOf(item?.finished == true) }
    var problem by remember { mutableStateOf<String?>(null) }
    var askCancel by remember { mutableStateOf(false) }
    var now by remember { mutableStateOf(System.currentTimeMillis()) }
    val listState = rememberLazyListState()

    LaunchedEffect(taskId) {
        if (preview.isNotEmpty()) {
            events = preview
            return@LaunchedEffect
        }
        var after = 0
        while (true) {
            val api = desk.api()
            if (api == null) {
                problem = "还没有配对"
                break
            }
            try {
                val page = withContext(Dispatchers.IO) { api.logs(projectId, taskId, after) }
                problem = null
                if (page.events.isNotEmpty()) {
                    after = page.next
                    events = merge(events, page.events)
                    if (page.events.any { it.type == "task/finished" }) {
                        ended = true
                        desk.refresh()
                        break
                    }
                }
            } catch (_: Exception) {
                problem = "轨迹读取失败，正在重试…"
            }
            delay(2_000)
        }
    }
    LaunchedEffect(Unit) {
        while (true) {
            now = System.currentTimeMillis()
            delay(1_000)
        }
    }
    LaunchedEffect(events.size, following) {
        if (following && events.isNotEmpty()) listState.animateScrollToItem(events.lastIndex)
    }

    val startedAt = desk.running?.takeIf { it.taskId == taskId }?.startedAt
        ?: events.firstOrNull { it.at != null }?.at
        ?: item?.submittedAt

    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        NavTop("执行轨迹", onBack = onBack, trailing = {
            Row(
                Modifier.clip(RoundedCornerShape(4.dp)).background(if (following) Tok.Panel else Tok.Panel2)
                    .clickable(role = Role.Button) { following = !following }
                    .padding(horizontal = 7.dp, vertical = 3.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                if (following) Dot(Tok.Green, 5.dp) else Dot(Tok.Tx3, 5.dp)
                Spacer(Modifier.width(5.dp))
                Text(
                    if (following) "跟随" else "已暂停",
                    color = if (following) Tok.Green else Tok.Tx3,
                    fontSize = 9.5.sp,
                    letterSpacing = 0.5.sp,
                )
            }
        })
        Column(Modifier.fillMaxWidth().padding(horizontal = 18.dp).weight(1f)) {
            Column(Modifier.padding(top = 2.dp, bottom = 12.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    val color = when {
                        ended -> Tok.Tx3
                        item?.status == "failed" || item?.status == "interrupted" -> Tok.Red
                        else -> Tok.Green
                    }
                    Dot(color)
                    Spacer(Modifier.width(8.dp))
                    Text(
                        when {
                            ended -> "已结束"
                            item == null || item.status == "running" -> "运行中"
                            item.status == "queued" -> "等待电脑取走"
                            else -> item.status
                        },
                        color = color,
                        fontSize = 11.5.sp,
                    )
                    Spacer(Modifier.weight(1f))
                    Text(elapsed(startedAt, now), color = Tok.Tx3, fontSize = 10.5.sp, fontFamily = Tok.Mono)
                }
                Spacer(Modifier.height(8.dp))
                Text(
                    item?.summary?.takeIf { it.isNotBlank() } ?: ("任务 " + shortId(taskId)),
                    color = Tok.Tx,
                    fontSize = 14.sp,
                    fontWeight = FontWeight.Medium,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    projectId + " · " + desk.label + " · " + shortId(taskId),
                    color = Tok.Tx3,
                    fontSize = 10.sp,
                    fontFamily = Tok.Mono,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Column(Modifier.fillMaxWidth().weight(1f)) {
                Box(Modifier.fillMaxWidth().height(1.dp).background(Tok.Line))
                if (events.isEmpty()) {
                    EmptyState(
                        if (problem != null) problem!! else "还没有事件",
                        if (problem != null) "检查电脑是否在线。" else "任务被电脑取走后，会在这里逐条出现。",
                    )
                } else {
                    LazyColumn(
                        Modifier.fillMaxSize(),
                        state = listState,
                        contentPadding = PaddingValues(top = 10.dp, bottom = 12.dp),
                    ) {
                        items(events.size) { index -> EventRow(events[index]) }
                    }
                }
            }
            if (!ended) {
                Column(Modifier.padding(top = 10.dp, bottom = 26.dp)) {
                    PButton("叫停任务", kind = ButtonKind.Danger, enabled = !desk.busy, onClick = { askCancel = true })
                }
            } else {
                Spacer(Modifier.height(26.dp))
            }
        }
        if (askCancel) AlertDialog(
            onDismissRequest = { askCancel = false },
            containerColor = Tok.Panel,
            titleContentColor = Tok.Tx,
            textContentColor = Tok.Tx2,
            shape = RoundedCornerShape(14.dp),

            title = { Text("叫停这个任务？", color = Tok.Tx, fontSize = 15.sp) },
            text = {
                Text(
                    "正在执行的命令会中断，已经产生的改动会留在工作目录里。叫停后不能续跑，只能重新提交。",
                    color = Tok.Tx2,
                    fontSize = 12.sp,
                    lineHeight = 18.sp,
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    askCancel = false
                    onCancel()
                }) { Text("叫停", color = Tok.Red) }
            },
            dismissButton = {
                TextButton(onClick = { askCancel = false }) { Text("返回", color = Tok.Tx3) }
            },
        )
    }
}

@Composable
private fun EventRow(event: TraceEvent) {
    val (label, color) = describe(event)
    Row(Modifier.fillMaxWidth().padding(vertical = 5.dp)) {
        Text(
            stamp(event.at),
            Modifier.width(64.dp),
            color = Tok.Tx3,
            fontSize = 9.5.sp,
            fontFamily = Tok.Mono,
            maxLines = 1,
            softWrap = false,
        )
        Column(Modifier.weight(1f)) {
            Text(label, color = color, fontSize = 11.5.sp, fontWeight = FontWeight.Medium)
            if (event.text.isNotEmpty()) {
                Spacer(Modifier.height(2.dp))
                if (isCommandOutput(event.type)) {
                    Text(
                        event.text,
                        Modifier.fillMaxWidth().padding(top = 4.dp).clip(RoundedCornerShape(6.dp))
                            .background(Tok.Code).border(1.dp, Tok.Line, RoundedCornerShape(6.dp))
                            .padding(horizontal = 9.dp, vertical = 7.dp),
                        color = Tok.Tx3,
                        fontSize = 10.5.sp,
                        lineHeight = 16.sp,
                        fontFamily = Tok.Mono,
                    )
                } else {
                    Text(
                        event.text,
                        color = Tok.Tx2,
                        fontSize = 11.sp,
                        lineHeight = 17.sp,
                        fontFamily = if (event.type.endsWith("/delta")) Tok.Mono else androidx.compose.ui.text.font.FontFamily.Default,
                    )
                }
            }
        }
    }
}

private fun describe(event: TraceEvent): Pair<String, Color> = when (event.type) {
    "turn/started" -> "开始执行" to Tok.Tx3
    "approval/requested" -> "请求你在手机上批准" to Tok.Amber
    "approval/approved" -> "已在手机上批准" to Tok.Green
    "approval/declined" -> "已在手机上拒绝" to Tok.Red
    "turn/completed" -> "本轮结束 · " + event.status to if (event.status == "completed") Tok.Green else Tok.Red
    "task/finished" -> "任务结束 · " + event.status to if (event.status == "completed") Tok.Green else Tok.Red
    "item/agentMessage/delta" -> "Codex" to Tok.Tx
    "item/commandExecution/outputDelta" -> "命令输出" to Tok.Tx3
    else -> event.type to Tok.Tx3
}

private fun isCommandOutput(type: String) = type == "item/commandExecution/outputDelta"

internal fun merge(previous: List<TraceEvent>, incoming: List<TraceEvent>): List<TraceEvent> {
    val merged = previous.toMutableList()
    for (event in incoming) {
        val last = merged.lastOrNull()
        if (last != null && last.type == event.type && event.type.endsWith("/delta")) {
            merged[merged.lastIndex] = last.copy(text = last.text + event.text)
        } else {
            merged.add(event)
        }
    }
    return merged.takeLast(400)
}

private fun elapsed(from: Long?, now: Long): String {
    if (from == null) return ""
    val seconds = ((now - from) / 1000).coerceAtLeast(0)
    val minutes = seconds / 60
    return if (minutes < 60) String.format("%dm %02ds", minutes, seconds % 60)
    else String.format("%dh %02dm", minutes / 60, minutes % 60)
}
