using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Management;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Ec20PhoneTool
{
    static class Program
    {
        private const string MutexName = @"Global\Ec20PhoneToolSingleInstance";
        internal const string ShowEventName = @"Global\Ec20PhoneToolShowMainWindowEvent";

        [STAThread]
        static void Main(string[] args)
        {
            bool startHidden = Array.Exists(args, delegate(string arg)
            {
                return string.Equals(arg, "/background", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(arg, "/hidden", StringComparison.OrdinalIgnoreCase);
            });

            bool createdNew;
            using (var mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    if (!startHidden)
                    {
                        SignalExistingInstanceToShow();
                        NativeMethods.PostMessage(NativeMethods.HWND_BROADCAST, NativeMethods.ShowMainWindowMessage, new IntPtr(1), IntPtr.Zero);
                    }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm(startHidden));
                GC.KeepAlive(mutex);
            }
        }

        private static void SignalExistingInstanceToShow()
        {
            try
            {
                using (var showEvent = EventWaitHandle.OpenExisting(ShowEventName))
                {
                    showEvent.Set();
                }
            }
            catch
            {
            }
        }
    }

    internal static class NativeMethods
    {
        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xffff);
        public static readonly int ShowMainWindowMessage = RegisterWindowMessage("EC20_PHONE_TOOL_SHOW_MAIN_WINDOW_20260818");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    }

    public class MainForm : Form
    {
        private SerialPort port;
        private ComboBox portBox;
        private Button refreshButton;
        private Button connectButton;
        private Button startupButton;
        private Panel connectionDot;
        private Label connectionTextLabel;
        private Label signalLabel;
        private TextBox numberBox;
        private TextBox smsNumberBox;
        private TextBox smsBox;
        private TextBox logBox;
        private TextBox smsDetailBox;
        private ListView smsListView;
        private ListView sentSmsListView;
        private ListView callListView;
        private Label statusLabel;
        private System.Windows.Forms.Timer pollTimer;
        private System.Windows.Forms.Timer autoConnectTimer;
        private System.Windows.Forms.Timer showSignalTimer;
        private EventWaitHandle showSignalEvent;
        private NotifyIcon notifyIcon;
        private CallPopupForm callPopup;
        private string lastCallerNumber = "";
        private string serialReceiveBuffer = "";
        private int statusPollTicks;
        private int lastSignal = -1;
        private bool simReady;
        private bool networkReady;
        private bool networkSearching;
        private bool readingSms;
        private int noServiceTicks;
        private int recoveryStage;
        private volatile bool waitingForSmsPrompt;
        private readonly List<SmsRecord> smsRecords = new List<SmsRecord>();
        private readonly List<CallRecord> callRecords = new List<CallRecord>();
        private readonly string dataDir;
        private readonly string smsStorePath;
        private readonly string callStorePath;
        private readonly bool startHidden;
        private bool allowExit;
        private bool autoConnectFinished;
        private int autoConnectAttempts;
        private string currentCallNumber = "";
        private string currentCallDirection = "";
        private DateTime currentCallStartedAt;
        private bool currentCallActive;
        private const int MaxAutoConnectAttempts = 10;
        private const string StartupRunName = "EC20电话短信工具";

        public MainForm(bool startHidden)
        {
            this.startHidden = startHidden;
            Text = "EC20 电话短信工具";
            Width = 900;
            Height = 680;
            MinimumSize = new Size(760, 560);
            Font = new Font("Segoe UI", 10f);
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EC20电话短信工具");
            smsStorePath = Path.Combine(dataDir, "短信记录.tsv");
            callStorePath = Path.Combine(dataDir, "通话记录.tsv");
            BuildUi();
            LoadLocalData();
            RefreshPorts();
            pollTimer = new System.Windows.Forms.Timer();
            pollTimer.Interval = 1000;
            pollTimer.Tick += delegate { PollModemStatus(); };
            autoConnectTimer = new System.Windows.Forms.Timer();
            autoConnectTimer.Interval = 1000;
            autoConnectTimer.Tick += delegate { AutoConnectTick(); };
            showSignalEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            showSignalTimer = new System.Windows.Forms.Timer();
            showSignalTimer.Interval = 500;
            showSignalTimer.Tick += delegate { CheckShowSignal(); };
            showSignalTimer.Start();
            Shown += delegate
            {
                if (startHidden) HideToTray();
                autoConnectTimer.Start();
            };
        }

        private bool IsConnected
        {
            get { return port != null && port.IsOpen; }
        }

        private void BuildUi()
        {
            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(14);
            root.RowCount = 3;
            root.ColumnCount = 1;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            Controls.Add(root);

            var topRoot = new TableLayoutPanel();
            topRoot.Dock = DockStyle.Fill;
            topRoot.RowCount = 2;
            topRoot.ColumnCount = 1;
            topRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            topRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.Controls.Add(topRoot, 0, 0);

            var top = new FlowLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.WrapContents = true;
            top.AutoScroll = false;
            topRoot.Controls.Add(top, 0, 0);

            var topSecond = new FlowLayoutPanel();
            topSecond.Dock = DockStyle.Fill;
            topSecond.WrapContents = true;
            topSecond.AutoScroll = false;
            topRoot.Controls.Add(topSecond, 0, 1);

            top.Controls.Add(new Label { Text = "AT 端口", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
            portBox = new ComboBox { Width = 220, DropDownStyle = ComboBoxStyle.DropDown };
            top.Controls.Add(portBox);

            refreshButton = new Button { Text = "刷新", Width = 90, Height = 32 };
            refreshButton.Click += delegate { RefreshPorts(); };
            top.Controls.Add(refreshButton);

            connectButton = new Button { Text = "连接", Width = 100, Height = 32 };
            connectButton.Click += delegate { ToggleConnection(); };
            top.Controls.Add(connectButton);

            connectionDot = new Panel { Width = 18, Height = 18, Margin = new Padding(12, 8, 2, 0) };
            connectionDot.Paint += delegate(object sender, PaintEventArgs e) { PaintConnectionDot(e.Graphics); };
            top.Controls.Add(connectionDot);

            connectionTextLabel = new Label { Text = "未连接", AutoSize = true, Padding = new Padding(0, 8, 10, 0) };
            top.Controls.Add(connectionTextLabel);

            signalLabel = new Label { Text = "信号：▁ 0/31", AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            top.Controls.Add(signalLabel);

            startupButton = new Button { Text = "开机自启：检查中", Width = 150, Height = 32 };
            startupButton.Click += delegate { ToggleStartup(); };
            topSecond.Controls.Add(startupButton);

            var audioCheckButton = new Button { Text = "音频检查", Width = 110, Height = 32 };
            audioCheckButton.Click += delegate { CheckAudioDevices(); };
            topSecond.Controls.Add(audioCheckButton);

            var recoverButton = new Button { Text = "重新搜网", Width = 110, Height = 32 };
            recoverButton.Click += delegate { RecoverService(); };
            topSecond.Controls.Add(recoverButton);

            var soundButton = new Button { Text = "声音设置", Width = 130, Height = 32 };
            soundButton.Click += delegate { System.Diagnostics.Process.Start("ms-settings:sound"); };
            topSecond.Controls.Add(soundButton);

            var calls = new FlowLayoutPanel();
            calls.Dock = DockStyle.Top;
            calls.Height = 66;
            calls.WrapContents = true;
            calls.AutoScroll = false;

            calls.Controls.Add(new Label { Text = "号码", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
            numberBox = new TextBox { Width = 250 };
            calls.Controls.Add(numberBox);

            AddButton(calls, "拨号", delegate { Dial(); });
            AddButton(calls, "接听", delegate { AnswerCall(); });
            AddButton(calls, "挂断", delegate { HangUpCall(); });

            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            root.Controls.Add(tabs, 0, 1);

            var phonePage = new TabPage("电话");
            phonePage.Padding = new Padding(8);
            tabs.Controls.Add(phonePage);

            var phoneRoot = new TableLayoutPanel();
            phoneRoot.Dock = DockStyle.Fill;
            phoneRoot.RowCount = 4;
            phoneRoot.ColumnCount = 1;
            phoneRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
            phoneRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            phoneRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            phoneRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            phonePage.Controls.Add(phoneRoot);
            phoneRoot.Controls.Add(calls, 0, 0);

            var phoneHint = new Label
            {
                Text = "通话音频请在 Windows 声音设置中选择 AC Interface 麦克风和扬声器。",
                Dock = DockStyle.Top,
                AutoSize = true,
                Padding = new Padding(2, 8, 0, 0)
            };
            phoneRoot.Controls.Add(phoneHint, 0, 1);

            var callTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = false };
            phoneRoot.Controls.Add(callTools, 0, 2);
            AddButton(callTools, "删除选中", delegate { DeleteSelectedCall(); });
            AddButton(callTools, "刷新列表", delegate { RefreshCallList(); });
            callListView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
            callListView.Columns.Add("开始时间", 150);
            callListView.Columns.Add("方向", 80);
            callListView.Columns.Add("号码", 150);
            callListView.Columns.Add("结果", 100);
            callListView.Columns.Add("时长", 90);
            phoneRoot.Controls.Add(callListView, 0, 3);

            var smsPage = new TabPage("短信");
            smsPage.Padding = new Padding(8);
            tabs.Controls.Add(smsPage);
            var smsRoot = new TableLayoutPanel();
            smsRoot.Dock = DockStyle.Fill;
            smsRoot.RowCount = 4;
            smsRoot.ColumnCount = 1;
            smsRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            smsRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            smsRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            smsRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            smsPage.Controls.Add(smsRoot);
            var smsTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            smsRoot.Controls.Add(smsTools, 0, 0);
            AddButton(smsTools, "读取短信", delegate { ReadSms(); });
            AddButton(smsTools, "删除选中", delegate { DeleteSelectedSms(); });
            AddButton(smsTools, "刷新列表", delegate { RefreshSmsList(); });

            var smsPanel = new TableLayoutPanel();
            smsPanel.Dock = DockStyle.Fill;
            smsPanel.ColumnCount = 3;
            smsPanel.RowCount = 2;
            smsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
            smsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            smsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            smsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            smsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            smsRoot.Controls.Add(smsPanel, 0, 1);

            smsPanel.Controls.Add(new Label { Text = "发送号码", AutoSize = true }, 0, 0);
            var smsNumberPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            smsPanel.Controls.Add(smsNumberPanel, 0, 1);
            smsNumberPanel.Controls.Add(new Label { Text = "号码", AutoSize = true, Padding = new Padding(0, 8, 4, 0) });
            smsNumberBox = new TextBox { Width = 200 };
            smsNumberPanel.Controls.Add(smsNumberBox);
            smsPanel.Controls.Add(new Label { Text = "短信内容", AutoSize = true }, 1, 0);
            smsBox = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical };
            smsPanel.Controls.Add(smsBox, 1, 1);
            var sendSmsButton = new Button { Text = "发送短信", Dock = DockStyle.Fill };
            sendSmsButton.Click += delegate { SendSms(); };
            smsPanel.Controls.Add(sendSmsButton, 2, 1);
            var smsInnerTabs = new TabControl { Dock = DockStyle.Fill };
            smsRoot.Controls.Add(smsInnerTabs, 0, 2);
            var receivedPage = new TabPage("收到的短信");
            var sentPage = new TabPage("发送的短信");
            smsInnerTabs.Controls.Add(receivedPage);
            smsInnerTabs.Controls.Add(sentPage);
            smsListView = CreateSmsListView();
            sentSmsListView = CreateSmsListView();
            receivedPage.Controls.Add(smsListView);
            sentPage.Controls.Add(sentSmsListView);
            smsDetailBox = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };
            smsRoot.Controls.Add(smsDetailBox, 0, 3);

            var atPage = new TabPage("AT信令");
            atPage.Padding = new Padding(8);
            tabs.Controls.Add(atPage);
            var atRoot = new TableLayoutPanel();
            atRoot.Dock = DockStyle.Fill;
            atRoot.RowCount = 2;
            atRoot.ColumnCount = 1;
            atRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            atRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            atPage.Controls.Add(atRoot);
            var atTools = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            atRoot.Controls.Add(atTools, 0, 0);
            AddButton(atTools, "保存日志", delegate { SaveAtLog(); });
            logBox = new TextBox();
            logBox.Dock = DockStyle.Fill;
            logBox.Multiline = true;
            logBox.ScrollBars = ScrollBars.Both;
            logBox.WordWrap = false;
            logBox.Font = new Font("Consolas", 10f);
            atRoot.Controls.Add(logBox, 0, 1);

            statusLabel = new Label { Text = "未连接。请选择 COM4，或点击刷新端口。", Dock = DockStyle.Fill };
            root.Controls.Add(statusLabel, 0, 2);

            notifyIcon = new NotifyIcon();
            notifyIcon.Icon = SystemIcons.Application;
            notifyIcon.Text = "EC20 电话短信工具";
            notifyIcon.Visible = true;
            var trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("打开主界面", null, delegate { ShowMainWindow(); });
            trayMenu.Items.Add("重新连接 EC20", null, delegate { RestartAutoConnect(); });
            trayMenu.Items.Add("退出", null, delegate { ExitApplication(); });
            notifyIcon.ContextMenuStrip = trayMenu;
            notifyIcon.DoubleClick += delegate
            {
                ShowMainWindow();
            };

            UpdateConnectionIndicators(false, -1);
            UpdateStartupButton();
        }

        private void AddButton(Control parent, string text, Action action)
        {
            var button = new Button { Text = text, Width = 90, Height = 32 };
            button.Click += delegate { action(); };
            parent.Controls.Add(button);
        }

        private ListView CreateSmsListView()
        {
            var listView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, HideSelection = false };
            listView.Columns.Add("时间", 150);
            listView.Columns.Add("号码", 160);
            listView.Columns.Add("内容预览", 600);
            listView.SelectedIndexChanged += delegate { ShowSelectedSmsDetail(); };
            listView.DoubleClick += delegate
            {
                if (listView.SelectedItems.Count > 0)
                {
                    var record = listView.SelectedItems[0].Tag as SmsRecord;
                    if (record != null) MessageBox.Show(record.Text, "短信内容", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            return listView;
        }

        private void PaintConnectionDot(Graphics graphics)
        {
            bool ready = IsServiceReady();
            bool searching = IsConnected && simReady && networkSearching && !ready;
            Color color = ready ? Color.FromArgb(35, 170, 75) : (searching ? Color.FromArgb(230, 170, 30) : Color.FromArgb(210, 45, 45));
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(Color.FromArgb(120, 120, 120)))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillEllipse(brush, 2, 2, 14, 14);
                graphics.DrawEllipse(pen, 2, 2, 14, 14);
            }
        }

        private void UpdateConnectionIndicators(bool connected, int signal)
        {
            if (signal >= 0) lastSignal = signal;
            bool ready = connected && IsServiceReady();
            bool searching = connected && simReady && networkSearching && !ready;
            if (connectionTextLabel != null)
            {
                if (ready) connectionTextLabel.Text = "可用";
                else if (!connected) connectionTextLabel.Text = "未连接";
                else if (!simReady) connectionTextLabel.Text = "SIM 未就绪";
                else if (searching) connectionTextLabel.Text = "搜网中";
                else connectionTextLabel.Text = "无服务";
            }
            if (connectionDot != null) connectionDot.Invalidate();
            if (signalLabel != null)
            {
                if (!connected || (!ready && !searching))
                {
                    signalLabel.Text = "信号：--";
                }
                else if (lastSignal < 0 || lastSignal == 99)
                {
                    signalLabel.Text = "信号：? --/31";
                }
                else
                {
                    signalLabel.Text = "信号：" + SignalIcon(lastSignal) + " " + lastSignal + "/31";
                }
            }
        }

        private bool IsServiceReady()
        {
            return IsConnected && simReady && networkReady;
        }

        private string SignalIcon(int signal)
        {
            if (signal <= 5) return "▁";
            if (signal <= 12) return "▂▃";
            if (signal <= 20) return "▂▃▅";
            return "▂▃▅▇";
        }

        private void RefreshPorts()
        {
            var ports = GetPorts();

            portBox.Items.Clear();
            foreach (var name in ports) portBox.Items.Add(name);
            if (ports.Contains("COM4")) portBox.SelectedItem = "COM4";
            else if (portBox.Items.Count > 0) portBox.SelectedIndex = 0;
        }

        private List<string> GetPorts()
        {
            var ports = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Quectel USB AT Port%'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = Convert.ToString(obj["Name"]);
                        var match = Regex.Match(name ?? "", @"\(COM\d+\)");
                        if (match.Success) ports.Add(match.Value.Trim('(', ')'));
                    }
                }
            }
            catch { }

            foreach (var name in SerialPort.GetPortNames())
            {
                if (!ports.Contains(name)) ports.Add(name);
            }

            return ports;
        }

        private void ToggleConnection()
        {
            if (IsConnected)
            {
                Disconnect("已断开连接。");
                return;
            }

            try
            {
                connectButton.Enabled = false;
                connectButton.Text = "连接中";
                statusLabel.Text = "正在连接 EC20，请稍候。";
                Application.DoEvents();
                string info = ConnectToPort(Convert.ToString(portBox.Text), true);
                autoConnectFinished = true;
                ShowNotification("EC20 已连接", info);
            }
            catch (Exception ex)
            {
                connectButton.Enabled = true;
                connectButton.Text = "连接";
                statusLabel.Text = "连接失败。";
                RefreshPorts();
                if (IsPortBusyError(ex))
                {
                    StartReconnectAfterPortLoss("连接失败：端口正在被占用，可能是 EC20 刚拔插后旧连接还没释放。正在重新寻找 EC20。");
                    MessageBox.Show("端口正在被占用，可能是 EC20 刚拔插后 Windows 还没释放旧连接。工具会自动重新寻找 EC20。", "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    MessageBox.Show(ex.Message, "连接失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private string ConnectToPort(string portName, bool collectInfo)
        {
            if (string.IsNullOrWhiteSpace(portName)) throw new InvalidOperationException("请选择 EC20 的 AT 端口。");
            if (IsConnected) Disconnect("重新连接。");
            else ReleasePortOnly();

            var newPort = new SerialPort(portName.Trim(), 115200, Parity.None, 8, StopBits.One);
            newPort.Encoding = Encoding.ASCII;
            newPort.ReadTimeout = 1200;
            newPort.WriteTimeout = 3000;
            newPort.DtrEnable = true;
            newPort.RtsEnable = true;
            newPort.Open();

            string info = "端口：" + newPort.PortName;
            try
            {
                SendCommandAndRead(newPort, "ATE0", 500);
                SendCommandAndRead(newPort, "AT", 800);
                if (collectInfo)
                {
                    info = BuildConnectionInfo(newPort);
                }
            }
            catch
            {
                newPort.Close();
                throw;
            }

            port = newPort;
            port.DataReceived += OnDataReceived;
            connectButton.Text = "断开";
            connectButton.Enabled = true;
            UpdateConnectionIndicators(true, -1);
            statusLabel.Text = "已连接 " + port.PortName + "。通话请在 Windows 声音设置中选择 AC Interface 麦克风和扬声器。";
            Log("已连接 " + port.PortName);
            InitializeModem();
            pollTimer.Start();
            return info;
        }

        private string SendCommandAndRead(SerialPort targetPort, string command, int waitMs)
        {
            targetPort.DiscardInBuffer();
            targetPort.Write(command + "\r");
            Thread.Sleep(waitMs);
            return targetPort.ReadExisting().Replace("\0", "");
        }

        private string BuildConnectionInfo(SerialPort targetPort)
        {
            var lines = new List<string>();
            lines.Add("端口：" + targetPort.PortName);

            string cpin = SendCommandAndRead(targetPort, "AT+CPIN?", 900);
            if (cpin.Contains("READY")) lines.Add("SIM：已就绪");
            else if (cpin.Contains("+CPIN:")) lines.Add("SIM：" + OneLine(cpin));
            ParseServiceStatusFromText(cpin);

            string csq = SendCommandAndRead(targetPort, "AT+CSQ", 900);
            var csqMatch = Regex.Match(csq, @"\+CSQ:\s*(\d+),");
            if (csqMatch.Success)
            {
                int signal;
                if (int.TryParse(csqMatch.Groups[1].Value, out signal)) UpdateConnectionIndicators(true, signal);
                lines.Add("信号：" + csqMatch.Groups[1].Value + "/31");
            }

            string cereg = SendCommandAndRead(targetPort, "AT+CEREG?", 900);
            ParseServiceStatusFromText(cereg);
            string creg = SendCommandAndRead(targetPort, "AT+CREG?", 900);
            ParseServiceStatusFromText(creg);
            string cgreg = SendCommandAndRead(targetPort, "AT+CGREG?", 900);
            ParseServiceStatusFromText(cgreg);

            string cops = SendCommandAndRead(targetPort, "AT+COPS?", 1200);
            ParseServiceStatusFromText(cops);
            var copsMatch = Regex.Match(cops, @"""([^""]+)""");
            if (copsMatch.Success) lines.Add("运营商：" + copsMatch.Groups[1].Value);

            string qnwinfo = SendCommandAndRead(targetPort, "AT+QNWINFO", 1200);
            ParseServiceStatusFromText(qnwinfo);
            var networkMatch = Regex.Match(qnwinfo, @"\+QNWINFO:\s*""([^""]+)""");
            if (networkMatch.Success) lines.Add("网络：" + networkMatch.Groups[1].Value);
            UpdateConnectionIndicators(IsConnected || targetPort.IsOpen, lastSignal);

            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private string OneLine(string text)
        {
            return Regex.Replace(text ?? "", @"\s+", " ").Trim();
        }

        private void Disconnect(string message)
        {
            pollTimer.Stop();
            ReleasePortOnly();
            connectButton.Text = "连接";
            connectButton.Enabled = true;
            simReady = false;
            networkReady = false;
            networkSearching = false;
            lastSignal = -1;
            readingSms = false;
            noServiceTicks = 0;
            recoveryStage = 0;
            UpdateConnectionIndicators(false, -1);
            statusLabel.Text = message;
            Log(message);
        }

        private void ReleasePortOnly()
        {
            if (port != null)
            {
                try { port.DataReceived -= OnDataReceived; } catch { }
                try { if (port.IsOpen) port.Close(); } catch { }
                try { port.Dispose(); } catch { }
                port = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private void LoadLocalData()
        {
            Directory.CreateDirectory(dataDir);
            LoadSmsRecords();
            LoadCallRecords();
            RefreshSmsList();
            RefreshCallList();
        }

        private void LoadSmsRecords()
        {
            smsRecords.Clear();
            if (!File.Exists(smsStorePath)) return;
            foreach (string line in File.ReadAllLines(smsStorePath, Encoding.UTF8))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 6) continue;
                DateTime receivedAt;
                int modemIndex;
                if (!DateTime.TryParse(parts[0], out receivedAt)) receivedAt = DateTime.Now;
                int.TryParse(parts[4], out modemIndex);
                smsRecords.Add(new SmsRecord
                {
                    ReceivedAt = receivedAt,
                    Direction = DecodeField(parts[1]),
                    Number = DecodeField(parts[2]),
                    Text = DecodeField(parts[3]),
                    ModemIndex = modemIndex,
                    Storage = DecodeField(parts[5]),
                    SegmentIndexes = parts.Length >= 7 ? DecodeField(parts[6]) : modemIndex.ToString()
                });
            }
        }

        private void SaveSmsRecords()
        {
            Directory.CreateDirectory(dataDir);
            var lines = new List<string>();
            foreach (var record in smsRecords)
            {
                lines.Add(record.ReceivedAt.ToString("o") + "\t" + EncodeField(record.Direction) + "\t" + EncodeField(record.Number) + "\t" + EncodeField(record.Text) + "\t" + record.ModemIndex + "\t" + EncodeField(record.Storage) + "\t" + EncodeField(record.SegmentIndexes));
            }
            File.WriteAllLines(smsStorePath, lines.ToArray(), Encoding.UTF8);
        }

        private void LoadCallRecords()
        {
            callRecords.Clear();
            if (!File.Exists(callStorePath)) return;
            foreach (string line in File.ReadAllLines(callStorePath, Encoding.UTF8))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 6) continue;
                DateTime startedAt;
                int seconds;
                if (!DateTime.TryParse(parts[0], out startedAt)) startedAt = DateTime.Now;
                int.TryParse(parts[4], out seconds);
                callRecords.Add(new CallRecord
                {
                    StartedAt = startedAt,
                    Direction = DecodeField(parts[1]),
                    Number = DecodeField(parts[2]),
                    Result = DecodeField(parts[3]),
                    DurationSeconds = seconds,
                    Note = DecodeField(parts[5])
                });
            }
        }

        private void SaveCallRecords()
        {
            Directory.CreateDirectory(dataDir);
            var lines = new List<string>();
            foreach (var record in callRecords)
            {
                lines.Add(record.StartedAt.ToString("o") + "\t" + EncodeField(record.Direction) + "\t" + EncodeField(record.Number) + "\t" + EncodeField(record.Result) + "\t" + record.DurationSeconds + "\t" + EncodeField(record.Note));
            }
            File.WriteAllLines(callStorePath, lines.ToArray(), Encoding.UTF8);
        }

        private string EncodeField(string text)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));
        }

        private string DecodeField(string text)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(text ?? "")); }
            catch { return text ?? ""; }
        }

        private void RefreshSmsList()
        {
            if (smsListView == null || sentSmsListView == null) return;
            smsListView.Items.Clear();
            sentSmsListView.Items.Clear();
            for (int i = smsRecords.Count - 1; i >= 0; i--)
            {
                var record = smsRecords[i];
                var target = record.Direction == "发出" ? sentSmsListView : smsListView;
                target.Items.Add(CreateSmsListItem(record));
            }
            ShowSelectedSmsDetail();
        }

        private ListViewItem CreateSmsListItem(SmsRecord record)
        {
            var item = new ListViewItem(record.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(record.Number);
            item.SubItems.Add(record.Text);
            item.Tag = record;
            return item;
        }

        private void ShowSelectedSmsDetail()
        {
            if (smsDetailBox == null) return;
            SmsRecord record = GetSelectedSmsRecord();
            if (record == null)
            {
                smsDetailBox.Text = "";
                return;
            }

            smsDetailBox.Text = "时间：" + record.ReceivedAt.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine
                + "方向：" + record.Direction + Environment.NewLine
                + "号码：" + record.Number + Environment.NewLine
                + "内容：" + Environment.NewLine + record.Text;
        }

        private SmsRecord GetSelectedSmsRecord()
        {
            if (smsListView != null && smsListView.SelectedItems.Count > 0) return smsListView.SelectedItems[0].Tag as SmsRecord;
            if (sentSmsListView != null && sentSmsListView.SelectedItems.Count > 0) return sentSmsListView.SelectedItems[0].Tag as SmsRecord;
            return null;
        }

        private void RefreshCallList()
        {
            if (callListView == null) return;
            callListView.Items.Clear();
            for (int i = callRecords.Count - 1; i >= 0; i--)
            {
                var record = callRecords[i];
                var item = new ListViewItem(record.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                item.SubItems.Add(record.Direction);
                item.SubItems.Add(record.Number);
                item.SubItems.Add(record.Result);
                item.SubItems.Add(FormatDuration(record.DurationSeconds));
                item.Tag = record;
                callListView.Items.Add(item);
            }
        }

        private void DeleteSelectedSms()
        {
            var toDelete = new List<SmsRecord>();
            AddSelectedSmsRecords(toDelete, smsListView);
            AddSelectedSmsRecords(toDelete, sentSmsListView);

            if (toDelete.Count == 0)
            {
                MessageBox.Show("请先选择要删除的短信。", "没有选择");
                return;
            }

            foreach (var record in toDelete)
            {
                smsRecords.Remove(record);
                if (IsConnected && record.ModemIndex > 0)
                {
                    foreach (int index in GetSegmentIndexes(record))
                    {
                        SendCommandSilent("AT+CMGD=" + index);
                    }
                }
            }
            SaveSmsRecords();
            RefreshSmsList();
            statusLabel.Text = "已删除选中的短信。";
        }

        private void AddSelectedSmsRecords(List<SmsRecord> target, ListView listView)
        {
            if (listView == null) return;
            foreach (ListViewItem item in listView.SelectedItems)
            {
                var record = item.Tag as SmsRecord;
                if (record != null && !target.Contains(record)) target.Add(record);
            }
        }

        private void DeleteSelectedCall()
        {
            if (callListView.SelectedItems.Count == 0)
            {
                MessageBox.Show("请先选择要删除的通话记录。", "没有选择");
                return;
            }

            var toDelete = new List<CallRecord>();
            foreach (ListViewItem item in callListView.SelectedItems)
            {
                var record = item.Tag as CallRecord;
                if (record != null) toDelete.Add(record);
            }

            foreach (var record in toDelete) callRecords.Remove(record);
            SaveCallRecords();
            RefreshCallList();
            statusLabel.Text = "已删除选中的通话记录。";
        }

        private void CheckAudioDevices()
        {
            bool foundAudio = false;
            var names = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%AC Interface%' OR Name LIKE '%USB Audio%'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = Convert.ToString(obj["Name"]);
                        if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name))
                        {
                            names.Add(name);
                            if (name.IndexOf("AC Interface", StringComparison.OrdinalIgnoreCase) >= 0) foundAudio = true;
                        }
                    }
                }
            }
            catch
            {
            }

            string message;
            if (foundAudio)
            {
                message = "已检测到 AC Interface 音频设备。" + Environment.NewLine
                    + "如果你已经把 AC Interface 麦克风和扬声器设置为默认通讯设备，EC20 通话通常会走这组设备。" + Environment.NewLine
                    + "默认设备保持原来的扬声器/麦克风是正常的。";
            }
            else
            {
                message = "没有检测到名称包含 AC Interface 的音频设备。" + Environment.NewLine
                    + "请确认 EC20 已启用 UAC 音频、设备管理器中有对应音频设备，并重新插拔模块。";
            }

            if (names.Count > 0)
            {
                message += Environment.NewLine + Environment.NewLine + "检测到的相关设备：" + Environment.NewLine + string.Join(Environment.NewLine, names.ToArray());
            }

            MessageBox.Show(message, "EC20 音频检查", MessageBoxButtons.OK, foundAudio ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void RecoverService()
        {
            if (!IsConnected)
            {
                MessageBox.Show("请先连接 EC20 的 AT 端口。", "未连接");
                return;
            }

            noServiceTicks = 0;
            recoveryStage = 2;
            networkSearching = true;
            statusLabel.Text = "正在恢复 EC20 电话/短信服务。";
            UpdateConnectionIndicators(true, lastSignal);
            Log("正在恢复 EC20 电话/短信服务。");
            SendCommandSilent("AT+CPIN?");
            SendCommandSilent("AT+QSIMDET=1,1");
            SendCommandSilent("AT+COPS=0");
            ResetRadioAndSearchNetwork();
        }

        private void ResetRadioAndSearchNetwork()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!IsConnected) return;
                    SendCommandSilent("AT+CFUN=0");
                    Thread.Sleep(2500);
                    if (!IsConnected) return;
                    SendCommandSilent("AT+CFUN=1");
                    Thread.Sleep(3500);
                    if (!IsConnected) return;
                    SendCommandSilent("ATE0");
                    SendCommandSilent("AT+CMEE=2");
                    SendCommandSilent("AT+CMGF=1");
                    SendCommandSilent("AT+CSCS=\"GSM\"");
                    SendCommandSilent("AT+CNMI=2,1,0,0,0");
                    SendCommandSilent("AT+CLIP=1");
                    SendCommandSilent("AT+COLP=1");
                    SendCommandSilent("AT+QPCMV=1,2");
                    SendCommandSilent("AT+CLVL=5");
                    SendCommandSilent("AT+COPS=0");
                    QueryStatusSilent();
                }
                catch
                {
                }
            });
        }

        private string FormatDuration(int seconds)
        {
            if (seconds < 0) seconds = 0;
            return (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
        }

        private int[] GetSegmentIndexes(SmsRecord record)
        {
            var indexes = new List<int>();
            string value = string.IsNullOrWhiteSpace(record.SegmentIndexes) ? record.ModemIndex.ToString() : record.SegmentIndexes;
            foreach (string part in value.Split(','))
            {
                int index;
                if (int.TryParse(part.Trim(), out index) && index > 0 && !indexes.Contains(index)) indexes.Add(index);
            }
            return indexes.ToArray();
        }

        private void AutoConnectTick()
        {
            if (autoConnectFinished || IsConnected)
            {
                autoConnectTimer.Stop();
                return;
            }

            autoConnectAttempts++;
            RefreshPorts();
            string candidate = FindPreferredAtPort();
            statusLabel.Text = "正在后台寻找并连接 EC20，第 " + autoConnectAttempts + " 次。";
            if (!string.IsNullOrEmpty(candidate))
            {
                try
                {
                    connectButton.Enabled = false;
                    connectButton.Text = "连接中";
                    if (!portBox.Items.Contains(candidate)) portBox.Items.Add(candidate);
                    portBox.SelectedItem = candidate;
                    string info = ConnectToPort(candidate, true);
                    autoConnectFinished = true;
                    autoConnectTimer.Stop();
                    ShowNotification("EC20 连接成功", info);
                    return;
                }
                catch (Exception ex)
                {
                    connectButton.Enabled = true;
                    connectButton.Text = "连接";
                    Log("自动连接失败：" + ex.Message);
                }
            }

            statusLabel.Text = "正在后台寻找 EC20 AT 端口，第 " + autoConnectAttempts + " 次。";
            if (autoConnectAttempts >= MaxAutoConnectAttempts)
            {
                autoConnectFinished = true;
                autoConnectTimer.Stop();
                string message = "已尝试 " + MaxAutoConnectAttempts + " 次，仍未找到可用的 EC20 AT 端口。请检查模块供电、USB 线和驱动。";
                statusLabel.Text = message;
                ShowNotification("EC20 连接失败", message);
                MessageBox.Show(message, "EC20 连接失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else
            {
                autoConnectTimer.Interval = 10000;
            }
        }

        private string FindPreferredAtPort()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Name LIKE '%Quectel USB AT Port%'"))
                {
                    foreach (ManagementObject obj in searcher.Get())
                    {
                        string name = Convert.ToString(obj["Name"]);
                        var match = Regex.Match(name ?? "", @"\(COM\d+\)");
                        if (match.Success) return match.Value.Trim('(', ')');
                    }
                }
            }
            catch { }

            if (portBox.Items.Contains("COM4")) return "COM4";
            return portBox.Items.Count > 0 ? Convert.ToString(portBox.Items[0]) : "";
        }

        private void RestartAutoConnect()
        {
            Disconnect("准备重新连接 EC20。");
            autoConnectAttempts = 0;
            autoConnectFinished = false;
            autoConnectTimer.Interval = 1000;
            autoConnectTimer.Start();
            ShowNotification("EC20 正在连接", "正在重新寻找 EC20 AT 端口。");
        }

        private void InitializeModem()
        {
            SendCommandSilent("ATE0");
            SendCommandSilent("AT+CMEE=2");
            SendCommandSilent("AT+CMGF=1");
            SendCommandSilent("AT+CSCS=\"GSM\"");
            SendCommandSilent("AT+CNMI=2,1,0,0,0");
            SendCommandSilent("AT+CLIP=1");
            SendCommandSilent("AT+COLP=1");
            SendCommandSilent("AT+QPCMV=1,2");
            SendCommandSilent("AT+CLVL=5");
            QueryStatus();
        }

        private void QueryStatus()
        {
            SendCommand("AT+CPIN?");
            SendCommand("AT+CSQ");
            SendCommand("AT+CREG?");
            SendCommand("AT+CGREG?");
            SendCommand("AT+CEREG?");
            SendCommand("AT+COPS?");
            SendCommand("AT+QNWINFO");
            SendCommand("AT+CPMS?");
            SendCommand("AT+CLCC");
        }

        private void QueryStatusSilent()
        {
            SendCommandSilent("AT+CPIN?");
            SendCommandSilent("AT+CSQ");
            SendCommandSilent("AT+CREG?");
            SendCommandSilent("AT+CGREG?");
            SendCommandSilent("AT+CEREG?");
            SendCommandSilent("AT+COPS?");
            SendCommandSilent("AT+QNWINFO");
        }

        private void PollModemStatus()
        {
            if (!IsConnected)
            {
                UpdateConnectionIndicators(false, -1);
                return;
            }

            try
            {
                if (!ConnectedPortStillPresent())
                {
                    StartReconnectAfterPortLoss("EC20 已拔出，正在等待重新插入。");
                    return;
                }

                SendCommandSilent("AT+CLCC");
                statusPollTicks++;
                if (statusPollTicks == 1 || statusPollTicks >= 5)
                {
                    statusPollTicks = 0;
                    QueryStatusSilent();
                }
                AutoRecoverNoService();
            }
            catch (Exception ex)
            {
                if (IsPortBusyError(ex) || ex is IOException || ex is InvalidOperationException)
                {
                    StartReconnectAfterPortLoss("EC20 已断开或端口暂时不可用，正在重新寻找。");
                }
                else
                {
                    throw;
                }
            }
        }

        private bool IsPortBusyError(Exception ex)
        {
            if (ex == null) return false;
            return ex is UnauthorizedAccessException
                || ex is IOException
                || (ex.Message != null && (ex.Message.Contains("正在使用") || ex.Message.Contains("不存在") || ex.Message.Contains("access") || ex.Message.Contains("denied")));
        }

        private bool ConnectedPortStillPresent()
        {
            if (port == null) return false;
            string portName = port.PortName;
            if (string.IsNullOrEmpty(portName)) return false;
            try
            {
                foreach (string name in SerialPort.GetPortNames())
                {
                    if (string.Equals(name, portName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch
            {
                return false;
            }
            return false;
        }

        private void StartReconnectAfterPortLoss(string message)
        {
            pollTimer.Stop();
            ReleasePortOnly();
            connectButton.Text = "连接";
            connectButton.Enabled = true;
            simReady = false;
            networkReady = false;
            networkSearching = false;
            lastSignal = -1;
            readingSms = false;
            noServiceTicks = 0;
            recoveryStage = 0;
            UpdateConnectionIndicators(false, -1);
            statusLabel.Text = message;
            Log(message);
            RefreshPorts();
            autoConnectAttempts = 0;
            autoConnectFinished = false;
            autoConnectTimer.Interval = 1000;
            autoConnectTimer.Start();
        }

        private void AutoRecoverNoService()
        {
            if (!IsConnected || !simReady)
            {
                noServiceTicks = 0;
                recoveryStage = 0;
                return;
            }

            if (networkReady)
            {
                noServiceTicks = 0;
                recoveryStage = 0;
                return;
            }

            noServiceTicks++;
            if (noServiceTicks == 8 && recoveryStage < 1)
            {
                recoveryStage = 1;
                statusLabel.Text = "SIM 已就绪，正在重新搜网。";
                networkSearching = true;
                UpdateConnectionIndicators(true, lastSignal);
                SendCommandSilent("AT+COPS=0");
                QueryStatusSilent();
            }
            else if (noServiceTicks == 25 && recoveryStage < 2)
            {
                recoveryStage = 2;
                statusLabel.Text = "仍无服务，正在重启 EC20 射频后重新搜网。";
                networkSearching = true;
                UpdateConnectionIndicators(true, lastSignal);
                ResetRadioAndSearchNetwork();
            }
            else if (noServiceTicks == 50 && recoveryStage < 3)
            {
                recoveryStage = 3;
                statusLabel.Text = "仍无服务，请确认 SIM 卡已插好、未欠费，并检查天线/信号。";
                ShowNotification("EC20 仍无服务", "SIM 已就绪但网络未注册，请检查 SIM 卡、天线和当前位置信号。");
            }
        }

        private void Dial()
        {
            string number = Regex.Replace(numberBox.Text, @"[^\d+*#]", "");
            if (number.Length == 0)
            {
                MessageBox.Show("请先输入电话号码。", "缺少号码");
                return;
            }
            lastCallerNumber = number;
            StartCallHistory(number, "拨出");
            ShowCallPopup(number, false);
            SendCommand("ATD" + number + ";");
        }

        private void SendSms()
        {
            string rawNumber = smsNumberBox == null || string.IsNullOrWhiteSpace(smsNumberBox.Text) ? numberBox.Text : smsNumberBox.Text;
            string number = Regex.Replace(rawNumber, @"[^\d+]", "");
            if (number.Length == 0)
            {
                MessageBox.Show("请先输入电话号码。", "缺少号码");
                return;
            }
            if (smsBox.Text.Length == 0)
            {
                MessageBox.Show("请先输入短信内容。", "缺少短信内容");
                return;
            }
            if (!IsConnected) return;
            waitingForSmsPrompt = true;
            Log(">> AT+CMGS=\"" + number + "\"");
            SendCommandSilent("AT+CSCS=\"GSM\"");
            port.Write("AT+CMGS=\"" + number + "\"\r");
            ThreadPool.QueueUserWorkItem(delegate
            {
                Thread.Sleep(900);
                if (waitingForSmsPrompt && IsConnected)
                {
                    port.Write(smsBox.Text + char.ConvertFromUtf32(26));
                    AddSmsRecord("发出", number, smsBox.Text, 0, "本机");
                    waitingForSmsPrompt = false;
                    BeginInvoke((Action)(delegate
                    {
                        Log(">> [短信内容已发送]");
                        RefreshSmsList();
                    }));
                }
            });
        }

        private void ReadSms()
        {
            if (!IsConnected)
            {
                MessageBox.Show("请先连接 EC20 的 AT 端口。", "未连接");
                return;
            }
            readingSms = true;
            statusLabel.Text = "正在读取短信。";
            SendCommand("AT+CMGL=\"ALL\"");
        }

        private void SendCommand(string command)
        {
            if (!IsConnected)
            {
                MessageBox.Show("请先连接 EC20 的 AT 端口。", "未连接");
                return;
            }
            Log(">> " + command);
            port.Write(command + "\r");
        }

        private void AnswerCall()
        {
            SendCommand("ATA");
            if (string.IsNullOrEmpty(currentCallDirection)) StartCallHistory(lastCallerNumber, "来电");
            MarkCallActive();
            if (callPopup != null) callPopup.SetActive();
        }

        private void HangUpCall()
        {
            if (!IsConnected)
            {
                MessageBox.Show("请先连接 EC20 的 AT 端口。", "未连接");
                return;
            }
            Log(">> ATH");
            port.Write("ATH\r");
            FinishCallHistory("已挂断");
            if (callPopup != null) callPopup.ClosePopup();
        }

        private void SendCommandSilent(string command)
        {
            if (!IsConnected) return;
            port.Write(command + "\r");
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                string text = port.ReadExisting().Replace("\0", "");
                BeginInvoke((Action)(delegate
                {
                    Log(text);
                    AppendAndParseSerialText(text);
                    ParseServiceStatusFromText(text);
                    UpdateSignalFromText(text);
                    HandleCallAndSmsEvents(text);
                }));
            }
            catch { }
        }

        private void UpdateSignalFromText(string text)
        {
            var match = Regex.Match(text ?? "", @"\+CSQ:\s*(\d+),");
            if (!match.Success) return;
            int signal;
            if (int.TryParse(match.Groups[1].Value, out signal)) UpdateConnectionIndicators(IsConnected, signal);
        }

        private void ParseServiceStatusFromText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string upper = text.ToUpperInvariant();

            if (upper.Contains("+CPIN: READY")) simReady = true;
            else if (upper.Contains("+CPIN:"))
            {
                simReady = false;
                networkReady = false;
                networkSearching = false;
            }

            if (upper.Contains("NO SERVICE") && !networkReady)
            {
                networkReady = false;
                networkSearching = false;
            }

            var cops = Regex.Match(text, @"\+COPS:\s*(\d+)");
            if (cops.Success && cops.Groups[1].Value == "0" && !text.Contains("\"") && !networkReady)
            {
                networkReady = false;
                networkSearching = true;
            }
            else if (cops.Success && text.Contains("\""))
            {
                networkReady = true;
                networkSearching = false;
            }

            foreach (Match match in Regex.Matches(text, @"\+(?:CEREG|CREG|CGREG):\s*(?:\d+,)?(\d+)"))
            {
                string value = match.Groups[1].Value;
                if (value == "1" || value == "5")
                {
                    networkReady = true;
                    networkSearching = false;
                }
                else if (value == "2")
                {
                    networkReady = false;
                    networkSearching = true;
                }
                else if (value == "0" || value == "3" || value == "4")
                {
                    networkReady = false;
                    networkSearching = false;
                }
            }

            var qnwinfo = Regex.Match(text, @"\+QNWINFO:\s*""([^""]*)""");
            if (qnwinfo.Success)
            {
                string network = qnwinfo.Groups[1].Value.ToUpperInvariant();
                if (network.Length > 0 && network != "NO SERVICE" && network != "NONE")
                {
                    networkReady = true;
                    networkSearching = false;
                }
                else if (!networkReady)
                {
                    networkSearching = network != "NO SERVICE" && network != "NONE";
                }
            }

            if (networkReady)
            {
                noServiceTicks = 0;
                recoveryStage = 0;
                networkSearching = false;
            }

            UpdateConnectionIndicators(IsConnected, lastSignal);
        }

        private void AppendAndParseSerialText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            serialReceiveBuffer += text.Replace("\r", "");
            if (serialReceiveBuffer.Length > 120000)
            {
                serialReceiveBuffer = serialReceiveBuffer.Substring(serialReceiveBuffer.Length - 60000);
            }

            bool looksComplete = serialReceiveBuffer.Contains("\nOK")
                || serialReceiveBuffer.Contains("\nERROR")
                || serialReceiveBuffer.Contains("\n+CME ERROR")
                || serialReceiveBuffer.Contains("\n+CMS ERROR");

            if (serialReceiveBuffer.Contains("+CMGL:") && looksComplete)
            {
                LogDecodedSmsLines(serialReceiveBuffer);
                ParseSmsList(serialReceiveBuffer);
                if (readingSms && ContainsAtError(serialReceiveBuffer))
                {
                    statusLabel.Text = "短信读取失败，EC20 返回 ERROR。";
                    readingSms = false;
                }
                serialReceiveBuffer = "";
            }
            else if (!serialReceiveBuffer.Contains("+CMGL:") && looksComplete)
            {
                if (readingSms && ContainsAtError(serialReceiveBuffer))
                {
                    statusLabel.Text = "短信读取失败，EC20 返回 ERROR。";
                    readingSms = false;
                }
                serialReceiveBuffer = "";
            }
        }

        private bool ContainsAtError(string text)
        {
            string upper = (text ?? "").ToUpperInvariant();
            return upper.Contains("\nERROR") || upper.Contains("+CME ERROR") || upper.Contains("+CMS ERROR");
        }

        private void HandleCallAndSmsEvents(string text)
        {
            string clipNumber = ExtractClipNumber(text);
            if (!string.IsNullOrEmpty(clipNumber))
            {
                lastCallerNumber = clipNumber;
                if (!string.IsNullOrEmpty(currentCallDirection) && currentCallNumber == "未知号码") currentCallNumber = clipNumber;
                if (callPopup != null) callPopup.SetNumber(clipNumber);
            }

            string clccNumber = ExtractClccNumber(text);
            if (!string.IsNullOrEmpty(clccNumber))
            {
                lastCallerNumber = clccNumber;
                if (!string.IsNullOrEmpty(currentCallDirection) && currentCallNumber == "未知号码") currentCallNumber = clccNumber;
                if (callPopup != null) callPopup.SetNumber(clccNumber);
            }

            if (text.Contains("RING"))
            {
                string number = string.IsNullOrEmpty(lastCallerNumber) ? "未知号码" : lastCallerNumber;
                if (string.IsNullOrEmpty(currentCallDirection)) StartCallHistory(number, "来电");
                statusLabel.Text = "有来电。点击“接听”或“挂断”。";
                ShowNotification("EC20 来电", "来电号码：" + number);
                ShowCallPopup(number, true);
            }

            if (text.Contains("+CMTI:"))
            {
                statusLabel.Text = "收到新短信，正在读取。";
                ShowNotification("EC20 新短信", "收到新短信，正在读取并保存到本机。");
                ReadSms();
            }

            if (text.Contains("NO CARRIER") || text.Contains("BUSY") || text.Contains("NO ANSWER"))
            {
                statusLabel.Text = "通话已结束。";
                if (text.Contains("BUSY")) FinishCallHistory("对方忙");
                else if (text.Contains("NO ANSWER")) FinishCallHistory("未接听");
                else FinishCallHistory(currentCallActive ? "已结束" : "未接/取消");
                if (callPopup != null) callPopup.ClosePopup();
            }
        }

        private void ParseSmsList(string text)
        {
            string normalized = text.Replace("\r", "");
            var matches = Regex.Matches(normalized, @"\+CMGL:\s*(\d+),""([^""]*)"",""([^""]*)"",[^,\n]*(?:,""([^""]*)"")?[^\n]*\n(.*?)(?=\n\+CMGL:|\nOK|\nERROR|\n\+CME ERROR|\n\+CMS ERROR|\z)", RegexOptions.Singleline);
            foreach (Match match in matches)
            {
                int modemIndex;
                int.TryParse(match.Groups[1].Value, out modemIndex);
                string status = match.Groups[2].Value;
                string number = match.Groups[3].Value;
                string timeText = match.Groups[4].Value;
                string body = CleanSmsBody(match.Groups[5].Value);
                string decoded = TryDecodeUcs2(body);
                if (!string.IsNullOrWhiteSpace(decoded)) body = decoded;
                DateTime receivedAt = ParseModemTime(timeText);
                AddSmsRecord(status.Contains("UNSENT") || status.Contains("SENT") ? "发出" : "收到", number, body, modemIndex, "EC20", receivedAt);
            }
            if (matches.Count > 0)
            {
                SaveSmsRecords();
                RefreshSmsList();
                statusLabel.Text = "短信读取完成，已保存到本机。";
                readingSms = false;
            }
            else if (readingSms && !ContainsAtError(text) && text.Contains("OK"))
            {
                statusLabel.Text = "短信读取完成，没有新的短信。";
                readingSms = false;
            }
        }

        private string CleanSmsBody(string body)
        {
            var lines = new List<string>();
            foreach (string rawLine in (body ?? "").Replace("\r", "").Split('\n'))
            {
                string line = rawLine.TrimEnd();
                if (line.Length == 0) continue;
                if (line == "OK" || line == "ERROR") continue;
                if (line.StartsWith("+CME ERROR") || line.StartsWith("+CMS ERROR")) continue;
                lines.Add(line);
            }
            return string.Join(Environment.NewLine, lines.ToArray()).Trim();
        }

        private DateTime ParseModemTime(string text)
        {
            var match = Regex.Match(text ?? "", @"(\d\d)/(\d\d)/(\d\d),(\d\d):(\d\d):(\d\d)");
            if (!match.Success) return DateTime.Now;
            try
            {
                int year = 2000 + int.Parse(match.Groups[1].Value);
                int month = int.Parse(match.Groups[2].Value);
                int day = int.Parse(match.Groups[3].Value);
                int hour = int.Parse(match.Groups[4].Value);
                int minute = int.Parse(match.Groups[5].Value);
                int second = int.Parse(match.Groups[6].Value);
                return new DateTime(year, month, day, hour, minute, second);
            }
            catch
            {
                return DateTime.Now;
            }
        }

        private void AddSmsRecord(string direction, string number, string body, int modemIndex, string storage)
        {
            AddSmsRecord(direction, number, body, modemIndex, storage, DateTime.Now);
        }

        private void AddSmsRecord(string direction, string number, string body, int modemIndex, string storage, DateTime receivedAt)
        {
            if (string.IsNullOrWhiteSpace(body)) return;
            foreach (var existing in smsRecords)
            {
                if (modemIndex > 0 && existing.Storage == storage && Array.IndexOf(GetSegmentIndexes(existing), modemIndex) >= 0) return;
                if (existing.Number == number && existing.Text == body && Math.Abs((existing.ReceivedAt - receivedAt).TotalSeconds) < 5) return;
            }

            smsRecords.Add(new SmsRecord
            {
                ReceivedAt = receivedAt,
                Direction = direction,
                Number = string.IsNullOrWhiteSpace(number) ? "未知号码" : number,
                Text = body,
                ModemIndex = modemIndex,
                Storage = storage,
                SegmentIndexes = modemIndex > 0 ? modemIndex.ToString() : ""
            });
            SaveSmsRecords();
        }

        private void MergeAdjacentSmsSegments()
        {
            if (smsRecords.Count < 2) return;
            smsRecords.Sort(delegate(SmsRecord a, SmsRecord b)
            {
                int result = a.ReceivedAt.CompareTo(b.ReceivedAt);
                if (result != 0) return result;
                return FirstSegmentIndex(a).CompareTo(FirstSegmentIndex(b));
            });
            var merged = new List<SmsRecord>();
            foreach (var record in smsRecords)
            {
                if (merged.Count == 0)
                {
                    merged.Add(record);
                    continue;
                }

                var previous = merged[merged.Count - 1];
                bool sameSender = string.Equals(previous.Number, record.Number, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(previous.Direction, record.Direction, StringComparison.OrdinalIgnoreCase);
                bool closeTime = Math.Abs((record.ReceivedAt - previous.ReceivedAt).TotalSeconds) <= 20;
                bool likelySegment = previous.Text.Length >= 55 || record.Text.Length >= 55;

                if (sameSender && closeTime && likelySegment && !previous.Text.Contains(record.Text))
                {
                    previous.Text = previous.Text + record.Text;
                    previous.SegmentIndexes = JoinIndexes(previous.SegmentIndexes, record.SegmentIndexes, record.ModemIndex);
                }
                else
                {
                    merged.Add(record);
                }
            }
            smsRecords.Clear();
            smsRecords.AddRange(merged);
        }

        private int FirstSegmentIndex(SmsRecord record)
        {
            int best = record.ModemIndex > 0 ? record.ModemIndex : int.MaxValue;
            foreach (int index in GetSegmentIndexes(record))
            {
                if (index > 0 && index < best) best = index;
            }
            return best;
        }

        private string JoinIndexes(string existing, string incoming, int incomingIndex)
        {
            var values = new List<string>();
            foreach (string part in (existing ?? "").Split(','))
            {
                string value = part.Trim();
                if (value.Length > 0 && !values.Contains(value)) values.Add(value);
            }
            foreach (string part in (incoming ?? "").Split(','))
            {
                string value = part.Trim();
                if (value.Length > 0 && !values.Contains(value)) values.Add(value);
            }
            if (incomingIndex > 0 && !values.Contains(incomingIndex.ToString())) values.Add(incomingIndex.ToString());
            return string.Join(",", values.ToArray());
        }

        private void StartCallHistory(string number, string direction)
        {
            currentCallNumber = string.IsNullOrWhiteSpace(number) ? "未知号码" : number;
            currentCallDirection = direction;
            currentCallStartedAt = DateTime.Now;
            currentCallActive = false;
        }

        private void MarkCallActive()
        {
            if (string.IsNullOrEmpty(currentCallDirection)) StartCallHistory(lastCallerNumber, "通话");
            currentCallActive = true;
        }

        private void FinishCallHistory(string result)
        {
            if (string.IsNullOrEmpty(currentCallDirection)) return;
            int seconds = currentCallActive ? (int)Math.Max(0, (DateTime.Now - currentCallStartedAt).TotalSeconds) : 0;
            callRecords.Add(new CallRecord
            {
                StartedAt = currentCallStartedAt,
                Direction = currentCallDirection,
                Number = currentCallNumber,
                Result = result,
                DurationSeconds = seconds,
                Note = ""
            });
            SaveCallRecords();
            RefreshCallList();
            currentCallNumber = "";
            currentCallDirection = "";
            currentCallActive = false;
        }

        private string ExtractClipNumber(string text)
        {
            var match = Regex.Match(text, @"\+CLIP:\s*""([^""]+)""");
            return match.Success ? match.Groups[1].Value : "";
        }

        private string ExtractClccNumber(string text)
        {
            var match = Regex.Match(text, @"\+CLCC:\s*\d+,\d+,\d+,\d+,\d+,""([^""]*)""");
            return match.Success ? match.Groups[1].Value : "";
        }

        private void ShowNotification(string title, string message)
        {
            if (notifyIcon == null) return;
            notifyIcon.BalloonTipTitle = title;
            notifyIcon.BalloonTipText = message;
            notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
            notifyIcon.ShowBalloonTip(8000);
        }

        private void ShowMainWindow()
        {
            ShowInTaskbar = true;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        private void CheckShowSignal()
        {
            try
            {
                if (showSignalEvent != null && showSignalEvent.WaitOne(0))
                {
                    ShowMainWindow();
                }
            }
            catch
            {
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeMethods.ShowMainWindowMessage)
            {
                if (m.WParam != IntPtr.Zero) ShowMainWindow();
                return;
            }

            base.WndProc(ref m);
        }

        private void HideToTray()
        {
            ShowInTaskbar = false;
            Hide();
        }

        private void ExitApplication()
        {
            allowExit = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideToTray();
                ShowNotification("EC20 已在后台运行", "来电、短信和连接状态会继续提醒。");
                return;
            }

            if (notifyIcon != null) notifyIcon.Visible = false;
            if (notifyIcon != null) notifyIcon.Dispose();
            if (showSignalTimer != null) showSignalTimer.Stop();
            if (showSignalEvent != null) showSignalEvent.Dispose();
            if (port != null && port.IsOpen) port.Close();
            base.OnFormClosing(e);
        }

        private void ToggleStartup()
        {
            try
            {
                if (IsStartupEnabled()) DisableStartup();
                else EnableStartup();
                UpdateStartupButton();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "开机自启设置失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void UpdateStartupButton()
        {
            if (startupButton == null) return;
            startupButton.Text = IsStartupEnabled() ? "开机自启：已开启" : "开机自启：已关闭";
        }

        private bool IsStartupEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
            {
                string value = key == null ? "" : Convert.ToString(key.GetValue(StartupRunName));
                return !string.IsNullOrEmpty(value);
            }
        }

        private void EnableStartup()
        {
            string exePath = Application.ExecutablePath;
            string value = "\"" + exePath + "\" /background";
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                key.SetValue(StartupRunName, value);
            }
        }

        private void DisableStartup()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (key != null) key.DeleteValue(StartupRunName, false);
            }
        }

        private void ShowCallPopup(string number, bool incoming)
        {
            if (callPopup == null || callPopup.IsDisposed)
            {
                callPopup = new CallPopupForm(AnswerCall, HangUpCall);
            }
            callPopup.SetNumber(number);
            callPopup.SetIncoming(incoming);
            callPopup.Show();
            callPopup.Activate();
        }

        private void Log(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " " + text.TrimEnd() + Environment.NewLine);
            logBox.SelectionStart = logBox.TextLength;
            logBox.ScrollToCaret();
        }

        private void SaveAtLog()
        {
            string logText = logBox == null ? "" : logBox.Text;
            if (string.IsNullOrWhiteSpace(logText))
            {
                MessageBox.Show("当前还没有 AT 信令日志可保存。", "保存日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string defaultName = "AT信令日志_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt";
            string appDir = Path.GetDirectoryName(Application.ExecutablePath);
            if (string.IsNullOrEmpty(appDir)) appDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string logPath = Path.Combine(appDir, defaultName);
            File.WriteAllText(logPath, logText, Encoding.UTF8);
            statusLabel.Text = "AT 信令日志已保存：" + logPath;
            MessageBox.Show("AT 信令日志已保存到程序同目录。", "保存日志", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void LogDecodedSmsLines(string text)
        {
            foreach (var rawLine in text.Replace("\r", "").Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length < 8 || (line.Length % 4) != 0) continue;
                if (!Regex.IsMatch(line, @"\A[0-9A-Fa-f]+\z")) continue;
                string decoded = TryDecodeUcs2(line);
                if (!string.IsNullOrWhiteSpace(decoded))
                {
                    logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + " 短信内容: " + decoded + Environment.NewLine);
                }
            }
        }

        private string TryDecodeUcs2(string hex)
        {
            try
            {
                var chars = new List<char>();
                for (int i = 0; i + 3 < hex.Length; i += 4)
                {
                    int value = Convert.ToInt32(hex.Substring(i, 4), 16);
                    if (value == 0) continue;
                    chars.Add((char)value);
                }
                string decoded = new string(chars.ToArray());
                int readable = 0;
                foreach (char c in decoded)
                {
                    if (!char.IsControl(c)) readable++;
                }
                return readable >= Math.Max(2, decoded.Length / 2) ? decoded : "";
            }
            catch
            {
                return "";
            }
        }
    }

    public class SmsRecord
    {
        public DateTime ReceivedAt { get; set; }
        public string Direction { get; set; }
        public string Number { get; set; }
        public string Text { get; set; }
        public int ModemIndex { get; set; }
        public string Storage { get; set; }
        public string SegmentIndexes { get; set; }
    }

    public class CallRecord
    {
        public DateTime StartedAt { get; set; }
        public string Direction { get; set; }
        public string Number { get; set; }
        public string Result { get; set; }
        public int DurationSeconds { get; set; }
        public string Note { get; set; }
    }

    public class CallPopupForm : Form
    {
        private readonly Action answerAction;
        private readonly Action hangUpAction;
        private readonly Label titleLabel;
        private readonly Label numberLabel;
        private readonly Label durationLabel;
        private readonly Button answerButton;
        private readonly Button hangUpButton;
        private readonly System.Windows.Forms.Timer durationTimer;
        private DateTime callStartTime;
        private bool active;

        public CallPopupForm(Action answerAction, Action hangUpAction)
        {
            this.answerAction = answerAction;
            this.hangUpAction = hangUpAction;
            Text = "EC20 通话";
            Width = 360;
            Height = 220;
            MinimumSize = new Size(340, 200);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            Font = new Font("Segoe UI", 11f);

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(16);
            root.RowCount = 4;
            root.ColumnCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            titleLabel = new Label { Text = "来电", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), AutoSize = false };
            root.Controls.Add(titleLabel, 0, 0);
            root.SetColumnSpan(titleLabel, 2);

            numberLabel = new Label { Text = "未知号码", Dock = DockStyle.Fill, AutoSize = false };
            root.Controls.Add(numberLabel, 0, 1);
            root.SetColumnSpan(numberLabel, 2);

            durationLabel = new Label { Text = "通话时长：00:00", Dock = DockStyle.Fill, AutoSize = false };
            root.Controls.Add(durationLabel, 0, 2);
            root.SetColumnSpan(durationLabel, 2);

            answerButton = new Button { Text = "接听", Dock = DockStyle.Fill, Height = 40 };
            answerButton.Click += delegate { answerAction(); SetActive(); };
            root.Controls.Add(answerButton, 0, 3);

            hangUpButton = new Button { Text = "挂断", Dock = DockStyle.Fill, Height = 40 };
            hangUpButton.Click += delegate { hangUpAction(); };
            root.Controls.Add(hangUpButton, 1, 3);

            durationTimer = new System.Windows.Forms.Timer();
            durationTimer.Interval = 1000;
            durationTimer.Tick += delegate { UpdateDuration(); };
        }

        public void SetNumber(string number)
        {
            numberLabel.Text = "号码：" + (string.IsNullOrEmpty(number) ? "未知号码" : number);
        }

        public void SetIncoming(bool incoming)
        {
            titleLabel.Text = incoming ? "来电" : "正在拨号";
            if (!incoming) SetActive();
        }

        public void SetActive()
        {
            if (!active)
            {
                active = true;
                callStartTime = DateTime.Now;
                durationTimer.Start();
            }
            titleLabel.Text = "通话中";
            answerButton.Enabled = false;
            UpdateDuration();
        }

        public void ClosePopup()
        {
            durationTimer.Stop();
            active = false;
            Hide();
        }

        private void UpdateDuration()
        {
            if (!active)
            {
                durationLabel.Text = "通话时长：00:00";
                return;
            }
            TimeSpan elapsed = DateTime.Now - callStartTime;
            durationLabel.Text = "通话时长：" + ((int)elapsed.TotalMinutes).ToString("00") + ":" + elapsed.Seconds.ToString("00");
        }
    }
}
