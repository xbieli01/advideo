using System.Text.Json;
using System.Text.Json.Serialization;
using Advideo.Orchestrator.Core.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Advideo.Orchestrator.Infrastructure.Postgres;

/// <summary>
/// Minimal JSONB document store: one table per aggregate (id text PK, doc jsonb, updated_at).
/// Aggregates are small and read whole, so documents beat a relational mapping here and keep
/// schema evolution in code (new optional fields need no migration).
/// </summary>
public sealed class PostgresDocumentStore<T>(NpgsqlDataSource db, string table) where T : class
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Table { get; } = table;

    public async Task<T?> GetAsync(string id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT doc FROM {Table} WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        var raw = await cmd.ExecuteScalarAsync(ct) as string;
        return raw is null ? null : JsonSerializer.Deserialize<T>(raw, Json);
    }

    public async Task<IReadOnlyList<T>> ListAsync(string? whereSql, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"SELECT doc FROM {Table} {(whereSql is null ? "" : "WHERE " + whereSql)} ORDER BY id");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await r.ReadAsync(ct)) list.Add(JsonSerializer.Deserialize<T>(r.GetString(0), Json)!);
        return list;
    }

    public async Task UpsertAsync(string id, T doc, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"INSERT INTO {Table} (id, doc, updated_at) VALUES ($1, $2, now()) " +
            "ON CONFLICT (id) DO UPDATE SET doc = EXCLUDED.doc, updated_at = now()");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(doc, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand($"DELETE FROM {Table} WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}

/// <summary>Creates the tables if missing (idempotent; run at startup).</summary>
public static class PostgresSchema
{
    public static readonly string[] Tables = ["advideo_platforms", "advideo_campaigns", "advideo_jobs"];

    public static async Task EnsureAsync(NpgsqlDataSource db, CancellationToken ct = default)
    {
        foreach (var t in Tables)
        {
            await using var cmd = db.CreateCommand(
                $"CREATE TABLE IF NOT EXISTS {t} (id text PRIMARY KEY, doc jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now())");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var idx = db.CreateCommand(
            "CREATE INDEX IF NOT EXISTS advideo_campaigns_status ON advideo_campaigns ((doc->>'status'))"))
            await idx.ExecuteNonQueryAsync(ct);
    }
}

public sealed class PostgresReadinessProbe(NpgsqlDataSource db) : IReadinessProbe
{
    public string Name => "postgres";

    public async Task CheckAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
    }
}
