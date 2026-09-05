using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace EcloudKeepAlive
{
    internal static class Program
    {
        private const string AppName = "移动云电脑保活";
        private const string RegisterStartupTaskArgument = "--register-startup-task";
        private const string UnregisterStartupTaskArgument = "--unregister-startup-task";

        [STAThread]
        private static void Main()
        {
            if (HasArgument(RegisterStartupTaskArgument))
            {
                string errorMessage;
                var success = MainForm.RegisterWindowsStartupTask(out errorMessage);
                if (!success)
                {
                    MessageBox.Show(errorMessage, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                Environment.ExitCode = success ? 0 : 1;
                return;
            }

            if (HasArgument(UnregisterStartupTaskArgument))
            {
                string errorMessage;
                var success = MainForm.UnregisterWindowsStartupTask(out errorMessage);
                if (!success)
                {
                    MessageBox.Show(errorMessage, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                Environment.ExitCode = success ? 0 : 1;
                return;
            }

            bool created;
            using (var mutex = new System.Threading.Mutex(true, "EcloudKeepAlive.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("移动云电脑保活工具已经在运行。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
        }

        private static bool HasArgument(string expected)
        {
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class MainForm : Form
    {
        private const string DefaultClientPath = @"C:\Program Files (x86)\ecloud\CloudComputer\Ecloud Cloud Computer Application.exe";
        private const string LegacyRunKeyName = "EcloudKeepAlive";
        private const string StartupTaskName = "EcloudKeepAlive.UnattendedStartup";
        private const string RegisterStartupTaskArgument = "--register-startup-task";
        private const string UnregisterStartupTaskArgument = "--unregister-startup-task";
        private const string StartupArgument = "--startup";
        private const int UserInfoLevelPassword = 1003;
        private const DataProtectionScope PasswordProtectionScope = DataProtectionScope.LocalMachine;
        private const int TaskActionExec = 0;
        private const int TaskCreateOrUpdate = 6;
        private const int TaskLogonInteractiveToken = 3;
        private const int TaskRunLevelHighest = 1;
        private const int TaskTriggerLogon = 9;

        private readonly Timer timer = new Timer();
        private readonly NotifyIcon trayIcon = new NotifyIcon();
        private readonly string settingsPath;

        private TextBox clientPathTextBox;
        private NumericUpDown intervalNumeric;
        private CheckBox startWithWindowsCheckBox;
        private CheckBox autoStartFrpcCheckBox;
        private CheckBox autoKillClientCheckBox;
        private CheckBox autoChangePasswordCheckBox;
        private NumericUpDown killClientDelayNumeric;
        private TextBox frpDirectoryTextBox;
        private TextBox newPasswordTextBox;
        private Button passwordVisibilityButton;
        private Label statusLabel;
        private Button startButton;
        private Button stopButton;
        private DateTime? nextRunAt;
        private bool reallyExit;
        private bool loadingSettings;
        private bool newPasswordVisible;
        private string lastFrpStatus = "未启用";
        private string lastPasswordChangeStatus = "未启用";
        private string lastStartupTaskStatus = "未注册";
        private string lastClientKillStatus = "未启用";

        [StructLayout(LayoutKind.Sequential)]
        private struct UserInfo1003
        {
            public IntPtr Password;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int NetUserSetInfo(
            string serverName,
            string userName,
            int level,
            ref UserInfo1003 userInfo,
            out int parameterError);

        public MainForm()
        {
            settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "EcloudKeepAlive",
                "settings.ini");

            Text = "移动云电脑保活";
            StartPosition = FormStartPosition.CenterScreen;
            Width = 560;
            Height = 520;
            MinimumSize = new Size(560, 520);
            Icon = SystemIcons.Application;

            BuildUi();
            LoadSettings();
            ConfigureTrayIcon();

            timer.Tick += OnTimerTick;
            RunStartupTasks();

            if (IsStartedFromWindowsStartup())
            {
                StartKeepAlive();
            }
            else
            {
                UpdateStatus("未启动，请点击“立即保活”或“开始保活”。");
            }
        }

        private void BuildUi()
        {
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(246, 248, 251);

            var shell = new TableLayoutPanel();
            shell.Dock = DockStyle.Fill;
            shell.Padding = new Padding(14);
            shell.ColumnCount = 1;
            shell.RowCount = 2;
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            shell.BackColor = BackColor;
            Controls.Add(shell);

            var header = new TableLayoutPanel();
            header.Dock = DockStyle.Fill;
            header.ColumnCount = 2;
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
            header.BackColor = BackColor;
            shell.Controls.Add(header, 0, 0);

            var titlePanel = new FlowLayoutPanel();
            titlePanel.Dock = DockStyle.Fill;
            titlePanel.FlowDirection = FlowDirection.TopDown;
            titlePanel.WrapContents = false;
            titlePanel.BackColor = BackColor;
            header.Controls.Add(titlePanel, 0, 0);

            titlePanel.Controls.Add(new Label
            {
                Text = "移动云电脑保活",
                AutoSize = true,
                Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
                ForeColor = Color.FromArgb(24, 35, 53),
                Margin = new Padding(0, 4, 0, 0)
            });
            titlePanel.Controls.Add(new Label
            {
                Text = "定时连接、FRP 启动和开机自启集中管理",
                AutoSize = true,
                ForeColor = Color.FromArgb(93, 106, 126),
                Margin = new Padding(1, 2, 0, 0)
            });

            var statusPill = new Label
            {
                Text = "托盘运行",
                Dock = DockStyle.Top,
                Height = 28,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = Color.FromArgb(226, 241, 255),
                ForeColor = Color.FromArgb(24, 93, 158),
                Margin = new Padding(0, 10, 0, 0)
            };
            header.Controls.Add(statusPill, 1, 0);

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            tabs.Font = new Font(Font.FontFamily, 9F);
            shell.Controls.Add(tabs, 0, 1);

            var keepAlivePage = CreateTabPage("保活");
            var startupPage = CreateTabPage("启动项");
            var statusPage = CreateTabPage("状态");
            tabs.TabPages.Add(keepAlivePage);
            tabs.TabPages.Add(startupPage);
            tabs.TabPages.Add(statusPage);

            BuildKeepAlivePage(keepAlivePage);
            BuildStartupPage(startupPage);
            BuildStatusPage(statusPage);
        }

        private TabPage CreateTabPage(string text)
        {
            var page = new TabPage(text);
            page.BackColor = Color.FromArgb(246, 248, 251);
            page.Padding = new Padding(0, 10, 0, 0);
            page.AutoScroll = true;
            return page;
        }

        private void BuildKeepAlivePage(TabPage page)
        {
            var root = CreatePageLayout(3);
            page.Controls.Add(root);

            var clientCard = CreateCard("云电脑客户端", 3);
            root.Controls.Add(clientCard, 0, 0);

            clientCard.Controls.Add(CreateFieldLabel("客户端路径"), 0, 0);
            clientPathTextBox = CreateTextBox();
            clientCard.Controls.Add(clientPathTextBox, 1, 0);
            var browseButton = CreateButton("浏览...", 82, false);
            browseButton.Margin = new Padding(0, 5, 0, 5);
            browseButton.Click += delegate { BrowseClientPath(); };
            clientCard.Controls.Add(browseButton, 2, 0);

            clientCard.Controls.Add(CreateFieldLabel("连接间隔"), 0, 1);
            intervalNumeric = new NumericUpDown { Minimum = 1, Maximum = 10080, Value = 1380, Width = 90 };
            var intervalPanel = CreateValuePanel(intervalNumeric, "分钟，推荐 1380 分钟");
            clientCard.Controls.Add(intervalPanel, 1, 1);
            clientCard.SetColumnSpan(intervalPanel, 2);

            startWithWindowsCheckBox = CreateCheckBox("开机自动运行本工具并启用保活", false);
            startWithWindowsCheckBox.CheckedChanged += delegate
            {
                if (!loadingSettings)
                {
                    SetStartWithWindows(startWithWindowsCheckBox.Checked);
                }
            };
            clientCard.Controls.Add(startWithWindowsCheckBox, 1, 2);
            clientCard.SetColumnSpan(startWithWindowsCheckBox, 2);

            var killCard = CreateCard("客户端收尾", 2);
            root.Controls.Add(killCard, 0, 1);

            killClientDelayNumeric = new NumericUpDown { Minimum = 5, Maximum = 600, Value = 30, Width = 90 };
            killCard.Controls.Add(CreateFieldLabel("结束延迟"), 0, 0);
            var killDelayPanel = CreateValuePanel(killClientDelayNumeric, "秒，默认 30 秒");
            killCard.Controls.Add(killDelayPanel, 1, 0);
            killCard.SetColumnSpan(killDelayPanel, 2);

            autoKillClientCheckBox = CreateCheckBox("启动客户端后自动结束客户端进程", true);
            autoKillClientCheckBox.Margin = new Padding(0, 2, 0, 0);
            killCard.Controls.Add(autoKillClientCheckBox, 1, 1);
            killCard.SetColumnSpan(autoKillClientCheckBox, 2);

            var actionBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Color.FromArgb(246, 248, 251),
                Margin = new Padding(0, 0, 0, 4),
                Padding = new Padding(0, 0, 0, 0)
            };

            startButton = CreateButton("开始保活", 104, true);
            startButton.Margin = new Padding(0, 0, 8, 0);
            stopButton = CreateButton("停止", 82, false);
            stopButton.Margin = new Padding(0, 0, 8, 0);
            var triggerButton = CreateButton("立即保活", 104, false);
            triggerButton.Margin = new Padding(0, 0, 8, 0);
            var saveButton = CreateButton("保存设置", 104, false);
            saveButton.Margin = new Padding(0);
            stopButton.Enabled = false;

            startButton.Click += delegate { StartKeepAlive(); };
            stopButton.Click += delegate { StopKeepAlive(); };
            triggerButton.Click += delegate { RunKeepAliveNow(); };
            saveButton.Click += delegate { SaveSettings(); MessageBox.Show("设置已保存。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); };

            actionBar.Controls.Add(startButton);
            actionBar.Controls.Add(stopButton);
            actionBar.Controls.Add(triggerButton);
            actionBar.Controls.Add(saveButton);
            root.Controls.Add(actionBar, 0, 2);
        }

        private void BuildStartupPage(TabPage page)
        {
            var root = CreatePageLayout(2);
            page.Controls.Add(root);

            var frpCard = CreateCard("FRP", 2);
            root.Controls.Add(frpCard, 0, 0);

            frpCard.Controls.Add(CreateFieldLabel("FRP 目录"), 0, 0);
            frpDirectoryTextBox = CreateTextBox();
            frpCard.Controls.Add(frpDirectoryTextBox, 1, 0);
            var browseFrpButton = CreateButton("浏览...", 82, false);
            browseFrpButton.Margin = new Padding(0, 5, 0, 5);
            browseFrpButton.Click += delegate { BrowseFrpDirectory(); };
            frpCard.Controls.Add(browseFrpButton, 2, 0);

            var frpcActionPanel = CreateInlinePanel();
            autoStartFrpcCheckBox = CreateCheckBox("打开本工具时自动启动 frpc", false);
            autoStartFrpcCheckBox.AutoSize = true;
            autoStartFrpcCheckBox.Dock = DockStyle.None;
            autoStartFrpcCheckBox.Margin = new Padding(0, 4, 12, 0);
            var startFrpcButton = CreateButton("启动 frpc", 100, false);
            startFrpcButton.Margin = new Padding(0);
            startFrpcButton.Click += delegate { StartFrpc(true); };
            frpcActionPanel.Controls.Add(autoStartFrpcCheckBox);
            frpcActionPanel.Controls.Add(startFrpcButton);
            frpCard.Controls.Add(frpcActionPanel, 1, 1);
            frpCard.SetColumnSpan(frpcActionPanel, 2);

            var passwordCard = CreateCard("当前登录用户密码", 4);
            root.Controls.Add(passwordCard, 0, 1);

            passwordCard.Controls.Add(CreateFieldLabel("当前账户"), 0, 0);
            var accountLabel = CreateValueLabel(Environment.UserDomainName + "\\" + Environment.UserName);
            passwordCard.Controls.Add(accountLabel, 1, 0);
            passwordCard.SetColumnSpan(accountLabel, 2);

            passwordCard.Controls.Add(CreateFieldLabel("新密码"), 0, 1);
            newPasswordTextBox = CreateTextBox();
            newPasswordTextBox.UseSystemPasswordChar = true;
            passwordCard.Controls.Add(newPasswordTextBox, 1, 1);

            passwordVisibilityButton = CreateButton("👁", 42, false);
            passwordVisibilityButton.Dock = DockStyle.Left;
            passwordVisibilityButton.Margin = new Padding(0, 5, 0, 5);
            passwordVisibilityButton.Font = new Font("Segoe UI Symbol", 10F);
            passwordVisibilityButton.AccessibleName = "显示密码";
            passwordVisibilityButton.Click += delegate
            {
                newPasswordVisible = !newPasswordVisible;
                newPasswordTextBox.UseSystemPasswordChar = !newPasswordVisible;
                passwordVisibilityButton.Text = newPasswordVisible ? "🙈" : "👁";
                passwordVisibilityButton.AccessibleName = newPasswordVisible ? "隐藏密码" : "显示密码";
            };
            passwordCard.Controls.Add(passwordVisibilityButton, 2, 1);

            autoChangePasswordCheckBox = CreateCheckBox("开机启动时自动设置当前用户密码", false);
            autoChangePasswordCheckBox.Margin = new Padding(0, 2, 0, 0);
            passwordCard.Controls.Add(autoChangePasswordCheckBox, 1, 2);
            passwordCard.SetColumnSpan(autoChangePasswordCheckBox, 2);

            var passwordActionPanel = CreateInlinePanel();
            var registerTaskButton = CreateButton("立即注册到 Windows 计划任务中", 220, false);
            registerTaskButton.Margin = new Padding(0);
            registerTaskButton.Click += delegate { RegisterStartupTaskFromUi(); };
            passwordActionPanel.Controls.Add(registerTaskButton);
            passwordActionPanel.Controls.Add(CreateHintLabel("首次注册需管理员授权，之后开机无需确认。"));
            passwordCard.Controls.Add(passwordActionPanel, 1, 3);
            passwordCard.SetColumnSpan(passwordActionPanel, 2);
        }

        private void BuildStatusPage(TabPage page)
        {
            var card = CreateCard("运行状态", 1);
            card.Dock = DockStyle.Fill;
            page.Controls.Add(card);

            statusLabel = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                Padding = new Padding(12),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(44, 55, 72),
                Font = new Font("Consolas", 9F)
            };
            card.Controls.Add(statusLabel, 0, 0);
            card.SetColumnSpan(statusLabel, 3);
        }

        private TableLayoutPanel CreatePageLayout(int rows)
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 1;
            root.RowCount = rows;
            root.BackColor = Color.FromArgb(246, 248, 251);
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var i = 0; i < rows; i++)
            {
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            return root;
        }

        private TableLayoutPanel CreateCard(string title, int rows)
        {
            return CreateCard(title, rows, false);
        }

        private TableLayoutPanel CreateCard(string title, int rows, bool expandTextArea)
        {
            var card = new TableLayoutPanel();
            card.Dock = DockStyle.Top;
            card.AutoSize = true;
            card.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            card.Margin = new Padding(0, 0, 0, 10);
            card.Padding = new Padding(12, 32, 12, 8);
            card.ColumnCount = 3;
            card.RowCount = rows;
            card.BackColor = Color.White;
            card.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            card.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            for (var i = 0; i < rows; i++)
            {
                var expands = expandTextArea && i == 2;
                card.RowStyles.Add(new RowStyle(expands ? SizeType.Percent : SizeType.AutoSize, expands ? 100 : 0));
            }
            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(224, 230, 238)))
                {
                    e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
                }
                using (var brush = new SolidBrush(Color.FromArgb(24, 35, 53)))
                {
                    e.Graphics.DrawString(title, new Font(Font.FontFamily, 10F, FontStyle.Bold), brush, 14, 12);
                }
            };
            return card;
        }

        private Label CreateFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(93, 106, 126),
                Margin = new Padding(0)
            };
        }

        private Label CreateHintLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(8, 8, 0, 0),
                ForeColor = Color.FromArgb(109, 122, 141)
            };
        }

        private Label CreateValueLabel(string text)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(109, 122, 141),
                Margin = new Padding(0)
            };
        }

        private TextBox CreateTextBox()
        {
            return new TextBox
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Height = 32,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font(Font.FontFamily, 9F),
                Margin = new Padding(0, 5, 8, 5)
            };
        }

        private CheckBox CreateCheckBox(string text, bool isChecked)
        {
            return new CheckBox
            {
                Text = text,
                Checked = isChecked,
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.System,
                ForeColor = Color.FromArgb(44, 55, 72)
            };
        }

        private FlowLayoutPanel CreateInlinePanel()
        {
            return new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.White,
                Margin = new Padding(0, 2, 0, 0)
            };
        }

        private TableLayoutPanel CreateValuePanel(Control valueControl, string hintText)
        {
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.White,
                Margin = new Padding(0, 2, 0, 0),
                Padding = new Padding(0)
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            valueControl.Dock = DockStyle.Fill;
            valueControl.Margin = new Padding(0, 5, 8, 5);
            panel.Controls.Add(valueControl, 0, 0);

            panel.Controls.Add(new Label
            {
                Text = hintText,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                ForeColor = Color.FromArgb(109, 122, 141),
                Margin = new Padding(0)
            }, 1, 0);

            return panel;
        }

        private Button CreateButton(string text, int width, bool primary)
        {
            var button = new Button
            {
                Text = text,
                Width = width,
                Height = 32,
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Color.FromArgb(31, 111, 235) : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(44, 55, 72),
                Margin = new Padding(6, 0, 0, 0),
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(31, 111, 235) : Color.FromArgb(207, 216, 228);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(24, 94, 201) : Color.FromArgb(241, 245, 249);
            return button;
        }

        private void ConfigureTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示窗口", null, delegate { ShowFromTray(); });
            menu.Items.Add("立即保活", null, delegate { RunKeepAliveNow(); });
            menu.Items.Add("启动 frpc", null, delegate { StartFrpc(true); });
            menu.Items.Add("注册计划任务", null, delegate { RegisterStartupTaskFromUi(); });
            menu.Items.Add("退出", null, delegate { ExitApplication(); });

            trayIcon.Icon = SystemIcons.Application;
            trayIcon.Text = "移动云电脑保活";
            trayIcon.Visible = true;
            trayIcon.ContextMenuStrip = menu;
            trayIcon.DoubleClick += delegate { ShowFromTray(); };
        }

        private void BrowseClientPath()
        {
            using (var dialog = new System.Windows.Forms.OpenFileDialog())
            {
                dialog.Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                dialog.FileName = clientPathTextBox.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    clientPathTextBox.Text = dialog.FileName;
                }
            }
        }

        private void BrowseFrpDirectory()
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "请选择包含 frpc.exe 的 FRP 目录";
                dialog.SelectedPath = frpDirectoryTextBox.Text.Trim();
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    frpDirectoryTextBox.Text = dialog.SelectedPath;
                }
            }
        }

        private void StartKeepAlive()
        {
            if (!ValidateClientPath())
            {
                return;
            }

            SaveSettings();
            timer.Stop();
            timer.Interval = Math.Max(1, (int)intervalNumeric.Value) * 60 * 1000;
            timer.Start();
            nextRunAt = DateTime.Now.AddMinutes((double)intervalNumeric.Value);

            startButton.Enabled = false;
            stopButton.Enabled = true;

            UpdateStatus("保活已启动，等待下一次计划连接。");
        }

        private void RunKeepAliveNow()
        {
            if (!timer.Enabled)
            {
                StartKeepAlive();
                if (!timer.Enabled)
                {
                    return;
                }
            }

            if (LaunchClient(true))
            {
                nextRunAt = DateTime.Now.AddMinutes((double)intervalNumeric.Value);
                UpdateStatus("已立即保活，并已启用定时保活。");
            }
        }

        private void StopKeepAlive()
        {
            timer.Stop();
            nextRunAt = null;
            startButton.Enabled = true;
            stopButton.Enabled = false;
            UpdateStatus("已停止。");
            SaveSettings();
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (LaunchClient(false))
            {
                nextRunAt = DateTime.Now.AddMinutes((double)intervalNumeric.Value);
                UpdateStatus("已按计划连接一次。");
            }
        }

        private bool LaunchClient(bool showMessage)
        {
            if (!ValidateClientPath())
            {
                return false;
            }

            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = clientPathTextBox.Text.Trim(),
                    WorkingDirectory = Path.GetDirectoryName(clientPathTextBox.Text.Trim()),
                    UseShellExecute = true
                };
                var process = Process.Start(info);
                ScheduleClientKill(process);
                UpdateStatus("已启动云电脑客户端。");

                if (showMessage)
                {
                    trayIcon.ShowBalloonTip(2500, Text, "已启动云电脑客户端。", ToolTipIcon.Info);
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("启动客户端失败：\r\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus("启动失败：" + ex.Message);
                return false;
            }
        }

        private void ScheduleClientKill(Process process)
        {
            if (!autoKillClientCheckBox.Checked)
            {
                lastClientKillStatus = "未启用";
                return;
            }

            SaveSettings();
            var delaySeconds = (int)killClientDelayNumeric.Value;
            var clientPath = clientPathTextBox.Text.Trim();
            var processName = Path.GetFileNameWithoutExtension(clientPath);

            lastClientKillStatus = "已安排 " + delaySeconds + " 秒后结束客户端";
            var killTimer = new Timer();
            killTimer.Interval = delaySeconds * 1000;
            killTimer.Tick += delegate
            {
                killTimer.Stop();
                killTimer.Dispose();
                KillClientProcess(process, processName, clientPath);
            };
            killTimer.Start();
        }

        private void KillClientProcess(Process startedProcess, string processName, string clientPath)
        {
            var killed = 0;

            try
            {
                if (startedProcess != null)
                {
                    startedProcess.Refresh();
                    if (!startedProcess.HasExited)
                    {
                        startedProcess.Kill();
                        killed++;
                    }
                }
            }
            catch
            {
            }

            try
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        if (!IsSameExecutable(process, clientPath)) continue;
                        try
                        {
                            process.Kill();
                            killed++;
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }

            lastClientKillStatus = killed > 0 ? "已结束客户端进程 " + killed + " 个" : "未发现需要结束的客户端进程";
            UpdateStatus(lastClientKillStatus);
        }

        private bool IsSameExecutable(Process process, string expectedPath)
        {
            try
            {
                var actualPath = process.MainModule.FileName;
                return string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void RunStartupTasks()
        {
            if (autoStartFrpcCheckBox.Checked)
            {
                StartFrpc(false);
            }

            if (autoChangePasswordCheckBox.Checked && IsStartedFromWindowsStartup())
            {
                SetCurrentUserPassword(false);
            }
        }

        private string StartFrpc(bool showMessage)
        {
            SaveSettings();

            var directory = frpDirectoryTextBox.Text.Trim();
            if (directory.Length == 0)
            {
                var message = "未设置 FRP 目录。";
                if (showMessage) MessageBox.Show(message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                lastFrpStatus = message;
                UpdateStatus(message);
                return message;
            }

            if (!Directory.Exists(directory))
            {
                var message = "FRP 目录不存在。";
                if (showMessage) MessageBox.Show(message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                lastFrpStatus = message;
                UpdateStatus(message);
                return message;
            }

            var frpcPath = Path.Combine(directory, "frpc.exe");
            if (!File.Exists(frpcPath))
            {
                var message = "FRP 目录中找不到 frpc.exe。";
                if (showMessage) MessageBox.Show(message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                lastFrpStatus = message;
                UpdateStatus(message);
                return message;
            }

            if (IsProcessRunning("frpc"))
            {
                var message = "frpc 已在运行，未重复启动。";
                if (showMessage) trayIcon.ShowBalloonTip(2500, Text, message, ToolTipIcon.Info);
                lastFrpStatus = message;
                UpdateStatus(message);
                return message;
            }

            var arguments = GetFrpcArguments(directory);
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = frpcPath,
                    Arguments = arguments,
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(info);

                var message = "已启动 frpc。";
                if (showMessage) trayIcon.ShowBalloonTip(2500, Text, message, ToolTipIcon.Info);
                lastFrpStatus = message;
                UpdateStatus(message);
                return message;
            }
            catch (Exception ex)
            {
                var message = "启动 frpc 失败：" + ex.Message;
                if (showMessage) MessageBox.Show(message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                lastFrpStatus = message;
                UpdateStatus(message);
                return message;
            }
        }

        private string GetFrpcArguments(string directory)
        {
            var toml = Path.Combine(directory, "frpc.toml");
            if (File.Exists(toml)) return "-c \"" + toml + "\"";

            var ini = Path.Combine(directory, "frpc.ini");
            if (File.Exists(ini)) return "-c \"" + ini + "\"";

            return string.Empty;
        }

        private bool IsProcessRunning(string processName)
        {
            try
            {
                return Process.GetProcessesByName(processName).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private string SetCurrentUserPassword(bool showMessage)
        {
            var newPassword = newPasswordTextBox.Text;

            if (newPassword.Length == 0)
            {
                return ReportPasswordChange("请填写新密码。", showMessage, true);
            }

            try
            {
                SaveSettings();
                var userInfo = new UserInfo1003();
                var passwordPointer = Marshal.StringToHGlobalUni(newPassword);
                int status;
                int parameterError;
                try
                {
                    userInfo.Password = passwordPointer;
                    status = NetUserSetInfo(
                        null,
                        Environment.UserName,
                        UserInfoLevelPassword,
                        ref userInfo,
                        out parameterError);
                }
                finally
                {
                    Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer);
                }

                if (status != 0)
                {
                    var message = status == 5
                        ? "修改当前登录用户密码失败：需要以管理员身份运行本工具（错误码 5：拒绝访问）。"
                        : "修改当前登录用户密码失败：错误码 " + status + "（" + new Win32Exception(status).Message + "）";
                    if (parameterError > 0)
                    {
                        message += " 参数位置：" + parameterError + "。";
                    }
                    return ReportPasswordChange(message, showMessage, true);
                }

                var successMessage = "当前登录用户密码设置成功。";
                lastPasswordChangeStatus = successMessage;
                UpdateStatus(successMessage);
                if (showMessage)
                {
                    MessageBox.Show(successMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return successMessage;
            }
            catch (Exception ex)
            {
                var message = "修改当前用户密码失败：" + ex.Message;
                return ReportPasswordChange(message, showMessage, true);
            }
        }

        private string ReportPasswordChange(string message, bool showMessage, bool warning)
        {
            lastPasswordChangeStatus = message;
            UpdateStatus(message);
            if (showMessage)
            {
                MessageBox.Show(
                    message,
                    Text,
                    MessageBoxButtons.OK,
                    warning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            return message;
        }

        private string ProtectPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
            {
                return string.Empty;
            }

            var plainBytes = Encoding.UTF8.GetBytes(password);
            try
            {
                var protectedBytes = ProtectedData.Protect(plainBytes, null, PasswordProtectionScope);
                try
                {
                    return Convert.ToBase64String(protectedBytes);
                }
                finally
                {
                    Array.Clear(protectedBytes, 0, protectedBytes.Length);
                }
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }

        private string UnprotectPassword(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            try
            {
                return UnprotectPassword(value, PasswordProtectionScope);
            }
            catch
            {
                try
                {
                    return UnprotectPassword(value, DataProtectionScope.CurrentUser);
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        private string UnprotectPassword(string value, DataProtectionScope scope)
        {
            var protectedBytes = Convert.FromBase64String(value);
            try
            {
                var plainBytes = ProtectedData.Unprotect(protectedBytes, null, scope);
                try
                {
                    return Encoding.UTF8.GetString(plainBytes);
                }
                finally
                {
                    Array.Clear(plainBytes, 0, plainBytes.Length);
                }
            }
            finally
            {
                Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }

        private bool ValidateClientPath()
        {
            var path = clientPathTextBox.Text.Trim();
            if (File.Exists(path))
            {
                return true;
            }

            MessageBox.Show("找不到云电脑客户端程序，请检查路径。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            UpdateStatus("客户端路径无效。");
            return false;
        }

        private void UpdateStatus(string message)
        {
            var builder = new StringBuilder();
            builder.AppendLine("状态：" + message);
            builder.AppendLine("当前时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            builder.AppendLine("客户端：" + clientPathTextBox.Text.Trim());
            builder.AppendLine("间隔：" + intervalNumeric.Value + " 分钟");
            builder.AppendLine("客户端自动结束：" + (autoKillClientCheckBox.Checked ? killClientDelayNumeric.Value + " 秒后执行" : "未启用"));
            builder.AppendLine("结束客户端状态：" + lastClientKillStatus);
            builder.AppendLine("FRP 目录：" + (frpDirectoryTextBox.Text.Trim().Length == 0 ? "未设置" : frpDirectoryTextBox.Text.Trim()));
            builder.AppendLine("FRP 状态：" + lastFrpStatus);
            builder.AppendLine("密码修改状态：" + lastPasswordChangeStatus);
            builder.AppendLine("计划任务状态：" + lastStartupTaskStatus);
            builder.AppendLine("下次计划连接：" + (nextRunAt.HasValue ? nextRunAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "无"));
            statusLabel.Text = builder.ToString();
            trayIcon.Text = nextRunAt.HasValue ? "移动云电脑保活 - 运行中" : "移动云电脑保活";
        }

        private void LoadSettings()
        {
            loadingSettings = true;
            clientPathTextBox.Text = DefaultClientPath;

            var settings = ReadSettings();
            if (settings.ContainsKey("ClientPath")) clientPathTextBox.Text = settings["ClientPath"];
            if (settings.ContainsKey("IntervalMinutes"))
            {
                decimal value;
                if (decimal.TryParse(settings["IntervalMinutes"], out value))
                {
                    intervalNumeric.Value = Math.Min(intervalNumeric.Maximum, Math.Max(intervalNumeric.Minimum, value));
                }
            }
            if (settings.ContainsKey("AutoKillClient")) autoKillClientCheckBox.Checked = settings["AutoKillClient"] == "true";
            if (settings.ContainsKey("KillClientDelaySeconds"))
            {
                decimal value;
                if (decimal.TryParse(settings["KillClientDelaySeconds"], out value))
                {
                    killClientDelayNumeric.Value = Math.Min(killClientDelayNumeric.Maximum, Math.Max(killClientDelayNumeric.Minimum, value));
                }
            }
            if (settings.ContainsKey("FrpDirectory")) frpDirectoryTextBox.Text = settings["FrpDirectory"];
            if (settings.ContainsKey("AutoStartFrpc")) autoStartFrpcCheckBox.Checked = settings["AutoStartFrpc"] == "true";
            if (settings.ContainsKey("AutoChangePassword")) autoChangePasswordCheckBox.Checked = settings["AutoChangePassword"] == "true";
            if (settings.ContainsKey("NewPasswordProtectedBase64")) newPasswordTextBox.Text = UnprotectPassword(settings["NewPasswordProtectedBase64"]);

            RemoveLegacyStartupEntry();
            startWithWindowsCheckBox.Checked = IsStartWithWindowsEnabled();
            lastStartupTaskStatus = startWithWindowsCheckBox.Checked ? "已注册" : "未注册";
            loadingSettings = false;
        }

        private Dictionary<string, string> ReadSettings()
        {
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(settingsPath))
            {
                return settings;
            }

            foreach (var line in File.ReadAllLines(settingsPath, Encoding.UTF8))
            {
                var index = line.IndexOf('=');
                if (index <= 0) continue;
                settings[line.Substring(0, index)] = line.Substring(index + 1);
            }
            return settings;
        }

        private void SaveSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
            var lines = new[]
            {
                "ClientPath=" + clientPathTextBox.Text.Trim(),
                "IntervalMinutes=" + intervalNumeric.Value,
                "AutoKillClient=" + autoKillClientCheckBox.Checked.ToString().ToLowerInvariant(),
                "KillClientDelaySeconds=" + killClientDelayNumeric.Value,
                "FrpDirectory=" + frpDirectoryTextBox.Text.Trim(),
                "AutoStartFrpc=" + autoStartFrpcCheckBox.Checked.ToString().ToLowerInvariant(),
                "AutoChangePassword=" + autoChangePasswordCheckBox.Checked.ToString().ToLowerInvariant(),
                "NewPasswordProtectedBase64=" + ProtectPassword(newPasswordTextBox.Text)
            };
            File.WriteAllLines(settingsPath, lines, Encoding.UTF8);
        }

        private void RegisterStartupTaskFromUi()
        {
            if (autoChangePasswordCheckBox.Checked && newPasswordTextBox.Text.Length == 0)
            {
                lastStartupTaskStatus = "未注册：请先填写新密码。";
                UpdateStatus(lastStartupTaskStatus);
                MessageBox.Show(lastStartupTaskStatus, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                SaveSettings();
            }
            catch (Exception ex)
            {
                lastStartupTaskStatus = "未注册，保存设置失败：" + ex.Message;
                UpdateStatus(lastStartupTaskStatus);
                MessageBox.Show(lastStartupTaskStatus, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string errorMessage;
            if (!RunElevatedTaskOperation(RegisterStartupTaskArgument, out errorMessage))
            {
                lastStartupTaskStatus = errorMessage;
                UpdateStatus(errorMessage);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    MessageBox.Show(errorMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            RemoveLegacyStartupEntry();
            SetStartWithWindowsCheckbox(true);
            lastStartupTaskStatus = "已注册，开机将自动启动本工具并启用保活";
            UpdateStatus(lastStartupTaskStatus);
            MessageBox.Show(
                "Windows 计划任务注册成功。以后开机时会自动启动本工具并启用保活，无需再次点击管理员授权。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void SetStartWithWindows(bool enabled)
        {
            if (enabled)
            {
                if (IsStartupTaskRegistered())
                {
                    RemoveLegacyStartupEntry();
                    lastStartupTaskStatus = "已注册";
                    UpdateStatus(lastStartupTaskStatus);
                    return;
                }

                try
                {
                    SaveSettings();
                }
                catch (Exception ex)
                {
                    SetStartWithWindowsCheckbox(false);
                    lastStartupTaskStatus = "未注册，保存设置失败：" + ex.Message;
                    UpdateStatus(lastStartupTaskStatus);
                    MessageBox.Show(lastStartupTaskStatus, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string errorMessage;
                if (RunElevatedTaskOperation(RegisterStartupTaskArgument, out errorMessage))
                {
                    RemoveLegacyStartupEntry();
                    lastStartupTaskStatus = "已注册";
                    UpdateStatus(lastStartupTaskStatus);
                    return;
                }

                SetStartWithWindowsCheckbox(false);
                lastStartupTaskStatus = errorMessage;
                UpdateStatus(errorMessage);
                if (!string.IsNullOrEmpty(errorMessage))
                {
                    MessageBox.Show(errorMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            RemoveLegacyStartupEntry();
            if (!IsStartupTaskRegistered())
            {
                lastStartupTaskStatus = "未注册";
                UpdateStatus(lastStartupTaskStatus);
                return;
            }

            string unregisterErrorMessage;
            if (RunElevatedTaskOperation(UnregisterStartupTaskArgument, out unregisterErrorMessage))
            {
                lastStartupTaskStatus = "已取消注册";
                UpdateStatus(lastStartupTaskStatus);
                return;
            }

            SetStartWithWindowsCheckbox(true);
            lastStartupTaskStatus = unregisterErrorMessage;
            UpdateStatus(unregisterErrorMessage);
            if (!string.IsNullOrEmpty(unregisterErrorMessage))
            {
                MessageBox.Show(unregisterErrorMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private bool IsStartWithWindowsEnabled()
        {
            return IsStartupTaskRegistered();
        }

        private bool RunElevatedTaskOperation(string operationArgument, out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = Application.ExecutablePath,
                    Arguments = operationArgument,
                    WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath),
                    UseShellExecute = true,
                    Verb = "runas"
                };
                var process = Process.Start(info);
                if (process == null)
                {
                    errorMessage = "无法启动管理员操作。";
                    return false;
                }

                using (process)
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        errorMessage = "管理员操作未完成。";
                        return false;
                    }
                }

                return true;
            }
            catch (Win32Exception ex)
            {
                errorMessage = ex.NativeErrorCode == 1223
                    ? "已取消管理员授权。"
                    : "请求管理员权限失败：" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = "请求管理员权限失败：" + ex.Message;
                return false;
            }
        }

        internal static bool RegisterWindowsStartupTask(out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                var serviceType = Type.GetTypeFromProgID("Schedule.Service");
                if (serviceType == null)
                {
                    errorMessage = "当前系统无法访问 Windows 任务计划程序。";
                    return false;
                }

                dynamic service = Activator.CreateInstance(serviceType);
                service.Connect();
                dynamic rootFolder = service.GetFolder("\\");
                dynamic taskDefinition = service.NewTask(0);
                var userId = GetCurrentUserId();

                taskDefinition.RegistrationInfo.Description = "移动云电脑保活无人值守启动任务";
                taskDefinition.Settings.Enabled = true;
                taskDefinition.Settings.StartWhenAvailable = true;
                taskDefinition.Settings.AllowDemandStart = true;
                taskDefinition.Principal.UserId = userId;
                taskDefinition.Principal.LogonType = TaskLogonInteractiveToken;
                taskDefinition.Principal.RunLevel = TaskRunLevelHighest;

                dynamic trigger = taskDefinition.Triggers.Create(TaskTriggerLogon);
                trigger.Enabled = true;
                trigger.UserId = userId;

                dynamic action = taskDefinition.Actions.Create(TaskActionExec);
                action.Path = Application.ExecutablePath;
                action.Arguments = StartupArgument;
                action.WorkingDirectory = Path.GetDirectoryName(Application.ExecutablePath);

                rootFolder.RegisterTaskDefinition(
                    StartupTaskName,
                    taskDefinition,
                    TaskCreateOrUpdate,
                    null,
                    null,
                    TaskLogonInteractiveToken,
                    null);

                RemoveLegacyStartupEntry();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = "注册 Windows 计划任务失败：\r\n" + ex.Message;
                return false;
            }
        }

        internal static bool UnregisterWindowsStartupTask(out string errorMessage)
        {
            errorMessage = string.Empty;
            try
            {
                var serviceType = Type.GetTypeFromProgID("Schedule.Service");
                if (serviceType != null)
                {
                    dynamic service = Activator.CreateInstance(serviceType);
                    service.Connect();
                    dynamic rootFolder = service.GetFolder("\\");
                    try
                    {
                        rootFolder.DeleteTask(StartupTaskName, 0);
                    }
                    catch (COMException ex) when ((uint)ex.HResult == 0x80070002)
                    {
                    }
                }

                RemoveLegacyStartupEntry();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = "取消注册 Windows 计划任务失败：\r\n" + ex.Message;
                return false;
            }
        }

        private static bool IsStartupTaskRegistered()
        {
            try
            {
                var serviceType = Type.GetTypeFromProgID("Schedule.Service");
                if (serviceType == null)
                {
                    return false;
                }

                dynamic service = Activator.CreateInstance(serviceType);
                service.Connect();
                dynamic rootFolder = service.GetFolder("\\");
                rootFolder.GetTask(StartupTaskName);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetCurrentUserId()
        {
            var domainName = Environment.UserDomainName;
            return string.IsNullOrWhiteSpace(domainName)
                ? Environment.UserName
                : domainName + "\\" + Environment.UserName;
        }

        private static void RemoveLegacyStartupEntry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        key.DeleteValue(LegacyRunKeyName, false);
                    }
                }
            }
            catch
            {
            }
        }

        private void SetStartWithWindowsCheckbox(bool enabled)
        {
            loadingSettings = true;
            startWithWindowsCheckBox.Checked = enabled;
            loadingSettings = false;
        }

        private bool IsStartedFromWindowsStartup()
        {
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, StartupArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                trayIcon.ShowBalloonTip(2500, Text, "程序已隐藏到托盘，会继续按设置保活。", ToolTipIcon.Info);
                return;
            }

            SaveSettings();
            trayIcon.Visible = false;
            base.OnFormClosing(e);
        }

        private void ExitApplication()
        {
            reallyExit = true;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                trayIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
