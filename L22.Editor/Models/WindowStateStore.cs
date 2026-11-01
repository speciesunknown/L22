using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace L22.Editor.Models;

// Tiny SQLite-backed key/value store for cross-session UI state. Lives at
// %LOCALAPPDATA%\L22Editor\state.db (equivalent on other OSes). First used
// for the main window's size / position / maximized flag so re-opening the
// editor lands where the user left it; the schema is intentionally generic
// so future UI prefs (panel split sizes, recently-opened docs) can land in
// the same table without a schema migration.
public static class WindowStateStore
{
    public static string DbPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "L22Editor",
        "state.db");

    private static string ConnString => $"Data Source={DbPath}";

    private static void EnsureSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE IF NOT EXISTS kv (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    private static SqliteConnection Open()
    {
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var conn = new SqliteConnection(ConnString);
        conn.Open();
        EnsureSchema(conn);
        return conn;
    }

    public static Dictionary<string, string> LoadAll()
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM kv;";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result[r.GetString(0)] = r.GetString(1);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WindowStateStore] load failed: {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    public static void Save(IReadOnlyDictionary<string, string> kv)
    {
        try
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "INSERT INTO kv(key, value) VALUES ($k, $v) " +
                "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
            var pk = cmd.CreateParameter(); pk.ParameterName = "$k"; cmd.Parameters.Add(pk);
            var pv = cmd.CreateParameter(); pv.ParameterName = "$v"; cmd.Parameters.Add(pv);
            foreach (var (k, v) in kv)
            {
                pk.Value = k;
                pv.Value = v;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WindowStateStore] save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
