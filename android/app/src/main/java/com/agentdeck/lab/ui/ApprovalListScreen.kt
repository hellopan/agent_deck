package com.agentdeck.lab.ui

import androidx.compose.foundation.background
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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.agentdeck.lab.Desk
import com.agentdeck.lab.deviceLabel

/**
 * Screen 2. Approvals are grouped by the machine that raised them: a request
 * from an offline machine cannot be approved, and putting it in the same list
 * without saying so turns a dead row into a bug report.
 *
 * The phone can only hold approvals for the machine it is currently polling, so
 * the other groups say "switch and look" instead of inventing a count.
 */
@Composable
internal fun ApprovalListScreen(desk: Desk, onApproval: (String) -> Unit, onDevices: () -> Unit) {
    val devices = desk.config?.optJSONObject("devices")?.keys()?.asSequence()?.toList().orEmpty()
    val sorted = desk.approvals.sortedByDescending { it.secondsLeft }

    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        PageHead("审批", "跨设备汇总 · 按提交时间排序")
        LazyColumn(
            Modifier.fillMaxSize().padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            contentPadding = PaddingValues(bottom = 18.dp),
        ) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.padding(start = 2.dp, top = 2.dp)) {
                    Dot(if (desk.reachable == true) Tok.Green else Tok.Tx3)
                    Spacer(Modifier.width(6.dp))
                    Text(
                        desk.label + " · " + (if (desk.reachable == true) "已连接" else "连不上") +
                            " · " + sorted.size + " 条",
                        color = Tok.Tx3, fontSize = 12.sp, maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            }
            items(sorted, key = { it.id }) { approval ->
                ApprovalRow(approval) { onApproval(approval.id) }
            }
            if (sorted.isEmpty()) {
                item { EmptyState("没有待批准的请求", "电脑上的 Codex 需要放行时会出现在这里，并推送到通知栏。") }
            }
            val others = devices.filter { it != desk.device }
            items(others, key = { "d" + it }) { id ->
                Column(Modifier.padding(top = 4.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically,
                        modifier = Modifier.padding(start = 2.dp, bottom = 8.dp)) {
                        Dot(Tok.DotOff)
                        Spacer(Modifier.width(6.dp))
                        Text(deviceLabel(desk.hosts[id], id) + " · 离线",
                            color = Tok.Tx3, fontSize = 12.sp)
                    }
                    Row(
                        Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Soft)
                            .clickable { desk.switchDevice(id) }
                            .padding(horizontal = 14.dp, vertical = 13.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text("切换这台电脑后查看它的审批", color = Tok.Tx3, fontSize = 12.5.sp,
                            modifier = Modifier.weight(1f))
                        Chevron(Tok.Tx3, Modifier.size(14.dp).rotate(180f))
                    }
                }
            }
            item {
                Text(
                    "审批绑定会话、运行实例和请求编号。过期、重放、项目停用或旧运行实例的请求都会被拒绝。",
                    Modifier.padding(start = 2.dp, end = 2.dp, top = 6.dp),
                    color = Tok.Tx3, fontSize = 12.sp, lineHeight = 19.sp,
                )
            }
        }
    }
}
