using System.Globalization;
using System.Text.Json;

namespace AgentDeck.Tray;

// 项目页与日志页。两页都直接读本地磁盘：授权清单就是 agent 启动时读的那个
// 文件，日志目录就是传给 -task-logs 的那个目录。中间没有缓存层，所以不存在
// 「界面显示已授权、实际没生效」这种偏差——这两页显示的就是生效中的配置。
internal sealed partial class ReviewWindow
{
    private readonly Label projectCount = CountLabel();
    private readonly DataGridView projectGrid =
        Sheet("还没有授权项目。用「打开授权清单」添加一个目录，手机才能在那里跑任务。");
    private readonly Label logCount = CountLabel();
    private readonly DataGridView logGrid =
        Sheet("日志目录还是空的。任何任务跑过一次之后，这里才会有文件。");

    private Control ProjectsPage()
    {
        if (projectGrid.Columns.Count == 0)
        {
            projectGrid.Columns.Add(Column("项目", 116, mono: true));
            projectGrid.Columns.Add(Column("目录", 0, fill: true, mono: true));
            projectGrid.Columns.Add(Column("写入范围", 104));
            projectGrid.Columns.Add(Column("状态", 76));
        }

        var edit = DeckTheme.Button("打开授权清单", primary: true);
        edit.Click += (_, _) => EditProjects();
        var actions = DeckTheme.Actions(DeckTheme.Canvas);
        actions.Controls.Add(edit);

        var card = CardShell(
            DeckTheme.CardHeader("授权项目", null, "越界写入一律拒绝", projectCount), projectGrid);
        var layout = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(layout, card, 100, fill: true);
        return Page("项目", "哪些目录允许跑任务，以及每个目录的写入范围。", layout, actions);
    }

    private Control LogsPage()
    {
        if (logGrid.Columns.Count == 0)
        {
            logGrid.Columns.Add(Column("日志", 0, fill: true, mono: true));
            logGrid.Columns.Add(Column("大小", 84, mono: true));
            logGrid.Columns.Add(Column("最后写入", 108, mono: true));
        }

        var open = DeckTheme.Button("打开日志目录");
        open.Click += (_, _) => RevealDirectory(LogsPath);
        var actions = DeckTheme.Actions(DeckTheme.Canvas);
        actions.Controls.Add(open);

        var card = CardShell(
            DeckTheme.CardHeader("任务日志", null, "按任务存放，事件流在任务页看", logCount), logGrid);
        var layout = DeckTheme.Table(background: DeckTheme.Canvas);
        DeckTheme.Row(layout, card, 100, fill: true);
        return Page("日志", "任务、连接与审批的原始记录，按项目与任务分目录。", layout, actions);
    }

    private void RefreshProjectsPage()
    {
        if (projectGrid.IsDisposed) return;
        projectGrid.Rows.Clear();
        int count = 0;
        try
        {
            if (File.Exists(ProjectsPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(ProjectsPath));
                if (document.RootElement.TryGetProperty("projects", out var list) &&
                    list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in list.EnumerateArray())
                    {
                        string id = Field(entry, "id");
                        string directory = Field(entry, "directory");
                        string mode = Field(entry, "mode");
                        bool enabled = entry.TryGetProperty("enabled", out var flag) &&
                                       flag.ValueKind == JsonValueKind.True;
                        projectGrid.Rows.Add(id, directory,
                            mode == "workspace-write" ? "项目目录可写" : "只读",
                            enabled ? "已启用" : "已停用");
                        count++;
                    }
                }
            }
        }
        catch (Exception error)
        {
            SetStatus($"授权清单读不出来：{error.Message}");
        }

        projectCount.Text = count.ToString(CultureInfo.InvariantCulture);
    }

    private void RefreshLogsPage()
    {
        if (logGrid.IsDisposed) return;
        logGrid.Rows.Clear();
        int count = 0;
        try
        {
            var root = LogsPath;
            if (Directory.Exists(root))
            {
                var files = new DirectoryInfo(root)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Take(200);
                foreach (var file in files)
                {
                    string relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                    logGrid.Rows.Add(relative, FileSize(file.Length),
                        file.LastWriteTime.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
                    count++;
                }
            }
        }
        catch (Exception error)
        {
            SetStatus($"日志目录读不出来：{error.Message}");
        }

        logCount.Text = count.ToString(CultureInfo.InvariantCulture);
    }

    private void EditProjects()
    {
        using var editor = new ProjectEditor(ProjectsPath);
        editor.ShowDialog(this);
        RefreshProjectsPage();
    }

    private void RevealDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            SetStatus("日志目录还不存在——还没有任务写过日志。");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true
        });
    }

    private static string Field(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string FileSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B"
    };

    private static Control CardShell(Control header, Control body)
    {
        var table = DeckTheme.Table(new Padding(0), DeckTheme.Surface);
        DeckTheme.Row(table, header, 42);
        DeckTheme.Row(table, body, 100, fill: true);
        var card = new DeckTheme.CardPanel();
        card.Controls.Add(table);
        return card;
    }

    private static Label CountLabel()
    {
        var label = DeckTheme.Label("0", DeckTheme.MonoSmall, DeckTheme.Dim);
        label.AutoSize = true;
        label.Dock = DockStyle.None;
        label.Margin = new Padding(9, 0, 0, 0);
        return label;
    }

    private static DataGridViewTextBoxColumn Column(string name, int width, bool fill = false, bool mono = false) =>
        new()
        {
            HeaderText = name,
            Width = width,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = new DataGridViewCellStyle
            {
                Font = mono ? DeckTheme.MonoSmall : DeckTheme.Small,
                Padding = new Padding(10, 0, 0, 0)
            }
        };

    // The design's table: bg-alt header in 11px grey, no vertical rules, no zebra.
    // WinForms draws both by default, so every one of them is switched off here.
    private static DataGridView Sheet(string emptyText)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AllowUserToOrderColumns = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            RowHeadersVisible = false,
            BorderStyle = BorderStyle.None,
            BackgroundColor = DeckTheme.Surface,
            GridColor = DeckTheme.Line2,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            ColumnHeadersHeight = 30,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            EnableHeadersVisualStyles = false,
            Font = DeckTheme.Small,
            Margin = Padding.Empty
        };
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = DeckTheme.Surface2,
            ForeColor = DeckTheme.Dim,
            SelectionBackColor = DeckTheme.Surface2,
            SelectionForeColor = DeckTheme.Dim,
            Font = DeckTheme.Tiny,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 0, 0)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = DeckTheme.Surface,
            ForeColor = DeckTheme.Muted,
            SelectionBackColor = DeckTheme.Selected,
            SelectionForeColor = DeckTheme.Ink,
            Font = DeckTheme.Small,
            Padding = new Padding(10, 0, 0, 0)
        };
        // Drawn by the grid itself rather than by a transparent label on top: the
        // overlay wipes the header row, because the grid below it never repaints.
        grid.Paint += (_, e) =>
        {
            if (grid.Rows.Count > 0) return;
            TextRenderer.DrawText(e.Graphics, emptyText, DeckTheme.Small,
                new Rectangle(0, grid.ColumnHeadersHeight, grid.Width, grid.Height - grid.ColumnHeadersHeight),
                DeckTheme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        };
        grid.RowTemplate.Height = 33;
        return grid;
    }
}
