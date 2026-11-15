package com.agentdeck.lab.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.Canvas

@Composable
internal fun Dot(color: Color, size: Dp = 5.dp) {
    Box(Modifier.size(size).clip(CircleShape).background(color))
}

@Composable
internal fun Chevron(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val path = Path().apply {
            moveTo(w * 0.62f, h * 0.2f)
            lineTo(w * 0.36f, h * 0.5f)
            lineTo(w * 0.62f, h * 0.8f)
        }
        drawPath(path, color, style = Stroke(width = w * 0.13f, cap = StrokeCap.Round))
    }
}

@Composable
internal fun PlusGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val stroke = size.width * 0.13f
        drawLine(color, Offset(size.width * 0.5f, size.height * 0.18f), Offset(size.width * 0.5f, size.height * 0.82f), stroke, StrokeCap.Round)
        drawLine(color, Offset(size.width * 0.18f, size.height * 0.5f), Offset(size.width * 0.82f, size.height * 0.5f), stroke, StrokeCap.Round)
    }
}

@Composable
internal fun LockGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val stroke = w * 0.1f
        drawRoundRect(
            color,
            topLeft = Offset(w * 0.2f, h * 0.45f),
            size = androidx.compose.ui.geometry.Size(w * 0.6f, h * 0.42f),
            cornerRadius = androidx.compose.ui.geometry.CornerRadius(w * 0.12f),
            style = Stroke(width = stroke),
        )
        val shackle = Path().apply {
            moveTo(w * 0.33f, h * 0.45f)
            lineTo(w * 0.33f, h * 0.33f)
            cubicTo(w * 0.33f, h * 0.12f, w * 0.67f, h * 0.12f, w * 0.67f, h * 0.33f)
            lineTo(w * 0.67f, h * 0.45f)
        }
        drawPath(shackle, color, style = Stroke(width = stroke, cap = StrokeCap.Round))
    }
}

@Composable
internal fun AlertGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val stroke = w * 0.1f
        val triangle = Path().apply {
            moveTo(w * 0.5f, h * 0.1f)
            lineTo(w * 0.95f, h * 0.88f)
            lineTo(w * 0.05f, h * 0.88f)
            close()
        }
        drawPath(triangle, color, style = Stroke(width = stroke))
        drawLine(color, Offset(w * 0.5f, h * 0.38f), Offset(w * 0.5f, h * 0.62f), stroke, StrokeCap.Round)
        drawCircle(color, radius = stroke * 0.55f, center = Offset(w * 0.5f, h * 0.75f))
    }
}

@Composable
internal fun NavTop(
    title: String,
    modifier: Modifier = Modifier,
    onBack: (() -> Unit)? = null,
    trailing: @Composable RowScope.() -> Unit = {},
) {
    Row(
        modifier.fillMaxWidth().height(46.dp).padding(horizontal = 18.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (onBack != null) {
            Box(
                Modifier.size(28.dp).clip(RoundedCornerShape(7.dp)).background(Tok.Panel2)
                    .clickable(role = Role.Button, onClick = onBack),
                contentAlignment = Alignment.Center,
            ) { Chevron(Tok.Tx2, Modifier.size(15.dp)) }
            Spacer(Modifier.width(10.dp))
        }
        Text(title, color = Tok.Tx, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, letterSpacing = (-0.2).sp)
        Spacer(Modifier.weight(1f))
        trailing()
    }
}

@Composable
internal fun DeskCard(
    modifier: Modifier = Modifier,
    border: Color = Tok.Line,
    background: Color = Tok.Panel,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(
        modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(background)
            .border(1.dp, border, RoundedCornerShape(10.dp)).padding(horizontal = 13.dp, vertical = 12.dp),
        content = content,
    )
}

@Composable
internal fun Tag(text: String, fg: Color, bg: Color) {
    Text(
        text,
        color = fg,
        fontSize = 9.5.sp,
        maxLines = 1,
        modifier = Modifier.clip(RoundedCornerShape(2.dp)).background(bg).padding(horizontal = 5.dp, vertical = 1.5.dp),
    )
}

@Composable
internal fun MetaLabel(text: String, modifier: Modifier = Modifier) {
    Text(text, color = Tok.Tx3, fontSize = 9.sp, letterSpacing = 0.6.sp, maxLines = 1, modifier = modifier)
}

internal enum class ButtonKind { Primary, Danger, Ghost }

@Composable
internal fun PButton(
    text: String,
    modifier: Modifier = Modifier,
    kind: ButtonKind = ButtonKind.Ghost,
    enabled: Boolean = true,
    leading: (@Composable () -> Unit)? = null,
    onClick: () -> Unit,
) {
    val border = when (kind) {
        ButtonKind.Primary -> Tok.Btn
        ButtonKind.Danger -> Tok.Red.copy(alpha = 0.42f)
        ButtonKind.Ghost -> Tok.Line
    }
    val background = if (kind == ButtonKind.Primary) Tok.Btn else Color.Transparent
    val color = when (kind) {
        ButtonKind.Primary -> Color.White
        ButtonKind.Danger -> Tok.Red
        ButtonKind.Ghost -> Tok.Tx2
    }
    val alpha = if (enabled) 1f else 0.38f
    Row(
        modifier.fillMaxWidth().height(44.dp).clip(RoundedCornerShape(10.dp))
            .background(if (enabled) background else background.copy(alpha = 0.4f))
            .border(1.dp, border.copy(alpha = border.alpha * alpha), RoundedCornerShape(10.dp))
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        leading?.let {
            it()
            Spacer(Modifier.width(7.dp))
        }
        Text(text, color = color.copy(alpha = alpha), fontSize = 14.sp, fontWeight = if (kind == ButtonKind.Primary) FontWeight.SemiBold else FontWeight.Medium)
    }
}

@Composable
internal fun SegTabs(labels: List<String>, selected: Int, onSelect: (Int) -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).background(Tok.Panel)
            .border(1.dp, Tok.Line, RoundedCornerShape(8.dp)).padding(3.dp),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        labels.forEachIndexed { index, label ->
            val on = index == selected
            Box(
                Modifier.weight(1f).clip(RoundedCornerShape(6.dp))
                    .background(if (on) Tok.Panel2 else Color.Transparent)
                    .clickable(role = Role.Tab) { onSelect(index) }
                    .padding(vertical = 6.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    label,
                    color = if (on) Tok.Tx else Tok.Tx3,
                    fontSize = 11.5.sp,
                    fontWeight = if (on) FontWeight.Medium else FontWeight.Normal,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

@Composable
internal fun KeyValueCell(label: String, value: String, modifier: Modifier = Modifier, valueColor: Color = Tok.Tx2) {
    Column(modifier) {
        MetaLabel(label)
        Spacer(Modifier.height(5.dp))
        Text(value, color = valueColor, fontSize = 11.sp, fontFamily = Tok.Mono, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
internal fun EmptyState(title: String, hint: String) {
    Column(
        Modifier.fillMaxWidth().padding(vertical = 48.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(title, color = Tok.Tx2, fontSize = 13.sp)
        Spacer(Modifier.height(8.dp))
        Text(hint, color = Tok.Tx3, fontSize = 11.sp)
    }
}

@Composable
internal fun Hint(text: String) {
    Text(text, color = Tok.Tx3, fontSize = 11.sp, lineHeight = 18.sp)
}
