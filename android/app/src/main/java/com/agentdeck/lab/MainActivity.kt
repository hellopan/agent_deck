package com.agentdeck.lab

import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_SECURE)
        enableEdgeToEdge()
        setContent { MaterialTheme { Surface { LabScreen() } } }
    }

    @OptIn(ExperimentalLayoutApi::class)
    @Composable
    private fun LabScreen() {
        val storage = remember { LabState(this) }
        val scope = rememberCoroutineScope()
        var config by remember { mutableStateOf<JSONObject?>(null) }
        var endpoint by remember { mutableStateOf("") }
        var device by remember { mutableStateOf("a") }
        var projects by remember { mutableStateOf(emptyList<String>()) }
        var project by remember { mutableStateOf("") }
        var prompt by remember { mutableStateOf("") }
        var requestID by remember { mutableStateOf("") }
        var taskID by remember { mutableStateOf("") }
        var taskStatus by remember { mutableStateOf("") }
        var cursor by remember { mutableIntStateOf(0) }
        var log by remember { mutableStateOf("") }
        var status by remember { mutableStateOf("请扫描 Windows 客户端显示的配对码。") }
        var fingerprint by remember { mutableStateOf("") }
        var busy by remember { mutableStateOf(false) }
        var confirmCancel by remember { mutableStateOf(false) }

        fun persist() {
            val pairing = config ?: return
            storage.write(JSONObject().put("config", pairing).put("endpoint", endpoint).put("device", device)
                .put("project", project).put("requestId", requestID).put("taskId", taskID)
                .put("pendingPrompt", if (requestID.isNotEmpty() && taskID.isEmpty()) prompt else ""))
        }
        fun perform(action: suspend () -> Unit) {
            if (busy) return
            scope.launch {
                busy = true
                status = "正在处理…"
                try {
                    action()
                } catch (cancel: kotlinx.coroutines.CancellationException) {
                    throw cancel
                } catch (_: Exception) {
                    status = "操作失败。请检查 Relay 地址、实验配置和电脑连接，再重试。"
                } finally {
                    busy = false
                }
            }
        }
        suspend fun query(action: String, payload: JSONObject? = null): String {
            val pairing = config ?: error("请扫描配对码")
            val origin = endpoint
            val target = device
            return withContext(Dispatchers.IO) {
                val command = if (payload == null) action else "$action $payload"
                require(command.toByteArray(Charsets.UTF_8).size < 24 * 1024)
                RelayClient(pairing, target, origin).query(command)
            }
        }
        fun resetTask() {
            taskID = ""
            requestID = ""
            taskStatus = ""
            cursor = 0
            log = ""
        }
        suspend fun submit() {
            if (requestID.isEmpty()) requestID = UUID.randomUUID().toString().replace("-", "")
            persist()
            val reply = JSONObject(query("agentdeck-task-submit-v1",
                JSONObject().put("projectId", project).put("requestId", requestID).put("prompt", prompt)))
            val id = reply.getString("taskId")
            require(id.matches(Regex("[a-f0-9]{32}")))
            taskID = id
            taskStatus = reply.getString("status")
            persist()
            status = "回执已收到。待审核任务可在 Windows 上处理。"
        }
        val scanner = rememberLauncherForActivityResult(ScanContract()) { scan ->
            scan.contents?.let { code -> perform {
                val candidate = withContext(Dispatchers.IO) { requestPairing(code) }
                device = candidate.device
                endpoint = candidate.config.getString("relayUrl")
                config = candidate.config
                fingerprint = candidate.fingerprint
                projects = emptyList()
                project = ""
                resetTask()
                persist()
                status = "请在电脑上核对设备指纹并确认，然后刷新项目。"
            } }
        }
        LaunchedEffect(Unit) {
            try {
                val saved = storage.read()
                if (saved != null && saved.optJSONObject("config")?.has("keyAlias") == true) {
                    config = saved.getJSONObject("config")
                    endpoint = saved.getString("endpoint")
                    device = saved.getString("device")
                    project = saved.optString("project")
                    requestID = saved.optString("requestId")
                    taskID = saved.optString("taskId")
                    prompt = saved.optString("pendingPrompt")
                    status = "已恢复本机配置。刷新项目或查询原任务。"
                }
            } catch (_: Exception) {
                status = "本机配置不可用，请重新扫码。"
            }
        }

        Column(Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp, vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Text("AgentDeck", style = MaterialTheme.typography.headlineMedium)
            Text(status, style = MaterialTheme.typography.bodyMedium)
            OutlinedButton(onClick = { scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                .setPrompt("扫描 AgentDeck 电脑端配对码").setBeepEnabled(false)) },
                enabled = !busy) { Text("扫描电脑配对码") }
            if (fingerprint.isNotEmpty()) {
                Text("手机指纹")
                SelectionContainer { Text(fingerprint.chunked(8).joinToString(" "), fontFamily = FontFamily.Monospace) }
            }
            OutlinedTextField(endpoint, {
                endpoint = it
                projects = emptyList()
                project = ""
                resetTask()
            }, Modifier.fillMaxWidth(), label = { Text("Relay WSS 地址") }, enabled = !busy, singleLine = true)
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                config?.optJSONObject("devices")?.keys()?.asSequence()?.toList().orEmpty().forEach { id ->
                    FilterChip(selected = device == id, onClick = {
                        if (device != id) {
                            device = id
                            project = ""
                            projects = emptyList()
                            resetTask()
                            perform { persist(); status = "已切换设备。" }
                        }
                    }, label = { Text("设备 " + id.take(8).uppercase()) }, enabled = !busy)
                }
                OutlinedButton(onClick = { perform {
                    val reply = JSONArray(query("agentdeck-projects-v1"))
                    fingerprint = ""
                    projects = List(reply.length()) { reply.getString(it) }
                    if (project !in projects) {
                        project = projects.firstOrNull().orEmpty()
                        resetTask()
                    }
                    persist()
                    status = if (projects.isEmpty()) "设备在线。请在电脑上授权项目。" else "设备在线，项目列表已更新。"
                } }, enabled = config != null && !busy) { Text("刷新项目") }
            }
            if (projects.isNotEmpty()) {
                Text("授权项目", style = MaterialTheme.typography.titleMedium)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    projects.forEach { id ->
                        FilterChip(selected = project == id, onClick = {
                            if (project != id) {
                                project = id
                                resetTask()
                                perform { persist(); status = "已切换项目。" }
                            }
                        }, label = { Text(id) }, enabled = !busy)
                    }
                }
            }
            if (project.isNotEmpty()) {
                OutlinedTextField(prompt, { prompt = it }, Modifier.fillMaxWidth(),
                    label = { Text("任务提示") }, enabled = !busy && requestID.isEmpty(), minLines = 3)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = { perform { submit() } },
                        enabled = !busy && taskID.isEmpty() && prompt.isNotBlank() && prompt.toByteArray().size <= 4096) {
                        Text(if (requestID.isEmpty()) "提交任务" else "重试原提交")
                    }
                    if (requestID.isNotEmpty()) {
                        OutlinedButton(onClick = { perform {
                            val id = taskID.ifEmpty {
                                java.security.MessageDigest.getInstance("SHA-256").digest(requestID.toByteArray())
                                    .take(16).joinToString("") { "%02x".format(it.toInt() and 255) }
                            }
                            val reply = JSONObject(query("agentdeck-task-receipt-v1",
                                JSONObject().put("projectId", project).put("taskId", id)))
                            require(reply.getString("taskId") == id)
                            taskID = id
                            taskStatus = reply.getString("status")
                            persist()
                            status = "任务状态已更新。"
                        } }, enabled = !busy) { Text("查询回执") }
                        TextButton(onClick = { perform {
                            resetTask()
                            prompt = ""
                            persist()
                            status = "可以填写新任务。"
                        } }, enabled = !busy) { Text("新任务") }
                    }
                }
            }
            if (taskID.isNotEmpty()) {
                Text("任务 " + taskID.take(12), style = MaterialTheme.typography.titleMedium)
                Text(when (taskStatus) {
                    "awaiting_local_review" -> "待电脑审核"
                    "claimed" -> "已领取，等待最终状态"
                    "declined" -> "已拒绝"
                    "completed" -> "已完成"
                    "failed" -> "执行失败"
                    "interrupted" -> "已中断"
                    else -> "查询回执可刷新状态"
                })
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { perform {
                        val reply = JSONObject(query("agentdeck-task-log-v1",
                            JSONObject().put("projectId", project).put("taskId", taskID).put("after", cursor)))
                        val page = reply.getJSONObject("page")
                        val events = page.getJSONArray("events")
                        for (i in 0 until events.length()) {
                            val event = events.getJSONObject(i)
                            log = (log + event.optString("text")).takeLast(32768)
                            if (event.optString("type") == "task/finished") taskStatus = event.getString("status")
                        }
                        cursor = page.getInt("next")
                        status = if (events.length() == 0) "目前没有新日志。" else "已补拉日志。"
                    } }, enabled = !busy) { Text("补拉日志") }
                    TextButton(onClick = { confirmCancel = true }, enabled = !busy &&
                        taskStatus !in listOf("declined", "completed", "failed", "interrupted")) { Text("请求取消") }
                }
                if (log.isNotBlank()) SelectionContainer { Text(log, fontFamily = FontFamily.Monospace) }
            }
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        }
        if (confirmCancel) AlertDialog(
            onDismissRequest = { confirmCancel = false },
            title = { Text("请求取消任务？") },
            text = { Text("设备 ${device.uppercase()} · $project · ${taskID.take(12)}") },
            confirmButton = { TextButton(onClick = {
                confirmCancel = false
                perform {
                    val reply = JSONObject(query("agentdeck-task-cancel-v1",
                        JSONObject().put("projectId", project).put("taskId", taskID)))
                    require(reply.optBoolean("requested"))
                    status = "取消请求已记录。请查询最终状态。"
                }
            }) { Text("请求取消") } },
            dismissButton = { TextButton(onClick = { confirmCancel = false }) { Text("返回") } }
        )
    }
}
