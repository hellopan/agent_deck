package com.agentdeck.lab

import android.Manifest
import android.app.KeyguardManager
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.graphics.Color as AndroidColor
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
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
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
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
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.agentdeck.lab.ui.ApprovalListScreen
import com.agentdeck.lab.ui.ApprovalScreen
import com.agentdeck.lab.ui.AboutScreen
import com.agentdeck.lab.ui.ConnectScreen
import com.agentdeck.lab.ui.DeckTabBar
import com.agentdeck.lab.ui.DeviceDetailScreen
import com.agentdeck.lab.ui.DeviceListScreen
import com.agentdeck.lab.ui.Dot
import com.agentdeck.lab.ui.SubmitDialog
import com.agentdeck.lab.ui.TaskListScreen
import com.agentdeck.lab.ui.Tok
import com.agentdeck.lab.ui.TraceScreen
import com.agentdeck.lab.ui.deskColors
import com.agentdeck.lab.ui.deskTypography
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private sealed interface Route {
    data object Home : Route
    data class Approval(val id: String) : Route
    data class Trace(val taskId: String, val projectId: String) : Route
    data class Device(val id: String) : Route
    data object Connect : Route
    data object About : Route
}

class MainActivity : ComponentActivity() {
    private val incomingApproval = mutableStateOf<String?>(null)
    private var unlockGraceUntil = 0L

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Screenshots of a secure window are blank, so debug builds stay capturable
        // for UI review; release builds keep the flag.
        if (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE == 0) {
            window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        }
        val dark = (resources.configuration.uiMode and Configuration.UI_MODE_NIGHT_MASK) ==
            Configuration.UI_MODE_NIGHT_YES
        com.agentdeck.lab.ui.Tok.dark = dark
        enableEdgeToEdge(
            statusBarStyle = if (dark) SystemBarStyle.dark(AndroidColor.TRANSPARENT)
            else SystemBarStyle.light(AndroidColor.TRANSPARENT, AndroidColor.TRANSPARENT),
            navigationBarStyle = if (dark) SystemBarStyle.dark(AndroidColor.TRANSPARENT)
            else SystemBarStyle.light(AndroidColor.TRANSPARENT, AndroidColor.TRANSPARENT),
        )
        readDeepLink(intent)
        setContent {
            val systemDark = isSystemInDarkTheme()
            Tok.dark = systemDark
            MaterialTheme(colorScheme = deskColors(systemDark), typography = deskTypography()) {
                Box(Modifier.fillMaxSize().background(Tok.Bg)) { AgentDeckApp() }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        readDeepLink(intent)
    }

    // Both forms are accepted: the extra from our own notification, and the
    // documented agentdeck:// scheme so a link can be pasted into a chat.
    private fun readDeepLink(intent: Intent?) {
        if (intent == null) return
        val extra = intent.getStringExtra(EXTRA_APPROVAL)
        val fromData = intent.data?.takeIf { it.scheme == "agentdeck" }?.pathSegments?.lastOrNull()
        val id = extra?.takeIf { it.isNotBlank() } ?: fromData?.takeIf { it.isNotBlank() }
        if (id == null) return
        incomingApproval.value = id
        // Tapping a notification from the lock screen means the device was just
        // unlocked; asking for the credential again seconds later would be the
        // second prompt of the same approval. Two minutes, then ask again.
        unlockGraceUntil = SystemClock.elapsedRealtime() + 120_000
    }

    @Composable
    private fun AgentDeckApp() {
        val scope = rememberCoroutineScope()
        val desk = remember { Desk(LabState(this), scope) }
        var route by remember { mutableStateOf<Route>(Route.Home) }
        var tab by rememberSaveable { mutableIntStateOf(0) }
        var showSubmit by remember { mutableStateOf(false) }
        var allowId by remember { mutableStateOf<String?>(null) }
        var noLockId by remember { mutableStateOf<String?>(null) }
        var wasAwaiting by remember { mutableStateOf(false) }
        var notificationsOn by remember { mutableStateOf(hasNotifications()) }

        val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
            // Cancelling is not a failure, but silence here was: a scan that
            // returned nothing looked exactly like a scanner that never opened.
            val text = result.contents?.trim()
            when {
                !text.isNullOrEmpty() -> desk.pair(text)
                // A denied camera permission closes the capture screen with no
                // explanation of its own, so name that case instead of blaming
                // the QR code.
                checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED ->
                    desk.fail("没有相机权限，扫码已关闭。请在系统设置里允许相机后重试。")
                else -> desk.fail("没有读到二维码，已取消。再扫一次即可。")
            }
        }
        // QA hook: a debug build accepts a pairing payload as an intent extra, so
        // the handshake can be reproduced on an emulator without pointing a
        // camera at the desktop. Release builds ignore it.
        LaunchedEffect(Unit) {
            if (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0) {
                // Base64 first: adb shell re-parses its arguments, so a raw JSON
                // payload arrives with its quotes stripped.
                val payload = intent?.getStringExtra("pairingB64")
                    ?.let { String(android.util.Base64.decode(it, android.util.Base64.DEFAULT)) }
                    ?.takeIf { it.isNotBlank() }
                    ?: intent?.getStringExtra("pairing")
                payload?.takeIf { it.isNotBlank() }?.let { desk.pair(it) }
            }
        }
        val credential = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
            val id = allowId
            allowId = null
            if (result.resultCode == RESULT_OK && id != null) {
                unlockGraceUntil = SystemClock.elapsedRealtime() + 120_000
                desk.decide(id, true)
            }
        }
        val notificationPermission = rememberLauncherForActivityResult(
            ActivityResultContracts.RequestPermission(),
        ) { granted -> notificationsOn = granted }

        LaunchedEffect(Unit) {
            desk.restore()
            if (!desk.paired) route = Route.Connect
            desk.start()
            if (Build.VERSION.SDK_INT >= 33 && !hasNotifications()) {
                notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
            }
        }
        DisposableEffect(Unit) { onDispose { desk.stop() } }
        LaunchedEffect(desk.paired) {
            if (desk.paired) DeckService.start(this@MainActivity)
            else DeckService.stop(this@MainActivity)
        }
        LaunchedEffect(desk.submitted) { if (desk.submitted > 0) showSubmit = false }
        LaunchedEffect(desk.awaitingConfirm) {
            if (wasAwaiting && !desk.awaitingConfirm && desk.reachable == true && route == Route.Connect) {
                delay(900)
                route = Route.Home
            }
            wasAwaiting = desk.awaitingConfirm
        }
        LaunchedEffect(desk.notice, desk.error) {
            if ((desk.notice.isNotEmpty() || desk.error != null) && route != Route.Connect) {
                delay(4_500)
                desk.dismissNotice()
            }
        }
        // A notification tapped before the first sync has to wait for the
        // approval table; once it has been read, an id that is no longer there
        // falls back to the list with one line of explanation.
        LaunchedEffect(incomingApproval.value, desk.paired, desk.reachable) {
            val id = incomingApproval.value ?: return@LaunchedEffect
            if (!desk.paired) return@LaunchedEffect
            if (desk.reachable != true && desk.approvals.isEmpty()) return@LaunchedEffect
            incomingApproval.value = null
            route = Route.Approval(id)
        }
        LaunchedEffect(route) {
            val current = route as? Route.Approval ?: return@LaunchedEffect
            delay(3_500)
            if (desk.reachable == true && desk.approvals.none { it.id == current.id }) {
                route = Route.Home
                tab = 0
                desk.showNotice("该审批已处理")
            }
        }
        BackHandler(enabled = desk.paired && route != Route.Home) { route = Route.Home }
        BackHandler(enabled = desk.paired && route == Route.Home && tab != 0) { tab = 0 }

        fun scan() {
            // A stale failure from the last attempt would otherwise sit under
            // the button and read as the result of this one.
            desk.dismissNotice()
            scanner.launch(
                ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                    .setPrompt("扫描 AgentDeck 电脑端配对码")
                    .setBeepEnabled(false)
                    // The shell is portrait everywhere; a capture screen that
                    // rotates to landscape also forces this activity through a
                    // recreation in the middle of the hand-off.
                    .setOrientationLocked(true)
                    .setBarcodeImageEnabled(false)
            )
        }

        // Deprecated in favour of BiometricPrompt, but this call is the only one
        // that works on API 29 without an extra dependency; revisit when the app
        // raises minSdk.
        @Suppress("DEPRECATION")
        fun requestAllow(id: String) {
            if (SystemClock.elapsedRealtime() < unlockGraceUntil) {
                desk.decide(id, true)
                return
            }
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
                Route.Home -> Column(Modifier.fillMaxSize()) {
                    Box(Modifier.fillMaxWidth().weight(1f)) {
                        when (tab) {
                            0 -> TaskListScreen(
                                desk = desk,
                                onApproval = { route = Route.Approval(it) },
                                onTrace = { route = Route.Trace(it.taskId, it.projectId) },
                                onSubmit = { showSubmit = true },
                                onDevices = {
                                    if (desk.device.isNotEmpty()) route = Route.Device(desk.device)
                                    else route = Route.Connect
                                },
                                onStatus = { route = Route.Connect },
                            )
                            1 -> DeviceListScreen(
                                desk = desk,
                                notificationsOn = notificationsOn,
                                onOpen = { route = Route.Device(it) },
                                onScan = { scan() },
                                onAbout = { route = Route.About },
                            )
                            else -> ApprovalListScreen(
                                desk = desk,
                                onApproval = { route = Route.Approval(it) },
                                onDevices = { tab = 1 },
                            )
                        }
                    }
                    DeckTabBar(selected = tab, approvals = desk.approvals.size) { tab = it }
                }
                is Route.Approval -> {
                    val item = desk.approvals.firstOrNull { it.id == current.id }
                    ApprovalScreen(
                        desk = desk,
                        approval = item,
                        device = desk.label,
                        busy = desk.busy,
                        onAllow = { requestAllow(current.id) },
                        onDecline = { desk.decide(current.id, false) },
                        onBack = { route = Route.Home },
                    )
                }
                is Route.Trace -> {
                    val item = desk.items.firstOrNull { it.taskId == current.taskId }
                    TraceScreen(
                        desk = desk,
                        item = item,
                        taskId = current.taskId,
                        projectId = current.projectId,
                        onBack = { route = Route.Home },
                        onCancel = { desk.cancel(current.taskId, current.projectId) },
                    )
                }
                is Route.Device -> DeviceDetailScreen(
                    desk = desk,
                    deviceId = current.id,
                    notificationsOn = notificationsOn,
                    onBack = { route = Route.Home },
                    onPickProject = {
                        route = Route.Home
                        tab = 0
                    },
                )
                Route.Connect -> ConnectScreen(
                    desk = desk,
                    onScan = { scan() },
                    onBack = if (desk.paired) ({ route = Route.Home }) else null,
                    onRefresh = { desk.refresh() },
                    onAbout = { route = Route.About },
                )
                Route.About -> AboutScreen(onBack = { route = Route.Home })
            }

            if (desk.busy) {
                Box(Modifier.align(Alignment.TopCenter).statusBarsPadding().fillMaxWidth().height(2.dp).background(Tok.Amber))
            }
            // The unpaired connect screen prints the last error under its scan
            // button; a banner would show the same sentence twice.
            val banner = if (route == Route.Connect && !desk.paired) null
            else desk.error ?: desk.notice.takeIf { it.isNotEmpty() && route != Route.Connect }
            if (banner != null) {
                NoticeBar(
                    text = banner,
                    isError = desk.error != null,
                    modifier = Modifier.align(Alignment.BottomCenter)
                        .padding(bottom = if (route == Route.Home) 66.dp else 0.dp),
                    onDismiss = { desk.dismissNotice() },
                )
            }
            if (showSubmit) SubmitDialog(desk = desk, onDismiss = { showSubmit = false })
            if (noLockId != null) AlertDialog(
                onDismissRequest = { noLockId = null },
                containerColor = Tok.Panel,
                titleContentColor = Tok.Tx,
                textContentColor = Tok.Tx2,
                shape = RoundedCornerShape(16.dp),
                title = { Text("这台手机没有设置锁屏", color = Tok.Tx, fontSize = 15.sp) },
                text = {
                    Text(
                        "批准会直接生效，任何人拿到手机都能放行命令。建议先在系统里设置锁屏或指纹，再回来批准。",
                        color = Tok.Tx2, fontSize = 12.5.sp, lineHeight = 19.sp,
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

    private fun hasNotifications(): Boolean =
        Build.VERSION.SDK_INT < 33 ||
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    companion object {
        const val EXTRA_APPROVAL = "com.agentdeck.lab.APPROVAL"
    }
}

@Composable
private fun NoticeBar(text: String, isError: Boolean, modifier: Modifier = Modifier, onDismiss: () -> Unit) {
    Row(
        modifier.padding(horizontal = 16.dp, vertical = 14.dp)
            .fillMaxWidth().clip(RoundedCornerShape(10.dp))
            .background(if (isError) Tok.RedBg else Tok.Panel2)
            .border(1.dp, if (isError) Tok.RedLine else Tok.Line, RoundedCornerShape(10.dp))
            .clickable { onDismiss() }
            .padding(horizontal = 12.dp, vertical = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Dot(if (isError) Tok.Red else Tok.Green)
        Spacer(Modifier.width(9.dp))
        Text(
            text,
            color = if (isError) Tok.Red.copy(alpha = 0.9f) else Tok.Tx2,
            fontSize = 12.sp,
            lineHeight = 18.sp,
            maxLines = 3,
            overflow = TextOverflow.Ellipsis,
        )
    }
}
