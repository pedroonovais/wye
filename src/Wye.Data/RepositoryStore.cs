using Microsoft.Data.Sqlite;

namespace Wye.Data;

public sealed record SavedRepository(long Id, string Path, string Name, DateTimeOffset LastOpened);

/// <summary>
/// Lista de repositórios adicionados e preferências simples, em SQLite.
/// Equivale ao IndexedDB/Dexie do GitHub Desktop (app/src/lib/databases/repositories-database.ts),
/// mas num arquivo único que pode ser inspecionado com qualquer cliente SQLite.
/// </summary>
public sealed class RepositoryStore
{
    private readonly string _connectionString;

    public RepositoryStore(string databasePath)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Migrate();
    }

    public static string DefaultDatabasePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "wye",
            "wye.db");

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private void Migrate()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS repositories (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                path        TEXT NOT NULL UNIQUE,
                name        TEXT NOT NULL,
                last_opened INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<SavedRepository> GetAll()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, path, name, last_opened FROM repositories ORDER BY name COLLATE NOCASE";
        using var r = cmd.ExecuteReader();
        var list = new List<SavedRepository>();
        while (r.Read())
        {
            list.Add(new SavedRepository(r.GetInt64(0), r.GetString(1), r.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3))));
        }

        return list;
    }

    public SavedRepository Add(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(full);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO repositories (path, name, last_opened) VALUES ($path, $name, $now)
            ON CONFLICT(path) DO UPDATE SET last_opened = excluded.last_opened
            RETURNING id, path, name, last_opened;
            """;
        cmd.Parameters.AddWithValue("$path", full);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using var r = cmd.ExecuteReader();
        r.Read();
        return new SavedRepository(r.GetInt64(0), r.GetString(1), r.GetString(2),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)));
    }

    public void Remove(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM repositories WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Touch(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE repositories SET last_opened = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    public string? GetSetting(string key)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.ExecuteNonQuery();
    }
}
