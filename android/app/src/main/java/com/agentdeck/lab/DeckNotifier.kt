package com.agentdeck.lab

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import com.agentdeck.lab.ui.Tok

/**
 * Notification spec lives in design/v3-tokens.md appendix B. Three event
 * channels and one service channel: the service channel is not an event, it is
 * the notification Android requires for a foreground service, and it is kept at
 * IMPORTANCE_MIN so it never makes a sound.
 *
 * Nothing here puts a command, a path or a branch name into a field. The only
 * facts a lock screen may show are how many requests are waiting, which project
 * they belong to, and one number.
 */
internal object DeckNotifier {
    private const val CHANNEL_APPROVAL = "approval"
    private const val CHANNEL_FAILURE = "failure"
    private const val CHANNEL_DONE = "done"
    private const val CHANNEL_SERVICE = "service"

    private const val ID_SERVICE = 1
    private const val ID_APPROVAL = 2
    private const val ID_FAILURE = 3
    private const val ID_DONE = 4

    private const val TAG_APPROVAL = "approval"
    private const val TAG_FAILURE = "failure"
    private const val TAG_DONE = "done"

    fun ensure(context: Context) {
        Tok.dark = (context.resources.configuration.uiMode and
            android.content.res.Configuration.UI_MODE_NIGHT_MASK) ==
            android.content.res.Configuration.UI_MODE_NIGHT_YES
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_APPROVAL, "待批准", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "有命令等待你批准"
                setShowBadge(true)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_FAILURE, "任务失败", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "任务失败或需要人工介入"
                setShowBadge(true)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_DONE, "任务完成", NotificationManager.IMPORTANCE_LOW).apply {
                description = "任务正常结束，静默"
                setShowBadge(false)
            },
        )
        manager.createNotificationChannel(
            NotificationChannel(CHANNEL_SERVICE, "后台连接", NotificationManager.IMPORTANCE_MIN).apply {
                description = "保持与电脑的连接，息屏也能收到待批准"
                setShowBadge(false)
            },
        )
    }

    fun ongoing(context: Context, label: String, detail: String): Notification =
        base(context, CHANNEL_SERVICE, label, detail)
            .setSmallIcon(R.drawable.ic_notification)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .setShowWhen(false)
            .setPriority(Notification.PRIORITY_MIN)
            .setContentIntent(open(context, null))
            .build()

    fun updateOngoing(context: Context, label: String, detail: String) {
        context.getSystemService(NotificationManager::class.java)
            ?.notify(ID_SERVICE, ongoing(context, label, detail))
    }

    fun approval(context: Context, approvals: List<ApprovalItem>, label: String) {
        if (approvals.isEmpty()) return
        val first = approvals.first()
        val title = if (approvals.size == 1) "1 条待批准" else "${approvals.size} 条待批准"
        val body = when {
            approvals.size == 1 -> "$label · 剩余 ${fmt(first.secondsLeft)}"
            else -> "$label 等 ${approvals.map { it.projectId }.distinct().size} 个项目"
        }
        val notification = base(context, CHANNEL_APPROVAL, title, body)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(color(Tok.Red))
            .setPriority(Notification.PRIORITY_HIGH)
            .setCategory(Notification.CATEGORY_MESSAGE)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .setOnlyAlertOnce(true)
            .setAutoCancel(false)
            .setOngoing(false)
            .setContentIntent(open(context, first.id))
            .build()
        context.getSystemService(NotificationManager::class.java)
            ?.notify(TAG_APPROVAL, ID_APPROVAL, notification)
    }

    fun clearApproval(context: Context) {
        context.getSystemService(NotificationManager::class.java)?.cancel(TAG_APPROVAL, ID_APPROVAL)
    }

    fun failure(context: Context, label: String) {
        val notification = base(context, CHANNEL_FAILURE, "任务失败", "$label · 需要人看一眼")
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(color(Tok.Red))
            .setPriority(Notification.PRIORITY_HIGH)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .setAutoCancel(true)
            .setContentIntent(open(context, null))
            .build()
        context.getSystemService(NotificationManager::class.java)?.notify(TAG_FAILURE, ID_FAILURE, notification)
    }

    fun done(context: Context, label: String, seconds: Int) {
        val notification = base(context, CHANNEL_DONE, "任务完成", "$label · 耗时 ${fmt(seconds)}")
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(color(Tok.Green))
            .setPriority(Notification.PRIORITY_LOW)
            .setVisibility(Notification.VISIBILITY_PUBLIC)
            .setOnlyAlertOnce(true)
            .setAutoCancel(true)
            .setContentIntent(open(context, null))
            .build()
        context.getSystemService(NotificationManager::class.java)?.notify(TAG_DONE, ID_DONE, notification)
    }

    private fun base(context: Context, channel: String, title: String, text: String): Notification.Builder =
        Notification.Builder(context, channel)

    private fun open(context: Context, approvalId: String?): PendingIntent {
        val intent = Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP
            if (approvalId != null) {
                putExtra(MainActivity.EXTRA_APPROVAL, approvalId)
                data = Uri.parse("agentdeck://approval/$approvalId")
            }
        }
        return PendingIntent.getActivity(
            context,
            approvalId?.hashCode() ?: 0,
            intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
    }

    private fun color(value: androidx.compose.ui.graphics.Color): Int = value.toArgb()

    private fun fmt(seconds: Int): String {
        val safe = seconds.coerceAtLeast(0)
        return String.format("%d:%02d", safe / 60, safe % 60)
    }
}

private fun androidx.compose.ui.graphics.Color.toArgb(): Int = android.graphics.Color.argb(
    (alpha * 255).toInt(), (red * 255).toInt(), (green * 255).toInt(), (blue * 255).toInt(),
)
