package com.agentdeck.lab.ui

import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/**
 * Tokens from design/v3-tokens.md, phone column. The phone forks only the
 * neutral layer from the desktop (warm paper vs cool grey); every status colour
 * is shared, because a red that means "needs a human" must not change shade
 * between the two screens a user looks at in the same minute.
 *
 * `dark` is written once per composition from the system setting. There is no
 * in-app switch: following the system is the whole feature, and two sources of
 * truth for a colour scheme is how half a screen ends up stale.
 */
internal object Tok {
    var dark = false

    private fun pick(light: Long, night: Long) = Color(if (dark) night else light)

    val Bg: Color get() = pick(0xFFF7F4EF, 0xFF161513)
    val Panel: Color get() = pick(0xFFFFFFFF, 0xFF1E1C19)
    val Panel2: Color get() = pick(0xFFF4F2EE, 0xFF23272B)
    val Line: Color get() = pick(0xFFE9E3DA, 0xFF2F2C27)
    val Line2: Color get() = pick(0xFFF1ECE5, 0xFF262420)
    val Tx: Color get() = pick(0xFF1B1F24, 0xFFE8E5DF)
    val Tx2: Color get() = pick(0xFF5F6058, 0xFFA8A49B)
    val Tx3: Color get() = pick(0xFF9C9A92, 0xFF767268)

    // Action colour. In dark it inverts to near-white, so every primary button
    // must pair it with `OnAct` instead of assuming white text.
    val Btn: Color get() = pick(0xFF1B1F24, 0xFFE8E5DF)
    val BtnPressed: Color get() = pick(0xFF2B3138, 0xFFF6F3EC)
    val OnAct: Color get() = pick(0xFFFFFFFF, 0xFF161513)

    val Amber: Color get() = pick(0xFFA16207, 0xFFD9A23C)
    val AmberBg: Color get() = pick(0xFFFDF7E6, 0xFF2A2113)
    val AmberLine: Color get() = pick(0xFFF0E2BD, 0xFF453520)
    val Green: Color get() = pick(0xFF15803D, 0xFF3FB950)
    val GreenBg: Color get() = pick(0xFFECF7EF, 0xFF13261A)
    val GreenLine: Color get() = pick(0xFFCFE8D8, 0xFF1F4A2C)
    val Red: Color get() = pick(0xFFB42318, 0xFFF85149)
    val RedBg: Color get() = pick(0xFFFDECEB, 0xFF2A1614)
    val RedLine: Color get() = pick(0xFFF6D3D0, 0xFF52241F)
    val OnRed: Color get() = pick(0xFFFFFFFF, 0xFF161513)

    val TabBar: Color get() = pick(0xFFFBF9F6, 0xFF131211)
    val BadgeRing: Color get() = pick(0xFFFBF9F6, 0xFF131211)
    val SegOn: Color get() = pick(0xFFF2EFE9, 0xFF2A2724)
    val TagDone: Color get() = pick(0xFFF2F0EB, 0xFF23272B)
    val Soft: Color get() = pick(0xFFF4F2EE, 0xFF23272B)
    val ReadyOff: Color get() = pick(0xFFE6E0D7, 0xFF2A2724)
    val DotOff: Color get() = pick(0xFFC2BEB5, 0xFF555B62)

    val Code: Color get() = pick(0xFFF6F3EE, 0xFF101210)
    val Preview: Color get() = pick(0xFFF8F7F4, 0xFF1F2225)
    val EvDot: Color get() = pick(0xFFC9C4BA, 0xFF4A5158)
    val EvFile: Color get() = pick(0xFFC99A3F, 0xFFD9A23C)
    val EvMsg: Color get() = pick(0xFF9AA0A6, 0xFF7C848C)
    val EvLine: Color get() = pick(0xFFE4DED3, 0xFF443E36)

    val Mono = FontFamily.Monospace
}

internal fun deskColors(dark: Boolean) = if (dark) darkColorScheme(
    primary = Tok.Btn,
    onPrimary = Tok.OnAct,
    background = Tok.Bg,
    onBackground = Tok.Tx,
    surface = Tok.Panel,
    onSurface = Tok.Tx,
    surfaceVariant = Tok.Panel2,
    onSurfaceVariant = Tok.Tx2,
    outline = Tok.Line,
    error = Tok.Red,
) else lightColorScheme(
    primary = Tok.Btn,
    onPrimary = Tok.OnAct,
    background = Tok.Bg,
    onBackground = Tok.Tx,
    surface = Tok.Panel,
    onSurface = Tok.Tx,
    surfaceVariant = Tok.Panel2,
    onSurfaceVariant = Tok.Tx2,
    outline = Tok.Line,
    error = Tok.Red,
)

@Composable
internal fun deskTypography() = Typography(
    headlineMedium = TextStyle(fontSize = 22.sp, fontWeight = FontWeight.SemiBold, color = Tok.Tx),
    titleMedium = TextStyle(fontSize = 14.sp, fontWeight = FontWeight.Medium, color = Tok.Tx),
    bodyMedium = TextStyle(fontSize = 12.sp, color = Tok.Tx2),
    bodySmall = TextStyle(fontSize = 10.5.sp, color = Tok.Tx3),
)
