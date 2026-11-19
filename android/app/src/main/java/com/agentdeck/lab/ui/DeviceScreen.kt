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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.agentdeck.lab.Desk
import com.agentdeck.lab.deviceLabel

/**
 * Readiness on the phone is about the phone's ability to act, not a copy of the
 * desktop's four checks: channel, projects, pairing and notification permission.
 * The desktop's own readiness lives on the host page, where it can be measured.
 */
private data class Ready(val label: String, val ok: Boolean, val value: String)

private fun readiness(desk: Desk, current: Boolean, notificationsOn: Boolean): List<Ready> = listOf(
    Ready("中枢通道", current && desk.reachable == true,
        if (current) desk.latency?.let { "$it ms" } ?: "连不上" else "未连接"),
    Ready("项目", current && desk.projects.isNotEmpty(),
        if (current) "${desk.projects.size} 个启用" else "—"),
    Ready("配对", current, if (current) "1 台手机" else "—"),
    Ready("通知", current && notificationsOn, if (notificationsOn) "已开启" else "未开启"),
)

@Composable
internal fun DeviceListScreen(
    desk: Desk,
    notificationsOn: Boolean,
    onOpen: (String) -> Unit,
    onScan: () -> Unit,
    onAbout: () -> Unit,
) {
    val devices = desk.config?.optJSONObject("devices")?.keys()?.asSequence()?.toList().orEmpty()
    val online = if (desk.reachable == true) 1 else 0

    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        PageHead("设备", "${devices.size} 台电脑 · $online 台在线", trailing = {
            Box(
                Modifier.clip(RoundedCornerShape(9.dp)).background(Tok.Panel)
                    .border(1.dp, Tok.Line, RoundedCornerShape(9.dp))
                    .clickable { onScan() }.padding(horizontal = 12.dp, vertical = 7.dp),
            ) { Text("扫码添加", color = Tok.Tx, fontSize = 12.5.sp) }
        })
        LazyColumn(
            Modifier.fillMaxSize().padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            contentPadding = PaddingValues(bottom = 18.dp),
        ) {
            items(devices, key = { it }) { id ->
                DeviceCard(desk, id, notificationsOn, onOpen)
            }
            if (devices.isEmpty()) {
                item { EmptyState("还没有配对的电脑", "在电脑端打开配对二维码，用这台手机扫码。") }
            }
            item {
                Text(
                    "手机只看得到电脑上已启用的项目，看不到目录结构，也不能指定任意路径。",
                    Modifier.padding(start = 2.dp, end = 2.dp, top = 6.dp),
                    color = Tok.Tx3, fontSize = 12.sp, lineHeight = 19.sp,
                )
            }
            item {
                DeskCard(modifier = Modifier.clickable { onAbout() }) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(Modifier.weight(1f)) {
                            Text("关于 AgentDeck", color = Tok.Tx, fontSize = 13.5.sp,
                                fontWeight = FontWeight.SemiBold)
                            Spacer(Modifier.height(3.dp))
                            Text("版本、源码地址与问题反馈", color = Tok.Tx3, fontSize = 11.sp)
                        }
                        Text("›", color = Tok.Tx3, fontSize = 18.sp)
                    }
                }
            }
        }
    }
}

@Composable
private fun DeviceCard(desk: Desk, id: String, notificationsOn: Boolean, onOpen: (String) -> Unit) {
    val current = id == desk.device
    val online = current && desk.reachable == true
    val ready = readiness(desk, current, notificationsOn)
    val passed = ready.count { it.ok }
    val missing = ready.firstOrNull { !it.ok }

    DeskCard {
        Row(verticalAlignment = Alignment.Top) {
            Column(Modifier.weight(1f)) {
                Text(deviceLabel(desk.hosts[id], id), color = if (online) Tok.Tx else Tok.Tx2,
                    fontSize = 15.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1,
                    overflow = TextOverflow.Ellipsis)
                Spacer(Modifier.height(3.dp))
                Text(
                    when {
                        online -> "刚在线" + (desk.latency?.let { " · $it ms" } ?: "") +
                            (desk.items.count { it.status == "running" }.let { if (it > 0) " · $it 个任务在跑" else "" })
                        current -> "连不上 · 检查电脑是否开机"
                        else -> "未连接 · 点按切换"
                    },
                    color = Tok.Tx3, fontSize = 11.5.sp,
                )
            }
            if (online) {
                Row(
                    Modifier.clip(RoundedCornerShape(7.dp)).background(Tok.GreenBg)
                        .border(1.dp, Tok.GreenLine, RoundedCornerShape(7.dp))
                        .padding(horizontal = 8.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Dot(Tok.Green, 5.dp); Spacer(Modifier.width(5.dp))
                    Text("已连接", color = Tok.Green, fontSize = 11.5.sp)
                }
            } else {
                Row(
                    Modifier.clip(RoundedCornerShape(7.dp)).background(Tok.Soft)
                        .padding(horizontal = 8.dp, vertical = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Dot(Tok.DotOff, 5.dp); Spacer(Modifier.width(5.dp))
                    Text("离线", color = Tok.Tx3, fontSize = 11.5.sp)
                }
            }
        }
        Spacer(Modifier.height(12.dp))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            ready.forEach { item ->
                Box(
                    Modifier.weight(1f).height(4.dp).clip(RoundedCornerShape(2.dp))
                        .background(if (item.ok) Tok.Green else Tok.ReadyOff),
                )
            }
        }
        Spacer(Modifier.height(8.dp))
        Text(
            "就绪 $passed/${ready.size}" + (missing?.let { " · ${it.label}${if (it.label == "通知") "未开启" else "未就绪"}" } ?: ""),
            color = Tok.Tx3,
            fontSize = 11.5.sp,
        )
        Spacer(Modifier.height(12.dp))
        // A machine this phone is not currently paired with has nothing to open:
        // its detail page would be four grey bars. One honest button, the same
        // shape the design gives the offline card.
        if (!current) {
            PButton("切换到这台电脑", height = 36.dp, onClick = { desk.switchDevice(id) })
            return@DeskCard
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Box(Modifier.weight(1f)) {
                PButton("进入", kind = ButtonKind.Primary, height = 36.dp, onClick = { onOpen(id) })
            }
            Box(Modifier.weight(1f)) {
                PButton(if (online) "重连" else "重新连接", height = 36.dp, onClick = { desk.refresh() })
            }
        }
    }
}

@Composable
internal fun DeviceDetailScreen(
    desk: Desk,
    deviceId: String,
    notificationsOn: Boolean,
    onBack: () -> Unit,
    onPickProject: () -> Unit,
) {
    val current = deviceId == desk.device
    val ready = readiness(desk, current, notificationsOn)
    val passed = ready.count { it.ok }

    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        NavTop(
            deviceLabel(desk.hosts[deviceId], deviceId),
            subtitle = {
                Text(
                    (if (current && desk.reachable == true) "已连接" else "未连接") +
                        (if (current) desk.latency?.let { " · $it ms" } ?: "" else "") +
                        " · " + deviceId.take(8).uppercase(),
                    color = Tok.Tx3, fontSize = 12.sp,
                )
            },
            onBack = onBack,
            trailing = {
                Text(
                    "重新检查",
                    Modifier.clip(RoundedCornerShape(7.dp)).clickable {
                        if (current) desk.refresh() else desk.switchDevice(deviceId)
                    }.padding(horizontal = 8.dp, vertical = 6.dp),
                    color = Tok.Amber, fontSize = 12.5.sp,
                )
            },
        )
        Column(Modifier.fillMaxSize().padding(horizontal = 16.dp).verticalScroll(rememberScrollState())) {
            SectionTitle("就绪度")
            Column(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
                    .border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
                    .padding(horizontal = 14.dp, vertical = 2.dp),
            ) {
                ready.forEachIndexed { index, item ->
                    Row(
                        Modifier.fillMaxWidth().padding(vertical = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Dot(if (item.ok) Tok.Green else Tok.EvFile, 6.dp)
                        Spacer(Modifier.width(9.dp))
                        Text(item.label, color = Tok.Tx, fontSize = 13.sp, modifier = Modifier.weight(1f))
                        Text(item.value, color = if (item.ok) Tok.Tx3 else Tok.Amber,
                            fontSize = 11.5.sp, fontFamily = Tok.Mono)
                    }
                    if (index < ready.lastIndex) Hairline()
                }
            }
            Text("就绪 $passed/${ready.size}", Modifier.padding(start = 2.dp, top = 8.dp),
                color = Tok.Tx3, fontSize = 11.5.sp)

            SectionTitle("项目")
            Column(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
                    .border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
                    .padding(horizontal = 14.dp, vertical = 2.dp),
            ) {
                if (desk.projects.isEmpty()) {
                    Text("没有启用的项目", Modifier.padding(vertical = 14.dp),
                        color = Tok.Tx3, fontSize = 12.5.sp)
                }
                desk.projects.forEachIndexed { index, project ->
                    Row(
                        Modifier.fillMaxWidth()
                            .clickable(enabled = current) {
                                desk.switchProject(project)
                                onPickProject()
                            }
                            .padding(vertical = 13.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Column(Modifier.weight(1f)) {
                            Text(project, color = Tok.Tx, fontSize = 13.5.sp,
                                fontWeight = if (project == desk.project) FontWeight.SemiBold else FontWeight.Normal)
                            Spacer(Modifier.height(2.dp))
                            Text(
                                if (project == desk.project) "当前项目" else "点按切换到该项目",
                                color = Tok.Tx3, fontSize = 11.sp, fontFamily = Tok.Mono,
                            )
                        }
                        if (project == desk.project) Tag("当前", Tok.Tx2, Tok.TagDone, Tok.Line)
                    }
                    if (index < desk.projects.lastIndex) Hairline()
                }
            }

            SectionTitle("本机")
            Column(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
                    .border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
                    .padding(horizontal = 14.dp, vertical = 2.dp),
            ) {
                Row(Modifier.fillMaxWidth().padding(vertical = 12.dp)) {
                    Text("设备 ID", Modifier.width(84.dp), color = Tok.Tx3, fontSize = 12.5.sp)
                    Text(deviceId, color = Tok.Tx, fontSize = 12.5.sp, fontFamily = Tok.Mono)
                }
                Hairline()
                Row(Modifier.fillMaxWidth().padding(vertical = 12.dp)) {
                    Text("手机指纹", Modifier.width(84.dp), color = Tok.Tx3, fontSize = 12.5.sp)
                    Text(
                        desk.fingerprint.chunked(8).joinToString(" ").ifEmpty { "未生成" },
                        color = Tok.Tx2, fontSize = 11.sp, lineHeight = 17.sp, fontFamily = Tok.Mono,
                    )
                }
            }
            Text(
                "引擎版本与登录状态在电脑端「设置 → 引擎」查看，手机不重复探测；未就绪的引擎不会出现在这里的项目动作里。",
                Modifier.padding(start = 2.dp, end = 2.dp, top = 10.dp, bottom = 18.dp),
                color = Tok.Tx3, fontSize = 12.sp, lineHeight = 19.sp,
            )
        }
    }
}
