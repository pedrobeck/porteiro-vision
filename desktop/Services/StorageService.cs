using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Vision.Models;

namespace Vision.Services;

public class StorageService
{
    private readonly object _lock = new();
    private readonly string _dataDir;
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly string _camerasFile;
    private readonly string _settingsFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public StorageService(string? baseDir = null)
    {
        baseDir ??= ResolveBaseDir();
        _dataDir = Path.Combine(baseDir, "data");
        _camerasFile = Path.Combine(_dataDir, "cameras.json");
        _settingsFile = Path.Combine(_dataDir, "settings.json");
        _dbPath = Path.Combine(_dataDir, "vision.db");
        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;";

        EnsureDir();
        InitDatabase();
        MigrateJsonIfEmpty();
    }

    public static string ResolveBaseDir()
    {
        string projDir = @"D:\Projetos\Vision";
        if (Directory.Exists(projDir) && Directory.Exists(Path.Combine(projDir, "static")) && Directory.Exists(Path.Combine(projDir, "templates")))
            return projDir;

        string candidate = AppContext.BaseDirectory;
        string parent = Path.GetFullPath(Path.Combine(candidate, ".."));
        if (Directory.Exists(Path.Combine(parent, "static")) && Directory.Exists(Path.Combine(parent, "templates")))
            return parent;

        if (Directory.Exists(Path.Combine(candidate, "static")) && Directory.Exists(Path.Combine(candidate, "templates")))
            return candidate;

        return projDir;
    }

    private void EnsureDir()
    {
        try
        {
            if (!Directory.Exists(_dataDir))
            {
                Directory.CreateDirectory(_dataDir);
            }
        }
        catch { }
    }

    private SqliteConnection CreateConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private void InitDatabase()
    {
        lock (_lock)
        {
            try
            {
                using var conn = CreateConnection();

                // Habilita Write-Ahead Logging (WAL) para alta concorrência e zero corrupção
                using (var pragmaCmd = conn.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                    pragmaCmd.ExecuteNonQuery();
                }

                // Criação das tabelas
                string createTablesSql = @"
                    CREATE TABLE IF NOT EXISTS cameras (
                        id TEXT PRIMARY KEY,
                        name TEXT NOT NULL,
                        host TEXT,
                        port INTEGER DEFAULT 554,
                        protocol TEXT DEFAULT 'rtsp',
                        brand TEXT DEFAULT 'generic',
                        username TEXT,
                        password TEXT,
                        channel INTEGER DEFAULT 1,
                        stream_type TEXT DEFAULT 'sub',
                        custom_url TEXT,
                        ptz_enabled INTEGER DEFAULT 0,
                        multi_lens INTEGER DEFAULT 0,
                        display_order REAL DEFAULT 0,
                        created_at TEXT,
                        motion_sensitivity TEXT DEFAULT 'medium',
                        parent_id TEXT,
                        is_sub_lens INTEGER DEFAULT 0
                    );

                    CREATE TABLE IF NOT EXISTS settings (
                        key TEXT PRIMARY KEY,
                        value TEXT
                    );

                    CREATE TABLE IF NOT EXISTS recording_events (
                        id TEXT PRIMARY KEY,
                        camera_id TEXT,
                        camera_name TEXT,
                        timestamp INTEGER,
                        date_str TEXT,
                        time_str TEXT,
                        duration_sec INTEGER,
                        video_file TEXT,
                        thumb_file TEXT,
                        size_bytes INTEGER,
                        size_str TEXT,
                        created_at TEXT DEFAULT CURRENT_TIMESTAMP
                    );
                ";

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = createTablesSql;
                    cmd.ExecuteNonQuery();
                }

                Console.WriteLine("[SQLite] Banco de dados vision.db pronto (WAL mode ativado).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Erro ao inicializar banco: {ex.Message}");
            }
        }
    }

    private void MigrateJsonIfEmpty()
    {
        lock (_lock)
        {
            try
            {
                using var conn = CreateConnection();

                // 1. Migração de Câmeras
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM cameras;";
                    long count = (long)(countCmd.ExecuteScalar() ?? 0L);
                    if (count == 0 && File.Exists(_camerasFile))
                    {
                        string json = File.ReadAllText(_camerasFile);
                        var cameras = JsonSerializer.Deserialize<List<CameraConfig>>(json, JsonOptions);
                        if (cameras != null && cameras.Count > 0)
                        {
                            Console.WriteLine($"[SQLite] Importando {cameras.Count} câmeras de cameras.json para o banco vision.db...");
                            SaveCamerasInternal(conn, cameras);
                            Console.WriteLine("[SQLite] Migração de câmeras concluída com sucesso!");
                        }
                    }
                }

                // 2. Migração de Configurações
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM settings;";
                    long count = (long)(countCmd.ExecuteScalar() ?? 0L);
                    if (count == 0 && File.Exists(_settingsFile))
                    {
                        string json = File.ReadAllText(_settingsFile);
                        var settings = JsonSerializer.Deserialize<SettingsConfig>(json, JsonOptions);
                        if (settings != null)
                        {
                            SaveSettingsInternal(conn, settings);
                            Console.WriteLine("[SQLite] Migração de configurações concluída com sucesso!");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Erro na migração inicial de JSON para SQLite: {ex.Message}");
            }
        }
    }

    public SettingsConfig LoadSettings()
    {
        lock (_lock)
        {
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM settings WHERE key = 'app_settings';";
                var val = cmd.ExecuteScalar() as string;
                if (!string.IsNullOrWhiteSpace(val))
                {
                    return JsonSerializer.Deserialize<SettingsConfig>(val, JsonOptions) ?? new SettingsConfig();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Erro ao carregar configurações: {ex.Message}");
            }

            // Fallback para arquivo JSON se o banco ainda não tinha
            if (File.Exists(_settingsFile))
            {
                try
                {
                    string json = File.ReadAllText(_settingsFile);
                    return JsonSerializer.Deserialize<SettingsConfig>(json, JsonOptions) ?? new SettingsConfig();
                }
                catch { }
            }

            return new SettingsConfig();
        }
    }

    public bool SaveSettings(SettingsConfig settings)
    {
        lock (_lock)
        {
            try
            {
                using var conn = CreateConnection();
                SaveSettingsInternal(conn, settings);

                // Espelha backup em JSON
                try
                {
                    string json = JsonSerializer.Serialize(settings, JsonOptions);
                    File.WriteAllText(_settingsFile, json);
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Erro ao salvar configurações: {ex.Message}");
                return false;
            }
        }
    }

    private static void SaveSettingsInternal(SqliteConnection conn, SettingsConfig settings)
    {
        string json = JsonSerializer.Serialize(settings, JsonOptions);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO settings (key, value) VALUES ('app_settings', @val)
            ON CONFLICT(key) DO UPDATE SET value = @val;
        ";
        cmd.Parameters.AddWithValue("@val", json);
        cmd.ExecuteNonQuery();
    }

    public List<CameraConfig> LoadCameras()
    {
        lock (_lock)
        {
            var result = new List<CameraConfig>();
            try
            {
                using var conn = CreateConnection();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT id, name, host, port, protocol, brand, username, password,
                           channel, stream_type, custom_url, ptz_enabled, multi_lens,
                           display_order, created_at, motion_sensitivity, parent_id, is_sub_lens
                    FROM cameras
                    ORDER BY display_order ASC, name ASC;
                ";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var cam = new CameraConfig
                    {
                        Id = reader.GetString(0),
                        Name = reader.GetString(1),
                        Host = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Port = reader.IsDBNull(3) ? 554 : reader.GetInt32(3),
                        Protocol = reader.IsDBNull(4) ? "rtsp" : reader.GetString(4),
                        Brand = reader.IsDBNull(5) ? "generic" : reader.GetString(5),
                        Username = reader.IsDBNull(6) ? "" : reader.GetString(6),
                        Password = reader.IsDBNull(7) ? "" : reader.GetString(7),
                        Channel = reader.IsDBNull(8) ? 1 : reader.GetInt32(8),
                        StreamType = reader.IsDBNull(9) ? "sub" : reader.GetString(9),
                        CustomUrl = reader.IsDBNull(10) ? "" : reader.GetString(10),
                        PtzEnabled = !reader.IsDBNull(11) && reader.GetInt32(11) == 1,
                        MultiLens = !reader.IsDBNull(12) && reader.GetInt32(12) == 1,
                        Order = reader.IsDBNull(13) ? 0 : reader.GetDouble(13),
                        CreatedAt = reader.IsDBNull(14) ? "" : reader.GetString(14),
                        MotionSensitivity = reader.IsDBNull(15) ? "medium" : reader.GetString(15),
                        ParentId = reader.IsDBNull(16) ? null : reader.GetString(16),
                        IsSubLens = !reader.IsDBNull(17) ? reader.GetInt32(17) == 1 : null
                    };
                    result.Add(cam);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Erro ao carregar câmeras: {ex.Message}");
                // Fallback para JSON em caso extremo
                if (File.Exists(_camerasFile))
                {
                    try
                    {
                        string json = File.ReadAllText(_camerasFile);
                        return JsonSerializer.Deserialize<List<CameraConfig>>(json, JsonOptions) ?? new List<CameraConfig>();
                    }
                    catch { }
                }
            }
            return result;
        }
    }

    public bool SaveCameras(List<CameraConfig> cameras)
    {
        lock (_lock)
        {
            try
            {
                using var conn = CreateConnection();
                using var trans = conn.BeginTransaction();
                SaveCamerasInternal(conn, cameras, trans);
                trans.Commit();

                // Espelha backup em JSON de forma atômica
                try
                {
                    string json = JsonSerializer.Serialize(cameras, JsonOptions);
                    File.WriteAllText(_camerasFile, json);
                }
                catch { }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SQLite] Erro ao salvar câmeras: {ex.Message}");
                return false;
            }
        }
    }

    private static void SaveCamerasInternal(SqliteConnection conn, List<CameraConfig> cameras, SqliteTransaction? trans = null)
    {
        using (var deleteCmd = conn.CreateCommand())
        {
            if (trans != null) deleteCmd.Transaction = trans;
            deleteCmd.CommandText = "DELETE FROM cameras;";
            deleteCmd.ExecuteNonQuery();
        }

        string insertSql = @"
            INSERT INTO cameras (
                id, name, host, port, protocol, brand, username, password,
                channel, stream_type, custom_url, ptz_enabled, multi_lens,
                display_order, created_at, motion_sensitivity, parent_id, is_sub_lens
            ) VALUES (
                @id, @name, @host, @port, @protocol, @brand, @username, @password,
                @channel, @stream_type, @custom_url, @ptz_enabled, @multi_lens,
                @display_order, @created_at, @motion_sensitivity, @parent_id, @is_sub_lens
            );
        ";

        foreach (var cam in cameras)
        {
            using var cmd = conn.CreateCommand();
            if (trans != null) cmd.Transaction = trans;
            cmd.CommandText = insertSql;

            cmd.Parameters.AddWithValue("@id", cam.Id ?? Guid.NewGuid().ToString("N")[..8]);
            cmd.Parameters.AddWithValue("@name", cam.Name ?? "Câmera");
            cmd.Parameters.AddWithValue("@host", (object?)cam.Host ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@port", cam.Port);
            cmd.Parameters.AddWithValue("@protocol", (object?)cam.Protocol ?? "rtsp");
            cmd.Parameters.AddWithValue("@brand", (object?)cam.Brand ?? "generic");
            cmd.Parameters.AddWithValue("@username", (object?)cam.Username ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@password", (object?)cam.Password ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@channel", cam.Channel);
            cmd.Parameters.AddWithValue("@stream_type", (object?)cam.StreamType ?? "sub");
            cmd.Parameters.AddWithValue("@custom_url", (object?)cam.CustomUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ptz_enabled", cam.PtzEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@multi_lens", cam.MultiLens ? 1 : 0);
            cmd.Parameters.AddWithValue("@display_order", cam.Order);
            cmd.Parameters.AddWithValue("@created_at", (object?)cam.CreatedAt ?? DateTime.Now.ToString("s"));
            cmd.Parameters.AddWithValue("@motion_sensitivity", (object?)cam.MotionSensitivity ?? "medium");
            cmd.Parameters.AddWithValue("@parent_id", (object?)cam.ParentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@is_sub_lens", cam.IsSubLens.HasValue ? (cam.IsSubLens.Value ? 1 : 0) : DBNull.Value);

            cmd.ExecuteNonQuery();
        }
    }

    public CameraConfig AddCamera(CameraConfig camData)
    {
        lock (_lock)
        {
            var cameras = LoadCameras();
            camData.Id = Guid.NewGuid().ToString("N")[..8];
            if (string.IsNullOrWhiteSpace(camData.Name))
                camData.Name = $"Câmera {cameras.Count + 1}";

            camData.Order = cameras.Count;
            camData.CreatedAt = DateTime.Now.ToString("s");

            if (string.IsNullOrWhiteSpace(camData.CustomUrl))
            {
                camData.CustomUrl = BuildRtspUrl(camData);
            }

            cameras.Add(camData);
            SaveCameras(cameras);
            return camData;
        }
    }

    public CameraConfig? UpdateCamera(string camId, Action<CameraConfig> updater)
    {
        lock (_lock)
        {
            if (camId.Contains("_sub1") || camId.Contains("_sub2"))
                camId = camId.Split('_')[0];

            var cameras = LoadCameras();
            var target = cameras.FirstOrDefault(c => c.Id == camId);
            if (target == null) return null;

            updater(target);

            if (string.IsNullOrWhiteSpace(target.CustomUrl))
                target.CustomUrl = BuildRtspUrl(target);

            SaveCameras(cameras);
            return target;
        }
    }

    public bool DeleteCamera(string camId)
    {
        lock (_lock)
        {
            if (camId.Contains("_sub1") || camId.Contains("_sub2"))
            {
                string parentId = camId.Split('_')[0];
                UpdateCamera(parentId, c => c.MultiLens = false);
                return true;
            }

            var cameras = LoadCameras();
            int removed = cameras.RemoveAll(c => c.Id == camId);
            if (removed > 0)
            {
                SaveCameras(cameras);
                return true;
            }
            return false;
        }
    }

    public CameraConfig? GetCamera(string camId)
    {
        lock (_lock)
        {
            var cameras = LoadCameras();
            var found = cameras.FirstOrDefault(c => c.Id == camId || c.Host == camId);
            if (found != null) return found;

            if (camId.Contains("_sub1") || camId.Contains("_sub2"))
            {
                string parentId = camId.Split('_')[0];
                var parent = cameras.FirstOrDefault(c => c.Id == parentId);
                if (parent != null)
                {
                    int subIdx = camId.EndsWith("_sub1") ? 1 : 2;
                    return new CameraConfig
                    {
                        Id = camId,
                        ParentId = parent.Id,
                        Name = $"{parent.Name} (Fixa {subIdx})",
                        Host = "127.0.0.1",
                        Port = 8554,
                        Brand = "generic",
                        Protocol = "rtsp",
                        Channel = subIdx,
                        StreamType = "main",
                        CustomUrl = $"rtsp://127.0.0.1:8554/{parent.Id}_ch{subIdx}",
                        PtzEnabled = false,
                        MultiLens = false,
                        Order = parent.Order + subIdx * 0.1,
                        CreatedAt = parent.CreatedAt
                    };
                }
            }
            return null;
        }
    }

    public static string BuildRtspUrl(CameraConfig camera, string? streamTypeOverride = null)
    {
        string brand = (camera.Brand ?? "generic").ToLowerInvariant();
        string host = string.IsNullOrWhiteSpace(camera.Host) ? "127.0.0.1" : camera.Host;
        int port = camera.Port > 0 ? camera.Port : 554;
        string user = camera.Username ?? "admin";
        string pwd = camera.Password ?? "";
        int ch = camera.Channel > 0 ? camera.Channel : 1;
        string stream = streamTypeOverride ?? camera.StreamType ?? "sub";
        int streamIdx = (stream == "main" || stream == "0") ? 0 : 1;

        if (brand != "generic" && !string.IsNullOrWhiteSpace(camera.Host))
        {
            string auth = !string.IsNullOrEmpty(user) ? $"{user}:{pwd}@" : "";
            return brand switch
            {
                "icsee" => $"rtsp://{auth}{host}:{port}/user={user}&pwd={pwd}&channel={ch}&stream={streamIdx}.sdp",
                "intelbras" => $"rtsp://{auth}{host}:{port}/cam/realmonitor?channel={ch}&subtype={streamIdx}",
                "tapo" => $"rtsp://{auth}{host}:{port}/{(streamIdx == 0 ? "stream1" : "stream2")}",
                "hikvision" => $"rtsp://{auth}{host}:{port}/Streaming/Channels/{(streamIdx == 0 ? $"{ch}01" : $"{ch}02")}",
                _ => $"rtsp://{auth}{host}:{port}/live/ch{streamIdx}"
            };
        }

        if (!string.IsNullOrWhiteSpace(camera.CustomUrl))
        {
            if (streamIdx == 1)
            {
                string u = camera.CustomUrl;
                if (u.Contains("/stream1")) return u.Replace("/stream1", "/stream2");
                if (u.Contains("stream=0.sdp")) return u.Replace("stream=0.sdp", "stream=1.sdp");
                if (u.Contains("subtype=0")) return u.Replace("subtype=0", "subtype=1");
                return u;
            }
            return camera.CustomUrl;
        }

        string defaultAuth = !string.IsNullOrEmpty(user) ? $"{user}:{pwd}@" : "";
        return $"rtsp://{defaultAuth}{host}:{port}/live/ch{streamIdx}";
    }
}
