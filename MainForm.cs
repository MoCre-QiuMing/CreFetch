using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace CreFetch
{
    public partial class MainForm : Form
    {
        private DownloadEngine engine;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private DataGridView taskGrid;
        private Label statusLabel;
        private Button settingsBtn;
        private System.Windows.Forms.Timer refreshTimer;

        public MainForm()
        {
            InitializeComponent();
            EnsureConfig();
            engine = new DownloadEngine();
            engine.OnUrlDetected += OnUrlDetected;
            engine.OnTaskUpdated += OnTaskUpdated;
            engine.Start();

            taskGrid.CellContentClick += TaskGrid_CellContentClick;

            SetupTrayIcon();
            refreshTimer = new System.Windows.Forms.Timer();
            refreshTimer.Interval = 500;
            refreshTimer.Tick += (s, e) => RefreshTasks();
            refreshTimer.Start();
            this.FormClosing += MainForm_FormClosing;
            this.Load += MainForm_Load;
        }

        private void InitializeComponent()
        {
            this.Text = "CreFetch 高速下载器";
            this.Size = new Size(950, 500);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(850, 400);

            var mainPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 2,
                ColumnCount = 1,
                Padding = new Padding(10)
            };
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            mainPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var topPanel = new Panel { Dock = DockStyle.Fill, Height = 40 };
            statusLabel = new Label
            {
                Text = "等待复制链接...",
                AutoSize = false,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Width = 500
            };
            settingsBtn = new Button
            {
                Text = "设置",
                Dock = DockStyle.Right,
                Width = 80
            };
            settingsBtn.Click += (s, e) => new SettingsForm().ShowDialog();
            topPanel.Controls.Add(settingsBtn);
            topPanel.Controls.Add(statusLabel);

            taskGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };

            taskGrid.Columns.Add("FileName", "文件名");
            taskGrid.Columns.Add("Size", "大小");
            taskGrid.Columns.Add("Progress", "进度");
            taskGrid.Columns.Add("Status", "状态");
            taskGrid.Columns.Add("Speed", "速度");
            taskGrid.Columns.Add("Time", "用时");

            var actionColumn = new DataGridViewButtonColumn();
            actionColumn.HeaderText = "操作";
            actionColumn.Name = "ActionColumn";
            actionColumn.Width = 80;
            taskGrid.Columns.Add(actionColumn);

            mainPanel.Controls.Add(topPanel, 0, 0);
            mainPanel.Controls.Add(taskGrid, 0, 1);
            this.Controls.Add(mainPanel);
        }

        private void EnsureConfig()
        {
            if (!File.Exists("config.ini"))
            {
                var content = @"[Download]
max_threads = 50
save_path = C:\Users\Administrator\Downloads
timeout = 30

[Behavior]
prompt_mode = 始终询问
smart_threshold_mb = 50

[App]
auto_start = false
show_notification = true
";
                File.WriteAllText("config.ini", content);
            }
        }

        private void SetupTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("显示主窗口", null, (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; });
            trayMenu.Items.Add("设置", null, (s, e) => new SettingsForm().ShowDialog());
            trayMenu.Items.Add("-");
            trayMenu.Items.Add("退出", null, (s, e) => { engine.Stop(); Application.Exit(); });

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Text = "CreFetch 高速下载器",
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; };
            trayIcon.ShowBalloonTip(2000, "CreFetch 已启动", "复制链接即可快速下载", ToolTipIcon.Info);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Hide();
        }

        private void OnUrlDetected(string url)
        {
            this.Invoke(() =>
            {
                var mode = ReadConfig("Behavior", "prompt_mode", "始终询问");
                if (mode == "自动添加")
                {
                    AddAndStartTask(url);
                }
                else if (mode == "始终询问")
                {
                    var result = MessageBox.Show($"检测到下载链接：\n\n{url}\n\n是否添加到下载列表？",
                        "CreFetch", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (result == DialogResult.Yes)
                        AddAndStartTask(url);
                }
                else
                {
                    AddAndStartTask(url);
                }
            });
        }

        private void AddAndStartTask(string url)
        {
            var taskId = engine.AddTask(url);
            if (!string.IsNullOrEmpty(taskId))
            {
                statusLabel.Text = $"已添加任务: {url}";
                RefreshTasks();
            }
        }

        private void OnTaskUpdated(string taskId, int progress, string status)
        {
            this.Invoke(() =>
            {
                RefreshTasks();
                if (status == "COMPLETED")
                {
                    trayIcon.ShowBalloonTip(3000, "下载完成", $"文件已保存到下载目录", ToolTipIcon.Info);
                }
                else if (status == "FAILED")
                {
                    trayIcon.ShowBalloonTip(3000, "下载失败", "请检查网络或链接是否有效", ToolTipIcon.Error);
                }
            });
        }

        private void RefreshTasks()
        {
            var tasks = engine.GetAllTasks();

            taskGrid.SuspendLayout();
            taskGrid.Rows.Clear();

            foreach (var task in tasks)
            {
                int rowIndex = taskGrid.Rows.Add();
                var row = taskGrid.Rows[rowIndex];
                row.Cells[0].Value = task.Filename;
                row.Cells[1].Value = task.TotalSize > 0 ? $"{task.TotalSize / 1024 / 1024:F1} MB" : "--";

                long currentDownloaded = task.DownloadedSize;
                if (task.Status == TaskStatus.DOWNLOADING || task.Status == TaskStatus.PAUSED)
                {
                    if (task.ChunkProgress != null && task.ChunkProgress.Count > 0)
                    {
                        long sum = 0;
                        lock (task.ChunkProgress)
                        {
                            foreach (var v in task.ChunkProgress) sum += v;
                        }
                        currentDownloaded = sum;
                    }
                }

                int progress = task.TotalSize > 0 ? (int)((currentDownloaded * 100) / task.TotalSize) : 0;
                row.Cells[2].Value = $"{progress}%";
                row.Cells[3].Value = GetStatusText(task.Status);

                if (task.SpeedBytesPerSecond > 0)
                    row.Cells[4].Value = $"{task.SpeedBytesPerSecond / 1024 / 1024:F2} MB/s";
                else
                    row.Cells[4].Value = "--";

                if (task.TotalTime.TotalSeconds > 0)
                    row.Cells[5].Value = task.TotalTime.ToString(@"hh\:mm\:ss");
                else
                    row.Cells[5].Value = "--";

                if (task.Status == TaskStatus.DOWNLOADING)
                    row.Cells[6].Value = "暂停";
                else if (task.Status == TaskStatus.PAUSED || task.Status == TaskStatus.PENDING)
                    row.Cells[6].Value = "继续";
                else
                    row.Cells[6].Value = "删除";

                row.Tag = task.TaskId;
            }

            taskGrid.ResumeLayout();
        }

        private void TaskGrid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.ColumnIndex == 6 && e.RowIndex >= 0)
            {
                var row = taskGrid.Rows[e.RowIndex];
                string taskId = row.Tag as string;
                if (string.IsNullOrEmpty(taskId)) return;

                string btnText = row.Cells[6].Value.ToString();

                if (btnText == "暂停")
                {
                    engine.PauseTask(taskId);
                    RefreshTasks();
                }
                else if (btnText == "继续")
                {
                    engine.ResumeTask(taskId);
                    RefreshTasks();
                }
                else if (btnText == "删除")
                {
                    if (MessageBox.Show("确定删除此任务？", "确认", MessageBoxButtons.YesNo) == DialogResult.Yes)
                    {
                        engine.RemoveTask(taskId);
                        RefreshTasks();
                    }
                }
            }
        }

        private string GetStatusText(TaskStatus status)
        {
            return status switch
            {
                TaskStatus.PENDING => "等待",
                TaskStatus.DOWNLOADING => "下载中",
                TaskStatus.PAUSED => "已暂停",
                TaskStatus.COMPLETED => "已完成",
                TaskStatus.FAILED => "失败",
                _ => "未知"
            };
        }

        private string ReadConfig(string section, string key, string defaultValue)
        {
            try
            {
                var lines = File.ReadAllLines("config.ini");
                bool inSection = false;
                foreach (var line in lines)
                {
                    if (line.StartsWith($"[{section}]")) { inSection = true; continue; }
                    if (line.StartsWith("[") && line.EndsWith("]")) { inSection = false; continue; }
                    if (inSection && line.StartsWith(key + "="))
                        return line.Substring(key.Length + 1).Trim();
                }
            }
            catch { }
            return defaultValue;
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                this.Hide();
                trayIcon.ShowBalloonTip(2000, "CreFetch", "已最小化到系统托盘", ToolTipIcon.Info);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                engine?.Stop();
                trayIcon?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}