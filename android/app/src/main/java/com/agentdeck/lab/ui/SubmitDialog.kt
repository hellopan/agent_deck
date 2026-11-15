package com.agentdeck.lab.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import com.agentdeck.lab.Desk

@Composable
internal fun SubmitDialog(desk: Desk, onDismiss: () -> Unit) {
    var text by remember { mutableStateOf(desk.draft) }
    val bytes = text.toByteArray(Charsets.UTF_8).size
    Dialog(onDismissRequest = {
        desk.editDraft(text)
        onDismiss()
    }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
        Box(Modifier.fillMaxSize().padding(horizontal = 16.dp), contentAlignment = Alignment.Center) {
            Column(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Tok.Panel)
                    .border(1.dp, Tok.Line, RoundedCornerShape(14.dp)).padding(16.dp),
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("提交新任务", color = Tok.Tx, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                    Spacer(Modifier.weight(1f))
                    Text(
                        "关闭",
                        color = Tok.Tx3,
                        fontSize = 11.sp,
                        modifier = Modifier.clip(RoundedCornerShape(6.dp))
                            .clickable(role = Role.Button) { desk.editDraft(text); onDismiss() }
                            .padding(horizontal = 8.dp, vertical = 4.dp),
                    )
                }
                Spacer(Modifier.height(14.dp))
                MetaLabel("项目")
                Spacer(Modifier.height(7.dp))
                if (desk.projects.isEmpty()) {
                    Text("电脑还没授权任何项目，先在电脑端「设置」里加入目录。", color = Tok.Amber, fontSize = 11.sp, lineHeight = 17.sp)
                } else {
                    LazyRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        items(desk.projects) { id ->
                            val on = id == desk.project
                            Text(
                                id,
                                color = if (on) Tok.Tx else Tok.Tx3,
                                fontSize = 11.sp,
                                fontFamily = Tok.Mono,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.clip(RoundedCornerShape(6.dp))
                                    .background(if (on) Tok.AmberBg else Tok.Code)
                                    .border(1.dp, if (on) Tok.Amber.copy(alpha = 0.45f) else Tok.Line2, RoundedCornerShape(6.dp))
                                    .clickable(role = Role.RadioButton) { desk.switchProject(id) }
                                    .padding(horizontal = 10.dp, vertical = 7.dp),
                            )
                        }
                    }
                }
                Spacer(Modifier.height(14.dp))
                MetaLabel("要做什么")
                Spacer(Modifier.height(7.dp))
                BasicTextField(
                    value = text,
                    onValueChange = { if (it.toByteArray(Charsets.UTF_8).size <= 4096) text = it },
                    textStyle = TextStyle(color = Tok.Tx, fontSize = 12.5.sp, fontFamily = Tok.Mono, lineHeight = 19.sp),
                    cursorBrush = SolidColor(Tok.Amber),
                    modifier = Modifier.fillMaxWidth().height(132.dp).clip(RoundedCornerShape(8.dp))
                        .background(Tok.Code).border(1.dp, Tok.Line, RoundedCornerShape(8.dp)).padding(12.dp),
                    decorationBox = { inner ->
                        Box {
                            if (text.isEmpty()) {
                                Text(
                                    "例如：补全 src/user 下五个模块的单元测试，只在只读范围内检查，不要改配置。",
                                    color = Tok.Tx3,
                                    fontSize = 12.sp,
                                    lineHeight = 18.sp,
                                )
                            }
                            inner()
                        }
                    },
                )
                Spacer(Modifier.height(8.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("越具体的提示词，越不需要中途批准。", color = Tok.Tx3, fontSize = 10.sp)
                    Spacer(Modifier.weight(1f))
                    Text(
                        "$bytes / 4096",
                        color = if (bytes > 3800) Tok.Amber else Tok.Tx3,
                        fontSize = 10.sp,
                        fontFamily = Tok.Mono,
                    )
                }
                desk.error?.let {
                    Spacer(Modifier.height(10.dp))
                    Text(it, color = Tok.Red, fontSize = 11.sp, lineHeight = 17.sp)
                }
                Spacer(Modifier.height(16.dp))
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(9.dp)) {
                    Box(Modifier.weight(1f)) {
                        PButton("取消", enabled = !desk.busy, onClick = { desk.editDraft(text); onDismiss() })
                    }
                    Box(Modifier.weight(1.4f)) {
                        PButton(
                            if (desk.busy) "正在提交…" else "提交",
                            kind = ButtonKind.Primary,
                            enabled = !desk.busy && desk.project.isNotEmpty() && text.isNotBlank(),
                            leading = { PlusGlyph(androidx.compose.ui.graphics.Color.White, Modifier.size(15.dp)) },
                            onClick = { desk.submit(text) },
                        )
                    }
                }
                Spacer(Modifier.height(4.dp))
            }
        }
    }
}
