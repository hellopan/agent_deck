package com.agentdeck.lab.ui

import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.agentdeck.lab.BuildConfig
import com.agentdeck.lab.R

/** The one external address the app links to. Keep it in sync with the desktop
 *  about pane and the repository's origin remote. */
internal const val GITHUB_URL = "https://github.com/hellopan/agent_deck"

/**
 * A real page rather than a fourth tab: the three home tabs are task-focused,
 * and squeezing one more into the bar would make all of them harder to hit.
 * Both the device list and the unpaired connect screen link here.
 */
@Composable
internal fun AboutScreen(onBack: () -> Unit) {
    val context = LocalContext.current
    Column(Modifier.fillMaxSize().background(Tok.Bg).statusBarsPadding()) {
        NavTop("关于", onBack = onBack)
        Column(
            Modifier.fillMaxSize().padding(horizontal = 18.dp)
                .verticalScroll(rememberScrollState()),
        ) {
            Spacer(Modifier.height(18.dp))
            // A plain PNG, not @mipmap/ic_launcher: on API 26+ that id resolves
            // to an AdaptiveIconDrawable, which a Compose Image cannot paint.
            Image(
                painter = painterResource(R.drawable.ic_app_mark),
                contentDescription = "AgentDeck 图标",
                modifier = Modifier.size(56.dp),
            )
            Spacer(Modifier.height(12.dp))
            Text("AgentDeck", color = Tok.Tx, fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.height(4.dp))
            Text("远程编码工作台", color = Tok.Tx2, fontSize = 13.sp)
            Spacer(Modifier.height(12.dp))
            Pill("v" + BuildConfig.VERSION_NAME)
            Spacer(Modifier.height(18.dp))
            Text(
                "桌面端（Windows 托盘）与 Android 端通过 Relay 连接；配对后端到端加密，" +
                    "手机上可查看项目、下发任务与审批。",
                color = Tok.Tx2, fontSize = 12.sp, lineHeight = 19.sp,
            )
            Spacer(Modifier.height(22.dp))

            DeskCard {
                MetaLabel("源码与反馈")
                Spacer(Modifier.height(8.dp))
                Text(
                    GITHUB_URL,
                    color = Tok.Tx, fontSize = 11.5.sp, fontFamily = Tok.Mono,
                    lineHeight = 17.sp,
                )
                Spacer(Modifier.height(14.dp))
                PButton("在浏览器中打开", kind = ButtonKind.Primary, height = 44.dp) {
                    openGithub(context)
                }
                Spacer(Modifier.height(8.dp))
                PButton("复制地址", kind = ButtonKind.Ghost, height = 44.dp) {
                    val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                    clipboard.setPrimaryClip(ClipData.newPlainText("AgentDeck GitHub", GITHUB_URL))
                    Toast.makeText(context, "已复制地址", Toast.LENGTH_SHORT).show()
                }
                Spacer(Modifier.height(12.dp))
                Text(
                    "问题反馈、更新日志与新版本安装包都在这个仓库的 Issues 与 Releases。",
                    color = Tok.Tx3, fontSize = 11.sp, lineHeight = 17.sp,
                )
            }
            Spacer(Modifier.height(16.dp))
            Text(
                "电脑端在「设置 → 接入 → 版本与更新」检查更新；手机端安装包从仓库 Releases 获取。",
                color = Tok.Tx3, fontSize = 11.sp, lineHeight = 17.sp,
            )
            Spacer(Modifier.height(28.dp))
        }
    }
}

private fun openGithub(context: Context) {
    // ACTION_VIEW with an https URI is resolved by the system; no browser
    // package needs to be named. Catch the rare device without one so the
    // button reports the problem instead of crashing the app.
    try {
        context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(GITHUB_URL)))
    } catch (error: ActivityNotFoundException) {
        Toast.makeText(context, "没有可用的浏览器", Toast.LENGTH_SHORT).show()
    }
}
