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

        // ---- 在线会话时长定时器（智能启停：有会话才跑） ----
        private readonly DispatcherTimer _sessionTick = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
            IsEnabled = false,
        };

        // ---- 历史日志文件（每次 Start→Stop 算一次会话，写到 [exe]\log\YYYY-MM-DD_HH-mm-ss.log） ----
        private StreamWriter _logWriter;
        private DateTime _sessionStart;

        // ---- 基础配置 ----
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

        // ---- 用户 ----
        public ObservableCollection<UserEntryVm> Users { get; } = new ObservableCollection<UserEntryVm>();
        public UserEntryVm NewUser { get; } = new UserEntryVm { Name = "test", Password = "test", ReadOnly = false };

        // ---- 在线会话 ----
        public ObservableCollection<SessionEntryVm> Sessions { get; } = new ObservableCollection<SessionEntryVm>();

        // ---- 日志 ----
        public ObservableCollection<LogEntryVm> LogLines { get; } = new ObservableCollection<LogEntryVm>();

        // ---- 本机 IP（状态栏显示，可手动刷新） ----
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
                    // 只看在线 + 非 Loopback + 非 Tunnel
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                    foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        var s = ip.Address.ToString();
                        // 跳过 169.254 自分配（没连上 DHCP 的网卡）
                        if (s.StartsWith("169.254.")) continue;
                        if (!ips.Contains(s)) ips.Add(s);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VM.RefreshLocalIps] 枚举网卡失败: {ex.Message}");
            }

            // 排序：先内网段（192.168.x / 10.x / 172.16-31.x），其他往后
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
            // 1. 先绑事件（后面 LoadFromDb 会触发 CollectionChanged，但 _loadingFromDb 会挡掉 flush）
            Users.CollectionChanged += (s, e) => { if (!_loadingFromDb) SaveUsers(); };

            // 2. Sessions 变化 → 智能启停定时器（无会话时不白跑 CPU）
            Sessions.CollectionChanged += (s, e) => UpdateSessionTickState();

            // 3. 定时器每秒批量刷新所有在线会话的 OnlineFor
            _sessionTick.Tick += (s, e) =>
            {
                foreach (var sess in Sessions)
                    sess.TickOnlineFor();
            };

            // 2. 从 SQLite 加载（期间 _loadingFromDb=true，任何自动 flush 都会被 skip）
            bool dbHadData = false;
            _loadingFromDb = true;
            try
            {
                // 应用设置
                var s = _repo.LoadSettings();
                if (s != null)
                {
                    Port = (ushort)s.Port;
                    RootDir = s.RootDir ?? RootDir;
                    MaxConnections = (uint)s.MaxConnections;
                    dbHadData = true;
                    Console.WriteLine($"[VM.ctor] loaded settings: port={Port} root={RootDir} maxConn={MaxConnections}");
                }
                // 用户
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

            // 3. DB 空 → seed 默认 admin/guest（只在首次启动时写 DB）
            if (!dbHadData)
            {
                var admin = new UserEntryVm { Name = "admin", Password = "admin123", ReadOnly = false, RootPath = "", Enabled = true };
                var guest = new UserEntryVm { Name = "guest", Password = "guestuser", ReadOnly = true, RootPath = "", Enabled = true };
                admin.PropertyChanged += OnUserPropertyChanged;
                guest.PropertyChanged += OnUserPropertyChanged;
                Users.Add(admin);   // CollectionChanged 被 _loadingFromDb 挡掉，手动 save
                Users.Add(guest);
                _loadingFromDb = false;
                SaveConfig();
                SaveUsers();
                Console.WriteLine("[VM.ctor] seeded default admin/guest to DB");
            }
            else
            {
                _loadingFromDb = false;
                // 已有数据，把 PropertyChanged 绑定补给已加载的用户
                foreach (var u in Users)
                    u.PropertyChanged -= OnUserPropertyChanged; // 防重复
                foreach (var u in Users)
                    u.PropertyChanged += OnUserPropertyChanged;
            }

            // 4. 启动时枚举一次本机 IP（状态栏显示用）
            RefreshLocalIps();

            // 5. 进程退出兜底——防止崩溃/强制关闭时 .log 还没 flush 就丢了
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CloseSessionLog("进程退出");
        }

        // ─────────────────── 历史日志文件写入 ───────────────────

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
                Directory.CreateDirectory(dir);  // 幂等，已存在则跳过

                _sessionStart = DateTime.Now;
                var fileName = _sessionStart.ToString("yyyy-MM-dd_HH-mm-ss") + ".log";
                var filePath = Path.Combine(dir, fileName);

                var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);
                _logWriter = new StreamWriter(fs, new UTF8Encoding(false), 4096)
                {
                    AutoFlush = true  // 每条写完立刻 flush，崩溃最多丢一条
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

        // 用户任意属性（Name/Password/ReadOnly/RootPath）编辑 → 自动 flush
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
                });
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

        // ──────────────────────────────────────────────
        // 命令
        // ──────────────────────────────────────────────

        public void Start()
        {
            if (_isTransitioning || IsRunning) return;
            _isTransitioning = true; OnPropertyChanged(nameof(ToggleButtonEnabled)); OnPropertyChanged(nameof(StartStopLabel));
            try
            {
                // 启动前强制 flush 一次（保险：防止 UI 刚编辑完还没触发 PropertyChanged）
                SaveConfig();
                SaveUsers();

                Console.WriteLine($"[VM.Start] port={Port} root={RootDir} maxConn={MaxConnections} users={Users.Count}");

                // 开本次会话的历史日志文件
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

                _server.ClearUsers();
                int enabledCount = 0, disabledCount = 0;
                foreach (var u in Users)
                {
                    // 无论 Enabled 与否都注册到 Rust（disabled 用户在 Rust 里 enabled=0，
                    // 由 Authenticator 统一拒绝登录，530 Login incorrect——比 C# 层跳过更安全：
                    // 即使 Rust 里后续改了逻辑，disabled 身份也能被识别）
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

                // 如果 Start 抛异常 → 关掉这次开的日志（原因：启动失败）
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

                // 关本次会话的历史日志文件
                CloseSessionLog("手动停止");
            }
        }

        public void Toggle()
        {
            if (IsRunning) Stop(); else Start();
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
            Users.Add(u); // → CollectionChanged → SaveUsers()
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
            Users.Remove(u); // → CollectionChanged → SaveUsers()
        }

        private void OnServerLog(FtpLogLevel level, string msg)
        {
            Console.WriteLine($"[VM][{level}] {msg}");
            // 写入本次会话的历史日志文件
            LogToFile(level, msg);
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // Session 事件单独处理（level=4），不进日志 Tab
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
        /// </summary>
        private void ParseSessionEvent(string msg)
        {
            if (msg.StartsWith("SESSION:ONLINE user="))
            {
                var user = msg.Substring("SESSION:ONLINE user=".Length).Trim();
                var old = Sessions.FirstOrDefault(s => s.Username == user);
                if (old != null) Sessions.Remove(old);
                var sess = new SessionEntryVm
                {
                    Username = user,
                    OnlineSince = DateTime.Now,
                    OpsCount = 0,
                };
                sess.TickOnlineFor();  // 立即刷一次，UI 不会先闪 "00:00:00"
                Sessions.Add(sess);
            }
            else if (msg.StartsWith("SESSION:OFFLINE user="))
            {
                var rest = msg.Substring("SESSION:OFFLINE user=".Length).Trim();
                var user = rest.Split(' ')[0];
                var old = Sessions.FirstOrDefault(s => s.Username == user);
                if (old != null) Sessions.Remove(old);
            }
        }

        private void Log(string tag, string msg)
        {
            Console.WriteLine($"[VM][{tag}] {msg}");
            // tag 是字符串，转成 FtpLogLevel
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
