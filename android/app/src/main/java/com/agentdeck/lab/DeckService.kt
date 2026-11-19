package com.agentdeck.lab

import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.IBinder
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * The only thing that keeps working when the screen is off. The Activity's poll
 * loop dies with the Activity, which is why a request that arrived while the
 * phone was in a pocket used to wait until the next time the app was opened.
 *
 * It polls the same two requests the UI does (approvals and queue) and posts at
 * most one notification per channel. A foreground service is not free: Doze
 * stops background timers, and this app cannot use FCM because the desktop is
 * self-hosted. Three seconds is the same cadence the foreground UI uses, and it
 * is the interval the notification appendix was written against.
 */
internal class DeckService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var loop: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        DeckNotifier.ensure(this)
        startForeground(ID, DeckNotifier.ongoing(this, "AgentDeck 正在连接", "息屏也能收到待批准"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (loop == null) loop = scope.launch { poll() }
        return START_STICKY
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    private suspend fun poll() {
        val store = LabState(this)
        var api: AgentApi? = null
        var label = "AgentDeckLab"
        var lastApprovalKey = ""
        val statuses = mutableMapOf<String, String>()

        while (scope.isActive) {
            try {
                if (api == null) {
                    val saved = withContext(Dispatchers.IO) { store.read() }
                        ?: throw IllegalStateException("还没有配对")
                    val config = saved.optJSONObject("config")?.takeIf { it.has("keyAlias") }
                        ?: throw IllegalStateException("还没有配对")
                    val device = saved.optString("device")
                    val hosts = saved.optJSONObject("hosts")
                    label = hosts?.optString(device)?.takeIf { it.isNotBlank() } ?: "AgentDeckLab"
                    api = AgentApi(config, device, saved.optString("endpoint"))
                }
                val snapshot = withContext(Dispatchers.IO) { api!!.approvals() }
                val approvals = snapshot.approvals
                if (approvals.isNotEmpty()) {
                    val key = approvals.joinToString(",") { it.id }
                    if (key != lastApprovalKey) {
                        DeckNotifier.approval(this, approvals, label)
                        lastApprovalKey = key
                    }
                } else if (lastApprovalKey.isNotEmpty()) {
                    DeckNotifier.clearApproval(this)
                    lastApprovalKey = ""
                }
                val detail = when {
                    approvals.isNotEmpty() -> "${approvals.size} 条待批准"
                    snapshot.running != null -> "任务运行中"
                    else -> "已连接，等待任务"
                }
                DeckNotifier.updateOngoing(this, label, detail)

                val queue = withContext(Dispatchers.IO) { api!!.queue() }
                for (item in queue) {
                    val previous = statuses[item.taskId]
                    when {
                        previous == null -> Unit
                        previous == item.status -> Unit
                        item.status == "failed" || item.status == "interrupted" ->
                            DeckNotifier.failure(this, label)
                        item.status == "completed" -> {
                            val elapsed = item.submittedAt?.let {
                                ((System.currentTimeMillis() - it) / 1000).toInt()
                            } ?: 0
                            DeckNotifier.done(this, label, elapsed)
                        }
                    }
                    statuses[item.taskId] = item.status
                }
            } catch (_: Exception) {
                api = null
                DeckNotifier.updateOngoing(this, label, "连不上电脑，正在重试")
            }
            delay(3_000)
        }
    }

    companion object {
        private const val ID = 1

        internal fun start(context: Context) {
            val intent = Intent(context, DeckService::class.java)
            context.startForegroundService(intent)
        }

        internal fun stop(context: Context) {
            context.stopService(Intent(context, DeckService::class.java))
        }
    }
}
