use std::ffi::CStr;
use std::fmt;
use std::fmt::Debug;
use std::os::raw::{c_char, c_int, c_void};
use std::path::{Path, PathBuf};
use std::panic;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle as ThreadJoinHandle;
use std::collections::HashMap;
use std::time::{Duration, Instant};
use tokio::sync::broadcast;

use libunftp::ServerBuilder;
use unftp_core::auth::{Authenticator, AuthenticationError, Credentials, Principal, UserDetail, UserDetailError, UserDetailProvider};
use unftp_core::storage::{Error as StorageError, ErrorKind, Fileinfo, Result as StorageResult, StorageBackend};
use unftp_sbe_fs::{Filesystem, Meta};
use unftp_sbe_rooter::{RooterVfs, UserWithRoot};
use tokio::sync::oneshot;

pub type LogCallback = Option<unsafe extern "C" fn(level: c_int, msg: *const c_char, ctx: *mut c_void)>;

#[derive(Debug, Clone)]
struct UserEntry {
    password: String,
    read_only: bool,
    // 用户专属根路径；None 表示退回到全局默认 root_dir
    root: Option<PathBuf>,
    // 是否启用。关闭后 Rust Authenticator 直接拒绝（530 Login incorrect），
    // 不进入 ANON→guest 兜底链路。
    enabled: bool,
}

// Authenticator 返回 Principal，UserDetailProvider 把 Principal + UserEntry → AppUser
#[derive(Debug, Clone, PartialEq, Eq)]
struct AppUser {
    username: String,
    root: Option<PathBuf>,
    /// true = 只读（guest 或匿名 fallback）
    read_only: bool,
}

impl UserDetail for AppUser {
    fn account_enabled(&self) -> bool { true }
    fn home(&self) -> Option<&Path> { self.root.as_deref() }
}

impl fmt::Display for AppUser {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "User({})", self.username)
    }
}

impl UserWithRoot for AppUser {
    fn user_root(&self) -> Option<PathBuf> {
        self.root.clone()
    }
}

struct RuntimeHandle {
    thread_handle: Option<ThreadJoinHandle<()>>,
    shutdown_tx: Option<oneshot::Sender<()>>,
}

// ──────────────────────────────────────────────
// 在线会话注册表
// ──────────────────────────────────────────────

#[derive(Clone, Debug)]
struct SessionInfo {
    username: String,
    #[allow(dead_code)]
    online_at: Instant,
    last_seen: Instant,
    ops_count: u64,
}

#[derive(Clone)]
struct SessionRegistry {
    sessions: Arc<Mutex<HashMap<String, SessionInfo>>>,
    logger: Arc<dyn Fn(c_int, &str) + Send + Sync>,
}

impl SessionRegistry {
    fn new(logger: Arc<dyn Fn(c_int, &str) + Send + Sync>) -> Self {
        Self { sessions: Arc::new(Mutex::new(HashMap::new())), logger }
    }
    fn on_login(&self, username: &str) {
        let now = Instant::now();
        let mut g = self.sessions.lock().unwrap();
        let is_new = !g.contains_key(username);
        g.insert(username.to_string(), SessionInfo {
            username: username.to_string(),
            online_at: now,
            last_seen: now,
            ops_count: 0,
        });
        drop(g);
        if is_new { (self.logger)(4, &format!("SESSION:ONLINE user={}", username)); }
    }
    fn on_op(&self, username: &str) {
        let mut g = self.sessions.lock().unwrap();
        if let Some(s) = g.get_mut(username) {
            s.last_seen = Instant::now();
            s.ops_count += 1;
        }
    }
    fn remove(&self, username: &str) {
        let mut g = self.sessions.lock().unwrap();
        if g.remove(username).is_some() {
            (self.logger)(4, &format!("SESSION:OFFLINE user={} (idle)", username));
        }
    }
    /// idle 清理：idle 超过 30 秒踢掉。
    /// 返回一个 Future，需要在 Tokio runtime 里 await（不能用 tokio::spawn，
    /// 因为调用点不在 runtime context 里）。
    async fn idle_cleanup(self, mut stop: broadcast::Receiver<()>) {
        let mut tick = tokio::time::interval(Duration::from_secs(1));
        loop {
            tokio::select! {
                _ = tick.tick() => {
                    let now = Instant::now();
                    let victims: Vec<String> = {
                        let g = self.sessions.lock().unwrap();
                        g.values().filter(|s| now.duration_since(s.last_seen) > Duration::from_secs(30))
                            .map(|s| s.username.clone()).collect()
                    };
                    for u in victims { self.remove(&u); }
                }
                _ = stop.recv() => break,
            }
        }
        // 停止事件：全部 offline
        let all: Vec<String> = self.sessions.lock().unwrap().keys().cloned().collect();
        for u in all { self.remove(&u); }
    }
}

/// StorageBackend wrapper：审计日志 + 只读拦截 + session 刷新

#[derive(Clone)]
struct ReadOnlyWrapper<S> {
    inner: S,
    logger: Arc<dyn Fn(c_int, &str) + Send + Sync>,
    sessions: SessionRegistry,
}

impl<S: std::fmt::Debug> std::fmt::Debug for ReadOnlyWrapper<S> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ReadOnlyWrapper").field("inner", &self.inner).finish()
    }
}

impl<S> ReadOnlyWrapper<S> {
    fn new(inner: S, logger: Arc<dyn Fn(c_int, &str) + Send + Sync>, sessions: SessionRegistry) -> Self {
        Self { inner, logger, sessions }
    }

    #[inline]
    fn log(&self, level: c_int, msg: &str) { (self.logger)(level, msg); }

    /// 判断这个 StorageError 是不是"客户端正常关闭数据连接"造成的。
    /// FTP 客户端在文件传完后会主动 FIN/RST 数据连接（或 FileZilla 有时直接 RST），
    /// tokio::io::copy 因此返回 BrokenPipe/ConnectionReset → 被映射成 ErrorKind::ConnectionClosed。
    /// 这属于正常收尾，不是真正的传输失败。
    #[inline]
    fn is_client_conn_closed(e: &StorageError) -> bool {
        matches!(e.kind(), ErrorKind::ConnectionClosed)
    }

    #[inline]
    fn deny_if_readonly(&self, user: &AppUser, _op: &str, cn_op: &str) -> StorageResult<()> {
        if user.read_only {
            self.log(2, &format!("访问拒绝：用户「{}」尝试{}，但该用户为只读权限", user.username, cn_op));
            return Err(StorageError::new(ErrorKind::PermissionDenied, "read-only user cannot modify files"));
        }
        Ok(())
    }
}

#[async_trait::async_trait]
impl<S> StorageBackend<AppUser> for ReadOnlyWrapper<S>
where
    S: StorageBackend<AppUser> + Send + Sync,
    S::Metadata: Send + Sync + 'static,
{
    type Metadata = S::Metadata;

    fn name(&self) -> &str { self.inner.name() }
    fn supported_features(&self) -> u32 { self.inner.supported_features() }

    // ── 读操作：成功 + 失败都打（但措辞精确，避免笼统"下载"混淆加载目录列表）──
    async fn metadata<P: AsRef<Path> + Send + Debug>(&self, user: &AppUser, path: P) -> StorageResult<Self::Metadata> {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        self.inner.metadata(user, path).await.inspect_err(|e| {
            self.log(2, &format!("操作失败：用户「{}」查询元数据 {:?} — {}", user.username, p, e));
        })
    }
    async fn list<P: AsRef<Path> + Send + Debug>(&self, user: &AppUser, path: P) -> StorageResult<Vec<Fileinfo<PathBuf, Self::Metadata>>> {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        let r = self.inner.list(user, path).await;
        match &r {
            Ok(items) => self.log(1, &format!("列出目录：用户「{}」目录 {:?}（{} 项）", user.username, p, items.len())),
            Err(e) => self.log(2, &format!("操作失败：用户「{}」列出目录 {:?} — {}", user.username, p, e)),
        }
        r
    }
    async fn get_into<'a, P, W: ?Sized>(&self, user: &AppUser, path: P, start_pos: u64, output: &'a mut W) -> StorageResult<u64>
    where W: tokio::io::AsyncWrite + Unpin + Sync + Send, P: AsRef<Path> + Send + Debug {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        let r = self.inner.get_into(user, path, start_pos, output).await;
        match &r {
            Ok(sz) => self.log(1, &format!("下载成功：用户「{}」下载 {:?}（{} 字节）", user.username, p, sz)),
            Err(e) if Self::is_client_conn_closed(e) => {
                self.log(1, &format!("下载结束：用户「{}」{:?} 数据连接关闭（可能为正常收尾）", user.username, p));
            }
            Err(e) => self.log(2, &format!("操作失败：用户「{}」下载文件 {:?} — {}", user.username, p, e)),
        }
        r
    }
    async fn get<P: AsRef<Path> + Send + Debug>(&self, user: &AppUser, path: P, start_pos: u64) -> StorageResult<Box<dyn tokio::io::AsyncRead + Send + Sync + Unpin>> {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        let r = self.inner.get(user, path, start_pos).await;
        match &r {
            Ok(_) => self.log(1, &format!("打开文件数据流：用户「{}」打开 {:?} 偏移 {}", user.username, p, start_pos)),
            Err(e) if Self::is_client_conn_closed(e) => {
                self.log(1, &format!("数据流结束：用户「{}」{:?} 数据连接关闭（正常收尾）", user.username, p));
            }
            Err(e) => self.log(2, &format!("操作失败：用户「{}」打开文件数据流 {:?} — {}", user.username, p, e)),
        }
        r
    }
    async fn cwd<P: AsRef<Path> + Send + Debug>(&self, user: &AppUser, path: P) -> StorageResult<()> {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        let r = self.inner.cwd(user, path).await;
        match &r {
            Ok(()) => self.log(1, &format!("目录切换：用户「{}」当前目录 {:?}", user.username, p)),
            Err(e) => self.log(2, &format!("操作失败：用户「{}」切换目录 {:?} — {}", user.username, p, e)),
        }
        r
    }

    // ── 写操作：成功 + 拒绝 + 失败都打 ──
    async fn put<P: AsRef<Path> + Send + Debug, R: tokio::io::AsyncRead + Send + Sync + Unpin + 'static>(
        &self, user: &AppUser, input: R, path: P, start_pos: u64) -> StorageResult<u64> {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        self.deny_if_readonly(user, "STOR", "上传文件")?;
        let r = self.inner.put(user, input, path, start_pos).await;
        match &r {
            Ok(sz) => self.log(1, &format!("上传成功：用户「{}」{:?} 大小 {} 字节", user.username, p, sz)),
            Err(e) if Self::is_client_conn_closed(e) => {
                self.log(1, &format!("上传结束：用户「{}」{:?} 数据连接关闭（可能为正常收尾）", user.username, p));
            }
            Err(e) => self.log(2, &format!("操作失败：用户「{}」上传 {:?} — {}", user.username, p, e)),
        }
        r
    }
    async fn del<P: AsRef<Path> + Send + Debug>(&self, user: &AppUser, path: P) -> StorageResult<()> {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        self.deny_if_readonly(user, "DELE", "删除文件")?;
        let r = self.inner.del(user, path).await;
        match &r {
            Ok(()) => self.log(1, &format!("删除成功：用户「{}」删除文件 {:?}", user.username, p)),
            Err(e) => self.log(2, &format!("操作失败：用户「{}」删除 {:?} — {}", user.username, p, e)),
        }
        r
    }
    async fn mkd<P: AsRef<Path> + Send + Debug>(&self, user: &AppUser, path: P) -> StorageResult<()> {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        self.deny_if_readonly(user, "MKD", "创建目录")?;
        let r = self.inner.mkd(user, path).await;
        match &r {
            Ok(()) => self.log(1, &format!("创建成功：用户「{}」创建目录 {:?}", user.username, p)),
            Err(e) => self.log(2, &format!("操作失败：用户「{}」创建目录 {:?} — {}", user.username, p, e)),
        }
        r
    }
    async fn rmd<P: AsRef<Path> + Send + Debug>(&self, user: &AppUser, path: P) -> StorageResult<()> {
        let p = path.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        self.deny_if_readonly(user, "RMD", "删除目录")?;
        let r = self.inner.rmd(user, path).await;
        match &r {
            Ok(()) => self.log(1, &format!("删除成功：用户「{}」删除目录 {:?}", user.username, p)),
            Err(e) => self.log(2, &format!("操作失败：用户「{}」删除目录 {:?} — {}", user.username, p, e)),
        }
        r
    }
    async fn rename<P: AsRef<Path> + Send + Debug>(&self, user: &AppUser, from: P, to: P) -> StorageResult<()> {
        let f = from.as_ref().to_path_buf();
        let t = to.as_ref().to_path_buf();
        self.sessions.on_op(&user.username);
        self.deny_if_readonly(user, "RNFR", "重命名")?;
        let r = self.inner.rename(user, from, to).await;
        match &r {
            Ok(()) => self.log(1, &format!("重命名成功：用户「{}」{:?} → {:?}", user.username, f, t)),
            Err(e) => self.log(2, &format!("操作失败：用户「{}」重命名 {:?}→{:?} — {}", user.username, f, t, e)),
        }
        r
    }
}

pub struct FtpServer {
    port: Mutex<u16>,
    root_dir: Mutex<PathBuf>,
    #[allow(dead_code)]
    passive_port_range: Mutex<(u16, u16)>,
    users: Mutex<HashMap<String, UserEntry>>,

    log_cb: Mutex<LogCallback>,
    log_ctx: Mutex<*mut c_void>,

    running: Arc<AtomicBool>,
    runtime: Mutex<Option<RuntimeHandle>>,
}

unsafe impl Send for FtpServer {}
unsafe impl Sync for FtpServer {}

impl FtpServer {
    fn new() -> Self {
        Self {
            port: Mutex::new(2121),
            root_dir: Mutex::new(PathBuf::from(".")),
            passive_port_range: Mutex::new((50000, 60000)),
            users: Mutex::new(HashMap::new()),
            log_cb: Mutex::new(None),
            log_ctx: Mutex::new(std::ptr::null_mut()),
            running: Arc::new(AtomicBool::new(false)),
            runtime: Mutex::new(None),
        }
    }

    fn emit_log(&self, level: c_int, msg: &str) {
        eprintln!("[ftp_core][{}] {}", level_name(level), msg);
        let cb = *self.log_cb.lock().unwrap();
        let ctx = *self.log_ctx.lock().unwrap();
        if let Some(f) = cb {
            if let Ok(cs) = std::ffi::CString::new(msg) {
                unsafe { f(level, cs.as_ptr(), ctx); }
            }
        }
    }
}

// ---- Authenticator: 永远放行（实现 anonymous 模式）
// 标记规则：Principal.username 带前缀
//   "AUTHED:original_name" → 已验证密码正确
//   "ANON:original_name"   → 用户不存在 / 密码错误 → UserDetailProvider 会映射到 guest

#[derive(Clone)]
struct SimpleAuth {
    users: Arc<Mutex<HashMap<String, UserEntry>>>,
    logger: Arc<dyn Fn(c_int, &str) + Send + Sync>,
    sessions: SessionRegistry,
}

impl std::fmt::Debug for SimpleAuth {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SimpleAuth").field("users", &self.users).finish()
    }
}

const AUTHED_PREFIX: &str = "AUTHED:";
const ANON_PREFIX: &str = "ANON:";

#[async_trait::async_trait]
impl Authenticator for SimpleAuth {
    async fn authenticate(
        &self,
        username: &str,
        creds: &Credentials,
    ) -> Result<Principal, AuthenticationError> {
        let password = creds.password.as_deref().unwrap_or("");
        let guard = self.users.lock().unwrap();
        match guard.get(username) {
            Some(u) if u.enabled && u.password == password => {
                (self.logger)(1, &format!("登录成功：用户「{}」（已注册）", username));
                drop(guard);
                self.sessions.on_login(username);
                Ok(Principal { username: format!("{}{}", AUTHED_PREFIX, username) })
            }
            Some(u) if !u.enabled => {
                // 用户存在但被禁用 → 直接拒绝（不泄露"用户存在但禁用"，按 FTP 规范统一回 530）
                (self.logger)(2, &format!("登录拒绝：用户「{}」已被禁用", username));
                Err(AuthenticationError::new("user disabled"))
            }
            Some(_) => {
                // 用户存在但密码错误 → guest 兜底（guest 禁用则 guest 兜底也拒绝）
                if let Some(guest) = guard.get("guest") {
                    if !guest.enabled {
                        (self.logger)(2, &format!("登录拒绝：用户「{}」密码错误且访客（guest）已禁用", username));
                        return Err(AuthenticationError::new("guest unavailable"));
                    }
                } else {
                    // guest 根本没配置 → 任何未知用户都应拒绝
                    (self.logger)(2, &format!("登录拒绝：用户「{}」密码错误，且没有可用的访客账号", username));
                    return Err(AuthenticationError::new("no guest account"));
                }
                (self.logger)(2, &format!("登录失败：用户「{}」密码错误，以访客身份处理", username));
                drop(guard);
                self.sessions.on_login(username);
                Ok(Principal { username: format!("{}{}", ANON_PREFIX, username) })
            }
            None => {
                // 用户不存在 → guest 兜底（guest 禁用则 guest 兜底也拒绝）
                if let Some(guest) = guard.get("guest") {
                    if !guest.enabled {
                        (self.logger)(2, &format!("登录拒绝：用户「{}」不存在且访客（guest）已禁用", username));
                        return Err(AuthenticationError::new("guest unavailable"));
                    }
                } else {
                    (self.logger)(2, &format!("登录拒绝：用户「{}」不存在，且没有可用的访客账号", username));
                    return Err(AuthenticationError::new("no guest account"));
                }
                (self.logger)(2, &format!("登录失败：用户「{}」不存在，以访客身份处理", username));
                drop(guard);
                self.sessions.on_login(username);
                Ok(Principal { username: format!("{}{}", ANON_PREFIX, username) })
            }
        }
    }
}

// ---- UserDetailProvider: 根据 Authenticator 的前缀标记决定用谁的权限
// AUTHED:xxx → 用 xxx 自己的 root/read_only
// ANON:xxx   → 用 guest 的 root/read_only（guest 必须存在，否则 fallback 到 None→全局 root）

#[derive(Debug, Clone)]
struct AppUserProvider {
    users: Arc<Mutex<HashMap<String, UserEntry>>>,
}

fn extract_username(principal: &Principal) -> (&str, bool /* authed */) {
    if let Some(name) = principal.username.strip_prefix(AUTHED_PREFIX) {
        (name, true)
    } else if let Some(name) = principal.username.strip_prefix(ANON_PREFIX) {
        (name, false)
    } else {
        // 没有前缀的旧调用，当作 AUTHED 对待（向后兼容）
        (principal.username.as_str(), true)
    }
}

#[async_trait::async_trait]
impl UserDetailProvider for AppUserProvider {
    type User = AppUser;

    async fn provide_user_detail(&self, principal: &Principal) -> Result<AppUser, UserDetailError> {
        let (name, authed) = extract_username(principal);
        let guard = self.users.lock().unwrap();

        if authed {
            // 密码验证通过 → 用该用户自己的配置
            match guard.get(name) {
                Some(u) => Ok(AppUser {
                    username: name.to_string(),
                    root: u.root.clone(),
                    read_only: u.read_only,
                }),
                None => {
                    eprintln!("[ftp_core] AUTHED user not found in map (shouldn't happen) → guest fallback");
                    Ok(resolve_guest(&guard, name))
                }
            }
        } else {
            // 未授权 → 映射到 guest（guest 不存在就用匿名配置：None→全局root）
            eprintln!("[ftp_core] ANON login user={} → treat as guest", name);
            Ok(resolve_guest(&guard, name))
        }
    }
}

/// 查 guest 的配置；找不到就返回匿名只读配置（read_only=true + root=None→全局root）
fn resolve_guest<'a>(guard: &'a std::sync::MutexGuard<'a, HashMap<String, UserEntry>>,
                     anon_username: &str) -> AppUser {
    match guard.get("guest") {
        Some(g) => AppUser {
            username: format!("{} (as guest)", anon_username),
            root: g.root.clone(),
            read_only: true, // ANON 一律只读，强制
        },
        None => AppUser {
            username: format!("{} (anonymous)", anon_username),
            root: None,      // RooterVfs 里会 fallback 到全局 root_dir
            read_only: true, // 匿名一律只读
        },
    }
}

// ---- 导出函数 ----

#[no_mangle]
pub extern "C" fn ftp_server_new() -> *mut FtpServer {
    eprintln!("[ftp_core] ftp_server_new() called");
    Box::into_raw(Box::new(FtpServer::new()))
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_free(handle: *mut FtpServer) {
    if handle.is_null() { return; }
    let _ = ftp_server_stop(handle);
    let srv = Box::from_raw(handle);
    *srv.log_cb.lock().unwrap() = None;
    *srv.log_ctx.lock().unwrap() = std::ptr::null_mut();
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_set_port(handle: *mut FtpServer, port: u16) -> c_int {
    if handle.is_null() { return -1; }
    let srv = &*handle;
    if srv.running.load(Ordering::Relaxed) { return -2; }
    *srv.port.lock().unwrap() = port;
    0
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_set_root(handle: *mut FtpServer, path: *const c_char) -> c_int {
    if handle.is_null() || path.is_null() { return -1; }
    let srv = &*handle;
    if srv.running.load(Ordering::Relaxed) { return -2; }
    let cstr = CStr::from_ptr(path);
    let p = match cstr.to_str() { Ok(s) => PathBuf::from(s), Err(_) => return -3 };
    *srv.root_dir.lock().unwrap() = p;
    0
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_set_max_conn(handle: *mut FtpServer, _n: u32) -> c_int {
    // libunftp 0.23 未暴露 max_connections API，占位
    if handle.is_null() { return -1; }
    0
}

/// 基础版本（向后兼容），默认 enabled=1
#[no_mangle]
pub unsafe extern "C" fn ftp_server_add_user(
    handle: *mut FtpServer,
    user: *const c_char,
    pass: *const c_char,
    readonly: c_int,
) -> c_int {
    ftp_server_add_user_ex(handle, user, pass, readonly, std::ptr::null(), 1)
}

/// 完整版：允许为用户指定专属根路径 + 是否启用
///   root == NULL 或空串 → 退回到全局默认
///   enabled == 0 → 禁用；密码对的用户也直接拒绝登录
#[no_mangle]
pub unsafe extern "C" fn ftp_server_add_user_with_root(
    handle: *mut FtpServer,
    user: *const c_char,
    pass: *const c_char,
    readonly: c_int,
    root: *const c_char,
) -> c_int {
    ftp_server_add_user_ex(handle, user, pass, readonly, root, 1)
}

/// 内部扩展版：enabled 参数
#[no_mangle]
pub unsafe extern "C" fn ftp_server_add_user_ex(
    handle: *mut FtpServer,
    user: *const c_char,
    pass: *const c_char,
    readonly: c_int,
    root: *const c_char,
    enabled: c_int,
) -> c_int {
    if handle.is_null() || user.is_null() || pass.is_null() { return -1; }
    let srv = &*handle;
    let u = match CStr::from_ptr(user).to_str() { Ok(s) => s.to_string(), Err(_) => return -2 };
    let p = match CStr::from_ptr(pass).to_str() { Ok(s) => s.to_string(), Err(_) => return -2 };
    let root_opt = if root.is_null() {
        None
    } else {
        let rs = CStr::from_ptr(root).to_str().map(|s| s.trim()).unwrap_or("");
        if rs.is_empty() { None } else { Some(PathBuf::from(rs)) }
    };
    eprintln!("[ftp_core] add_user user={} readonly={} root={:?} enabled={}", u, readonly, root_opt, enabled);
    let entry = UserEntry { password: p, read_only: readonly != 0, root: root_opt, enabled: enabled != 0 };
    srv.users.lock().unwrap().insert(u, entry);
    0
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_remove_user(handle: *mut FtpServer, user: *const c_char) -> c_int {
    if handle.is_null() || user.is_null() { return -1; }
    let srv = &*handle;
    let u = match CStr::from_ptr(user).to_str() { Ok(s) => s, Err(_) => return -2 };
    srv.users.lock().unwrap().remove(u);
    0
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_clear_users(handle: *mut FtpServer) -> c_int {
    if handle.is_null() { return -1; }
    let srv = &*handle;
    srv.users.lock().unwrap().clear();
    0
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_set_log_cb(
    handle: *mut FtpServer,
    cb: LogCallback,
    ctx: *mut c_void,
) -> c_int {
    if handle.is_null() { return -1; }
    let srv = &*handle;
    *srv.log_cb.lock().unwrap() = cb;
    *srv.log_ctx.lock().unwrap() = ctx;
    0
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_start(handle: *mut FtpServer) -> c_int {
    if handle.is_null() { return -1; }
    let srv = &*handle;
    if srv.running.load(Ordering::Relaxed) { return -2; }

    let port = *srv.port.lock().unwrap();
    let root = srv.root_dir.lock().unwrap().clone();
    let users = Arc::new(Mutex::new(srv.users.lock().unwrap().clone()));
    if !root.exists() {
        srv.emit_log(3, &format!("root dir invalid: {:?}", root));
        return -3;
    }
    if users.lock().unwrap().is_empty() {
        srv.emit_log(3, "no users configured");
        return -4;
    }

    let running = Arc::clone(&srv.running);
    let log_cb = *srv.log_cb.lock().unwrap();
    let log_ctx = *srv.log_ctx.lock().unwrap();
    // 函数指针和 raw ptr 都是 !Send，用 usize 绕一下
    let log_cb_addr = log_cb.map(|f| f as usize).unwrap_or(0usize);
    let log_ctx_addr = log_ctx as usize;

    let (shutdown_tx, shutdown_rx) = oneshot::channel::<()>();
    let running_clone = running.clone();
    let root_display = format!("{:?}", root);
    let bind_str = format!("0.0.0.0:{}", port);

    let thread_handle = std::thread::Builder::new()
        .name("ftp-core-worker".into())
        .spawn(move || {
            let cb_opt: LogCallback = if log_cb_addr != 0 {
                Some(unsafe { std::mem::transmute::<usize, unsafe extern "C" fn(c_int, *const c_char, *mut c_void)>(log_cb_addr) })
            } else { None };
            let ctx = log_ctx_addr as *mut c_void;

            // ── 1. 安装 panic hook：任何 Rust panic 先转 ERROR 日志再 abort ──
            // （无论 Release abort 还是 Debug unwind，hook 都会被调用；
            //   abort 模式下 hook 跑完才终止进程，所以 C# 侧一定能收到最后一条 PANIC 日志）
            {
                let cb_addr = log_cb_addr;  // usize 已是 Send + Sync
                let ctx_addr = log_ctx_addr;
                panic::set_hook(Box::new(move |info| {
                    let msg = format!(
                        "[RUST PANIC] {}\n{}",
                        info.payload().downcast_ref::<&str>().cloned().unwrap_or("<unknown>"),
                        info.location().map(|l| format!("at {}:{}", l.file(), l.line())).unwrap_or_default(),
                    );
                    // 先打到 stderr（Debug 控制台必现）
                    eprintln!("{}", msg);
                    // 再推给 C# 侧日志（level=3 错误），usize → transmute 回来
                    let cb: LogCallback = if cb_addr != 0 {
                        Some(unsafe { std::mem::transmute::<usize, unsafe extern "C" fn(c_int, *const c_char, *mut c_void)>(cb_addr) })
                    } else { None };
                    let ctx = ctx_addr as *mut c_void;
                    emit(&cb, ctx, 3, &msg);
                }));
            }

            let rt = match tokio::runtime::Builder::new_multi_thread()
                .worker_threads(2)
                .enable_io()
                .enable_time()
                .build()
            {
                Ok(r) => r,
                Err(e) => { emit(&cb_opt, ctx, 3, &format!("tokio rt failed: {}", e)); return; }
            };

            run_server(
                rt, &cb_opt, ctx, &log_cb_addr, &log_ctx_addr,
                &running_clone, &users, &root, &bind_str, &root_display,
                shutdown_rx,
            );

            emit(&cb_opt, ctx, 1, "FTP worker 退出");
            running_clone.store(false, Ordering::Release);
        });

    let thread_handle = match thread_handle {
        Ok(h) => h,
        Err(e) => {
            srv.emit_log(3, &format!("spawn thread failed: {}", e));
            return -5;
        }
    };

    *srv.runtime.lock().unwrap() = Some(RuntimeHandle {
        thread_handle: Some(thread_handle),
        shutdown_tx: Some(shutdown_tx),
    });
    0
}

/// FTP worker 主循环。内部任何 panic 都会先经 hook 转 ERROR 日志，然后 abort。
fn run_server(
    rt: tokio::runtime::Runtime,
    cb_opt: &LogCallback,
    ctx: *mut c_void,
    log_cb_addr: &usize,
    log_ctx_addr: &usize,
    running_clone: &Arc<AtomicBool>,
    users: &Arc<Mutex<HashMap<String, UserEntry>>>,
    root: &PathBuf,
    bind_str: &str,
    root_display: &str,
    shutdown_rx: oneshot::Receiver<()>,
) {
    // 统一 logger closure
    let cb_addr = *log_cb_addr;
    let ctx_addr = *log_ctx_addr;
    let logger: Arc<dyn Fn(c_int, &str) + Send + Sync> = Arc::new(move |level: c_int, msg: &str| {
        let cb: LogCallback = if cb_addr != 0 {
            Some(unsafe { std::mem::transmute::<usize, unsafe extern "C" fn(c_int, *const c_char, *mut c_void)>(cb_addr) })
        } else { None };
        let ctx = ctx_addr as *mut c_void;
        emit(&cb, ctx, level, msg);
    });

    let root_for_factory = root.clone();
    let sessions = SessionRegistry::new(logger.clone());

    let sessions_for_factory = sessions.clone();
    let logger_for_factory = logger.clone();
    let factory: Box<dyn (Fn() -> ReadOnlyWrapper<RooterVfs<Filesystem, AppUser, Meta>>) + Send + Sync> =
        Box::new(move || {
            let fs = Filesystem::new(root_for_factory.clone())
                .expect("Filesystem factory failed");
            let rooter = RooterVfs::<Filesystem, AppUser, Meta>::new(fs);
            ReadOnlyWrapper::new(rooter, logger_for_factory.clone(), sessions_for_factory.clone())
        });

    let auth = SimpleAuth { users: Arc::clone(users), logger: logger.clone(), sessions: sessions.clone() };
    let provider = AppUserProvider { users: Arc::clone(users) };

    let server = ServerBuilder::with_user_detail_provider(factory, Arc::new(provider))
        .authenticator(Arc::new(auth))
        .greeting("FServer FTP ready")
        .build()
        .expect("server build failed");

    let (stop_tx, stop_rx) = broadcast::channel::<()>(1);
    let cleanup_fut = sessions.idle_cleanup(stop_rx);

    running_clone.store(true, Ordering::Release);
    emit(cb_opt, ctx, 1, &format!("FTP 服务已启动，监听 {}，根目录 {:?}", bind_str, root_display));

    rt.block_on(async move {
        let listen_fut = server.listen(bind_str);
        tokio::select! {
            r = listen_fut => {
                match r {
                    Ok(_) => emit(cb_opt, ctx, 2, "服务监听已返回"),
                    Err(e) => emit(cb_opt, ctx, 3, &format!("监听错误：{}", e)),
                }
                let _ = stop_tx.send(());
            }
            _ = shutdown_rx => {
                emit(cb_opt, ctx, 1, "收到停止信号，正在关闭...");
                let _ = stop_tx.send(());
            }
            _ = cleanup_fut => { /* 不会单独退出，tick 永远跑 */ }
        }
    });

    emit(cb_opt, ctx, 1, "工作线程退出");
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_stop(handle: *mut FtpServer) -> c_int {
    if handle.is_null() { return -1; }
    let srv = &*handle;
    if !srv.running.load(Ordering::Relaxed) { return 0; }

    let rh_opt = srv.runtime.lock().unwrap().take();
    if let Some(mut rh) = rh_opt {
        if let Some(tx) = rh.shutdown_tx.take() {
            let _ = tx.send(());
        }
        if let Some(h) = rh.thread_handle.take() {
            let _ = h.join();
        }
    }
    0
}

#[no_mangle]
pub unsafe extern "C" fn ftp_server_is_running(handle: *mut FtpServer) -> c_int {
    if handle.is_null() { return 0; }
    if (*handle).running.load(Ordering::Relaxed) { 1 } else { 0 }
}

fn emit(cb: &LogCallback, ctx: *mut c_void, level: c_int, msg: &str) {
    eprintln!("[ftp_core][{}] {}", level_name(level), msg);
    if let Some(f) = *cb {
        if let Ok(cs) = std::ffi::CString::new(msg) {
            unsafe { f(level, cs.as_ptr(), ctx); }
        }
    }
}

fn level_name(l: c_int) -> &'static str {
    match l { 0 => "DEBUG", 1 => "INFO", 2 => "WARN", 3 => "ERROR", _ => "?" }
}
