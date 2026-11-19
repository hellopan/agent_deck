package com.agentdeck.lab.ui

import androidx.compose.foundation.Canvas
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
import androidx.compose.foundation.layout.offset
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
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
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

@Composable
internal fun Dot(color: Color, size: Dp = 5.dp) {
    Box(Modifier.size(size).clip(CircleShape).background(color))
}

@Composable
internal fun Chevron(color: Color, modifier: Modifier = Modifier, down: Boolean = false) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val path = Path().apply {
            if (down) {
                moveTo(w * 0.22f, h * 0.38f)
                lineTo(w * 0.5f, h * 0.66f)
                lineTo(w * 0.78f, h * 0.38f)
            } else {
                moveTo(w * 0.62f, h * 0.2f)
                lineTo(w * 0.36f, h * 0.5f)
                lineTo(w * 0.62f, h * 0.8f)
            }
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
internal fun UpGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val stroke = w * 0.11f
        drawLine(color, Offset(w * 0.5f, h * 0.78f), Offset(w * 0.5f, h * 0.22f), stroke, StrokeCap.Round)
        drawLine(color, Offset(w * 0.24f, h * 0.46f), Offset(w * 0.5f, h * 0.2f), stroke, StrokeCap.Round)
        drawLine(color, Offset(w * 0.76f, h * 0.46f), Offset(w * 0.5f, h * 0.2f), stroke, StrokeCap.Round)
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
            size = Size(w * 0.6f, h * 0.42f),
            cornerRadius = CornerRadius(w * 0.12f),
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
internal fun MonitorGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val stroke = w * 0.08f
        drawRoundRect(color, Offset(w * 0.12f, h * 0.16f), Size(w * 0.76f, h * 0.55f),
            CornerRadius(w * 0.06f), style = Stroke(width = stroke))
        drawLine(color, Offset(w * 0.36f, h * 0.88f), Offset(w * 0.64f, h * 0.88f), stroke, StrokeCap.Round)
        drawLine(color, Offset(w * 0.5f, h * 0.71f), Offset(w * 0.5f, h * 0.88f), stroke, StrokeCap.Round)
    }
}

@Composable
internal fun ShieldGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val stroke = w * 0.08f
        val shield = Path().apply {
            moveTo(w * 0.5f, h * 0.08f)
            lineTo(w * 0.88f, h * 0.24f)
            lineTo(w * 0.88f, h * 0.5f)
            cubicTo(w * 0.88f, h * 0.74f, w * 0.7f, h * 0.88f, w * 0.5f, h * 0.94f)
            cubicTo(w * 0.3f, h * 0.88f, w * 0.12f, h * 0.74f, w * 0.12f, h * 0.5f)
            lineTo(w * 0.12f, h * 0.24f)
            close()
        }
        drawPath(shield, color, style = Stroke(width = stroke))
        drawLine(color, Offset(w * 0.33f, h * 0.5f), Offset(w * 0.45f, h * 0.62f), stroke, StrokeCap.Round)
        drawLine(color, Offset(w * 0.45f, h * 0.62f), Offset(w * 0.68f, h * 0.38f), stroke, StrokeCap.Round)
    }
}

@Composable
internal fun ChatGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val stroke = w * 0.08f
        val bubble = Path().apply {
            moveTo(w * 0.14f, h * 0.2f)
            lineTo(w * 0.86f, h * 0.2f)
            lineTo(w * 0.86f, h * 0.66f)
            lineTo(w * 0.44f, h * 0.66f)
            lineTo(w * 0.26f, h * 0.84f)
            lineTo(w * 0.26f, h * 0.66f)
            lineTo(w * 0.14f, h * 0.66f)
            close()
        }
        drawPath(bubble, color, style = Stroke(width = stroke))
    }
}

/**
 * App bar: one 26px back chevron, a title with an optional second line, and at
 * most one trailing action. The subtitle is where the connection state lives on
 * every screen, so it never has to be repeated inside the content.
 */
@Composable
internal fun NavTop(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: @Composable (() -> Unit)? = null,
    onBack: (() -> Unit)? = null,
    trailing: @Composable RowScope.() -> Unit = {},
) {
    Row(
        modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp).height(48.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (onBack != null) {
            Box(
                Modifier.size(28.dp).clip(RoundedCornerShape(7.dp))
                    .clickable(role = Role.Button, onClick = onBack),
                contentAlignment = Alignment.Center,
            ) { Chevron(Tok.Tx, Modifier.size(17.dp)) }
            Spacer(Modifier.width(6.dp))
        }
        Column(Modifier.weight(1f)) {
            Text(title, color = Tok.Tx, fontSize = 17.sp, fontWeight = FontWeight.SemiBold,
                letterSpacing = 0.1.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            subtitle?.let {
                Spacer(Modifier.height(2.dp))
                it()
            }
        }
        Spacer(Modifier.width(10.dp))
        trailing()
    }
}

/** Page title used by the three tab screens, larger than a detail bar. */
@Composable
internal fun PageHead(title: String, subtitle: String? = null, trailing: @Composable RowScope.() -> Unit = {}) {
    Row(
        Modifier.fillMaxWidth().padding(start = 18.dp, end = 18.dp, top = 6.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, color = Tok.Tx, fontSize = 19.sp, fontWeight = FontWeight.SemiBold, letterSpacing = 0.2.sp)
            subtitle?.let {
                Spacer(Modifier.height(2.dp))
                Text(it, color = Tok.Tx3, fontSize = 12.5.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        trailing()
    }
}

@Composable
internal fun DeskCard(
    modifier: Modifier = Modifier,
    border: Color = Tok.Line,
    background: Color = Tok.Panel,
    padding: Dp = 14.dp,
    content: @Composable ColumnScope.() -> Unit,
) {
    Column(
        modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(background)
            .border(1.dp, border, RoundedCornerShape(16.dp)).padding(padding),
        content = content,
    )
}

/**
 * Risk / state tag. 11px, 20px tall, 6px radius, outline in the same hue: the
 * design uses a filled tint plus a hairline, never a solid block of colour.
 */
@Composable
internal fun Tag(text: String, fg: Color, bg: Color, border: Color? = null) {
    Text(
        text,
        color = fg,
        fontSize = 11.sp,
        fontWeight = FontWeight.Medium,
        letterSpacing = 0.2.sp,
        maxLines = 1,
        modifier = Modifier.clip(RoundedCornerShape(6.dp)).background(bg)
            .border(1.dp, border ?: bg, RoundedCornerShape(6.dp))
            .padding(horizontal = 6.dp, vertical = 3.dp),
    )
}

/** Neutral pill for facts that are neither a warning nor a state. */
@Composable
internal fun Pill(text: String, fg: Color = Tok.Tx2, bg: Color = Tok.Panel, border: Color = Tok.Line) {
    Row(
        Modifier.clip(RoundedCornerShape(7.dp)).background(bg).border(1.dp, border, RoundedCornerShape(7.dp))
            .padding(horizontal = 8.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(text, color = fg, fontSize = 11.5.sp, maxLines = 1)
    }
}

@Composable
internal fun MetaLabel(text: String, modifier: Modifier = Modifier) {
    Text(text, color = Tok.Tx3, fontSize = 11.sp, maxLines = 1, modifier = modifier)
}

internal enum class ButtonKind { Primary, Danger, Ghost }

@Composable
internal fun PButton(
    text: String,
    modifier: Modifier = Modifier,
    kind: ButtonKind = ButtonKind.Ghost,
    enabled: Boolean = true,
    height: Dp = 48.dp,
    leading: (@Composable () -> Unit)? = null,
    onClick: () -> Unit,
) {
    val border = when (kind) {
        ButtonKind.Primary -> Tok.Btn
        ButtonKind.Danger -> Tok.RedLine
        ButtonKind.Ghost -> Tok.Line
    }
    val background = when (kind) {
        ButtonKind.Primary -> Tok.Btn
        else -> Tok.Panel
    }
    val color = when (kind) {
        ButtonKind.Primary -> Tok.OnAct
        ButtonKind.Danger -> Tok.Red
        ButtonKind.Ghost -> Tok.Tx
    }
    val alpha = if (enabled) 1f else 0.38f
    Row(
        modifier.fillMaxWidth().height(height).clip(RoundedCornerShape(13.dp))
            .background(if (enabled) background else background.copy(alpha = 0.5f))
            .border(1.dp, border.copy(alpha = border.alpha * alpha), RoundedCornerShape(13.dp))
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        leading?.let {
            it()
            Spacer(Modifier.width(7.dp))
        }
        Text(text, color = color.copy(alpha = alpha), fontSize = 15.sp,
            fontWeight = if (kind == ButtonKind.Primary) FontWeight.SemiBold else FontWeight.Medium)
    }
}

@Composable
internal fun SegTabs(labels: List<String>, selected: Int, onSelect: (Int) -> Unit) {
    Row(
        Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(Tok.Panel)
            .border(1.dp, Tok.Line, RoundedCornerShape(14.dp)).padding(4.dp),
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        labels.forEachIndexed { index, label ->
            val on = index == selected
            Box(
                Modifier.weight(1f).clip(RoundedCornerShape(11.dp))
                    .background(if (on) Tok.SegOn else Color.Transparent)
                    .clickable(role = Role.Tab) { onSelect(index) }
                    .padding(vertical = 7.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    label,
                    color = if (on) Tok.Tx else Tok.Tx2,
                    fontSize = 13.sp,
                    fontWeight = if (on) FontWeight.Medium else FontWeight.Normal,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
    }
}

/**
 * The persistent composer. It is a button, not a text field: tapping it opens
 * the full submit sheet, where the prompt, project and 4 KB limit can all be
 * shown without squeezing a keyboard into this bar.
 */
@Composable
internal fun Dock(
    placeholder: String,
    hint: String,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
        Row(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(Tok.Panel)
                .border(1.dp, Tok.Line, RoundedCornerShape(16.dp))
                .clickable(enabled = enabled, role = Role.Button, onClick = onClick)
                .padding(start = 14.dp, end = 8.dp, top = 8.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(placeholder, Modifier.weight(1f), color = Tok.Tx3, fontSize = 14.sp,
                maxLines = 1, overflow = TextOverflow.Ellipsis)
            Box(
                Modifier.size(34.dp).clip(CircleShape)
                    .background(if (enabled) Tok.Btn else Tok.DotOff),
                contentAlignment = Alignment.Center,
            ) { UpGlyph(Tok.OnAct, Modifier.size(15.dp)) }
        }
        Spacer(Modifier.height(7.dp))
        Text(hint, Modifier.fillMaxWidth(), color = Tok.Tx3, fontSize = 11.sp,
            textAlign = androidx.compose.ui.text.style.TextAlign.Center)
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

@Composable
internal fun MenuGlyph(color: Color, modifier: Modifier = Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val stroke = w * 0.08f
        drawLine(color, Offset(w * 0.12f, h * 0.3f), Offset(w * 0.88f, h * 0.3f), stroke, StrokeCap.Round)
        drawLine(color, Offset(w * 0.12f, h * 0.52f), Offset(w * 0.88f, h * 0.52f), stroke, StrokeCap.Round)
        drawLine(color, Offset(w * 0.12f, h * 0.74f), Offset(w * 0.62f, h * 0.74f), stroke, StrokeCap.Round)
    }
}
