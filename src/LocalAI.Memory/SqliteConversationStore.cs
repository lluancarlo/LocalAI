using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using Microsoft.Data.Sqlite;
using static LocalAI.Memory.SqliteDatabase;

namespace LocalAI.Memory;

/// <summary>Conversations of the active assistant (<see cref="AssistantContext"/>).</summary>
public sealed class SqliteConversationStore(SqliteDatabase db, AssistantContext assistant) : IConversationStore
{
    public async Task<Conversation> CreateAsync(string title, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO conversations(title, created_at, updated_at, assistant_id) VALUES ($t, $n, $n, $a) RETURNING id;";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$a", assistant.CurrentId);
        cmd.Parameters.AddWithValue("$n", ToUnixMs(now));
        var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        return new Conversation(id, title, FromUnixMs(ToUnixMs(now)), FromUnixMs(ToUnixMs(now)));
    }

    public async Task<Conversation?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, title, created_at, updated_at FROM conversations WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadConversation(r) : null;
    }

    public async Task<IReadOnlyList<Conversation>> ListAsync(CancellationToken ct = default)
    {
        if (assistant.Current == null) return [];
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, title, created_at, updated_at FROM conversations WHERE assistant_id = $a ORDER BY updated_at DESC, id DESC;";
        cmd.Parameters.AddWithValue("$a", assistant.CurrentId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<Conversation>();
        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(ReadConversation(r));
        return list;
    }

    public async Task RenameAsync(long id, string title, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET title = $t WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM conversations WHERE id = $id;"; // messages cascade
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<StoredMessage> AddMessageAsync(StoredMessage message, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO messages(conversation_id, role, content, created_at, language, model, source, metadata, selected_text)
            VALUES ($c, $r, $content, $at, $lang, $model, $src, $meta, $sel) RETURNING id;
            UPDATE conversations SET updated_at = $at WHERE id = $c;
            """;
        cmd.Parameters.AddWithValue("$c", message.ConversationId);
        cmd.Parameters.AddWithValue("$r", message.Role.ToString());
        cmd.Parameters.AddWithValue("$content", message.Content);
        cmd.Parameters.AddWithValue("$at", ToUnixMs(message.CreatedAt));
        cmd.Parameters.AddWithValue("$lang", (object?)message.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)message.Model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$src", message.Source.ToString());
        cmd.Parameters.AddWithValue("$meta", (object?)message.MetadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sel", (object?)message.SelectedText ?? DBNull.Value);
        var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return message with { Id = id, CreatedAt = FromUnixMs(ToUnixMs(message.CreatedAt)) };
    }

    public Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(long conversationId, CancellationToken ct = default) =>
        QueryMessagesAsync(conversationId, int.MaxValue, ct);

    public Task<IReadOnlyList<StoredMessage>> GetRecentMessagesAsync(long conversationId, int limit, CancellationToken ct = default) =>
        QueryMessagesAsync(conversationId, limit, ct);

    private async Task<IReadOnlyList<StoredMessage>> QueryMessagesAsync(long conversationId, int limit, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM (
                SELECT id, conversation_id, role, content, created_at, language, model, source, metadata, selected_text
                FROM messages WHERE conversation_id = $c ORDER BY id DESC LIMIT $limit
            ) ORDER BY id ASC;
            """;
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$limit", limit);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<StoredMessage>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new StoredMessage
            {
                Id = r.GetInt64(0),
                ConversationId = r.GetInt64(1),
                Role = Enum.Parse<ChatRole>(r.GetString(2)),
                Content = r.GetString(3),
                CreatedAt = FromUnixMs(r.GetInt64(4)),
                Language = r.IsDBNull(5) ? null : r.GetString(5),
                Model = r.IsDBNull(6) ? null : r.GetString(6),
                Source = Enum.Parse<InputSource>(r.GetString(7)),
                MetadataJson = r.IsDBNull(8) ? null : r.GetString(8),
                SelectedText = r.IsDBNull(9) ? null : r.GetString(9),
            });
        }
        return list;
    }

    public async Task<IReadOnlyList<ConversationSearchHit>> SearchAsync(string query, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0 || assistant.Current == null) return [];
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.title, c.created_at, c.updated_at,
                   (SELECT m.content FROM messages m
                     WHERE m.conversation_id = c.id AND localai_contains(m.content, $q)
                     ORDER BY m.id DESC LIMIT 1) AS snippet
            FROM conversations c
            WHERE c.assistant_id = $a
              AND (localai_contains(c.title, $q)
                   OR EXISTS (SELECT 1 FROM messages m WHERE m.conversation_id = c.id AND localai_contains(m.content, $q)))
            ORDER BY c.updated_at DESC
            LIMIT 100;
            """;
        cmd.Parameters.AddWithValue("$q", query);
        cmd.Parameters.AddWithValue("$a", assistant.CurrentId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var hits = new List<ConversationSearchHit>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var snippet = r.IsDBNull(4) ? "" : MakeSnippet(r.GetString(4), query);
            hits.Add(new ConversationSearchHit(ReadConversation(r), snippet));
        }
        return hits;
    }

    internal static string MakeSnippet(string content, string query)
    {
        var idx = System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(content, query,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);
        if (idx < 0) idx = 0;
        var start = Math.Max(0, idx - 40);
        var len = Math.Min(content.Length - start, 120);
        var s = content.Substring(start, len).ReplaceLineEndings(" ");
        return (start > 0 ? "…" : "") + s + (start + len < content.Length ? "…" : "");
    }

    private static Conversation ReadConversation(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), FromUnixMs(r.GetInt64(2)), FromUnixMs(r.GetInt64(3)));
}
