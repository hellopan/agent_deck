package com.agentdeck.lab.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxHeight
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.agentdeck.lab.ApprovalItem
import com.agentdeck.lab.shortId

private const val APPROVAL_TTL_SECONDS = 300f

@Composable
internal fun ApprovalScreen(
    approval: ApprovalItem?,
    device: String,
    busy: Boolean,
    onAllow: () -> Unit,
    onDecline: () -> Unit,
    onBack: () -> Unit,
) {
    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        NavTop("审批", onBack = onBack, trailing = {
            Text(device, color = Tok.Tx3, fontSize = 10.5.sp, fontFamily = Tok.Mono, maxLines = 1)
        })
        if (approval == null) {
            Column(
                Modifier.fillMaxSize().padding(horizontal = 24.dp),
                verticalArrangement = Arrangement.Center,
                horizontalAlignment = Alignment.CenterHorizontally,
            ) {
                Text("这条审批已经失效", color = Tok.Tx, fontSize = 15.sp, fontWeight = FontWeight.Medium)
                Spacer(Modifier.height(8.dp))
                Text(
                    "可能已经处理过，或等待超过 5 分钟被自动拒绝。刷新任务列表可看到最新状态。",
                    color = Tok.Tx3,
                    fontSize = 11.5.sp,
                    lineHeight = 18.sp,
                )
                Spacer(Modifier.height(20.dp))
                PButton("返回任务", onClick = onBack)
            }
            return@Column
        }
        Column(
            Modifier.fillMaxSize().padding(horizontal = 18.dp).verticalScroll(rememberScrollState()),
        ) {
            RiskBanner(approval)
            Spacer(Modifier.height(12.dp))
            CommandBlock(approval)
            Spacer(Modifier.height(12.dp))
            FactGrid(approval, device)
            Spacer(Modifier.height(12.dp))
            ExpiryBar(approval.secondsLeft)
            Spacer(Modifier.height(12.dp))
            ScopeCard(approval)
            Spacer(Modifier.height(12.dp))
            Hint(
                "批准只对这一次执行有效，下一条命令仍需重新确认。拒绝后 Codex 会收到说明，" +
                    "可以换一种不需要该操作的做法。"
            )
            Spacer(Modifier.height(16.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(9.dp)) {
                Box(Modifier.weight(1f)) {
                    PButton("拒绝", kind = ButtonKind.Danger, enabled = !busy, onClick = onDecline)
                }
                Box(Modifier.weight(1.35f)) {
                    PButton(
                        "允许一次",
                        kind = ButtonKind.Primary,
                        enabled = !busy,
                        leading = { LockGlyph(androidx.compose.ui.graphics.Color.White, Modifier.size(15.dp)) },
                        onClick = onAllow,
                    )
                }
            }
            Spacer(Modifier.height(26.dp))
        }
    }
}

@Composable
private fun RiskBanner(approval: ApprovalItem) {
    val risk = riskOf(approval)
    val (title, detail) = when (risk) {
        RISK_DELETE -> "删除或推送类操作" to "这条命令可能删除文件或改写远端分支，无法自动撤销"
        RISK_WRITE -> "请求写入工作目录" to "沙箱为只读，写入需要你批准这一次"
        else -> "请求执行一条命令" to "命令在项目目录内以只读沙箱运行"
    }
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(Tok.RedBg)
            .border(1.dp, Tok.Red.copy(alpha = 0.3f), RoundedCornerShape(10.dp))
            .padding(horizontal = 13.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        AlertGlyph(Tok.Red, Modifier.size(18.dp))
        Spacer(Modifier.width(9.dp))
        Column(Modifier.weight(1f)) {
            Text(title, color = Tok.Red.copy(alpha = 0.86f), fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(3.dp))
            Text(detail, color = Tok.Tx3, fontSize = 10.5.sp, lineHeight = 16.sp)
        }
        Spacer(Modifier.width(8.dp))
        Text("高", color = Tok.Red, fontSize = 10.sp, letterSpacing = 1.sp)
    }
}

@Composable
private fun CommandBlock(approval: ApprovalItem) {
    val body = buildString {
        if (approval.cwd.isNotEmpty()) {
            append("cd ")
            append(approval.cwd)
            append('\n')
        }
        append(approval.command.ifEmpty { approval.reason.ifEmpty { "（电脑只提供了原因，没有给出命令）" } })
    }
    Row(
        Modifier.fillMaxWidth().height(androidx.compose.foundation.layout.IntrinsicSize.Min)
            .clip(RoundedCornerShape(8.dp)).background(Tok.Code)
            .border(1.dp, Tok.Line, RoundedCornerShape(8.dp)),
    ) {
        Box(Modifier.width(2.dp).fillMaxHeight().background(Tok.Red))
        Text(
            body,
            Modifier.padding(horizontal = 12.dp, vertical = 12.dp),
            color = Tok.Tx2,
            fontSize = 12.sp,
            lineHeight = 21.sp,
            fontFamily = Tok.Mono,
        )
    }
}

@Composable
private fun FactGrid(approval: ApprovalItem, device: String) {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).background(Tok.Line2)
            .border(1.dp, Tok.Line2, RoundedCornerShape(8.dp)),
        verticalArrangement = Arrangement.spacedBy(1.dp),
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(1.dp)) {
            GridCell("项目", approval.projectId, Modifier.weight(1f))
            GridCell("设备", device, Modifier.weight(1f))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(1.dp)) {
            GridCell("工作目录", approval.cwd, Modifier.weight(1f))
            GridCell("类型", kindLabel(approval), Modifier.weight(1f))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(1.dp)) {
            GridCell("来源", "Codex · 只读沙箱", Modifier.weight(1f))
            GridCell("请求 ID", shortId(approval.id), Modifier.weight(1f))
        }
    }
}

private fun kindLabel(approval: ApprovalItem): String = when {
    approval.kind.contains("file", true) -> "写入文件"
    approval.command.isEmpty() -> "等待说明"
    else -> "执行命令"
}

@Composable
private fun GridCell(label: String, value: String, modifier: Modifier = Modifier, valueColor: androidx.compose.ui.graphics.Color = Tok.Tx2) {
    Column(modifier.background(Tok.Panel).padding(horizontal = 12.dp, vertical = 10.dp)) {
        MetaLabel(label)
        Spacer(Modifier.height(5.dp))
        Text(value.ifEmpty { "—" }, color = valueColor, fontSize = 11.sp, fontFamily = Tok.Mono, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun ExpiryBar(secondsLeft: Int) {
    val fraction = (secondsLeft / APPROVAL_TTL_SECONDS).coerceIn(0.02f, 1f)
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).background(Tok.Panel)
            .border(1.dp, Tok.Line, RoundedCornerShape(8.dp)),
    ) {
        Box(Modifier.fillMaxWidth().height(3.dp).background(Tok.Panel2)) {
            Box(Modifier.fillMaxWidth(fraction).height(3.dp).background(Tok.Amber))
        }
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 9.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text("超时未处理将自动拒绝", color = Tok.Tx3, fontSize = 10.5.sp)
            Spacer(Modifier.weight(1f))
            Text(
                "${secondsLeft.coerceAtLeast(0) / 60} 分 ${secondsLeft.coerceAtLeast(0) % 60} 秒后失效",
                color = Tok.Amber,
                fontSize = 10.5.sp,
                fontFamily = Tok.Mono,
            )
        }
    }
}

@Composable
private fun ScopeCard(approval: ApprovalItem) {
    Column(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).background(Tok.Line2)
            .border(1.dp, Tok.Line, RoundedCornerShape(8.dp)),
        verticalArrangement = Arrangement.spacedBy(1.dp),
    ) {
        Box(Modifier.fillMaxWidth().background(Tok.Panel).padding(horizontal = 12.dp, vertical = 10.dp)) {
            MetaLabel("这次操作会影响什么")
        }
        ScopeRow(if (approval.cwd.isEmpty()) "项目目录" else approval.cwd, "仅此目录")
        ScopeRow("执行权限", "逐次批准")
        ScopeRow("授权范围", "一次有效")
    }
}

@Composable
private fun ScopeRow(text: String, tag: String) {
    Row(
        Modifier.fillMaxWidth().background(Tok.Panel).padding(horizontal = 12.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(text, color = Tok.Tx2, fontSize = 11.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        Spacer(Modifier.width(10.dp))
        Tag(tag, Tok.Tx3, Tok.Panel2)
    }
}
