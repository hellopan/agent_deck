package com.agentdeck.lab.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.agentdeck.lab.Desk

@Composable
internal fun ConnectScreen(
    desk: Desk,
    onScan: () -> Unit,
    onBack: (() -> Unit)?,
    onRefresh: () -> Unit,
) {
    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        NavTop("连接", onBack = onBack, trailing = {
            if (desk.paired) {
                Text(
                    "刷新",
                    color = Tok.Tx3,
                    fontSize = 10.5.sp,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable(role = Role.Button, onClick = onRefresh)
                        .padding(horizontal = 8.dp, vertical = 5.dp),
                )
            }
        })
        Column(
            Modifier.fillMaxSize().padding(horizontal = 18.dp).verticalScroll(rememberScrollState()),
        ) {
            if (!desk.paired) {
                Spacer(Modifier.height(24.dp))
                Text("AgentDeck", color = Tok.Tx, fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(8.dp))
                Text(
                    "手机遥控电脑上的 Codex。电脑常驻，手机只负责提交任务、批准命令、看执行轨迹。",
                    color = Tok.Tx2,
                    fontSize = 12.sp,
                    lineHeight = 19.sp,
                )
                Spacer(Modifier.height(22.dp))
                Step("1", "电脑上打开 AgentDeck 托盘", "第 2 页「状态」里有一个配对二维码")
                Step("2", "扫描二维码", "扫完手机会生成一把本机密钥，不会上传")
                Step("3", "电脑上核对指纹并确认", "确认后这台手机才能提交任务")
                Spacer(Modifier.height(22.dp))
                PButton("扫描电脑配对码", kind = ButtonKind.Primary, enabled = !desk.busy, onClick = onScan)
                desk.error?.let {
                    Spacer(Modifier.height(12.dp))
                    Text(it, color = Tok.Red, fontSize = 11.sp, lineHeight = 17.sp)
                }
                Spacer(Modifier.height(24.dp))
            } else {
                Spacer(Modifier.height(6.dp))
                StatusCard(desk)
                Spacer(Modifier.height(12.dp))
                IdentityCard(desk)
                Spacer(Modifier.height(12.dp))
                DevicesCard(desk)
                Spacer(Modifier.height(12.dp))
                Hint("配对只在本机保存密钥；撤销配对在电脑端「状态」页完成，撤销后这台手机立即失去全部权限。")
                Spacer(Modifier.height(18.dp))
                PButton("重新扫描配对码", onClick = onScan)
                Spacer(Modifier.height(10.dp))
                PButton("刷新连接", onClick = onRefresh)
                Spacer(Modifier.height(28.dp))
            }
        }
    }
}

@Composable
private fun Step(index: String, title: String, detail: String) {
    Row(Modifier.fillMaxWidth().padding(bottom = 14.dp)) {
        Box(
            Modifier.size(22.dp).clip(RoundedCornerShape(5.dp)).background(Tok.Panel2),
            contentAlignment = Alignment.Center,
        ) {
            Text(index, color = Tok.Tx2, fontSize = 11.sp, fontFamily = Tok.Mono)
        }
        Spacer(Modifier.width(11.dp))
        Column {
            Text(title, color = Tok.Tx, fontSize = 12.5.sp)
            Spacer(Modifier.height(3.dp))
            Text(detail, color = Tok.Tx3, fontSize = 10.5.sp, lineHeight = 16.sp)
        }
    }
}

@Composable
private fun StatusCard(desk: Desk) {
    val (title, color, detail) = when {
        desk.reachable == true -> Triple(
            "已连通",
            Tok.Green,
            desk.latency?.let { "往返 " + it + "ms · 队列 " + desk.items.size + " 条 · 待批准 " + desk.approvals.size + " 条" }
                ?: "队列 " + desk.items.size + " 条",
        )
        desk.awaitingConfirm -> Triple("等待电脑确认", Tok.Amber, "指纹已发到电脑，在电脑上点确认后自动连上")
        desk.reachable == false -> Triple("连不上电脑", Tok.Red, "检查电脑是否开机、托盘是否在跑、网络是否可达")
        else -> Triple("连接中", Tok.Tx3, "正在读取电脑状态…")
    }
    DeskCard(border = if (desk.reachable == true) Tok.Green.copy(alpha = 0.3f) else Tok.Line) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Dot(color, 7.dp)
            Spacer(Modifier.width(8.dp))
            Text(title, color = color, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        }
        Spacer(Modifier.height(8.dp))
        Text(detail, color = Tok.Tx3, fontSize = 11.sp, lineHeight = 17.sp)
    }
}

@Composable
private fun IdentityCard(desk: Desk) {
    DeskCard {
        MetaLabel("这台电脑")
        Spacer(Modifier.height(6.dp))
        Text(
            desk.label,
            color = Tok.Tx,
            fontSize = 14.sp,
            fontFamily = Tok.Mono,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Spacer(Modifier.height(12.dp))
        MetaLabel("设备 ID")
        Spacer(Modifier.height(5.dp))
        Text(desk.device, color = Tok.Tx2, fontSize = 11.sp, fontFamily = Tok.Mono)
        Spacer(Modifier.height(12.dp))
        MetaLabel("这台手机的指纹")
        Spacer(Modifier.height(5.dp))
        Text(
            desk.fingerprint.chunked(8).joinToString(" ").ifEmpty { "未生成" },
            color = Tok.Tx2,
            fontSize = 10.5.sp,
            lineHeight = 17.sp,
            fontFamily = Tok.Mono,
        )
    }
}

@Composable
private fun DevicesCard(desk: Desk) {
    val devices = desk.config?.optJSONObject("devices")?.keys()?.asSequence()?.toList().orEmpty()
    DeskCard {
        MetaLabel("已配对的电脑")
        Spacer(Modifier.height(8.dp))
        if (devices.isEmpty()) {
            Text("没有", color = Tok.Tx3, fontSize = 11.sp)
            return@DeskCard
        }
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            devices.forEach { id ->
                val selected = id == desk.device
                Row(
                    Modifier.fillMaxWidth().clip(RoundedCornerShape(7.dp))
                        .background(if (selected) Tok.AmberBg else Tok.Panel2)
                        .border(1.dp, if (selected) Tok.Amber.copy(alpha = 0.4f) else Color.Transparent, RoundedCornerShape(7.dp))
                        .clickable(role = Role.Button) { desk.switchDevice(id) }
                        .padding(horizontal = 10.dp, vertical = 9.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Dot(if (selected && desk.reachable == true) Tok.Green else Tok.Tx3)
                    Spacer(Modifier.width(7.dp))
                    Text(
                        com.agentdeck.lab.deviceLabel(desk.hosts[id], id),
                        color = if (selected) Tok.Tx else Tok.Tx2,
                        fontSize = 11.sp,
                        fontFamily = Tok.Mono,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    Text(
                        if (selected) "当前" else "切换",
                        color = if (selected) Tok.Amber else Tok.Tx3,
                        fontSize = 10.sp,
                        modifier = Modifier.padding(start = 8.dp),
                    )
                }
            }
        }
    }
}
