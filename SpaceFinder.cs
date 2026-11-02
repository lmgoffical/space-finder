// Space Finder - shows which folders and files take up the most disk space.
// Read-only: it never deletes or changes anything.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

class Node
{
    public string Name;
    public string FullPath;
    public bool IsDir;
    public long Size;
    public long Files;
    public Node Parent;
    public List<Node> Children = new List<Node>();
}

class MainForm : Form
{
    const int KeepFilesPerFolder = 40;

    ComboBox drives = new ComboBox();
    Button browse = new Button();
    Button scan = new Button();
    Button up = new Button();
    Label pathLabel = new Label();
    ListView list = new ListView();
    StatusStrip status = new StatusStrip();
    ToolStripStatusLabel statusText = new ToolStripStatusLabel();
    System.Windows.Forms.Timer progressTimer = new System.Windows.Forms.Timer();

    Node root, current;
    Thread worker;
    volatile bool cancel;
    long scannedFiles, scannedBytes;
    string scanningPath = "";

    static readonly Color Accent = Color.FromArgb(15, 107, 99);
    static readonly Color AccentWash = Color.FromArgb(220, 236, 233);
    static readonly Color FileColor = Color.FromArgb(242, 182, 50);

    public MainForm()
    {
        Text = "Space Finder";
        Width = 920; Height = 640;
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(238, 242, 241);

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 46, ColumnCount = 5, Padding = new Padding(8, 8, 8, 4) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        drives.DropDownStyle = ComboBoxStyle.DropDownList;
        drives.Dock = DockStyle.Fill;
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
                drives.Items.Add(d.Name);
        }
        if (drives.Items.Count > 0) drives.SelectedIndex = 0;

        StyleButton(scan, "Scan drive", true);
        scan.Click += delegate { if (worker != null) cancel = true; else if (drives.SelectedItem != null) StartScan((string)drives.SelectedItem); };
        StyleButton(browse, "Pick folder…", false);
        browse.Click += delegate { PickFolder(); };
        StyleButton(up, "▲ Up", false);
        up.Enabled = false;
        up.Click += delegate { if (current != null && current.Parent != null) Show(current.Parent); };

        pathLabel.Dock = DockStyle.Fill;
        pathLabel.TextAlign = ContentAlignment.MiddleLeft;
        pathLabel.AutoEllipsis = true;
        pathLabel.ForeColor = Color.FromArgb(77, 103, 101);
        pathLabel.Text = "Pick a drive and press Scan, or pick a folder.";

        top.Controls.Add(drives, 0, 0);
        top.Controls.Add(scan, 1, 0);
        top.Controls.Add(browse, 2, 0);
        top.Controls.Add(up, 3, 0);
        top.Controls.Add(pathLabel, 4, 0);

        list.Dock = DockStyle.Fill;
        list.View = View.Details;
        list.FullRowSelect = true;
        list.MultiSelect = false;
        list.BorderStyle = BorderStyle.None;
        list.Columns.Add("Name", 330);
        list.Columns.Add("Size", 100, HorizontalAlignment.Right);
        list.Columns.Add("Share of this folder", 220);
        list.Columns.Add("Files", 90, HorizontalAlignment.Right);
        list.OwnerDraw = true;
        list.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e) { e.DrawDefault = true; };
        list.DrawSubItem += DrawSubItem;
        list.DoubleClick += delegate { OpenSelected(); };
        list.KeyDown += delegate(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) OpenSelected();
            if (e.KeyCode == Keys.Back && current != null && current.Parent != null) Show(current.Parent);
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, delegate { OpenSelected(); });
        menu.Items.Add("Show in File Explorer", null, delegate { RevealSelected(); });
        menu.Items.Add("Copy path", null, delegate { var n = Selected(); if (n != null) Clipboard.SetText(n.FullPath); });
        list.ContextMenuStrip = menu;

        var listHolder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 8) };
        listHolder.Controls.Add(list);

        status.Items.Add(statusText);
        statusText.Text = "Ready. Tip: double-click a folder to look inside; Backspace goes up.";

        Controls.Add(listHolder);
        Controls.Add(top);
        Controls.Add(status);

        progressTimer.Interval = 200;
        progressTimer.Tick += delegate
        {
            statusText.Text = string.Format("Scanning… {0:N0} files, {1}   {2}", Interlocked.Read(ref scannedFiles), Human(Interlocked.Read(ref scannedBytes)), scanningPath);
        };
    }

    void StyleButton(Button b, string text, bool primary)
    {
        b.Text = text;
        b.Dock = DockStyle.Fill;
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Accent;
        b.BackColor = primary ? Accent : Color.White;
        b.ForeColor = primary ? Color.White : Accent;
        b.Margin = new Padding(0, 0, 6, 0);
        b.Cursor = Cursors.Hand;
    }

    void PickFolder()
    {
        if (worker != null) return;
        using (var dlg = new FolderBrowserDialog())
        {
            dlg.Description = "Choose a folder to scan";
            if (dlg.ShowDialog(this) == DialogResult.OK) StartScan(dlg.SelectedPath);
        }
    }

    void StartScan(string path)
    {
        cancel = false;
        scannedFiles = 0; scannedBytes = 0;
        list.Items.Clear();
        root = current = null;
        up.Enabled = false;
        browse.Enabled = false; drives.Enabled = false;
        scan.Text = "Stop";
        pathLabel.Text = path;
        progressTimer.Start();
        var started = DateTime.Now;

        worker = new Thread(delegate()
        {
            var r = new Node { Name = path, FullPath = path, IsDir = true };
            Walk(r);
            BeginInvoke((MethodInvoker)delegate
            {
                progressTimer.Stop();
                worker = null;
                scan.Text = "Scan drive";
                browse.Enabled = true; drives.Enabled = true;
                root = r;
                Show(r);
                statusText.Text = string.Format("{0} {1:N0} files, {2} in {3:0.0}s. Double-click a folder to look inside.",
                    cancel ? "Stopped early:" : "Done:", r.Files, Human(r.Size), (DateTime.Now - started).TotalSeconds);
            });
        });
        worker.IsBackground = true;
        worker.Start();
    }

    void Walk(Node dir)
    {
        if (cancel) return;
        scanningPath = dir.FullPath;
        var files = new List<Node>();
        DirectoryInfo di;
        IEnumerable<FileSystemInfo> entries;
        try { di = new DirectoryInfo(dir.FullPath); entries = di.EnumerateFileSystemInfos(); }
        catch { return; }

        try
        {
            foreach (var fsi in entries)
            {
                if (cancel) return;
                try
                {
                    // Skip junctions and symlinks so nothing is counted twice or loops forever.
                    if ((fsi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((fsi.Attributes & FileAttributes.Directory) != 0)
                    {
                        var child = new Node { Name = fsi.Name, FullPath = fsi.FullName, IsDir = true, Parent = dir };
                        Walk(child);
                        dir.Children.Add(child);
                        dir.Size += child.Size;
                        dir.Files += child.Files;
                    }
                    else
                    {
                        long len = ((FileInfo)fsi).Length;
                        files.Add(new Node { Name = fsi.Name, FullPath = fsi.FullName, IsDir = false, Size = len, Files = 1, Parent = dir });
                        dir.Size += len;
                        dir.Files += 1;
                        Interlocked.Increment(ref scannedFiles);
                        Interlocked.Add(ref scannedBytes, len);
                    }
                }
                catch { }
            }
        }
        catch { } // access denied part-way through a folder

        // Keep only the biggest files per folder so memory stays small on huge drives.
        long otherSize = 0, otherCount = 0;
        files.Sort((a, b) => b.Size.CompareTo(a.Size));
        for (int i = 0; i < files.Count; i++)
        {
            if (i < KeepFilesPerFolder) dir.Children.Add(files[i]);
            else { otherSize += files[i].Size; otherCount++; }
        }
        if (otherCount > 0)
            dir.Children.Add(new Node { Name = string.Format("({0:N0} smaller files)", otherCount), FullPath = dir.FullPath, Size = otherSize, Files = otherCount, Parent = dir });
        dir.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    void Show(Node n)
    {
        current = n;
        up.Enabled = n.Parent != null;
        pathLabel.Text = n.FullPath + "   —   " + Human(n.Size);
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var c in n.Children)
        {
            var item = new ListViewItem((c.IsDir ? "📁 " : "    ") + c.Name);
            item.SubItems.Add(Human(c.Size));
            item.SubItems.Add("");
            item.SubItems.Add(c.IsDir ? c.Files.ToString("N0") : "");
            item.Tag = c;
            list.Items.Add(item);
        }
        list.EndUpdate();
        if (list.Items.Count > 0) { list.Items[0].Selected = true; list.Items[0].Focused = true; }
        list.Focus();
    }

    void DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != 2) { e.DrawDefault = true; return; }
        e.DrawBackground();
        if (e.Item.Selected) e.Graphics.FillRectangle(SystemBrushes.Highlight, e.Bounds);
        var n = (Node)e.Item.Tag;
        double share = current != null && current.Size > 0 ? (double)n.Size / current.Size : 0;
        var r = new Rectangle(e.Bounds.X + 4, e.Bounds.Y + 4, e.Bounds.Width - 60, e.Bounds.Height - 8);
        if (r.Width < 10) return;
        using (var wash = new SolidBrush(AccentWash)) e.Graphics.FillRectangle(wash, r);
        using (var fill = new SolidBrush(n.IsDir ? Accent : FileColor))
            e.Graphics.FillRectangle(fill, r.X, r.Y, (int)Math.Max(share > 0 ? 1 : 0, r.Width * share), r.Height);
        var txtRect = new Rectangle(r.Right + 4, e.Bounds.Y, 52, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, (share * 100).ToString("0.0") + "%", list.Font, txtRect,
            e.Item.Selected ? SystemColors.HighlightText : list.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    Node Selected()
    {
        return list.SelectedItems.Count > 0 ? (Node)list.SelectedItems[0].Tag : null;
    }

    void OpenSelected()
    {
        var n = Selected();
        if (n == null) return;
        if (n.IsDir) { if (n.Children.Count > 0) Show(n); else RevealSelected(); }
        else RevealSelected();
    }

    void RevealSelected()
    {
        var n = Selected();
        if (n == null) return;
        try
        {
            if (n.IsDir || !File.Exists(n.FullPath)) System.Diagnostics.Process.Start("explorer.exe", "\"" + n.FullPath + "\"");
            else System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + n.FullPath + "\"");
        }
        catch { }
    }

    static string Human(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString(u == 0 ? "0" : v < 10 ? "0.00" : v < 100 ? "0.0" : "0") + " " + units[u];
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }
}
