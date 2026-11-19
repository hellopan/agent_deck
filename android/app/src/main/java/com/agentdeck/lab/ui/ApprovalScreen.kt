package com.agentdeck.lab.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.agentdeck.lab.ApprovalItem
import com.agentdeck.lab.Desk
import com.agentdeck.lab.TraceEvent
import com.agentdeck.lab.shortId
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

private const val APPROVAL_TTL_SECONDS = 300f

/**
 * Screen 3. The decision, with everything that makes it a decision: the exact
 * command, where it runs, what it may touch, and the two messages that led to
 * it. No "needs unlock" line — being here already means the phone is unlocked;
 * the gate is the system credential on the button, not a sentence.
 */
@Composable
internal fun ApprovalScreen(
    desk: Desk,
    approval: ApprovalItem?,
    device: String,
    busy: Boolean,
    onAllow: () -> Unit,
    onDecline: () -> Unit,
    onBack: () -> Unit,
    contextPreview: List<TraceEvent> = emptyList(),
) {
    var context by remember(approval?.id) { mutableStateOf(contextPreview) }
    LaunchedEffect(approval?.taskId) {
        if (contextPreview.isNotEmpty()) return@LaunchedEffect
        val item = approval ?: return@LaunchedEffect
        if (item.taskId.isEmpty()) return@LaunchedEffect
        val api = runCatching { desk.api() }.getOrNull() ?: return@LaunchedEffect
        runCatching {
            withContext(Dispatchers.IO) { api.logs(item.projectId, item.taskId, 0) }
        }.getOrNull()?.events
            ?.filter { it.text.isNotBlank() }
            ?.takeLast(2)
            ?.let { context = it }
    }

    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        NavTop(
            "审批",
            subtitle = {
                Text(device + " · 请求 #" + shortId(approval?.id.orEmpty()),
                    color = Tok.Tx3, fontSize = 12.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            },
            onBack = onBack,
        )
        if (approval == null) {
            Column(
                Modifier.fillMaxSize().padding(horizontal = 24.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text("这条审批已经失效", color = Tok.Tx, fontSize = 15.sp, fontWeight = FontWeight.Medium)
                Spacer(Modifier.height(8.dp))
                Text(
                    "可能已经处理过，或等待超过 5 分钟被自动拒绝。回到任务列表可看到最新状态。",
                    color = Tok.Tx3, fontSize = 12.sp, lineHeight = 19.sp,
                )
                Spacer(Modifier.height(20.dp))
                PButton("返回任务", onClick = onBack)
            }
            return@Column
        }

        Column(
            Modifier.fillMaxWidth().weight(1f).padding(horizontal = 16.dp)
                .verticalScroll(rememberScrollState()),
        ) {
            Spacer(Modifier.height(4.dp))
            Row(verticalAlignment = Alignment.CenterVertically) {
                val risk = riskOf(approval)
                Tag(risk.text, risk.fg, risk.bg, risk.line)
                Spacer(Modifier.width(6.dp))
                Tag("Codex", Tok.Tx2, Tok.TagDone, Tok.Line)
                Spacer(Modifier.width(6.dp))
                Tag(approval.projectId, Tok.Tx2, Tok.TagDone, Tok.Line)
                Spacer(Modifier.weight(1f))
                Row(
                    Modifier.clip(RoundedCornerShape(7.dp)).background(Tok.AmberBg)
                        .border(1.dp, Tok.AmberLine, RoundedCornerShape(7.dp))
                        .padding(horizontal = 8.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Dot(Tok.Amber, 5.dp)
                    Spacer(Modifier.width(5.dp))
                    Text("剩余 " + fmtTtl(approval.secondsLeft), color = Tok.Amber,
                        fontSize = 11.5.sp, fontFamily = Tok.Mono)
                }
            }
            Spacer(Modifier.height(10.dp))
            CommandBlock(approval)
            Spacer(Modifier.height(10.dp))
            FactsCard(approval)
            if (context.isNotEmpty()) {
                Spacer(Modifier.height(10.dp))
                ContextCard(approval, context)
            }
            Spacer(Modifier.height(10.dp))
            PolicyCard()
            Spacer(Modifier.height(4.dp))
            Row(Modifier.padding(horizontal = 2.dp, vertical = 10.dp)) {
                InfoGlyph(Tok.Tx3, Modifier.size(15.dp).padding(top = 1.dp))
                Spacer(Modifier.width(8.dp))
                Text(
                    "批准只对本次请求有效。过期、重放或旧运行实例的批准会被拒绝；拒绝不会中断会话，你可以在轨迹里继续追问。",
                    color = Tok.Tx3, fontSize = 12.sp, lineHeight = 19.sp,
                )
            }
            Spacer(Modifier.height(6.dp))
        }

        Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Box(Modifier.weight(1f)) {
                    PButton("拒绝", kind = ButtonKind.Danger, enabled = !busy, onClick = onDecline)
                }
                Box(Modifier.weight(1.35f)) {
                    PButton(
                        "允许一次",
                        kind = ButtonKind.Primary,
                        enabled = !busy,
                        leading = { LockGlyph(Tok.OnAct, Modifier.size(15.dp)) },
                        onClick = onAllow,
                    )
                }
            }
            Text("批准后任务在电脑上继续", Modifier.fillMaxWidth().padding(top = 7.dp, bottom = 12.dp),
                color = Tok.Tx3, fontSize = 11.sp,
                textAlign = androidx.compose.ui.text.style.TextAlign.Center)
        }
    }
}

@Composable
private fun InfoGlyph(color: androidx.compose.ui.graphics.Color, modifier: Modifier = Modifier) {
    androidx.compose.foundation.Canvas(modifier) {
        drawCircle(color, radius = size.minDimension / 2 - 1.dp.toPx(), style = androidx.compose.ui.graphics.drawscope.Stroke(1.3.dp.toPx()))
        drawLine(color, androidx.compose.ui.geometry.Offset(size.width / 2, size.height * 0.44f),
            androidx.compose.ui.geometry.Offset(size.width / 2, size.height * 0.72f), 1.4.dp.toPx(),
            androidx.compose.ui.graphics.StrokeCap.Round)
        drawCircle(color, radius = 0.9.dp.toPx(), center = androidx.compose.ui.geometry.Offset(size.width / 2, size.height * 0.3f))
    }
}

@Composable
private fun CommandBlock(approval: ApprovalItem) {
    // Only the command. The working directory is a row in the facts card right
    // below, and repeating it here made a one-line command look like two.
    val body = approval.command.ifEmpty {
        approval.reason.ifEmpty { "（电脑只提供了原因，没有给出命令）" }
    }
    Box(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(Tok.Code)
            .border(1.dp, Tok.Line2, RoundedCornerShape(12.dp))
            .padding(horizontal = 12.dp, vertical = 12.dp),
    ) {
        Text(body, color = Tok.Tx, fontSize = 13.5.sp, lineHeight = 21.sp, fontFamily = Tok.Mono)
    }
}

@Composable
private fun FactsCard(approval: ApprovalItem) {
    // The request TTL is the only timestamp the desktop publishes, so the
    // submission time is derived from the remaining seconds rather than invented.
    val submittedAt = System.currentTimeMillis() -
        ((APPROVAL_TTL_SECONDS - approval.secondsLeft) * 1000).toLong()
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
            .border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
            .padding(horizontal = 14.dp, vertical = 2.dp),
    ) {
        Fact("工作目录", approval.cwd.ifEmpty { "项目目录" }, mono = true)
        Fact("影响范围", impactOf(approval))
        Fact("会话", shortId(approval.taskId.ifEmpty { approval.id }), mono = true)
        Fact("提交时间", stamp(submittedAt) + " · 来自 Codex", last = true)
    }
}

@Composable
private fun Fact(key: String, value: String, mono: Boolean = false, last: Boolean = false) {
    Row(Modifier.fillMaxWidth().height(IntrinsicSize.Min)) {
        Text(key, Modifier.width(84.dp).padding(vertical = 10.dp), color = Tok.Tx3, fontSize = 12.5.sp)
        Text(
            value,
            Modifier.weight(1f).padding(vertical = 10.dp),
            color = Tok.Tx,
            fontSize = if (mono) 12.5.sp else 13.sp,
            fontFamily = if (mono) Tok.Mono else null,
            lineHeight = 19.sp,
        )
    }
    if (!last) Box(Modifier.fillMaxWidth().height(1.dp).background(Tok.Line2))
}

@Composable
private fun ContextCard(approval: ApprovalItem, events: List<TraceEvent>) {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
            .border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
            .padding(horizontal = 14.dp, vertical = 12.dp),
    ) {
        Text("来自会话 " + shortId(approval.taskId) + " · 最近两条", color = Tok.Tx3, fontSize = 11.5.sp)
        Spacer(Modifier.height(7.dp))
        events.forEachIndexed { index, event ->
            Row {
                Text(stamp(event.at), Modifier.width(62.dp), color = Tok.Tx3, fontSize = 11.sp,
                    fontFamily = Tok.Mono, maxLines = 1)
                Text(event.text, Modifier.weight(1f), color = Tok.Tx2, fontSize = 12.5.sp, lineHeight = 19.sp,
                    maxLines = 3, overflow = TextOverflow.Ellipsis)
            }
            if (index < events.lastIndex) Spacer(Modifier.height(6.dp))
        }
    }
}

@Composable
private fun PolicyCard() {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
            .border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
            .padding(horizontal = 14.dp, vertical = 2.dp),
    ) {
        Fact("访问范围", "项目目录内")
        Fact("命令审批", "按需请求许可 · 由电脑端配置", last = true)
    }
}

private fun impactOf(approval: ApprovalItem): String = when {
    approval.reason.isNotBlank() -> approval.reason
    approval.kind.contains("file", true) -> "写入项目目录内的文件"
    approval.command.isNotBlank() -> "在项目目录内执行一条命令"
    else -> "一次操作"
}
