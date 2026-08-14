using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CreFetch
{
    public partial class SettingsForm : Form
    {
        private NumericUpDown threadSpin;
        private TextBox pathBox;
        private Button browseBtn;
        private NumericUpDown timeoutSpin;
        private ComboBox modeCombo;
        private NumericUpDown thresholdSpin;
        private CheckBox autoStartCheck;
        private CheckBox notifyCheck;
        private Button saveBtn;
        private Button cancelBtn;

        public SettingsForm()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void InitializeComponent()
        {
            this.Text = "CreFetch 设置";
            this.Size = new Size(480, 460);
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 9,
                Padding = new Padding(15),
                AutoSize = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            int row = 0;
            layout.Controls.Add(new Label { Text = "下载线程数:", TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, row);
            threadSpin = new NumericUpDown { Minimum = 1, Maximum = 128, Value = 50, Width = 100 };
            layout.Controls.Add(threadSpin, 1, row++);

            layout.Controls.Add(new Label { Text = "保存路径:", TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, row);
            var pathPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true };
            pathBox = new TextBox { Width = 220 };
            browseBtn = new Button { Text = "浏览...", Width = 70 };
            browseBtn.Click += (s, e) =>
            {
                var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog() == DialogResult.OK)
                    pathBox.Text = dialog.SelectedPath;
            };
            pathPanel.Controls.Add(pathBox);
            pathPanel.Controls.Add(browseBtn);
            layout.Controls.Add(pathPanel, 1, row++);

            layout.Controls.Add(new Label { Text = "连接超时(秒):", TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, row);
            timeoutSpin = new NumericUpDown { Minimum = 5, Maximum = 300, Value = 30, Width = 100 };
            layout.Controls.Add(timeoutSpin, 1, row++);

            layout.Controls.Add(new Label { Text = "提示模式:", TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, row);
            modeCombo = new ComboBox { Items = { "始终询问", "自动添加", "智能提示" }, DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
            layout.Controls.Add(modeCombo, 1, row++);

            var explainLabel = new Label
            {
                Text = "控制剪贴板监控到下载链接时的默认操作：\n· 始终询问：弹出确认框，由您决定是否下载。\n· 自动添加：不弹窗，直接加入下载队列并开始。\n· 智能提示：根据下方阈值判断，大文件询问，小文件直下。",
                AutoSize = true,
                ForeColor = Color.DimGray
            };
            layout.Controls.Add(explainLabel, 1, row);
            layout.SetColumnSpan(explainLabel, 1);
            row++;

            layout.Controls.Add(new Label { Text = "智能阈值(MB):", TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, row);
            thresholdSpin = new NumericUpDown { Minimum = 0, Maximum = 99999, Value = 50, Width = 100 };
            layout.Controls.Add(thresholdSpin, 1, row++);

            layout.Controls.Add(new Label { Text = "开机自启动:", TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, row);
            autoStartCheck = new CheckBox { AutoSize = true };
            layout.Controls.Add(autoStartCheck, 1, row++);

            layout.Controls.Add(new Label { Text = "显示通知:", TextAlign = ContentAlignment.MiddleRight, AutoSize = true }, 0, row);
            notifyCheck = new CheckBox { AutoSize = true };
            layout.Controls.Add(notifyCheck, 1, row++);

            var btnPanel = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 15, 0, 0) };
            saveBtn = new Button { Text = "保存", Width = 80 };
            saveBtn.Click += SaveSettings;
            cancelBtn = new Button { Text = "取消", Width = 80 };
            cancelBtn.Click += (s, e) => this.Close();
            btnPanel.Controls.Add(saveBtn);
            btnPanel.Controls.Add(cancelBtn);
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
                        if (key == "max_threads") threadSpin.Value = int.TryParse(val, out int t) ? Math.Clamp(t, 1, 128) : 50;
                        else if (key == "save_path") pathBox.Text = val;
                        else if (key == "timeout") timeoutSpin.Value = int.TryParse(val, out int to) ? Math.Clamp(to, 5, 300) : 30;
                    }
                    else if (section == "Behavior")
                    {
                        if (key == "prompt_mode")
                        {
                            if (val == "始终询问" || val == "always") modeCombo.SelectedItem = "始终询问";
                            else if (val == "自动添加" || val == "never") modeCombo.SelectedItem = "自动添加";
                            else if (val == "智能提示" || val == "smart") modeCombo.SelectedItem = "智能提示";
                            else modeCombo.SelectedItem = "始终询问";
                        }
                        else if (key == "smart_threshold_mb") thresholdSpin.Value = decimal.TryParse(val, out decimal th) ? th : 50;
                    }
                    else if (section == "App")
                    {
                        if (key == "auto_start") autoStartCheck.Checked = val == "true";
                        else if (key == "show_notification") notifyCheck.Checked = val == "true";
                    }
                }
            }
            catch { }
        }

        private void SetAutoStart(bool enable)
        {
            try
            {
                string keyName = "CreFetch";
                string exePath = Application.ExecutablePath;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (enable)
                    {
                        key.SetValue(keyName, $"\"{exePath}\"");
                    }
                    else
                    {
                        key.DeleteValue(keyName, false);
                    }
                }
            }
            catch { }
        }

        private void SaveSettings(object sender, EventArgs e)
        {
            try
            {
                SetAutoStart(autoStartCheck.Checked);

                var content = $@"[Download]
max_threads = {threadSpin.Value}
save_path = {pathBox.Text}
timeout = {timeoutSpin.Value}

[Behavior]
prompt_mode = {modeCombo.SelectedItem}
smart_threshold_mb = {thresholdSpin.Value}

[App]
auto_start = {autoStartCheck.Checked.ToString().ToLower()}
show_notification = {notifyCheck.Checked.ToString().ToLower()}
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