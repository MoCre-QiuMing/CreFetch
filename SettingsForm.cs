using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CreFetch
{
    public partial class SettingsForm : Form
    {
        private NumericUpDown _threadSpin;
        private NumericUpDown _chunkSpin;
        private NumericUpDown _concurrentSpin;
        private NumericUpDown _bufferSpin;
        private NumericUpDown _retrySpin;
        private TextBox _pathBox;
        private Button _browseBtn;
        private NumericUpDown _timeoutSpin;
        private CheckBox _autoStartCheck;
        private CheckBox _notifyCheck;
        private Button _saveBtn;
        private Button _cancelBtn;

        public SettingsForm()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void InitializeComponent()
        {
            this.Text = "CreFetch 设置";
            this.Size = new Size(500, 480);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 10,
                Padding = new Padding(15),
                AutoSize = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int row = 0;

            layout.Controls.Add(new Label { Text = "并发任务数:", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            _concurrentSpin = new NumericUpDown { Minimum = 1, Maximum = 10, Value = 2, Width = 80 };
            layout.Controls.Add(_concurrentSpin, 1, row++);

            layout.Controls.Add(new Label { Text = "下载线程数:", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            _threadSpin = new NumericUpDown { Minimum = 1, Maximum = 128, Value = 12, Width = 80 };
            layout.Controls.Add(_threadSpin, 1, row++);

            layout.Controls.Add(new Label { Text = "分块数:", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            _chunkSpin = new NumericUpDown { Minimum = 4, Maximum = 512, Value = 96, Width = 80 };
            layout.Controls.Add(_chunkSpin, 1, row++);

            layout.Controls.Add(new Label { Text = "缓冲区大小(KB):", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            _bufferSpin = new NumericUpDown { Minimum = 64, Maximum = 16384, Value = 1024, Width = 80 };
            layout.Controls.Add(_bufferSpin, 1, row++);

            layout.Controls.Add(new Label { Text = "重试次数:", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            _retrySpin = new NumericUpDown { Minimum = 0, Maximum = 10, Value = 3, Width = 80 };
            layout.Controls.Add(_retrySpin, 1, row++);

            layout.Controls.Add(new Label { Text = "保存路径:", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            var pathPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            _pathBox = new TextBox { Width = 230 };
            _browseBtn = new Button { Text = "浏览...", Width = 70 };
            _browseBtn.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog() == DialogResult.OK)
                    _pathBox.Text = dialog.SelectedPath;
            };
            pathPanel.Controls.Add(_pathBox);
            pathPanel.Controls.Add(_browseBtn);
            layout.Controls.Add(pathPanel, 1, row++);

            layout.Controls.Add(new Label { Text = "连接超时(秒):", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            _timeoutSpin = new NumericUpDown { Minimum = 5, Maximum = 300, Value = 30, Width = 80 };
            layout.Controls.Add(_timeoutSpin, 1, row++);

            layout.Controls.Add(new Label { Text = "开机自启动:", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            _autoStartCheck = new CheckBox { AutoSize = true };
            layout.Controls.Add(_autoStartCheck, 1, row++);

            layout.Controls.Add(new Label { Text = "显示通知:", TextAlign = ContentAlignment.MiddleRight }, 0, row);
            _notifyCheck = new CheckBox { AutoSize = true };
            layout.Controls.Add(_notifyCheck, 1, row++);

            var btnPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 15, 0, 0) };
            _saveBtn = new Button { Text = "保存", Width = 80 };
            _saveBtn.Click += SaveSettings;
            _cancelBtn = new Button { Text = "取消", Width = 80 };
            _cancelBtn.Click += (s, e) => this.Close();
            btnPanel.Controls.Add(_saveBtn);
            btnPanel.Controls.Add(_cancelBtn);
            layout.Controls.Add(btnPanel, 1, row++);

            this.Controls.Add(layout);
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists("config.ini")) return;
                var lines = File.ReadAllLines("config.ini");
                string section = "";
                foreach (var line in lines)
                {
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        section = line.Trim('[', ']');
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(line) || !line.Contains("=")) continue;
                    var parts = line.Split('=');
                    if (parts.Length != 2) continue;
                    var key = parts[0].Trim();
                    var val = parts[1].Trim();

                    if (section == "Download")
                    {
                        switch (key)
                        {
                            case "max_concurrent_jobs": _concurrentSpin.Value = int.TryParse(val, out int c) ? Math.Clamp(c, 1, 10) : 2; break;
                            case "max_threads": _threadSpin.Value = int.TryParse(val, out int t) ? Math.Clamp(t, 1, 128) : 12; break;
                            case "chunk_count": _chunkSpin.Value = int.TryParse(val, out int ch) ? Math.Clamp(ch, 4, 512) : 96; break;
                            case "buffer_size_kb": _bufferSpin.Value = int.TryParse(val, out int b) ? Math.Clamp(b, 64, 16384) : 1024; break;
                            case "retry_times": _retrySpin.Value = int.TryParse(val, out int r) ? Math.Clamp(r, 0, 10) : 3; break;
                            case "save_path": _pathBox.Text = val; break;
                            case "timeout": _timeoutSpin.Value = int.TryParse(val, out int to) ? Math.Clamp(to, 5, 300) : 30; break;
                        }
                    }
                    else if (section == "App")
                    {
                        if (key == "auto_start") _autoStartCheck.Checked = val == "true";
                        else if (key == "show_notification") _notifyCheck.Checked = val == "true";
                    }
                }
            }
            catch { }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;
                if (enable)
                    key.SetValue("CreFetch", $"\"{Application.ExecutablePath}\"");
                else
                    key.DeleteValue("CreFetch", false);
            }
            catch { }
        }

        private void SaveSettings(object sender, EventArgs e)
        {
            try
            {
                SetAutoStart(_autoStartCheck.Checked);

                var content = $@"[Download]
max_concurrent_jobs = {_concurrentSpin.Value}
max_threads = {_threadSpin.Value}
chunk_count = {_chunkSpin.Value}
save_path = {_pathBox.Text}
timeout = {_timeoutSpin.Value}
buffer_size_kb = {_bufferSpin.Value}
retry_times = {_retrySpin.Value}

[App]
auto_start = {_autoStartCheck.Checked.ToString().ToLower()}
show_notification = {_notifyCheck.Checked.ToString().ToLower()}
";
                File.WriteAllText("config.ini", content);
                MessageBox.Show("配置已保存！", "成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}