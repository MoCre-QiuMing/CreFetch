using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CreFetch
{
    public partial class MainForm : Form
    {
        private DownloadEngine _engine;
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _trayMenu;

        private DataGridView _taskGrid;
        private Label _statusLabel;
        private Button _settingsBtn;
        private Button _pauseBtn;
        private Button _resumeBtn;
        private Button _deleteBtn;
        private System.Windows.Forms.Timer _refreshTimer;

        public MainForm()
        {
            InitializeComponent();
            EnsureConfig();
            _engine = new DownloadEngine();
            _engine.OnUrlDetected += OnUrlDetected;
            _engine.OnTaskUpdated += OnTaskUpdated;
            _engine.Start();

            SetupTrayIcon();
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _refreshTimer.Tick += (s, e) => RefreshTasks();
            _refreshTimer.Start();

            this.FormClosing += MainForm_FormClosing;
            this.Load += MainForm_Load;
        }

        private void InitializeComponent()
        {
            this.Text = "CreFetch 高速下载器";
            this.Size = new Size(1050, 600);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.MinimumSize = new Size(850, 450);
            this.Icon = SystemIcons.Application;

            var topPanel = new Panel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(10, 10, 10, 0) };
            _statusLabel = new Label
            {
                Text = "就绪 | 复制链接自动检测",
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("微软雅黑", 9),
                ForeColor = Color.Gray,
                AutoSize = true
            };
            _settingsBtn = new Button
            {
                Text = "设置",
                Dock = DockStyle.Right,
                Width = 80,
                FlatStyle = FlatStyle.Flat,
                Cursor = Cursors.Hand
            };
            _settingsBtn.Click += (s, e) =>
            {
                using var settingsForm = new SettingsForm();
                settingsForm.Owner = this;
                settingsForm.FormClosed += (s2, e2) =>
                {
                    _engine?.ReloadConfig();
                };
                settingsForm.ShowDialog();
            };
            topPanel.Controls.Add(_settingsBtn);
            topPanel.Controls.Add(_statusLabel);

            var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(10) };
            var flowLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            _deleteBtn = new Button { Text = "删除", Width = 80, Enabled = false, Cursor = Cursors.Hand };
            _resumeBtn = new Button { Text = "继续", Width = 80, Enabled = false, Cursor = Cursors.Hand };
            _pauseBtn = new Button { Text = "暂停", Width = 80, Enabled = false, Cursor = Cursors.Hand };

            _deleteBtn.Click += DeleteBtn_Click;
            _resumeBtn.Click += ResumeBtn_Click;
            _pauseBtn.Click += PauseBtn_Click;

            flowLayout.Controls.AddRange(new Control[] { _deleteBtn, _resumeBtn, _pauseBtn });
            bottomPanel.Controls.Add(flowLayout);

            _taskGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowTemplate = { Height = 35 }
            };
            _taskGrid.ColumnHeadersDefaultCellStyle.Font = new Font("微软雅黑", 9, FontStyle.Bold);
            _taskGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 242, 245);
            _taskGrid.DefaultCellStyle.Font = new Font("微软雅黑", 9);
            _taskGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 249, 250);
            _taskGrid.SelectionChanged += (s, e) => UpdateButtonStates();

            _taskGrid.Columns.Add("FileName", "文件名");
            _taskGrid.Columns.Add("Size", "大小");

            var progressCol = new DataGridViewProgressColumn();
            progressCol.Name = "ProgressBar";
            progressCol.HeaderText = "进度";
            progressCol.MinimumWidth = 150;
            _taskGrid.Columns.Add(progressCol);

            _taskGrid.Columns.Add("Status", "状态");
            _taskGrid.Columns.Add("Speed", "速度");
            _taskGrid.Columns.Add("Time", "用时");

            this.Controls.Add(_taskGrid);
            this.Controls.Add(bottomPanel);
            this.Controls.Add(topPanel);
        }

        private class DataGridViewProgressColumn : DataGridViewTextBoxColumn
        {
            public DataGridViewProgressColumn() => this.CellTemplate = new DataGridViewProgressCell();
        }

        private class DataGridViewProgressCell : DataGridViewTextBoxCell
        {
            protected override void Paint(Graphics graphics, Rectangle clipBounds, Rectangle cellBounds, int rowIndex,
                DataGridViewElementStates cellState, object value, object formattedValue, string errorText,
                DataGridViewCellStyle cellStyle, DataGridViewAdvancedBorderStyle advancedBorderStyle,
                DataGridViewPaintParts paintParts)
            {
                base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, value, formattedValue, errorText,
                    cellStyle, advancedBorderStyle, paintParts & ~DataGridViewPaintParts.ContentForeground);

                int progress = 0;
                if (value != null && int.TryParse(value.ToString(), out progress))
                {
                    progress = Math.Clamp(progress, 0, 100);
                    var rect = new Rectangle(cellBounds.X + 4, cellBounds.Y + 4, cellBounds.Width - 8, cellBounds.Height - 8);
                    graphics.FillRectangle(Brushes.LightGray, rect);
                    if (progress > 0)
                    {
                        var fill = new Rectangle(rect.X, rect.Y, (int)(rect.Width * progress / 100.0), rect.Height);
                        using (var brush = new LinearGradientBrush(fill, Color.LimeGreen, Color.ForestGreen, LinearGradientMode.Horizontal))
                            graphics.FillRectangle(brush, fill);
                    }
                    graphics.DrawRectangle(Pens.DimGray, rect);
                    var text = $"{progress}%";
                    using (var font = new Font("微软雅黑", 8, FontStyle.Bold))
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        graphics.DrawString(text, font, Brushes.Black, rect, sf);
                }
            }
        }

        private void EnsureConfig()
        {
            if (!File.Exists("config.ini"))
            {
                var content = @"[Download]
max_concurrent_jobs = 2
max_threads = 12
chunk_count = 96
save_path = C:\Users\Administrator\Downloads
timeout = 30
buffer_size_kb = 1024
retry_times = 3

[Behavior]
prompt_mode = 智能提示
smart_threshold_mb = 100

[App]
auto_start = false
show_notification = true
";
                File.WriteAllText("config.ini", content);
            }
        }

        private void SetupTrayIcon()
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("显示主窗口", null, (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; });
            _trayMenu.Items.Add("设置", null, (s, e) => new SettingsForm().ShowDialog());
            _trayMenu.Items.Add("-");
            _trayMenu.Items.Add("退出", null, (s, e) => { _engine?.Stop(); Application.Exit(); });

            _trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                ContextMenuStrip = _trayMenu,
                Text = "CreFetch 下载器",
                Visible = true
            };
            _trayIcon.DoubleClick += (s, e) => { this.Show(); this.WindowState = FormWindowState.Normal; };
            _trayIcon.ShowBalloonTip(2000, "CreFetch 已启动", "复制下载链接即可开始", ToolTipIcon.Info);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            this.WindowState = FormWindowState.Minimized;
            this.ShowInTaskbar = false;
            this.Hide();
        }

        private void UpdateButtonStates()
        {
            bool hasSelection = _taskGrid.SelectedRows.Count > 0;
            _pauseBtn.Enabled = false;
            _resumeBtn.Enabled = false;
            _deleteBtn.Enabled = hasSelection;

            if (hasSelection)
            {
                var row = _taskGrid.SelectedRows[0];
                string status = row.Cells["Status"].Value?.ToString() ?? "";
                if (status == "DOWNLOADING") _pauseBtn.Enabled = true;
                else if (status == "PAUSED" || status == "PENDING" || status == "FAILED")
                    _resumeBtn.Enabled = true;
            }
        }

        private void PauseBtn_Click(object sender, EventArgs e)
        {
            if (_taskGrid.SelectedRows.Count == 0) return;
            string taskId = _taskGrid.SelectedRows[0].Tag as string;
            if (!string.IsNullOrEmpty(taskId))
                _engine.PauseTask(taskId);
        }

        private void ResumeBtn_Click(object sender, EventArgs e)
        {
            if (_taskGrid.SelectedRows.Count == 0) return;
            string taskId = _taskGrid.SelectedRows[0].Tag as string;
            if (!string.IsNullOrEmpty(taskId))
                _engine.ResumeTask(taskId);
        }

        private void DeleteBtn_Click(object sender, EventArgs e)
        {
            if (_taskGrid.SelectedRows.Count == 0) return;
            string taskId = _taskGrid.SelectedRows[0].Tag as string;
            if (!string.IsNullOrEmpty(taskId) &&
                MessageBox.Show("确定删除此任务及其文件？", "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _engine.RemoveTask(taskId, true);
                RefreshTasks();
            }
        }

        private void OnUrlDetected(string url)
        {
            this.Invoke(() =>
            {
                var mode = ReadConfig("Behavior", "prompt_mode", "智能提示");
                bool shouldAdd = false;
                if (mode == "自动添加")
                    shouldAdd = true;
                else if (mode == "始终询问")
                    shouldAdd = MessageBox.Show($"检测到下载链接：\n\n{url}\n\n是否添加？", "CreFetch",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                else if (mode == "智能提示")
                {
                    shouldAdd = MessageBox.Show($"检测到下载链接：\n\n{url}\n\n是否添加？", "CreFetch",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
                }

                if (shouldAdd)
                {
                    var taskId = _engine.AddTask(url);
                    if (!string.IsNullOrEmpty(taskId))
                    {
                        RefreshTasks();
                    }
                }
            });
        }

        private void OnTaskUpdated(string taskId, int progress, string status, string speedText)
        {
            this.Invoke(() =>
            {
                RefreshTasks();
                if (status == "COMPLETED")
                    _trayIcon.ShowBalloonTip(3000, "下载完成", $"文件已保存", ToolTipIcon.Info);
                else if (status == "FAILED")
                    _trayIcon.ShowBalloonTip(3000, "下载失败", "请检查网络或链接", ToolTipIcon.Error);
            });
        }

        private void RefreshTasks()
        {
            string selectedId = null;
            if (_taskGrid.SelectedRows.Count > 0)
                selectedId = _taskGrid.SelectedRows[0].Tag as string;

            var tasks = _engine.GetAllTasks();
            _taskGrid.Rows.Clear();

            foreach (var task in tasks)
            {
                int rowIndex = _taskGrid.Rows.Add();
                var row = _taskGrid.Rows[rowIndex];

                row.Cells["FileName"].Value = task.Filename;
                row.Cells["Size"].Value = task.TotalSize > 0 ? $"{task.TotalSize / 1024 / 1024:F1} MB" : "--";

                int progress = task.TotalSize > 0 ? (int)((double)task.DownloadedSize / task.TotalSize * 100) : 0;
                row.Cells["ProgressBar"].Value = progress;
                row.Cells["Status"].Value = task.Status.ToString();

                if (task.Status == TaskStatus.DOWNLOADING)
                {
                    double speed = task.SpeedBytesPerSecond;
                    row.Cells["Speed"].Value = FormatSpeed(speed);
                }
                else
                {
                    row.Cells["Speed"].Value = "--";
                }

                row.Cells["Time"].Value = task.TotalTime.TotalSeconds > 0 ? task.TotalTime.ToString(@"hh\:mm\:ss") : "--";
                row.Tag = task.TaskId;

                switch (task.Status)
                {
                    case TaskStatus.COMPLETED:
                        row.DefaultCellStyle.ForeColor = Color.Green;
                        break;
                    case TaskStatus.FAILED:
                        row.DefaultCellStyle.ForeColor = Color.Red;
                        break;
                    case TaskStatus.PAUSED:
                        row.DefaultCellStyle.ForeColor = Color.Orange;
                        break;
                }
            }

            if (!string.IsNullOrEmpty(selectedId))
                foreach (DataGridViewRow row in _taskGrid.Rows)
                    if (row.Tag as string == selectedId) { row.Selected = true; break; }

            UpdateButtonStates();
        }

        private string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 0) return "--";
            if (bytesPerSec < 1024) return $"{bytesPerSec:F0} B/s";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:F1} KB/s";
            return $"{bytesPerSec / 1024 / 1024:F2} MB/s";
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
                _trayIcon.ShowBalloonTip(2000, "CreFetch", "已最小化到托盘", ToolTipIcon.Info);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _engine?.Dispose();
                _trayIcon?.Dispose();
                _refreshTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}