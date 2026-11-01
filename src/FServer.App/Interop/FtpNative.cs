using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FServer.App.Interop
{
    public enum FtpLogLevel : int
    {
        Debug   = 0,
        Info    = 1,
        Warn    = 2,
        Error   = 3,
        Session = 4,
    }

    /// <summary>
    /// 非托管回调签名。Rust 侧 msg 为 UTF-8 字符串（以 \0 结尾）。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback(int level, IntPtr msgUtf8, IntPtr ctx);

    /// <summary>
    /// opaque handle 包装，终结器会自动调用 ftp_server_free。
    /// </summary>
    public sealed class FtpServerHandle : SafeHandle
    {
        public FtpServerHandle() : base(IntPtr.Zero, true) { }

        public override bool IsInvalid => handle == IntPtr.Zero;

        public void AttachHandle(IntPtr ptr) => base.SetHandle(ptr);

        protected override bool ReleaseHandle()
        {
            if (!IsInvalid)
            {
                Native.ftp_server_free(handle);
                SetHandle(IntPtr.Zero);
            }
            return true;
        }
    }

    internal static class Native
    {
        private const string DllName = "ftp_core";

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr ftp_server_new();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void ftp_server_free(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftp_server_set_port(IntPtr handle, ushort port);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ftp_server_set_root(IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string path);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftp_server_set_max_conn(IntPtr handle, uint n);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ftp_server_add_user(IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string user,
            [MarshalAs(UnmanagedType.LPStr)] string pass,
            int readOnly);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ftp_server_add_user_with_root(IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string user,
            [MarshalAs(UnmanagedType.LPStr)] string pass,
            int readOnly,
            [MarshalAs(UnmanagedType.LPStr)] string root);

        /// <summary>
        /// 完整版：user / pass / readOnly / root / enabled。
        /// enabled=0 禁用用户——即使密码对的用户也被 Authenticator 直接拒绝（530 Login incorrect）。
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ftp_server_add_user_ex(IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string user,
            [MarshalAs(UnmanagedType.LPStr)] string pass,
            int readOnly,
            [MarshalAs(UnmanagedType.LPStr)] string root,
            int enabled);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ftp_server_remove_user(IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string user);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftp_server_clear_users(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftp_server_set_log_cb(IntPtr handle,
            IntPtr cb, IntPtr ctx);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftp_server_start(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftp_server_stop(IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool ftp_server_is_running(IntPtr handle);

        /// <summary>
        /// 强制踢掉一个在线会话。返回 0=成功、-1=handle 空、-2=用户不存在。
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ftp_server_kick_session(IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string user);

        /// <summary>
        /// 设置登录横幅 / MOTD。空串或 null 表示使用 libunftp 默认 "220 Ready"。
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int ftp_server_set_greeting(IntPtr handle,
            [MarshalAs(UnmanagedType.LPStr)] string banner);

        /// <summary>
        /// 设置被动模式端口范围。min=max=0 表示不限制让 OS 分配；
        /// 其他值要求 min in [1024..65535]、max in [1024..65535]、min <= max。
        /// Start 之前调用生效。
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int ftp_server_set_passive_port_range(IntPtr handle, int min, int max);
    }

    public sealed class FtpServer : IDisposable
    {
        private FtpServerHandle _handle;
        private LogCallback _callback;       // 强引用保活 delegate，防止函数指针悬空
        private IntPtr _callbackPtr;         // Marshal.GetFunctionPointerForDelegate 固定后的函数指针
        private IntPtr _userDataPtr;         // ctx = GCHandle.Alloc(this, Normal).ToIntPtr

        public event Action<FtpLogLevel, string> Log;

        public FtpServer()
        {
            var ptr = Native.ftp_server_new();
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("ftp_server_new 返回空");
            _handle = new FtpServerHandle();
            _handle.AttachHandle(ptr);
            InstallCallback();
        }

        private void InstallCallback()
        {
            _callback = new LogCallback(OnNativeLog);
            // Marshal.GetFunctionPointerForDelegate 内部会把 delegate 标记 pinned，
            // 只要 _callback 还有根，函数指针就一直有效
            _callbackPtr = Marshal.GetFunctionPointerForDelegate(_callback);
            _userDataPtr = GCHandle.ToIntPtr(GCHandle.Alloc(this, GCHandleType.Normal));
            Native.ftp_server_set_log_cb(_handle.DangerousGetHandle(), _callbackPtr, _userDataPtr);
            Console.WriteLine($"[Interop] InstallCallback cb_ptr=0x{_callbackPtr.ToInt64():X} ctx=0x{_userDataPtr.ToInt64():X}");
        }

        private static void OnNativeLog(int level, IntPtr msgUtf8, IntPtr ctx)
        {
            FtpLogLevel lvl = (FtpLogLevel)level;
            string msg = msgUtf8 == IntPtr.Zero ? "" : Utf8PtrToString(msgUtf8);
            FtpServer server = null;
            if (ctx != IntPtr.Zero)
            {
                try
                {
                    var gch = GCHandle.FromIntPtr(ctx);
                    server = gch.Target as FtpServer;
                }
                catch { }
            }
            server?.Log?.Invoke(lvl, msg);
        }

        private static string Utf8PtrToString(IntPtr p)
        {
            int len = 0;
            while (Marshal.ReadByte(p, len) != 0) len++;
            byte[] buf = new byte[len];
            Marshal.Copy(p, buf, 0, len);
            return Encoding.UTF8.GetString(buf);
        }

        public void SetPort(ushort port) => ThrowOn(Native.ftp_server_set_port(_handle.DangerousGetHandle(), port));
        public void SetRoot(string path) => ThrowOn(Native.ftp_server_set_root(_handle.DangerousGetHandle(), path));
        public void SetMaxConnections(uint n) => ThrowOn(Native.ftp_server_set_max_conn(_handle.DangerousGetHandle(), n));

        /// <summary>
        /// 注册用户到 Rust 层。enabled=false 的用户即使密码对也被 Authenticator 拒绝登录。
        /// </summary>
        public void AddUser(string user, string pass, bool readOnly, string root = null, bool enabled = true)
        {
            var rc = Native.ftp_server_add_user_ex(
                _handle.DangerousGetHandle(),
                user, pass,
                readOnly ? 1 : 0,
                string.IsNullOrEmpty(root) ? null : root,
                enabled ? 1 : 0);
            ThrowOn(rc);
        }

        public void RemoveUser(string user) => ThrowOn(Native.ftp_server_remove_user(_handle.DangerousGetHandle(), user));
        public void ClearUsers() => ThrowOn(Native.ftp_server_clear_users(_handle.DangerousGetHandle()));

        public void Start() => ThrowOn(Native.ftp_server_start(_handle.DangerousGetHandle()));
        public void Stop() => Native.ftp_server_stop(_handle.DangerousGetHandle());

        public bool IsRunning => Native.ftp_server_is_running(_handle.DangerousGetHandle());

        /// <summary>设置登录横幅 / MOTD（空串=用 libunftp 默认 220 Ready）。Start 之前调用生效。</summary>
        public void SetGreeting(string banner)
            => ThrowOn(Native.ftp_server_set_greeting(_handle.DangerousGetHandle(),
                string.IsNullOrEmpty(banner) ? null : banner));

        /// <summary>设置被动模式端口范围（Start 之前调用生效）。min=max=0 表示不限制让 OS 分配。</summary>
        public void SetPassivePortRange(ushort min, ushort max)
            => ThrowOn(Native.ftp_server_set_passive_port_range(
                _handle.DangerousGetHandle(), (int)min, (int)max));

        /// <summary>强制踢掉指定用户的在线会话。返回 true=成功。</summary>
        public bool KickSession(string username)
            => Native.ftp_server_kick_session(_handle.DangerousGetHandle(), username) == 0;

        private static void ThrowOn(int rc)
        {
            if (rc != 0) throw new InvalidOperationException($"Rust ftp_core 返回 {rc}");
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_handle != null && !_handle.IsInvalid)
            {
                // 先清 Rust 侧回调，防止 C# 对象被 GC 后还被回调
                if (_callbackPtr != IntPtr.Zero)
                {
                    Native.ftp_server_set_log_cb(_handle.DangerousGetHandle(), IntPtr.Zero, IntPtr.Zero);
                }
                if (_userDataPtr != IntPtr.Zero)
                {
                    GCHandle.FromIntPtr(_userDataPtr).Free();
                    _userDataPtr = IntPtr.Zero;
                }
                // 清 _callback 根引用 → Marshal 会把它 unpinned
                _callback = null;
                _callbackPtr = IntPtr.Zero;

                _handle.Dispose();
                _handle = null;
            }
        }

        ~FtpServer() { Dispose(false); }
    }
}
