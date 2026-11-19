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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.Text
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
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
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

/**
 * Screen 4. The journal is read bottom-up: the newest event is what matters, so
 * the list starts at the bottom and auto-follows until the user scrolls away.
 * Cancelling is the only destructive action here and it is confirmed by the
 * overlay in screen 7.
 */
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
    var follow by remember { mutableStateOf("") }
    var sentNote by remember { mutableStateOf<String?>(null) }
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
        if (following && events.isNotEmpty()) listState.animateScrollToItem(events.lastIndex + 1)
    }

    val startedAt = desk.running?.takeIf { it.taskId == taskId }?.startedAt
        ?: events.firstOrNull { it.at != null }?.at
        ?: item?.submittedAt
    val threadId = item?.threadId.orEmpty()

    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        NavTop(
            projectId + " · Codex",
            subtitle = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    val color = when {
                        ended -> Tok.Tx3
                        item?.status == "failed" || item?.status == "interrupted" -> Tok.Red
                        else -> Tok.Green
                    }
                    Dot(color)
                    Spacer(Modifier.width(6.dp))
                    Text(
                        when {
                            ended -> "已结束 · " + (item?.status?.let { statusWord(it) } ?: "")
                            item == null || item.status == "running" -> "运行中 · " + elapsed(startedAt, now)
                            item.status == "queued" -> "等待电脑取走"
                            else -> statusWord(item.status)
                        },
                        color = color, fontSize = 12.sp, maxLines = 1,
                    )
                    if (threadId.isNotEmpty()) {
                        Text(" · 会话 " + shortId(threadId).take(4).lowercase(),
                            color = Tok.Tx3, fontSize = 12.sp, maxLines = 1)
                    }
                }
            },
            onBack = onBack,
            trailing = {
                if (!ended) {
                    Text(
                        "叫停",
                        Modifier.clip(RoundedCornerShape(7.dp)).clickable { askCancel = true }
                            .padding(horizontal = 8.dp, vertical = 6.dp),
                        color = Tok.Red, fontSize = 12.5.sp,
                    )
                }
            },
        )

        Column(Modifier.fillMaxWidth().weight(1f).padding(horizontal = 16.dp)) {
            if (item?.summary?.isNotBlank() == true) {
                DeskCard {
                    Text("本轮目标 · " + clock(item.submittedAt) + " · 来自手机",
                        color = Tok.Tx3, fontSize = 11.5.sp)
                    Spacer(Modifier.height(5.dp))
                    Text(item.summary, color = Tok.Tx, fontSize = 13.5.sp, lineHeight = 20.sp)
                }
                Spacer(Modifier.height(12.dp))
            }
            Box(Modifier.fillMaxWidth().weight(1f)) {
                if (events.isEmpty()) {
                    EmptyState(
                        problem ?: "还没有事件",
                        if (problem != null) "检查电脑是否在线。" else "任务被电脑取走后，会在这里逐条出现。",
                    )
                } else {
                    LazyColumn(
                        Modifier.fillMaxSize(),
                        state = listState,
                        contentPadding = PaddingValues(top = 2.dp, bottom = 10.dp),
                    ) {
                        items(events.size) { index -> EventRow(events[index], index == events.lastIndex) }
                        item {
                            Text(
                                "已补齐至 " + (events.lastOrNull { it.at != null }?.at?.let { stamp(it) } ?: "--:--:--"),
                                Modifier.padding(start = 24.dp, top = 6.dp),
                                color = Tok.Tx3, fontSize = 11.sp,
                            )
                        }
                    }
                }
            }
        }

        FollowDock(
            desk = desk,
            ended = ended,
            threadId = threadId,
            follow = follow,
            sentNote = sentNote,
            onFollow = { follow = if (it.toByteArray(Charsets.UTF_8).size <= 4096) it else follow },
            onSend = {
                desk.followUp(threadId, follow) {
                    follow = ""
                    sentNote = "已发出，返回列表能看到新的一轮"
                }
            },
        )
    }

    if (askCancel) CancelDialog(
        onDismiss = { askCancel = false },
        onConfirm = {
            askCancel = false
            onCancel()
        },
    )
}

@Composable
private fun FollowDock(
    desk: Desk,
    ended: Boolean,
    threadId: String,
    follow: String,
    sentNote: String?,
    onFollow: (String) -> Unit,
    onSend: () -> Unit,
) {
    val usable = ended && threadId.isNotEmpty()
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp).imePadding()) {
        Row(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
                .border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
                .padding(start = 14.dp, end = 8.dp, top = 8.dp, bottom = 8.dp),
            verticalAlignment = Alignment.Bottom,
        ) {
            if (usable) {
                BasicTextField(
                    value = follow,
                    onValueChange = onFollow,
                    textStyle = TextStyle(color = Tok.Tx, fontSize = 14.sp, lineHeight = 20.sp),
                    cursorBrush = SolidColor(Tok.Btn),
                    modifier = Modifier.weight(1f).height(34.dp).padding(top = 6.dp),
                    decorationBox = { inner ->
                        Box {
                            if (follow.isEmpty()) {
                                Text("接着说，模型记得上一轮", color = Tok.Tx3, fontSize = 14.sp)
                            }
                            inner()
                        }
                    },
                )
            } else {
                Text(
                    if (ended) "这一轮没有建立会话，接不下去" else "任务结束后可以接着说",
                    Modifier.weight(1f).padding(vertical = 8.dp),
                    color = Tok.Tx3, fontSize = 14.sp, maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Box(
                Modifier.size(34.dp).clip(CircleShape)
                    .background(if (usable && follow.isNotBlank() && !desk.busy) Tok.Btn else Tok.DotOff)
                    .clickable(enabled = usable && follow.isNotBlank() && !desk.busy, role = Role.Button, onClick = onSend),
                contentAlignment = Alignment.Center,
            ) { UpGlyph(Tok.OnAct, Modifier.size(15.dp)) }
        }
        Text(
            sentNote ?: if (usable) "接在同一轮会话里，模型记得上面说过什么" else "叫停后不能续跑，只能重新提交",
            Modifier.fillMaxWidth().padding(top = 7.dp, bottom = 10.dp),
            color = if (sentNote != null) Tok.Green else Tok.Tx3,
            fontSize = 11.sp,
            textAlign = androidx.compose.ui.text.style.TextAlign.Center,
        )
    }
}

@Composable
private fun EventRow(event: TraceEvent, last: Boolean) {
    val style = describe(event)
    Row(Modifier.fillMaxWidth().height(IntrinsicSize.Min).padding(bottom = 2.dp)) {
        Box(Modifier.width(22.dp).fillMaxHeight()) {
            Box(
                Modifier.padding(start = 4.dp, top = 6.dp).size(7.dp).clip(CircleShape)
                    .background(style.dot),
            )
            if (!last) {
                Box(
                    Modifier.padding(start = 7.dp, top = 15.dp).width(1.dp).fillMaxHeight()
                        .background(Tok.EvLine),
                )
            }
        }
        Column(Modifier.weight(1f).padding(bottom = 10.dp)) {
            Row {
                Text(stamp(event.at), Modifier.width(62.dp), color = Tok.Tx3, fontSize = 11.sp,
                    fontFamily = Tok.Mono, maxLines = 1)
                Text(style.label, color = style.labelColor, fontSize = 13.sp, lineHeight = 19.sp,
                    fontWeight = if (style.command) FontWeight.Normal else FontWeight.Normal)
            }
            if (event.text.isNotEmpty()) {
                if (style.command) {
                    Text(
                        event.text,
                        Modifier.fillMaxWidth().padding(top = 5.dp).clip(RoundedCornerShape(6.dp))
                            .background(Tok.Code).padding(horizontal = 8.dp, vertical = 5.dp),
                        color = Tok.Tx2, fontSize = 12.5.sp, lineHeight = 18.sp, fontFamily = Tok.Mono,
                    )
                } else {
                    Text(
                        event.text,
                        Modifier.padding(top = 3.dp, end = 4.dp),
                        color = Tok.Tx2, fontSize = 13.sp, lineHeight = 19.sp,
                    )
                }
            }
        }
    }
}

private data class EventStyle(val label: String, val labelColor: Color, val dot: Color, val command: Boolean)

private fun describe(event: TraceEvent): EventStyle = when (event.type) {
    "turn/started" -> EventStyle("会话开始", Tok.Tx3, Tok.Green, false)
    "approval/requested" -> EventStyle("请求你在手机上批准", Tok.Tx2, Tok.Red, false)
    "approval/approved" -> EventStyle("已在手机上批准", Tok.Green, Tok.Green, false)
    "approval/declined" -> EventStyle("已在手机上拒绝", Tok.Red, Tok.Red, false)
    "turn/completed" -> EventStyle("本轮结束 · " + statusWord(event.status), Tok.Tx3, Tok.Green, false)
    "task/notice" -> EventStyle("提示", Tok.Tx3, Tok.Amber, false)
    "task/finished" -> EventStyle(
        "任务结束 · " + statusWord(event.status),
        if (event.status == "completed") Tok.Green else Tok.Red,
        if (event.status == "completed") Tok.Green else Tok.Red,
        false,
    )
    "item/agentMessage/delta" -> EventStyle("Codex", Tok.Tx, Tok.EvMsg, false)
    "item/commandExecution/outputDelta" -> EventStyle("执行命令", Tok.Tx3, Tok.Red, true)
    else -> EventStyle(event.type, Tok.Tx3, Tok.EvDot, false)
}

private fun statusWord(status: String): String = when (status) {
    "queued" -> "排队中"
    "running" -> "运行中"
    "completed" -> "已完成"
    "failed" -> "失败"
    "interrupted" -> "已中断"
    "canceled" -> "已叫停"
    else -> status
}

@Composable
internal fun CancelDialog(onDismiss: () -> Unit, onConfirm: () -> Unit) {
    // Dark needs a heavier scrim: the same 44% over a dark page reads as "no
    // modal at all", because there is no bright card to fall away from.
    val scrim = if (Tok.dark) 0.58f else 0.44f
    Box(
        Modifier.fillMaxSize().background(Color.Black.copy(alpha = scrim))
            .clickable(role = Role.Button, onClick = onDismiss),
        contentAlignment = Alignment.Center,
    ) {
        Column(
            Modifier.fillMaxWidth().padding(horizontal = 22.dp).clip(RoundedCornerShape(16.dp))
                .background(Tok.Panel).border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
                .padding(start = 18.dp, end = 18.dp, top = 18.dp, bottom = 12.dp)
                .clickable(
                    interactionSource = remember { androidx.compose.foundation.interaction.MutableInteractionSource() },
                    indication = null,
                ) {},
        ) {
            Text("叫停这个任务？", color = Tok.Tx, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(8.dp))
            Text(
                "正在执行的命令会中断，已经产生的改动会留在工作目录里。叫停后不能续跑，只能重新提交。",
                color = Tok.Tx2, fontSize = 12.5.sp, lineHeight = 20.sp,
            )
            Spacer(Modifier.height(14.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                Text(
                    "返回",
                    Modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onDismiss)
                        .padding(horizontal = 13.dp, vertical = 9.dp),
                    color = Tok.Tx3, fontSize = 13.sp,
                )
                Spacer(Modifier.width(4.dp))
                Text(
                    "叫停",
                    Modifier.clip(RoundedCornerShape(10.dp)).background(Tok.RedBg)
                        .clickable(onClick = onConfirm)
                        .padding(horizontal = 13.dp, vertical = 9.dp),
                    color = Tok.Red, fontSize = 13.sp, fontWeight = FontWeight.Medium,
                )
            }
        }
    }
}

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
    return if (minutes < 60) String.format("%02d:%02d", minutes, seconds % 60)
    else String.format("%dh %02dm", minutes / 60, minutes % 60)
}
