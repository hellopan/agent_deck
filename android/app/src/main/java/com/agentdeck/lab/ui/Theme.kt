package com.agentdeck.lab.ui

import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

/**
 * Tokens copied from the approved design (design/ui-v2.html). The phone stays
 * dark on purpose: the palette was signed off as one piece, and a second light
 * theme is explicitly out of scope for the first release.
 */
internal object Tok {
    val Bg = Color(0xFF0A0D11)
    val Bar = Color(0xFF0C1015)
    val Panel = Color(0xFF0E1319)
    val Panel2 = Color(0xFF141B23)
    val Line = Color(0xFF1D262F)
    val Line2 = Color(0xFF151D25)
    val Tx = Color(0xFFDEE6F0)
    val Tx2 = Color(0xFFA2B0BF)
    val Tx3 = Color(0xFF8494A4)
    val Amber = Color(0xFFD9A441)
    val AmberBg = Color(0xFF251D0E)
    val Green = Color(0xFF46B85C)
    val GreenBg = Color(0xFF12200F)
    val Btn = Color(0xFF238636)
    val Red = Color(0xFFE5534B)
    val RedBg = Color(0xFF2A1512)
    val Code = Color(0xFF0B1015)
    val Mono = FontFamily.Monospace
}

internal val DeskColors = darkColorScheme(
    primary = Tok.Btn,
    onPrimary = Color.White,
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
