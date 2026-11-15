package com.agentdeck.lab

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.util.UUID

/**
 * Everything the phone knows about the desktop. One poll loop keeps the queue and
 * the approval table fresh; every write goes back through [AgentApi] so the
 * desktop stays the only place that decides what may run.
 */
internal class Desk(private val store: LabState, private val scope: CoroutineScope) {
    var config by mutableStateOf<JSONObject?>(null)
        private set
    var endpoint by mutableStateOf("")
        private set
    var device by mutableStateOf("")
        private set
    var hosts by mutableStateOf<Map<String, String>>(emptyMap())
        private set
    var fingerprint by mutableStateOf("")
        private set
    var projects by mutableStateOf<List<String>>(emptyList())
        private set
    var project by mutableStateOf("")
        private set
    var items by mutableStateOf<List<QueueItem>>(emptyList())
        private set
    var approvals by mutableStateOf<List<ApprovalItem>>(emptyList())
        private set
    var running by mutableStateOf<RunningTask?>(null)
        private set
    var reachable by mutableStateOf<Boolean?>(null)
        private set
    var latency by mutableStateOf<Int?>(null)
        private set
    var awaitingConfirm by mutableStateOf(false)
        private set
    var notice by mutableStateOf("")
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var busy by mutableStateOf(false)
        private set
    var draft by mutableStateOf("")
        private set
    var submitted by mutableStateOf(0)
        private set

    private var requestId = ""
    private var pollJob: Job? = null

    val paired: Boolean get() = config != null
    val host: String? get() = hosts[device]
    val label: String get() = deviceLabel(hosts[device], device)

    fun editDraft(text: String) {
        draft = text
        save()
    }

    fun switchProject(id: String) {
        project = id
        save()
    }

    suspend fun restore() {
        val saved = try {
            withContext(Dispatchers.IO) { store.read() }
        } catch (_: Exception) {
            null
        } ?: return
        val pairing = saved.optJSONObject("config") ?: return
        if (!pairing.has("keyAlias")) return
        config = pairing
        endpoint = saved.optString("endpoint")
        device = saved.optString("device")
        project = saved.optString("project")
        draft = saved.optString("draft")
        requestId = saved.optString("requestId")
        hosts = readHosts(saved.optJSONObject("hosts"))
    }

    fun start() {
        if (pollJob?.isActive == true) return
        pollJob = scope.launch {
            while (isActive) {
                if (paired) sync(silent = true)
                delay(if (approvals.isNotEmpty()) 2_000 else 3_000)
            }
        }
    }

    fun stop() {
        pollJob?.cancel()
        pollJob = null
    }

    fun refresh() = scope.launch { sync(silent = false, announce = true) }

    fun pair(raw: String) = busyAction("正在配对…") {
        val candidate = withContext(Dispatchers.IO) { requestPairing(raw) }
        config = candidate.config
        endpoint = candidate.config.getString("relayUrl")
        device = candidate.device
        fingerprint = candidate.fingerprint
        hosts = hosts + (candidate.device to candidate.host)
        projects = emptyList()
        project = ""
        items = emptyList()
        approvals = emptyList()
        running = null
        awaitingConfirm = true
        reachable = null
        save()
        notice = "已把指纹发给电脑，请在电脑上确认这台手机"
    }

    fun switchDevice(id: String) {
        if (id == device) return
        device = id
        project = ""
        projects = emptyList()
        items = emptyList()
        approvals = emptyList()
        running = null
        reachable = null
        save()
        refresh()
    }

    fun submit(prompt: String) = busyAction("正在提交…") {
        val api = api() ?: error("还没有配对")
        require(project.isNotEmpty()) { "请选择项目" }
        val body = prompt.trim()
        require(body.isNotEmpty()) { "任务提示不能为空" }
        require(body.toByteArray(Charsets.UTF_8).size <= 4096) { "任务提示超过 4 KB" }
        if (requestId.isEmpty()) requestId = UUID.randomUUID().toString().replace("-", "")
        save()
        val id = withContext(Dispatchers.IO) { api.submit(project, requestId, body) }
        requestId = ""
        draft = ""
        submitted += 1
        save()
        notice = "已提交 " + shortId(id) + "，电脑会在队列里执行"
        sync(silent = true)
    }

    fun decide(id: String, accept: Boolean) = busyAction(if (accept) "正在批准…" else "正在拒绝…") {
        val api = api() ?: error("还没有配对")
        withContext(Dispatchers.IO) { api.decide(id, accept) }
        notice = if (accept) "已允许这一次执行" else "已拒绝，Codex 会收到说明"
        sync(silent = true)
    }

    fun cancel(taskId: String, projectId: String) = busyAction("正在叫停…") {
        val api = api() ?: error("还没有配对")
        withContext(Dispatchers.IO) { api.cancel(projectId.ifEmpty { project }, taskId) }
        notice = "叫停请求已记录"
        sync(silent = true)
    }

    /**
     * Fills the view state from a fixed snapshot. Only the debug screen gallery
     * calls this, so the UI can be reviewed without a paired desktop.
     */
    internal fun seedPreview(
        queue: List<QueueItem>,
        approvalList: List<ApprovalItem>,
        active: RunningTask?,
        paired: Boolean = true,
    ) {
        items = queue
        approvals = approvalList
        running = active
        reachable = paired
        latency = if (paired) 28 else null
        if (!paired) return
        config = JSONObject()
            .put("keyAlias", "preview")
            .put("relayUrl", "wss://preview.invalid")
            .put("devices", JSONObject()
                .put("8f2a1c4d", JSONObject())
                .put("b7e39a02", JSONObject()))
        device = "8f2a1c4d"
        hosts = mapOf("8f2a1c4d" to "DESKTOP-8F2A", "b7e39a02" to "LAPTOP-K3")
        fingerprint = "3f2a91d0".repeat(8)
        projects = listOf("web-app", "api-gateway")
        project = "web-app"
    }

    fun dismissNotice() {
        notice = ""
        error = null
    }

    internal fun api(): AgentApi? {
        val pairing = config ?: return null
        return AgentApi(pairing, device, endpoint)
    }

    private suspend fun sync(silent: Boolean, announce: Boolean = false) {
        val api = api() ?: return
        if (busy && !announce) return
        val started = System.nanoTime()
        try {
            val snapshot = withContext(Dispatchers.IO) { api.approvals() }
            latency = ((System.nanoTime() - started) / 1_000_000).toInt()
            approvals = snapshot.approvals
            running = snapshot.running
            val queue = withContext(Dispatchers.IO) { api.queue() }
            items = queue
            reachable = true
            awaitingConfirm = false
            error = null
            if (projects.isEmpty()) loadProjects(api)
        } catch (failure: Exception) {
            reachable = false
            if (!silent) error = readable(failure)
        }
    }

    private suspend fun loadProjects(api: AgentApi) {
        try {
            val ids = withContext(Dispatchers.IO) { api.projects() }
            projects = ids
            if (project !in ids) {
                project = ids.firstOrNull().orEmpty()
                save()
            }
        } catch (_: Exception) {
            // An unauthorized phone is expected to fail here; the poll loop retries.
        }
    }

    private fun save() {
        val pairing = config ?: return
        val hostMap = JSONObject()
        hosts.forEach { (id, name) -> hostMap.put(id, name) }
        val payload = JSONObject()
            .put("config", pairing)
            .put("endpoint", endpoint)
            .put("device", device)
            .put("project", project)
            .put("draft", draft)
            .put("requestId", requestId)
            .put("hosts", hostMap)
        scope.launch(Dispatchers.IO) {
            runCatching { store.write(payload) }
        }
    }

    private fun readHosts(raw: JSONObject?): Map<String, String> {
        if (raw == null) return emptyMap()
        val result = mutableMapOf<String, String>()
        for (key in raw.keys()) {
            val value = raw.optString(key)
            if (value.isNotEmpty()) result[key] = value
        }
        return result
    }

    private fun busyAction(progress: String, action: suspend () -> Unit) {
        if (busy) return
        scope.launch {
            busy = true
            notice = progress
            error = null
            try {
                action()
            } catch (cancel: kotlinx.coroutines.CancellationException) {
                throw cancel
            } catch (failure: Exception) {
                error = readable(failure)
                notice = ""
            } finally {
                busy = false
            }
        }
    }

    private fun readable(failure: Exception): String {
        val text = failure.message.orEmpty().trim()
        return when {
            text.isEmpty() -> "连不上电脑，检查手机与电脑的网络，再重试。"
            text.contains("phone not paired") -> "电脑不再信任这台手机，请重新配对。"
            else -> text
        }
    }
}
