using System.Drawing.Drawing2D;

namespace AgentDeck.Tray;

// The execution trace: one line per journal event, coloured by type. A
// RichTextBox would carry the text but not the colours, and it renders empty in
// off-screen captures, so the rows are painted directly on a scrollable canvas.
internal sealed class TraceView : ScrollableControl
{
    private const int LineHeight = 17;
    private const int TimeWidth = 56;
    private readonly List<Line> lines = [];

    internal TraceView()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = DeckTheme.Well;
        AutoScroll = false;
    }

    internal void Clear()
    {
        lines.Clear();
        AutoScrollMinSize = Size.Empty;
        AutoScrollPosition = Point.Empty;
        Invalidate();
    }

    internal void Append(string time, string label, string text, Color color)
    {
        lines.Add(new Line(time, label, text, color));
        var measured = TextRenderer.MeasureText(time + "  " + label + text, DeckTheme.Monospace,
            Size.Empty, TextFormatFlags.NoPadding).Width;
        AutoScrollMinSize = new Size(Math.Max(AutoScrollMinSize.Width, TimeWidth + measured + 32),
            lines.Count * LineHeight + 8);
        Invalidate();
    }

    /// <summary>Pins the view to the newest event, which is what LIVE means.</summary>
    internal void Follow()
    {
        if (lines.Count == 0) return;
        AutoScrollPosition = new Point(0, AutoScrollMinSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(DeckTheme.Well);
        int offset = AutoScrollPosition.Y;
        for (int index = 0; index < lines.Count; index++)
        {
            int y = 5 + index * LineHeight + offset;
            if (y + LineHeight < 0 || y > Height) continue;
            var line = lines[index];
            int x = 12;
            if (line.Time.Length > 0)
            {
                TextRenderer.DrawText(e.Graphics, line.Time, DeckTheme.Monospace,
                    new Point(x, y), DeckTheme.Dim, TextFormatFlags.NoPadding);
                x += TimeWidth;
            }
            var bounds = new Rectangle(x, y, Math.Max(20, Width - x - 8), LineHeight);
            TextRenderer.DrawText(e.Graphics, line.Label + line.Text, DeckTheme.Monospace,
                bounds, line.Color, TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    private sealed record Line(string Time, string Label, string Text, Color Color);
}
