using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentDeck.Tray;

internal sealed class ProjectEditor : Form
{
    private const int MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };
    private readonly string path;
    private readonly byte[]? original;
    private readonly BindingList<Project> rows;
    private readonly DataGridView grid;

    internal ProjectEditor(string path)
    {
        this.path = path;
        original = ReadPolicy(path);
        var entries = original is null ? [] :
            (JsonSerializer.Deserialize<Policy>(original, Json)
                ?? throw new InvalidDataException("授权清单格式错误。")).Projects ?? [];
        rows = new BindingList<Project>(entries);
        DeckTheme.Form(this, "项目授权");
        ClientSize = new Size(810, 480);
        MinimumSize = new Size(630, 365);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        grid = new DataGridView
        {
            Dock = DockStyle.Fill, AutoGenerateColumns = false, DataSource = rows,
            AllowUserToAddRows = false, AllowUserToDeleteRows = false, MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
            AccessibleName = "本机授权项目",
            BackgroundColor = DeckTheme.Well, GridColor = DeckTheme.Line,
            BorderStyle = BorderStyle.None, EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 36, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        };
        grid.RowTemplate.Height = 34;
        grid.ColumnHeadersDefaultCellStyle.BackColor = DeckTheme.Surface;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = DeckTheme.Muted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = DeckTheme.Surface;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = DeckTheme.Muted;
        grid.DefaultCellStyle.BackColor = DeckTheme.Well;
        grid.DefaultCellStyle.ForeColor = DeckTheme.Ink;
        grid.DefaultCellStyle.SelectionBackColor = DeckTheme.Active;
        grid.DefaultCellStyle.SelectionForeColor = DeckTheme.Ink;
        grid.Columns.Add(new DataGridViewTextBoxColumn
            { HeaderText = "项目 ID", DataPropertyName = nameof(Project.Id), Width = 140 });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "本机目录", DataPropertyName = nameof(Project.Directory),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true
        });
        grid.Columns.Add(new DataGridViewCheckBoxColumn
            { HeaderText = "启用", DataPropertyName = nameof(Project.Enabled), Width = 65 });

        var actions = DeckTheme.Actions();
        AddButton(actions, "添加目录", () =>
        {
            using var picker = new FolderBrowserDialog { Description = "选择允许远程控制的项目目录" };
            if (picker.ShowDialog(this) != DialogResult.OK)
                return;
            rows.Add(new Project { Directory = picker.SelectedPath });
            grid.CurrentCell = grid.Rows[^1].Cells[0];
            grid.BeginEdit(true);
        });
        AddButton(actions, "移除所选项目", () =>
        {
            if (grid.CurrentRow?.DataBoundItem is Project selected)
                rows.Remove(selected);
        });
        AddButton(actions, "保存授权", Save, primary: true);
        var cancel = AddButton(actions, "取消", Close);
        CancelButton = cancel;

        var layout = DeckTheme.Table(new Padding(26, 18, 26, 20));
        DeckTheme.Row(layout, DeckTheme.Label("项目授权", DeckTheme.Heading), 52);
        DeckTheme.Row(layout, DeckTheme.Label("新增项目默认停用。项目 ID 可用小写字母、数字、连字符和下划线。",
            DeckTheme.Body, DeckTheme.Muted), 45);
        DeckTheme.Row(layout, grid, 100, fill: true);
        DeckTheme.Row(layout, new Panel(), 14);
        DeckTheme.Row(layout, actions, 40);
        Controls.Add(layout);
    }

    private Button AddButton(FlowLayoutPanel panel, string title, Action action, bool primary = false)
    {
        var button = DeckTheme.Button(title, primary);
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        panel.Controls.Add(button);
        return button;
    }

    private void Save()
    {
        if (!grid.EndEdit())
            throw new InvalidDataException("请先完成当前编辑。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in rows)
        {
            if (entry.Id is null || !Regex.IsMatch(entry.Id, @"\A[a-z0-9][a-z0-9_-]{0,63}\z") || !ids.Add(entry.Id))
                throw new InvalidDataException("项目 ID 必须有效且唯一。");
            if (!Path.IsPathFullyQualified(entry.Directory))
                throw new InvalidDataException($"项目 {entry.Id} 需要本机绝对路径。");
            if (entry.Enabled)
                ValidateDirectory(entry.Directory);
        }
        var data = JsonSerializer.SerializeToUtf8Bytes(new Policy(rows.ToList()), Json);
        if (data.Length > MaxBytes)
            throw new InvalidDataException("项目授权清单超过 64 KiB。");
        if (MessageBox.Show(this, "保存后，Agent 会按新清单检查后续请求。确认保存？", "更新项目授权",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        using var saveLock = new FileStream(path + ".agentdeck.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var current = ReadPolicy(path);
        if ((current is null) != (original is null) ||
            (current is not null && !current.AsSpan().SequenceEqual(original)))
            throw new IOException("授权清单已在其他窗口修改。请关闭后重新打开。");
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $"projects-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(data);
                file.Flush(flushToDisk: true);
            }
            if (original is null)
                File.Move(temporary, path);
            else
                File.Replace(temporary, path, null);
        }
        finally
        {
            File.Delete(temporary);
        }
        DialogResult = DialogResult.OK;
    }

    private static byte[]? ReadPolicy(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("授权清单必须是本机普通文件。");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var bytes = new byte[MaxBytes + 1];
            var length = file.ReadAtLeast(bytes, bytes.Length, false);
            if (length > MaxBytes)
                throw new InvalidDataException("项目授权清单超过 64 KiB。");
            return bytes.AsSpan(0, length).ToArray();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private static void ValidateDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            directory.Refresh();
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"目录不存在或经过目录联接：{path}");
        }
    }

    private sealed record Policy(List<Project> Projects);

    private sealed class Project
    {
        public string Id { get; set; } = "";
        public string Directory { get; init; } = "";
        public bool Enabled { get; set; }
    }
}
