using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using Microsoft.Data.Sqlite;

namespace FServer.App.Services
{
    /// <summary>
    /// 与 SQLite 持久化打交道的单一入口。
    /// 只负责：DDL（首次建表 + 增量迁移）、Load（启动时读）、Save（内存 → DB）。
    /// 数据源唯一：fserver.db 放在 exe 同目录。
    /// </summary>
    public class SettingsRepository : IDisposable
    {
        private readonly string _dbPath;
        private readonly string _connStr;

        public SettingsRepository()
        {
            _dbPath = Path.Combine(AppContext.BaseDirectory, "fserver.db");
            _connStr = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
            }.ToString();
        }

        public string DbPath => _dbPath;

        private void EnsureSchema(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS app_settings (
    id              INTEGER PRIMARY KEY CHECK (id = 1),
    port            INTEGER NOT NULL,
    root_dir        TEXT    NOT NULL,
    max_connections INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS users (
    username  TEXT    PRIMARY KEY,
    password  TEXT    NOT NULL,
    read_only INTEGER NOT NULL DEFAULT 0,
    root_path TEXT    NULL,
    enabled   INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE IF NOT EXISTS daily_stats (
    date        TEXT    PRIMARY KEY,
    bytes_down  INTEGER NOT NULL DEFAULT 0,
    bytes_up    INTEGER NOT NULL DEFAULT 0,
    total_ops   INTEGER NOT NULL DEFAULT 0
);
";
            cmd.ExecuteNonQuery();

            MigrateUsersEnabled(conn);
            MigrateAppSettingsBanner(conn);
        }

        private void MigrateUsersEnabled(SqliteConnection conn)
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(users)";
            bool hasEnabled = false;
            using (var reader = pragma.ExecuteReader())
            {
                while (reader.Read())
                {
                    var name = reader.GetString(1);
                    if (name == "enabled") { hasEnabled = true; break; }
                }
            }
            if (!hasEnabled)
            {
                using var alter = conn.CreateCommand();
                alter.CommandText = "ALTER TABLE users ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1";
                alter.ExecuteNonQuery();
            }
        }

        private void MigrateAppSettingsBanner(SqliteConnection conn)
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA table_info(app_settings)";
            bool hasBanner = false, hasPmin = false, hasPmax = false;
            bool hasSilent = false, hasAutoStart = false;
            using (var reader = pragma.ExecuteReader())
            {
                while (reader.Read())
                {
                    var n = reader.GetString(1);
                    if (n == "banner_text") hasBanner = true;
                    if (n == "passive_port_min") hasPmin = true;
                    if (n == "passive_port_max") hasPmax = true;
                    if (n == "silent_startup") hasSilent = true;
                    if (n == "auto_start_server") hasAutoStart = true;
                }
            }
            if (!hasBanner)    { using var a = conn.CreateCommand(); a.CommandText = "ALTER TABLE app_settings ADD COLUMN banner_text TEXT NULL"; a.ExecuteNonQuery(); }
            if (!hasPmin)      { using var a = conn.CreateCommand(); a.CommandText = "ALTER TABLE app_settings ADD COLUMN passive_port_min INTEGER NOT NULL DEFAULT 0"; a.ExecuteNonQuery(); }
            if (!hasPmax)      { using var a = conn.CreateCommand(); a.CommandText = "ALTER TABLE app_settings ADD COLUMN passive_port_max INTEGER NOT NULL DEFAULT 0"; a.ExecuteNonQuery(); }
            if (!hasSilent)    { using var a = conn.CreateCommand(); a.CommandText = "ALTER TABLE app_settings ADD COLUMN silent_startup INTEGER NOT NULL DEFAULT 0"; a.ExecuteNonQuery(); }
            if (!hasAutoStart) { using var a = conn.CreateCommand(); a.CommandText = "ALTER TABLE app_settings ADD COLUMN auto_start_server INTEGER NOT NULL DEFAULT 0"; a.ExecuteNonQuery(); }
        }

        public AppSettings LoadSettings()
        {
            using var conn = Open();
            EnsureSchema(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT port, root_dir, max_connections, banner_text, passive_port_min, passive_port_max, silent_startup, auto_start_server FROM app_settings WHERE id = 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new AppSettings
                {
                    Port = reader.GetInt32(0),
                    RootDir = reader.GetString(1),
                    MaxConnections = reader.GetInt32(2),
                    BannerText = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    PassivePortMin = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    PassivePortMax = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    SilentStartup = reader.IsDBNull(6) ? false : reader.GetInt32(6) != 0,
                    AutoStartServer = reader.IsDBNull(7) ? false : reader.GetInt32(7) != 0,
                };
            }
            return null;
        }

        public List<UserRow> LoadUsers()
        {
            var list = new List<UserRow>();
            using var conn = Open();
            EnsureSchema(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT username, password, read_only, root_path, enabled FROM users ORDER BY username";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new UserRow
                {
                    Username = reader.GetString(0),
                    Password = reader.GetString(1),
                    ReadOnly = reader.GetInt32(2) != 0,
                    RootPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Enabled = reader.GetInt32(4) != 0,
                });
            }
            return list;
        }

        public void SaveSettings(AppSettings s)
        {
            using var conn = Open();
            EnsureSchema(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT OR REPLACE INTO app_settings (id, port, root_dir, max_connections, banner_text, passive_port_min, passive_port_max, silent_startup, auto_start_server)
VALUES (1, $port, $root_dir, $max_conn, $banner, $pmin, $pmax, $silent, $auto)
";
            cmd.Parameters.AddWithValue("$port", s.Port);
            cmd.Parameters.AddWithValue("$root_dir", s.RootDir ?? "");
            cmd.Parameters.AddWithValue("$max_conn", s.MaxConnections);
            cmd.Parameters.AddWithValue("$banner", (object)(s.BannerText ?? "") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pmin", s.PassivePortMin);
            cmd.Parameters.AddWithValue("$pmax", s.PassivePortMax);
            cmd.Parameters.AddWithValue("$silent", s.SilentStartup ? 1 : 0);
            cmd.Parameters.AddWithValue("$auto", s.AutoStartServer ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        public void SaveUsers(IEnumerable<UserRow> users)
        {
            using var conn = Open();
            EnsureSchema(conn);
            using var tx = conn.BeginTransaction();
            try
            {
                using (var clear = conn.CreateCommand())
                {
                    clear.CommandText = "DELETE FROM users";
                    clear.ExecuteNonQuery();
                }
                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = @"
INSERT INTO users (username, password, read_only, root_path, enabled)
VALUES ($u, $p, $r, $rp, $e)
";
                    var pU = ins.Parameters.Add("$u", SqliteType.Text);
                    var pP = ins.Parameters.Add("$p", SqliteType.Text);
                    var pR = ins.Parameters.Add("$r", SqliteType.Integer);
                    var pRp = ins.Parameters.Add("$rp", SqliteType.Text);
                    var pE = ins.Parameters.Add("$e", SqliteType.Integer);
                    foreach (var u in users)
                    {
                        pU.Value = u.Username;
                        pP.Value = u.Password;
                        pR.Value = u.ReadOnly ? 1 : 0;
                        pRp.Value = (object)u.RootPath ?? DBNull.Value;
                        pE.Value = u.Enabled ? 1 : 0;
                        ins.ExecuteNonQuery();
                    }
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        /// <summary>累加某一天的下载/上传字节和操作数。date 用服务器本地日期（DateTime.Today）。</summary>
        public void IncrementDaily(DateTime date, ulong bytesDown, ulong bytesUp, ulong ops)
        {
            var key = date.ToString("yyyy-MM-dd");
            using var conn = Open();
            EnsureSchema(conn);

            ulong oldDown = 0, oldUp = 0, oldOps = 0;
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT bytes_down, bytes_up, total_ops FROM daily_stats WHERE date = $k";
                sel.Parameters.AddWithValue("$k", key);
                using var r = sel.ExecuteReader();
                if (r.Read())
                {
                    oldDown = (ulong)r.GetInt64(0);
                    oldUp = (ulong)r.GetInt64(1);
                    oldOps = (ulong)r.GetInt64(2);
                }
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = @"
INSERT OR REPLACE INTO daily_stats (date, bytes_down, bytes_up, total_ops)
VALUES ($k, $d, $u, $o)
";
            ins.Parameters.AddWithValue("$k", key);
            ins.Parameters.AddWithValue("$d", (long)(oldDown + bytesDown));
            ins.Parameters.AddWithValue("$u", (long)(oldUp + bytesUp));
            ins.Parameters.AddWithValue("$o", (long)(oldOps + ops));
            ins.ExecuteNonQuery();
        }

        /// <summary>取最近 N 天的统计（含今天，近→远）。返回 count 行，不足则返回实际天数（空行补齐 0）。</summary>
        public List<DailyStatRow> LoadRecentDays(int n)
        {
            var result = new List<DailyStatRow>();
            using var conn = Open();
            EnsureSchema(conn);

            var today = DateTime.Today;
            var keys = new List<string>();
            for (int i = n - 1; i >= 0; i--)
                keys.Add(today.AddDays(-i).ToString("yyyy-MM-dd"));

            var map = new Dictionary<string, DailyStatRow>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT date, bytes_down, bytes_up, total_ops FROM daily_stats WHERE date >= $from AND date <= $to";
                cmd.Parameters.AddWithValue("$from", keys[0]);
                cmd.Parameters.AddWithValue("$to", keys[keys.Count - 1]);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var k = r.GetString(0);
                    map[k] = new DailyStatRow
                    {
                        Date = DateTime.ParseExact(k, "yyyy-MM-dd", null),
                        BytesDown = (ulong)r.GetInt64(1),
                        BytesUp = (ulong)r.GetInt64(2),
                        TotalOps = (ulong)r.GetInt64(3),
                    };
                }
            }

            foreach (var k in keys)
            {
                if (map.TryGetValue(k, out var row)) result.Add(row);
                else result.Add(new DailyStatRow { Date = DateTime.ParseExact(k, "yyyy-MM-dd", null) });
            }
            return result;
        }

        public SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connStr);
            conn.Open();
            return conn;
        }

        public void Dispose() { }
    }

    public class AppSettings
    {
        public int Port { get; set; }
        public string RootDir { get; set; }
        public int MaxConnections { get; set; }
        /// <summary>登录横幅 / MOTD。空串 = 使用 libunftp 默认 "220 Ready"。</summary>
        public string BannerText { get; set; } = "";
        /// <summary>被动模式端口范围最小值（0 = 不限制）。</summary>
        public int PassivePortMin { get; set; } = 0;
        /// <summary>被动模式端口范围最大值（0 = 不限制）。</summary>
        public int PassivePortMax { get; set; } = 0;
        /// <summary>静默启动：exe 启动时主窗口自动隐藏，仅驻留托盘。</summary>
        public bool SilentStartup { get; set; }
        /// <summary>系统开机自启：通过 HKCU\...\Run 注册表项实现。</summary>
        public bool AutoStartServer { get; set; }
    }

    public class UserRow
    {
        public string Username { get; set; }
        public string Password { get; set; }
        public bool ReadOnly { get; set; }
        /// <summary>null / empty 代表"使用全局默认根路径"</summary>
        public string RootPath { get; set; }
        /// <summary>true = 启用登录；false = 禁用（Start 时跳过）</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>单日传输统计。</summary>
    public class DailyStatRow
    {
        public DateTime Date { get; set; }
        public ulong BytesDown { get; set; }
        public ulong BytesUp { get; set; }
        public ulong TotalOps { get; set; }
    }
}
