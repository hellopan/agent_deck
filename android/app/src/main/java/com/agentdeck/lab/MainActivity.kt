package com.agentdeck.lab

import android.app.KeyguardManager
import android.content.pm.ApplicationInfo
import android.graphics.Color as AndroidColor
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.layout.size
import com.agentdeck.lab.ui.ApprovalScreen
import com.agentdeck.lab.ui.ButtonKind
import com.agentdeck.lab.ui.ConnectScreen
import com.agentdeck.lab.ui.DeskColors
import com.agentdeck.lab.ui.Dot
import com.agentdeck.lab.ui.PButton
import com.agentdeck.lab.ui.SubmitDialog
import com.agentdeck.lab.ui.TaskListScreen
import com.agentdeck.lab.ui.Tok
import com.agentdeck.lab.ui.TraceScreen
import com.agentdeck.lab.ui.deskTypography
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.delay

private sealed interface Route {
    data object Tasks : Route
    data class Approval(val id: String) : Route
    data class Trace(val taskId: String, val projectId: String) : Route
    data object Connect : Route
}

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Screenshots of a secure window are blank, so debug builds stay capturable
        // for UI review; release builds keep the flag.
        if (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE == 0) {
            window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        }
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(AndroidColor.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(AndroidColor.TRANSPARENT),
        )
        setContent {
            MaterialTheme(colorScheme = DeskColors, typography = deskTypography()) {
                Box(Modifier.fillMaxSize().background(Tok.Bg)) { AgentDeckApp() }
            }
        }
    }

    @Composable
    private fun AgentDeckApp() {
        val scope = rememberCoroutineScope()
        val desk = remember { Desk(LabState(this), scope) }
        var route by remember { mutableStateOf<Route>(Route.Tasks) }
        var showSubmit by remember { mutableStateOf(false) }
        var allowId by remember { mutableStateOf<String?>(null) }
        var noLockId by remember { mutableStateOf<String?>(null) }
        var wasAwaiting by remember { mutableStateOf(false) }

        val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
            result.contents?.let { desk.pair(it) }
        }
        val credential = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            val id = allowId
            allowId = null
            if (result.resultCode == RESULT_OK && id != null) desk.decide(id, true)
        }

        LaunchedEffect(Unit) {
            desk.restore()
            if (!desk.paired) route = Route.Connect
            desk.start()
        }
        DisposableEffect(Unit) { onDispose { desk.stop() } }
        LaunchedEffect(desk.submitted) { if (desk.submitted > 0) showSubmit = false }
        LaunchedEffect(desk.awaitingConfirm) {
            if (wasAwaiting && !desk.awaitingConfirm && desk.reachable == true && route == Route.Connect) {
                delay(900)
                route = Route.Tasks
            }
            wasAwaiting = desk.awaitingConfirm
        }
        LaunchedEffect(desk.notice, desk.error) {
            if ((desk.notice.isNotEmpty() || desk.error != null) && route != Route.Connect) {
                delay(4_500)
                desk.dismissNotice()
            }
        }
        BackHandler(enabled = desk.paired && route != Route.Tasks) {
            route = Route.Tasks
        }

        fun scan() = scanner.launch(
            ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                .setPrompt("扫描 AgentDeck 电脑端配对码")
                .setBeepEnabled(false)
        )

        // Deprecated in favour of BiometricPrompt, but this call is the only one
        // that works on API 29 without an extra dependency; revisit when the app
        // raises minSdk.
        @Suppress("DEPRECATION")
        fun requestAllow(id: String) {
            val keyguard = getSystemService(KeyguardManager::class.java)
            if (keyguard == null || !keyguard.isDeviceSecure) {
                noLockId = id
                return
            }
            val intent = keyguard.createConfirmDeviceCredentialIntent(
                "允许这一次执行",
                "确认是你在批准 Codex 的请求",
            )
            if (intent == null) {
                desk.decide(id, true)
                return
            }
            allowId = id
            credential.launch(intent)
        }

        Box(Modifier.fillMaxSize()) {
        when (val current = route) {
            Route.Tasks -> TaskListScreen(
                desk = desk,
                onApproval = { route = Route.Approval(it) },
                onTrace = { route = Route.Trace(it.taskId, it.projectId) },
                onSubmit = { showSubmit = true },
                onStatus = { route = Route.Connect },
            )
            is Route.Approval -> {
                val item = desk.approvals.firstOrNull { it.id == current.id }
                ApprovalScreen(
                    approval = item,
                    device = desk.label,
                    busy = desk.busy,
                    onAllow = { requestAllow(current.id) },
                    onDecline = { desk.decide(current.id, false) },
                    onBack = { route = Route.Tasks },
                )
            }
            is Route.Trace -> {
                val item = desk.items.firstOrNull { it.taskId == current.taskId }
                TraceScreen(
                    desk = desk,
                    item = item,
                    taskId = current.taskId,
                    projectId = current.projectId,
                    onBack = { route = Route.Tasks },
                    onCancel = { desk.cancel(current.taskId, current.projectId) },
                )
            }
            Route.Connect -> ConnectScreen(
                desk = desk,
                onScan = { scan() },
                onBack = if (desk.paired) ({ route = Route.Tasks }) else null,
                onRefresh = { desk.refresh() },
            )
        }

        if (desk.busy) {
            Box(Modifier.align(Alignment.TopCenter).statusBarsPadding().fillMaxWidth().height(2.dp).background(Tok.Amber))
        }
        val banner = desk.error ?: desk.notice.takeIf { it.isNotEmpty() && route != Route.Connect }
        if (banner != null) {
            NoticeBar(
                text = banner,
                isError = desk.error != null,
                modifier = Modifier.align(Alignment.BottomCenter),
                onDismiss = { desk.dismissNotice() },
            )
        }
        if (showSubmit) SubmitDialog(desk = desk, onDismiss = { showSubmit = false })
        if (noLockId != null) AlertDialog(
            onDismissRequest = { noLockId = null },
            containerColor = Tok.Panel,
            titleContentColor = Tok.Tx,
            textContentColor = Tok.Tx2,
            shape = RoundedCornerShape(14.dp),

            title = { Text("这台手机没有设置锁屏", color = Tok.Tx, fontSize = 15.sp) },
            text = {
                Text(
                    "批准会直接生效，任何人拿到手机都能放行命令。建议先在系统里设置锁屏或指纹，再回来批准。",
                    color = Tok.Tx2,
                    fontSize = 12.sp,
                    lineHeight = 18.sp,
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    val id = noLockId
                    noLockId = null
                    if (id != null) desk.decide(id, true)
                }) { Text("仍然批准", color = Tok.Red) }
            },
            dismissButton = {
                TextButton(onClick = { noLockId = null }) { Text("取消", color = Tok.Tx3) }
            },
        )
        }
    }
}

@Composable
private fun NoticeBar(text: String, isError: Boolean, modifier: Modifier = Modifier, onDismiss: () -> Unit) {
    Row(
        modifier.navigationBarsPadding().padding(horizontal = 16.dp, vertical = 14.dp)
            .fillMaxWidth().clip(RoundedCornerShape(10.dp))
            .background(if (isError) Tok.RedBg else Tok.Panel2)
            .border(1.dp, if (isError) Tok.Red.copy(alpha = 0.35f) else Tok.Line, RoundedCornerShape(10.dp))
            .clickable { onDismiss() }
            .padding(horizontal = 12.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Dot(if (isError) Tok.Red else Tok.Green)
        Spacer(Modifier.width(9.dp))
        Text(
            text,
            color = if (isError) Tok.Red.copy(alpha = 0.9f) else Tok.Tx2,
            fontSize = 11.5.sp,
            lineHeight = 17.sp,
            maxLines = 3,
            overflow = TextOverflow.Ellipsis,
        )
    }
}
