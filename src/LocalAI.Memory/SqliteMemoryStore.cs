using System.Runtime.InteropServices;
using LocalAI.Core.Memory;
using static LocalAI.Memory.SqliteDatabase;

namespace LocalAI.Memory;

/// <summary>Long-term memories with embeddings stored as float32 BLOBs.</summary>
public sealed class SqliteMemoryStore(SqliteDatabase db) : IMemoryStore
{
    public async Task<MemoryItem> AddAsync(MemoryItem item, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO memories(content, created_at, source_conversation_id, embedding, embedding_model)
            VALUES ($content, $at, $conv, $emb, $model) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$content", item.Content);
        cmd.Parameters.AddWithValue("$at", ToUnixMs(item.CreatedAt));
        cmd.Parameters.AddWithValue("$conv", (object?)item.SourceConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$emb", item.Embedding is { } e ? ToBlob(e) : DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)item.EmbeddingModel ?? DBNull.Value);
        var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return item with { Id = id };
    }

    public async Task<IReadOnlyList<MemoryItem>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, content, created_at, source_conversation_id, embedding, embedding_model FROM memories ORDER BY id;";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<MemoryItem>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MemoryItem
            {
                Id = r.GetInt64(0),
                Content = r.GetString(1),
                CreatedAt = FromUnixMs(r.GetInt64(2)),
                SourceConversationId = r.IsDBNull(3) ? null : r.GetInt64(3),
                Embedding = r.IsDBNull(4) ? null : FromBlob((byte[])r.GetValue(4)),
                EmbeddingModel = r.IsDBNull(5) ? null : r.GetString(5),
            });
        }
        return list;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM memories WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateEmbeddingAsync(long id, float[] embedding, string model, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE memories SET embedding = $e, embedding_model = $m WHERE id = $id;";
        cmd.Parameters.AddWithValue("$e", ToBlob(embedding));
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static byte[] ToBlob(float[] v) => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();
    internal static float[] FromBlob(byte[] b) => MemoryMarshal.Cast<byte, float>(b).ToArray();
}
