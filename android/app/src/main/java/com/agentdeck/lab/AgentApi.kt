package com.agentdeck.lab

import org.json.JSONArray
import org.json.JSONObject
import java.time.OffsetDateTime

internal data class QueueItem(
    val taskId: String,
    val projectId: String,
    val status: String,
    val submittedAt: Long?,
    val summary: String,
    val prompt: String,
    val threadId: String,
) {
    val running: Boolean get() = status == "running"
    val finished: Boolean get() = status == "completed" || status == "failed" ||
        status == "interrupted" || status == "canceled"
}

internal data class ApprovalItem(
    val id: String,
    val taskId: String,
    val projectId: String,
    val adapterId: String,
    val kind: String,
    val command: String,
    val cwd: String,
    val reason: String,
    val secondsLeft: Int,
)

internal data class RunningTask(
    val taskId: String,
    val projectId: String,
    val startedAt: Long?,
    val waitingFor: String,
)

internal data class Snapshot(
    val approvals: List<ApprovalItem>,
    val running: RunningTask?,
)

internal data class TraceEvent(
    val type: String,
    val text: String,
    val status: String,
    val at: Long?,
)

internal data class TracePage(val events: List<TraceEvent>, val next: Int)

/**
 * One short request per tunnel, matching the Agent's one-answer-per-connection
 * design. Every method blocks; callers run them on Dispatchers.IO.
 */
internal class AgentApi(config: JSONObject, private val device: String, private val endpoint: String) {
    private val relay = RelayClient(config, device, endpoint)

    private fun call(action: String, payload: JSONObject? = null): String =
        relay.query(if (payload == null) action else "$action $payload")

    fun projects(): List<String> {
        val reply = JSONArray(call("agentdeck-projects-v1"))
        return List(reply.length()) { reply.getString(it) }
    }

    fun queue(): List<QueueItem> {
        val items = JSONObject(call("agentdeck-queue-v1")).optJSONArray("items") ?: JSONArray()
        return List(items.length()) { index ->
            val item = items.getJSONObject(index)
            QueueItem(
                taskId = item.optString("taskId"),
                projectId = item.optString("projectId"),
                status = item.optString("status"),
                submittedAt = parseTime(item.optString("submittedAt")),
                summary = item.optString("summary"),
                prompt = item.optString("prompt"),
                threadId = item.optString("threadId"),
            )
        }.filter { it.taskId.isNotEmpty() }
    }

    fun approvals(): Snapshot {
        val reply = JSONObject(call("agentdeck-approvals-v1"))
        val list = reply.optJSONArray("approvals") ?: JSONArray()
        val approvals = List(list.length()) { index ->
            val item = list.getJSONObject(index)
            ApprovalItem(
                id = item.optString("id"),
                taskId = item.optString("taskId"),
                projectId = item.optString("projectId"),
                adapterId = item.optString("adapterId"),
                kind = item.optString("kind"),
                command = item.optString("command"),
                cwd = item.optString("cwd"),
                reason = item.optString("reason"),
                secondsLeft = item.optInt("secondsLeft"),
            )
        }.filter { it.id.isNotEmpty() }
        val running = reply.optJSONObject("running")?.let {
            RunningTask(
                taskId = it.optString("taskId"),
                projectId = it.optString("projectId"),
                startedAt = parseTime(it.optString("startedAt")),
                waitingFor = it.optString("waitingFor"),
            )
        }
        return Snapshot(approvals, running)
    }

    fun decide(approvalId: String, accept: Boolean) {
        val reply = JSONObject(call("agentdeck-approve-v1", JSONObject()
            .put("approvalId", approvalId)
            .put("decision", if (accept) "accept" else "decline")))
        require(reply.optBoolean("ok")) {
            reply.optString("error").ifEmpty { "电脑未接受这次决定" }
        }
    }

    fun submit(projectId: String, requestId: String, prompt: String, threadId: String = ""): String {
        val payload = JSONObject()
            .put("projectId", projectId).put("requestId", requestId).put("prompt", prompt)
        // An empty threadId asks for a new conversation; the Agent only accepts a
        // thread that one of its own earlier turns created.
        if (threadId.isNotEmpty()) payload.put("threadId", threadId)
        val reply = JSONObject(call("agentdeck-task-submit-v1", payload))
        val id = reply.optString("taskId")
        require(id.matches(Regex("[a-f0-9]{32}"))) { "电脑没有返回任务回执" }
        return id
    }

    fun cancel(projectId: String, taskId: String) {
        val reply = JSONObject(call("agentdeck-task-cancel-v1", JSONObject()
            .put("projectId", projectId).put("taskId", taskId)))
        require(reply.optBoolean("requested")) { "电脑拒绝了叫停请求" }
    }

    fun logs(projectId: String, taskId: String, after: Int): TracePage {
        val reply = JSONObject(call("agentdeck-task-log-v1", JSONObject()
            .put("projectId", projectId).put("taskId", taskId).put("after", after)))
        val page = reply.optJSONObject("page") ?: return TracePage(emptyList(), after)
        val events = page.optJSONArray("events") ?: JSONArray()
        val parsed = List(events.length()) { index ->
            val event = events.getJSONObject(index)
            TraceEvent(
                type = event.optString("type"),
                text = event.optString("text"),
                status = event.optString("status"),
                at = parseTime(event.optString("at")),
            )
        }
        return TracePage(parsed, page.optInt("next", after))
    }
}

internal fun parseTime(text: String?): Long? {
    if (text.isNullOrEmpty()) return null
    return try {
        OffsetDateTime.parse(text).toInstant().toEpochMilli()
    } catch (_: Exception) {
        null
    }
}

internal fun shortId(id: String): String = id.take(8).uppercase()

internal fun deviceLabel(host: String?, device: String): String =
    host?.takeIf { it.isNotBlank() } ?: "设备 " + device.take(8).uppercase()
