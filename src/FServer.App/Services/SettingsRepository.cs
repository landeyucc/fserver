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

        // ──────────────────────────────────────────────
        // 表结构（首次建表 + 增量迁移）
        // ──────────────────────────────────────────────

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
";
            cmd.ExecuteNonQuery();

            // 增量迁移：旧版 users 表没有 enabled 列时补上
            MigrateUsersEnabled(conn);
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
                    // 列名在索引 1
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

        // ──────────────────────────────────────────────
        // 载入：DB 空则返回 null / 空列表
        // ──────────────────────────────────────────────

        public AppSettings LoadSettings()
        {
            using var conn = Open();
            EnsureSchema(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT port, root_dir, max_connections FROM app_settings WHERE id = 1";
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new AppSettings
                {
                    Port = reader.GetInt32(0),
                    RootDir = reader.GetString(1),
                    MaxConnections = reader.GetInt32(2),
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

        // ──────────────────────────────────────────────
        // 保存：全量重建（事务保护）
        // ──────────────────────────────────────────────

        public void SaveSettings(AppSettings s)
        {
            using var conn = Open();
            EnsureSchema(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT OR REPLACE INTO app_settings (id, port, root_dir, max_connections)
VALUES (1, $port, $root_dir, $max_conn)
";
            cmd.Parameters.AddWithValue("$port", s.Port);
            cmd.Parameters.AddWithValue("$root_dir", s.RootDir ?? "");
            cmd.Parameters.AddWithValue("$max_conn", s.MaxConnections);
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

        // ──────────────────────────────────────────────
        // 辅助
        // ──────────────────────────────────────────────

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connStr);
            conn.Open();
            return conn;
        }

        public void Dispose() { /* connection 都在用完即关闭 */ }
    }

    public class AppSettings
    {
        public int Port { get; set; }
        public string RootDir { get; set; }
        public int MaxConnections { get; set; }
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
}
