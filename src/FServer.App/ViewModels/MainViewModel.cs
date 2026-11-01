using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using FServer.App.Interop;
using FServer.App.Services;

namespace FServer.App.ViewModels
{
    public class UserEntryVm : INotifyPropertyChanged
    {
        private string _name;
        private string _password;
        private bool _readOnly;
        private string _rootPath;
        private bool _enabled = true;

        public string Name
        {
            get => _name; set { _name = value; OnPropertyChanged(); }
        }
        public string Password
        {
            get => _password; set { _password = value; OnPropertyChanged(); }
        }
        public bool ReadOnly
        {
            get => _readOnly; set { _readOnly = value; OnPropertyChanged(); }
        }
        /// <summary>
        /// 用户专属根路径；空/NULL 表示退回到服务器全局默认根目录。
        /// </summary>
        public string RootPath
        {
            get => _rootPath; set { _rootPath = value; OnPropertyChanged(); }
        }
        /// <summary>
        /// 是否启用。关闭后启动 FTP 时此用户不会被注册，等同"禁用登录"。
        /// </summary>
        public bool Enabled
        {
            get => _enabled; set { _enabled = value; OnPropertyChanged(); }
        }
        /// <summary>
        /// 系统保护账号（guest 不能删）。用于 UI 禁用"删除选中"按钮。
        /// </summary>
        public bool IsProtected => string.Equals(Name, "guest", StringComparison.OrdinalIgnoreCase);

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string m = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(m));
    }

    /// <summary>
    /// 在线会话条目（Rust 侧 Authenticator 登录时登记，30s 无操作自动踢掉）。
    /// 实现 INotifyPropertyChanged，OnlineFor 会被 MainViewModel 的 1s 定时器批量触发刷新。
    /// </summary>
    public class SessionEntryVm : INotifyPropertyChanged
    {
        private string _username;
        private DateTime _onlineSince;
        private ulong _opsCount;
        private string _onlineFor;

        public string Username
        {
            get => _username; set { _username = value; OnPropertyChanged(); }
        }
        public DateTime OnlineSince
        {
            get => _onlineSince; set { _onlineSince = value; OnPropertyChanged(); }
        }
        public ulong OpsCount
        {
            get => _opsCount; set { _opsCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(OnlineFor)); }
        }

        /// <summary>
        /// 在线时长字符串 hh:mm:ss。由 MainViewModel 每秒批量更新，不是本地实时计算。
        /// </summary>
        public string OnlineFor
        {
            get => _onlineFor;
            set { _onlineFor = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 重新计算 OnlineFor —— 由定时器每秒调用一次（只在 Sessions 非空时触发）。
        /// </summary>
        public void TickOnlineFor()
        {
            var ts = DateTime.Now - OnlineSince;
            OnlineFor = ts.ToString(@"hh\:mm\:ss");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string m = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(m));
    }

    /// <summary>
    /// 最近一次下载/上传/删除/重命名等传输活动条目。
    /// 由 Rust 侧 libunftp DataListener 推 SESSION:DATA:* 事件，ParseSessionEvent 追加进来。
    /// </summary>
    public class TransferEntryVm
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public string Username { get; set; }
        /// <summary>操作类型：DOWNLOAD / UPLOAD / DELETE / RENAME / MAKEDIR / REMOVEDIR</summary>
        public string Op { get; set; }
        public string Path { get; set; }
        public string Extra { get; set; }
        public ulong Bytes { get; set; }
        public string BytesDisplay => FormatBytes(Bytes);

        /// <summary>统一 bytes→人类可读字符串（供 UI 绑定）。</summary>
        public static string FormatBytes(ulong b)
        {
            if (b >= 1_073_741_824) return $"{b / 1_073_741_824.0:F2} GB";
            if (b >= 1_048_576) return $"{b / 1_048_576.0:F2} MB";
            if (b >= 1024) return $"{b / 1024.0:F1} KB";
            return $"{b} B";
        }
    }

    /// <summary>
    /// 单条日志。比纯 string 多了 Time + Level，UI 可按级别着色、按时间排序。
    /// </summary>
    public class LogEntryVm
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public FtpLogLevel Level { get; set; }
        public string Message { get; set; }

        /// <summary>ListBox 绑定到 DisplayPath 时的纯文本（便于复制）。</summary>
        public string FullText => $"[{Time:HH:mm:ss}][{Level}] {Message}";
    }

    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly SettingsRepository _repo = new SettingsRepository();
        private FtpServer _server;
        /// <summary>启动从 DB 加载期间临时置 true，阻止自动 flush 出现中间态写入。</summary>
        private bool _loadingFromDb;

        private readonly DispatcherTimer _sessionTick = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
            IsEnabled = false,
        };

        private StreamWriter _logWriter;
        private DateTime _sessionStart;

        private ushort _port = 2121;
        public ushort Port
        {
            get => _port;
            set { _port = value; OnPropertyChanged(); SaveConfig(); }
        }

        private string _rootDir = System.IO.Directory.GetCurrentDirectory();
        public string RootDir
        {
            get => _rootDir;
            set { _rootDir = value; OnPropertyChanged(); SaveConfig(); }
        }

        private uint _maxConn = 10;
        public uint MaxConnections
        {
            get => _maxConn;
            set { _maxConn = value; OnPropertyChanged(); SaveConfig(); }
        }

        private string _bannerText = "";
        /// <summary>登录横幅 / MOTD，空串使用 libunftp 默认 220 Ready。改完自动持久化到 SQLite。</summary>
        public string BannerText
        {
            get => _bannerText;
            set { _bannerText = value ?? ""; OnPropertyChanged(); SaveConfig(); }
        }

        private ushort _passiveMinPort = 0;
        /// <summary>被动模式端口最小值。0 表示不限制，由 OS 自动分配。Start 前生效。</summary>
        public ushort PassiveMinPort
        {
            get => _passiveMinPort;
            set { _passiveMinPort = value; OnPropertyChanged(); SaveConfig(); }
        }

        private ushort _passiveMaxPort = 0;
        /// <summary>被动模式端口最大值。0 表示不限制，由 OS 自动分配。Start 前生效。</summary>
        public ushort PassiveMaxPort
        {
            get => _passiveMaxPort;
            set { _passiveMaxPort = value; OnPropertyChanged(); SaveConfig(); }
        }

        private bool _silentStartup;
        /// <summary>静默启动：启动时主窗自动隐藏，仅托盘运行。可与 AutoStartServer 独立或组合。</summary>
        public bool SilentStartup
        {
            get => _silentStartup;
            set { _silentStartup = value; OnPropertyChanged(); SaveConfig(); }
        }

        private bool _autoStartServer;
        /// <summary>开机自启：写入 HKCU\...\Run 注册表项。独立开关，不依赖 SilentStartup。</summary>
        public bool AutoStartServer
        {
            get => _autoStartServer;
            set { _autoStartServer = value; OnPropertyChanged(); SaveConfig(); }
        }

        /// <summary>编译期常量：Debug=true / Release=false，控制是否显示高级（被动端口、MOTD）UI。</summary>
        public bool IsDebugBuild
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

        private bool _isRunning;
        public bool IsRunning
        {
            get => _isRunning;
            set { _isRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotRunning)); OnPropertyChanged(nameof(StartStopLabel)); OnPropertyChanged(nameof(ToggleButtonEnabled)); }
        }
        public bool IsNotRunning => !IsRunning;
        private bool _isTransitioning;
        public bool ToggleButtonEnabled => !_isTransitioning;

        public string StartStopLabel => _isTransitioning
            ? (IsRunning ? "停止中..." : "启动中...")
            : (IsRunning ? "停止服务器" : "启动服务器");

        public ObservableCollection<UserEntryVm> Users { get; } = new ObservableCollection<UserEntryVm>();
        public UserEntryVm NewUser { get; } = new UserEntryVm { Name = "test", Password = "test", ReadOnly = false };

        public ObservableCollection<SessionEntryVm> Sessions { get; } = new ObservableCollection<SessionEntryVm>();

        public ObservableCollection<TransferEntryVm> Transfers { get; } = new ObservableCollection<TransferEntryVm>();

        private List<DailyStatRow> _recentDays = new();
        public List<DailyStatRow> RecentDays
        {
            get => _recentDays;
            private set { _recentDays = value; OnPropertyChanged(); }
        }
        public ulong TodayBytesDown => RecentDays.Count > 0 ? RecentDays[RecentDays.Count - 1].BytesDown : 0;
        public ulong TodayBytesUp => RecentDays.Count > 0 ? RecentDays[RecentDays.Count - 1].BytesUp : 0;
        public ulong TodayTotalOps => RecentDays.Count > 0 ? RecentDays[RecentDays.Count - 1].TotalOps : 0;
        public ulong TotalBytesDown7d => RecentDays.Aggregate(0ul, (a, b) => a + b.BytesDown);
        public ulong TotalBytesUp7d => RecentDays.Aggregate(0ul, (a, b) => a + b.BytesUp);
        public ulong TotalOps7d => RecentDays.Aggregate(0ul, (a, b) => a + b.TotalOps);
        public ulong PeakBytesDay7d => RecentDays.Aggregate(0ul, (a, b) => Math.Max(a, b.BytesDown + b.BytesUp));
        /// <summary>柱状图一行的数据。</summary>
        public class BarRowVm
        {
            public DateTime Date { get; set; }
            public string Label => Date.ToString("MM-dd");
            public ulong BytesDown { get; set; }
            public ulong BytesUp { get; set; }
            public ulong Total => BytesDown + BytesUp;
            public string TotalDisplay => TransferEntryVm.FormatBytes(Total);
            /// <summary>当天总量相对于 7 天峰值的比例 0~1。XAML 里用 * 固定宽算出像素宽。</summary>
            public double Ratio { get; set; }
        }
        private List<BarRowVm> _dashboardBars = new();
        public List<BarRowVm> DashboardBars
        {
            get => _dashboardBars;
            private set { _dashboardBars = value; OnPropertyChanged(); }
        }

        public void RefreshDashboard()
        {
            try
            {
                RecentDays = _repo.LoadRecentDays(7);
                OnPropertyChanged(nameof(TodayBytesDown));
                OnPropertyChanged(nameof(TodayBytesUp));
                OnPropertyChanged(nameof(TodayTotalOps));
                OnPropertyChanged(nameof(TotalBytesDown7d));
                OnPropertyChanged(nameof(TotalBytesUp7d));
                OnPropertyChanged(nameof(TotalOps7d));
                OnPropertyChanged(nameof(PeakBytesDay7d));

                var peak = PeakBytesDay7d == 0 ? 1 : PeakBytesDay7d;
                DashboardBars = RecentDays.Select(r => new BarRowVm
                {
                    Date = r.Date,
                    BytesDown = r.BytesDown,
                    BytesUp = r.BytesUp,
                    Ratio = (double)(r.BytesDown + r.BytesUp) / peak,
                }).ToList();
                OnPropertyChanged(nameof(DashboardBars));
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[VM.RefreshDashboard] {ex.Message}");
                Console.ResetColor();
            }
        }

        public ObservableCollection<LogEntryVm> LogLines { get; } = new ObservableCollection<LogEntryVm>();

        private string _localIpList = "";
        public string LocalIpList
        {
            get => _localIpList;
            private set { _localIpList = value; OnPropertyChanged(); }
        }

        private string _primaryIp = "";
        public string PrimaryIp
        {
            get => _primaryIp;
            private set { _primaryIp = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// 枚举本机所有活跃网卡的 IPv4 地址（排除 Loopback / 169.254 自分配）。
        /// PrimaryIp 取第一个可用地址；LocalIpList 存全部（逗号分隔）。
        /// 用户可点状态栏"刷新"按钮手动触发；网卡热插拔/切换 VPN 后 IP 会变。
        /// </summary>
        public void RefreshLocalIps()
        {
            var ips = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = ip.Address.ToString();
                        if (s.StartsWith("169.254.")) continue;
                        if (!ips.Contains(s)) ips.Add(s);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VM.RefreshLocalIps] 枚举网卡失败: {ex.Message}");
            }

            ips = ips.OrderBy(ip => ip.StartsWith("192.168.") ? 0
                                  : ip.StartsWith("10.") ? 1
                                  : ip.StartsWith("172.16.") || ip.StartsWith("172.17.") || ip.StartsWith("172.18.")
                                    || ip.StartsWith("172.19.") || ip.StartsWith("172.2") || ip.StartsWith("172.3") ? 2
                                  : 3).ToList();

            PrimaryIp = ips.FirstOrDefault() ?? "无（未连接网络）";
            LocalIpList = ips.Count == 0 ? "" : string.Join("，", ips);
        }

        public MainViewModel()
        {
            // LoadFromDb 期间 CollectionChanged 会被 _loadingFromDb 挡掉 flush
            Users.CollectionChanged += (s, e) => { if (!_loadingFromDb) SaveUsers(); };

            Sessions.CollectionChanged += (s, e) => UpdateSessionTickState();

            _sessionTick.Tick += (s, e) =>
            {
                foreach (var sess in Sessions)
                    sess.TickOnlineFor();
            };

            bool dbHadData = false;
            _loadingFromDb = true;
            try
            {
                var s = _repo.LoadSettings();
                if (s != null)
                {
                    Port = (ushort)s.Port;
                    RootDir = s.RootDir ?? RootDir;
                    MaxConnections = (uint)s.MaxConnections;
                    BannerText = s.BannerText ?? "";
                    PassiveMinPort = (ushort)(s.PassivePortMin < 0 ? 0 : s.PassivePortMin > 65535 ? 65535 : s.PassivePortMin);
                    PassiveMaxPort = (ushort)(s.PassivePortMax < 0 ? 0 : s.PassivePortMax > 65535 ? 65535 : s.PassivePortMax);
                    SilentStartup = s.SilentStartup;
                    AutoStartServer = s.AutoStartServer;
                    dbHadData = true;
                    Console.WriteLine($"[VM.ctor] loaded settings: port={Port} root={RootDir} maxConn={MaxConnections} banner={(string.IsNullOrEmpty(BannerText) ? "(默认)" : "已配置")} passive=[{PassiveMinPort},{PassiveMaxPort}] silent={SilentStartup} autoStart={AutoStartServer}");
                }
                var rows = _repo.LoadUsers();
                if (rows.Count > 0)
                {
                    foreach (var r in rows)
                    {
                        var vm = new UserEntryVm
                        {
                            Name = r.Username,
                            Password = r.Password,
                            ReadOnly = r.ReadOnly,
                            RootPath = r.RootPath ?? "",
                            Enabled = r.Enabled,
                        };
                        vm.PropertyChanged += OnUserPropertyChanged;
                        Users.Add(vm);
                    }
                    dbHadData = true;
                    Console.WriteLine($"[VM.ctor] loaded {rows.Count} users from DB");
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[VM.ctor] DB 加载失败（将使用默认值）: {ex.Message}");
                Console.ResetColor();
            }

            // DB 空 → seed 默认 admin/guest（只在首次启动时写 DB）
            if (!dbHadData)
            {
                var admin = new UserEntryVm { Name = "admin", Password = "admin123", ReadOnly = false, RootPath = "", Enabled = true };
                var guest = new UserEntryVm { Name = "guest", Password = "guestuser", ReadOnly = true, RootPath = "", Enabled = true };
                admin.PropertyChanged += OnUserPropertyChanged;
                guest.PropertyChanged += OnUserPropertyChanged;
                Users.Add(admin);
                Users.Add(guest);
                _loadingFromDb = false;
                SaveConfig();
                SaveUsers();
                Console.WriteLine("[VM.ctor] seeded default admin/guest to DB");
            }
            else
            {
                _loadingFromDb = false;
                foreach (var u in Users)
                    u.PropertyChanged -= OnUserPropertyChanged;
                foreach (var u in Users)
                    u.PropertyChanged += OnUserPropertyChanged;
            }

            RefreshLocalIps();
            RefreshDashboard();

            // 进程退出兜底——防止崩溃/强制关闭时 .log 还没 flush 就丢了
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CloseSessionLog("进程退出");
        }

        /// <summary>
        /// 打开本次会话的历史日志文件。每次 Start FTP 时调一次。
        /// 文件路径：[exe]\log\YYYY-MM-DD_HH-mm-ss.log
        /// </summary>
        private void OpenSessionLog()
        {
            CloseSessionLog("（上一会话被新会话覆盖）");

            try
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "log");
                Directory.CreateDirectory(dir);

                _sessionStart = DateTime.Now;
                var fileName = _sessionStart.ToString("yyyy-MM-dd_HH-mm-ss") + ".log";
                var filePath = Path.Combine(dir, fileName);

                var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _logWriter = new StreamWriter(fs, new UTF8Encoding(false), 4096)
                {
                    AutoFlush = true
                };

                _logWriter.WriteLine("==============================================");
                _logWriter.WriteLine($" FTP 会话开始  {_sessionStart:yyyy-MM-dd HH:mm:ss}");
                _logWriter.WriteLine($" 端口: {Port}    根目录: {RootDir}    最大连接: {MaxConnections}");
                _logWriter.WriteLine($" 用户数: {Users.Count}    本机 IP: {PrimaryIp}");
                _logWriter.WriteLine("==============================================");
                _logWriter.WriteLine();

                Console.WriteLine($"[VM.OpenSessionLog] 历史日志已开启: {filePath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VM.OpenSessionLog] 创建日志文件失败（将仅在内存/控制台记录）: {ex.Message}");
                _logWriter = null;
            }
        }

        /// <summary>
        /// 关闭本次会话的历史日志文件。Stop FTP 时调，或进程退出兜底调。
        /// </summary>
        private void CloseSessionLog(string reason)
        {
            if (_logWriter == null) return;
            try
            {
                var now = DateTime.Now;
                var duration = now - _sessionStart;
                _logWriter.WriteLine();
                _logWriter.WriteLine($"[会话结束]  {now:yyyy-MM-dd HH:mm:ss}  运行时长: {duration:hh\\:mm\\:ss}  原因: {reason}");
                _logWriter.Flush();
                _logWriter.Dispose();
                Console.WriteLine($"[VM.CloseSessionLog] 历史日志已关闭（{reason}）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VM.CloseSessionLog] 关闭日志文件异常: {ex.Message}");
            }
            _logWriter = null;
        }

        /// <summary>
        /// 往当前会话的 .log 文件写一条。级别和消息与 UI 侧完全一致。
        /// 文件格式：[2026-11-01 14:30:15][INFO ] 登录成功：用户「admin」
        /// </summary>
        private void LogToFile(FtpLogLevel level, string msg)
        {
            if (_logWriter == null) return;
            try
            {
                _logWriter.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][{level,-5}] {msg}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VM.LogToFile] 写日志文件失败: {ex.Message}");
            }
        }

        private void OnUserPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            SaveUsers();
        }

        private void SaveConfig()
        {
            try
            {
                _repo.SaveSettings(new AppSettings
                {
                    Port = Port,
                    RootDir = RootDir,
                    MaxConnections = (int)MaxConnections,
                    BannerText = BannerText,
                    PassivePortMin = PassiveMinPort,
                    PassivePortMax = PassiveMaxPort,
                    SilentStartup = SilentStartup,
                    AutoStartServer = AutoStartServer,
                });
                Services.RegistryHelper.SetAutoStart(AutoStartServer);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[VM.SaveConfig] {ex.Message}");
                Console.ResetColor();
            }
        }

        private void SaveUsers()
        {
            try
            {
                var rows = Users.Select(u => new UserRow
                {
                    Username = u.Name,
                    Password = u.Password,
                    ReadOnly = u.ReadOnly,
                    RootPath = string.IsNullOrWhiteSpace(u.RootPath) ? null : u.RootPath,
                    Enabled = u.Enabled,
                }).ToList();
                _repo.SaveUsers(rows);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[VM.SaveUsers] {ex.Message}");
                Console.ResetColor();
            }
        }

        public void Start()
        {
            if (_isTransitioning || IsRunning) return;
            _isTransitioning = true; OnPropertyChanged(nameof(ToggleButtonEnabled)); OnPropertyChanged(nameof(StartStopLabel));
            try
            {
                SaveConfig();
                SaveUsers();

                Console.WriteLine($"[VM.Start] port={Port} root={RootDir} maxConn={MaxConnections} users={Users.Count}");

                OpenSessionLog();

                if (_server == null)
                {
                    _server = new FtpServer();
                    _server.Log += OnServerLog;
                    Console.WriteLine("[VM.Start] FtpServer created + callback installed");
                }

                _server.SetPort(Port);
                _server.SetRoot(RootDir);
                _server.SetMaxConnections(MaxConnections);
                _server.SetGreeting(BannerText);

                if (PassiveMinPort == 0 && PassiveMaxPort == 0)
                {
                    _server.SetPassivePortRange(0, 0);
                }
                else
                {
                    if (PassiveMinPort < 1024 || PassiveMaxPort > 65535 || PassiveMinPort > PassiveMaxPort)
                    {
                        throw new InvalidOperationException($"被动端口范围无效：{PassiveMinPort}-{PassiveMaxPort}（需在 1024-65535 且 min<=max）");
                    }
                    _server.SetPassivePortRange(PassiveMinPort, PassiveMaxPort);
                }

                _server.ClearUsers();
                int enabledCount = 0, disabledCount = 0;
                foreach (var u in Users)
                {
                    // 无论 Enabled 与否都注册到 Rust：disabled 用户在 Rust 侧 enabled=0，
                    // 由 Authenticator 统一拒绝登录，比 C# 层跳过更安全——Rust 侧逻辑不会漏判
                    _server.AddUser(u.Name, u.Password, u.ReadOnly,
                        string.IsNullOrWhiteSpace(u.RootPath) ? null : u.RootPath,
                        u.Enabled);

                    if (!u.Enabled) disabledCount++;
                    else enabledCount++;
                }
                Console.WriteLine($"[VM.Start] registered {enabledCount} enabled users + {disabledCount} disabled users to Rust");

                Console.WriteLine("[VM.Start] calling ftp_server_start...");
                _server.Start();
                IsRunning = true;
                Console.WriteLine($"[VM.Start] started OK, IsRunning={_server.IsRunning}");
            }
            catch (Exception ex)
            {
                Log("ERROR", ex.Message);
                LogToFile(FtpLogLevel.Error, $"启动失败：{ex.Message}");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[VM.Start] EXCEPTION: {ex}");
                Console.ResetColor();
            }
            finally
            {
                _isTransitioning = false; OnPropertyChanged(nameof(ToggleButtonEnabled)); OnPropertyChanged(nameof(StartStopLabel));

                if (!IsRunning) CloseSessionLog("启动失败");
            }
        }

        public void Stop()
        {
            if (_isTransitioning || !IsRunning) return;
            _isTransitioning = true; OnPropertyChanged(nameof(ToggleButtonEnabled)); OnPropertyChanged(nameof(StartStopLabel));
            try
            {
                Console.WriteLine("[VM.Stop] calling ftp_server_stop...");
                _server?.Stop();
                IsRunning = false;
                Console.WriteLine("[VM.Stop] done");
            }
            catch (Exception ex)
            {
                Log("ERROR", ex.Message);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[VM.Stop] EXCEPTION: {ex}");
                Console.ResetColor();
            }
            finally
            {
                _isTransitioning = false; OnPropertyChanged(nameof(ToggleButtonEnabled)); OnPropertyChanged(nameof(StartStopLabel));

                CloseSessionLog("手动停止");
            }
        }

        public void Toggle()
        {
            if (IsRunning) Stop(); else Start();
        }

        /// <summary>强制踢掉一个在线会话。软踢：从注册表移除，客户端下一条命令被 server 拒绝。</summary>
        public bool KickSession(string username)
        {
            if (_server == null || !IsRunning) return false;
            try
            {
                return _server.KickSession(username);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"踢会话失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>历史日志目录（<exe>\log）。</summary>
        public string LogDir => System.IO.Path.Combine(AppContext.BaseDirectory, "log");

        /// <summary>打开历史日志目录（Explorer）。</summary>
        public void OpenLogFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(LogDir);
                System.Diagnostics.Process.Start("explorer.exe", LogDir);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"打开历史日志目录失败：{ex.Message}");
            }
        }

        /// <summary>历史日志文件列表（按时间倒序）。</summary>
        public class HistoryLogFileVm
        {
            public string Name { get; set; }
            public string FullPath { get; set; }
            public DateTime StartTime { get; set; }
            public DateTime EndTime { get; set; }
            public string Duration { get; set; }
            public long SizeBytes { get; set; }
            public string SizeDisplay => SizeBytes >= 1024
                ? $"{SizeBytes / 1024.0:F1} KB"
                : $"{SizeBytes} B";
        }

        private ObservableCollection<HistoryLogFileVm> _historyLogs = new();
        public ObservableCollection<HistoryLogFileVm> HistoryLogs
        {
            get => _historyLogs;
            private set { _historyLogs = value; OnPropertyChanged(); }
        }

        public void RefreshHistoryLogs()
        {
            HistoryLogs.Clear();
            try
            {
                if (!System.IO.Directory.Exists(LogDir)) return;
                foreach (var f in System.IO.Directory.GetFiles(LogDir, "*.log"))
                {
                    var fi = new System.IO.FileInfo(f);
                    var start = ParseStartTimeFromName(fi.Name);
                    var end = fi.LastWriteTime;
                    HistoryLogs.Add(new HistoryLogFileVm
                    {
                        Name = fi.Name,
                        FullPath = fi.FullName,
                        StartTime = start,
                        EndTime = end,
                        Duration = (end - start).TotalSeconds > 0
                            ? FormatDuration(end - start)
                            : "-",
                        SizeBytes = fi.Length,
                    });
                }
                var sorted = HistoryLogs.OrderByDescending(l => l.Name).ToList();
                HistoryLogs = new ObservableCollection<HistoryLogFileVm>(sorted);
            }
            catch (Exception ex)
            {
                Log("ERROR", $"读取历史日志目录失败：{ex.Message}");
            }
        }

        private static DateTime ParseStartTimeFromName(string filename)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(filename);
            if (name.Length < 19) return DateTime.MinValue;
            try
            {
                return DateTime.ParseExact(
                    $"{name.Substring(0, 4)}-{name.Substring(5, 2)}-{name.Substring(8, 2)} {name.Substring(11, 2)}:{name.Substring(14, 2)}:{name.Substring(17, 2)}",
                    "yyyy-MM-dd HH:mm:ss", null);
            }
            catch { return DateTime.MinValue; }
        }

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}天 {ts.Hours}小时";
            if (ts.TotalHours >= 1) return $"{ts.Hours}小时 {ts.Minutes}分";
            if (ts.TotalMinutes >= 1) return $"{ts.Minutes}分 {ts.Seconds}秒";
            return $"{ts.Seconds}秒";
        }

        private DispatcherTimer _logCleanupTimer;

        /// <summary>
        /// 启动日志自动清理：默认每天凌晨 3:00 扫一遍，删除超过 30 天的 .log 文件。
        /// 应用启动后立即执行一次（避免部署时积压旧文件）。
        /// </summary>
        public void StartLogAutoCleanup()
        {
            TryCleanupOldLogs();

            _logCleanupTimer = new DispatcherTimer
            {
                Interval = NextCleanupInterval(),
            };
            _logCleanupTimer.Tick += (s, e) =>
            {
                TryCleanupOldLogs();
                _logCleanupTimer.Interval = NextCleanupInterval();
            };
            _logCleanupTimer.Start();
        }

        /// <summary>
        /// 日志保留天数（默认 30 天）。0 或负数 = 永不清理。
        /// 未来可以挪到 Settings。目前硬编码常量。
        /// </summary>
        public int LogDaysToKeep { get; set; } = 30;

        private TimeSpan NextCleanupInterval()
        {
            var now = DateTime.Now;
            var next = now.Date.AddDays(1).AddHours(3);
            var diff = next - now;
            return diff.TotalMinutes < 1 ? TimeSpan.FromDays(1) : diff;
        }

        private void TryCleanupOldLogs()
        {
            try
            {
                if (LogDaysToKeep <= 0 || !System.IO.Directory.Exists(LogDir)) return;

                var cutoff = DateTime.Now.AddDays(-LogDaysToKeep);
                int removed = 0;
                foreach (var f in System.IO.Directory.GetFiles(LogDir, "*.log"))
                {
                    var fi = new System.IO.FileInfo(f);
                    if (fi.LastWriteTime < cutoff)
                    {
                        fi.Delete();
                        removed++;
                    }
                }
                if (removed > 0)
                {
                    Log("INFO", $"日志自动清理：删除 {removed} 个超过 {LogDaysToKeep} 天的历史日志");
                }
            }
            catch (Exception ex)
            {
                Log("ERROR", $"日志自动清理失败：{ex.Message}");
            }
        }

        public void AddNewUser()
        {
            if (string.IsNullOrWhiteSpace(NewUser.Name) || string.IsNullOrWhiteSpace(NewUser.Password)) return;
            var u = new UserEntryVm
            {
                Name = NewUser.Name,
                Password = NewUser.Password,
                ReadOnly = NewUser.ReadOnly,
                RootPath = "",
                Enabled = true,
            };
            u.PropertyChanged += OnUserPropertyChanged;
            Users.Add(u);
            NewUser.Password = "";
        }

        public void RemoveUser(UserEntryVm u)
        {
            if (u == null) return;
            if (u.IsProtected)
            {
                Log("WARN", $"[{u.Name}] 是系统保护账号，不能删除（可在【启用】列禁用）");
                return;
            }
            u.PropertyChanged -= OnUserPropertyChanged;
            Users.Remove(u);
        }

        private void OnServerLog(FtpLogLevel level, string msg)
        {
            Console.WriteLine($"[VM][{level}] {msg}");
            LogToFile(level, msg);
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (level == FtpLogLevel.Session)
                {
                    ParseSessionEvent(msg);
                    return;
                }
                LogLines.Add(new LogEntryVm { Time = DateTime.Now, Level = level, Message = msg });
                while (LogLines.Count > 2000) LogLines.RemoveAt(0);
            });
        }

        /// <summary>
        /// 智能启停在线时长定时器：Sessions 有任何条目就开，空了就关。
        /// 避免无会话时每秒空跑一次 Tick。
        /// </summary>
        private void UpdateSessionTickState()
        {
            if (Sessions.Count > 0)
            {
                if (!_sessionTick.IsEnabled) _sessionTick.Start();
            }
            else
            {
                if (_sessionTick.IsEnabled) _sessionTick.Stop();
            }
        }

        /// <summary>
        /// 解析 Rust 侧推的会话事件。格式：
        ///   SESSION:ONLINE user=xxx
        ///   SESSION:OFFLINE user=xxx (idle)
        ///   SESSION:KICK user=xxx (admin)
        ///   SESSION:DATA:DOWNLOAD user=xxx path=/foo/bar bytes=12345
        ///   SESSION:DATA:UPLOAD   user=xxx path=/foo/bar bytes=12345
        ///   SESSION:DATA:DELETE   user=xxx path=/foo/bar
        ///   SESSION:DATA:MAKEDIR  user=xxx path=/foo/bar
        ///   SESSION:DATA:REMOVEDIR user=xxx path=/foo/bar
        ///   SESSION:DATA:RENAME   user=xxx from=/a to=/b
        /// </summary>
        private void ParseSessionEvent(string msg)
        {
            Console.WriteLine($"[PARSE SESSION] msg={msg}");

            if (msg.StartsWith("SESSION:DATA:"))
            {
                HandleDataEvent(msg);
                return;
            }

            if (msg.StartsWith("SESSION:ONLINE user="))
            {
                var user = msg.Substring("SESSION:ONLINE user=".Length).Trim();
                var old = Sessions.FirstOrDefault(s => s.Username == user);
                if (old != null) Sessions.Remove(old);
                var since = DateTime.Now;
                Console.WriteLine($"[PARSE ONLINE] user={user}, OnlineSince={since:yyyy-MM-dd HH:mm:ss}");
                var sess = new SessionEntryVm
                {
                    Username = user,
                    OnlineSince = since,
                    OpsCount = 0,
                };
                sess.TickOnlineFor();
                Sessions.Add(sess);
                Console.WriteLine($"[PARSE ONLINE DONE] OnlineSince actually = {sess.OnlineSince:yyyy-MM-dd HH:mm:ss}");
            }
            else if (msg.StartsWith("SESSION:OFFLINE user="))
            {
                var rest = msg.Substring("SESSION:OFFLINE user=".Length).Trim();
                var user = rest.Split(' ')[0];
                Console.WriteLine($"[PARSE OFFLINE] user={user}");
                var old = Sessions.FirstOrDefault(s => s.Username == user);
                if (old != null) Sessions.Remove(old);
            }
        }

        /// <summary>从 SESSION:DATA:* 原始日志消息里提取 user / path / bytes / from / to 字段。</summary>
        private static string ExtractField(string msg, string key)
        {
            int i = msg.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + key.Length;
            int end = msg.IndexOf(' ', start);
            if (end < 0) end = msg.Length;
            return msg.Substring(start, end - start);
        }
        private static ulong ExtractUlongField(string msg, string key)
        {
            var s = ExtractField(msg, key);
            if (string.IsNullOrEmpty(s) || !ulong.TryParse(s, out var v)) return 0;
            return v;
        }

        private void HandleDataEvent(string msg)
        {
            // DOWNLOAD_COMPLETE / UPLOAD_COMPLETE 是精确统计口径（只统计 start_pos==0 的完整传输，
            // 避免 FileZilla 断点续传导致 daily_stats 被片段重复累加）
            if (msg.StartsWith("SESSION:DOWNLOAD_COMPLETE ") || msg.StartsWith("SESSION:UPLOAD_COMPLETE "))
            {
                var isDown = msg.StartsWith("SESSION:DOWNLOAD_COMPLETE ");
                var user1 = ExtractField(msg, "user=");
                var path1 = ExtractField(msg, "path=") ?? "";
                var bytes1 = ExtractUlongField(msg, "bytes=");
                if (!string.IsNullOrEmpty(user1) && bytes1 > 0)
                {
                    var entry1 = new TransferEntryVm
                    {
                        Time = DateTime.Now,
                        Username = user1,
                        Op = isDown ? "DOWNLOAD" : "UPLOAD",
                        Path = path1,
                        Bytes = bytes1,
                    };
                    Transfers.Insert(0, entry1);
                    while (Transfers.Count > 500) Transfers.RemoveAt(Transfers.Count - 1);

                    _repo.IncrementDaily(entry1.Time.Date,
                        isDown ? bytes1 : 0,
                        !isDown ? bytes1 : 0,
                        1);
                    RefreshDashboard();
                }
                return;
            }

            var op = msg.Substring("SESSION:DATA:".Length).Split(' ')[0];
            var user = ExtractField(msg, "user=");
            if (string.IsNullOrEmpty(user)) return;

            var entry = new TransferEntryVm
            {
                Time = DateTime.Now,
                Username = user,
                Op = op,
            };

            switch (op)
            {
                case "DOWNLOAD":
                case "UPLOAD":
                    entry.Path = ExtractField(msg, "path=") ?? "";
                    entry.Bytes = ExtractUlongField(msg, "bytes=");
                    break;
                case "DELETE":
                case "MAKEDIR":
                case "REMOVEDIR":
                    entry.Path = ExtractField(msg, "path=") ?? "";
                    break;
                case "RENAME":
                    entry.Path = ExtractField(msg, "from=") ?? "";
                    var to = ExtractField(msg, "to=");
                    entry.Extra = string.IsNullOrEmpty(to) ? "" : $"→ {to}";
                    break;
                default:
                    return;
            }

            Transfers.Insert(0, entry);
            while (Transfers.Count > 500) Transfers.RemoveAt(Transfers.Count - 1);
        }

        private void Log(string tag, string msg)
        {
            Console.WriteLine($"[VM][{tag}] {msg}");
            var level = tag switch
            {
                "ERROR" => FtpLogLevel.Error,
                "WARN"  => FtpLogLevel.Warn,
                "INFO"  => FtpLogLevel.Info,
                _       => FtpLogLevel.Debug,
            };
            LogLines.Add(new LogEntryVm { Time = DateTime.Now, Level = level, Message = msg });
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string m = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(m));
    }
}
