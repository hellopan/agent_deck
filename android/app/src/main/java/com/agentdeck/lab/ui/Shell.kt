package com.agentdeck.lab.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

internal enum class DeckTab(val label: String) { Tasks("任务"), Devices("设备"), Approvals("审批") }

/**
 * Three tabs, always visible on the home screen. Approvals became a first-class
 * tab in v3: it used to be a row inside the task list, which meant a request
 * could only be found by scrolling the wrong screen first.
 */
@Composable
internal fun DeckTabBar(selected: Int, approvals: Int, onSelect: (Int) -> Unit) {
    Column(Modifier.fillMaxWidth().background(Tok.TabBar).navigationBarsPadding()) {
        Box(Modifier.fillMaxWidth().height(1.dp).background(Tok.Line))
        Row(Modifier.fillMaxWidth().height(56.dp).padding(top = 7.dp)) {
            DeckTab.entries.forEachIndexed { index, tab ->
                TabItem(
                    tab = tab,
                    on = index == selected,
                    badge = if (index == 2) approvals else 0,
                    modifier = Modifier.weight(1f),
                    onClick = { onSelect(index) },
                )
            }
        }
    }
}

@Composable
private fun TabItem(tab: DeckTab, on: Boolean, badge: Int, modifier: Modifier, onClick: () -> Unit) {
    val color = if (on) Tok.Tx else Tok.Tx3
    Column(
        modifier.height(48.dp).clickable(role = Role.Tab, onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box {
            when (tab) {
                DeckTab.Tasks -> ChatGlyph(color, Modifier.size(21.dp))
                DeckTab.Devices -> MonitorGlyph(color, Modifier.size(21.dp))
                DeckTab.Approvals -> ShieldGlyph(color, Modifier.size(21.dp))
            }
            if (badge > 0) {
                // The ring is a border, not a shadow: Compose has no box-shadow,
                // and the badge sits on top of the icon so it needs to separate
                // itself from whatever it covers.
                Box(
                    Modifier.align(Alignment.TopEnd).offset(x = 9.dp, y = (-4).dp)
                        .height(16.dp).widthIn(min = 16.dp)
                        .clip(RoundedCornerShape(9.dp)).background(Tok.Red)
                        .border(1.5.dp, Tok.BadgeRing, RoundedCornerShape(9.dp))
                        .padding(horizontal = 4.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        if (badge > 9) "9+" else badge.toString(),
                        color = Tok.OnRed, fontSize = 10.sp, lineHeight = 10.sp,
                        fontFamily = Tok.Mono, fontWeight = FontWeight.Medium,
                    )
                }
            }
        }
        Spacer(Modifier.height(4.dp))
        Text(tab.label, color = color, fontSize = 11.sp,
            fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal)
    }
}

/** A one-line divider used between cards and inside grouped lists. */
@Composable
internal fun Hairline() {
    Box(Modifier.fillMaxWidth().height(1.dp).background(Tok.Line2))
}

/** Left colour bar used by approval and running cards. */
@Composable
internal fun EdgeBar(color: Color, height: Int = 0) {
    Box(
        Modifier.width(3.dp).height(if (height > 0) height.dp else 0.dp)
            .clip(RoundedCornerShape(2.dp)).background(color),
    )
}

@Composable
internal fun SectionTitle(text: String) {
    Text(text, Modifier.padding(start = 2.dp, top = 14.dp, bottom = 8.dp),
        color = Tok.Tx3, fontSize = 12.sp)
}
