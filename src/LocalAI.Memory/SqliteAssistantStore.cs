using System.Text.Json;
using LocalAI.Core.Assistants;
using LocalAI.Core.Speech;
using Microsoft.Data.Sqlite;
using static LocalAI.Memory.SqliteDatabase;

namespace LocalAI.Memory;

public sealed class SqliteAssistantStore(SqliteDatabase db) : IAssistantStore
{
    public async Task<IReadOnlyList<AssistantProfile>> ListAsync(CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, name, style_prompt, model_id, voice_id, language, voice_style, created_at FROM assistants ORDER BY id;";
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<AssistantProfile>();
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new AssistantProfile(
                r.GetInt64(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.GetString(5),
                ReadStyle(r.GetString(6)),
                FromUnixMs(r.GetInt64(7))));
        }
        return list;
    }

    public async Task<AssistantProfile> CreateAsync(NewAssistant assistant, VoiceStyle voiceStyle, CancellationToken ct = default)
    {
        var now = DateTimeOffset.Now;
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO assistants(name, style_prompt, model_id, voice_id, language, voice_style, created_at)
            VALUES ($name, $style, $model, $voice, $language, $voiceStyle, $at) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$name", assistant.Name);
        cmd.Parameters.AddWithValue("$style", assistant.StylePrompt);
        cmd.Parameters.AddWithValue("$model", assistant.ModelId);
        cmd.Parameters.AddWithValue("$voice", assistant.VoiceId);
        cmd.Parameters.AddWithValue("$language", assistant.Language);
        cmd.Parameters.AddWithValue("$voiceStyle", JsonSerializer.Serialize(voiceStyle));
        cmd.Parameters.AddWithValue("$at", ToUnixMs(now));
        var id = (long)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

        // Conversations and memories saved before assistants existed go to the first assistant created.
        await using var adopt = c.CreateCommand();
        adopt.Transaction = tx;
        adopt.CommandText = """
            UPDATE conversations SET assistant_id = $id WHERE assistant_id IS NULL;
            UPDATE memories SET assistant_id = $id WHERE assistant_id IS NULL;
            """;
        adopt.Parameters.AddWithValue("$id", id);
        await adopt.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);

        return new AssistantProfile(id, assistant.Name, assistant.StylePrompt, assistant.ModelId, assistant.VoiceId,
            assistant.Language, voiceStyle, FromUnixMs(ToUnixMs(now)));
    }

    public async Task SetVoiceStyleAsync(long id, VoiceStyle voiceStyle, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE assistants SET voice_style = $voiceStyle WHERE id = $id;";
        cmd.Parameters.AddWithValue("$voiceStyle", JsonSerializer.Serialize(voiceStyle));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<AssistantDataSummary> GetDataSummaryAsync(long id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM conversations WHERE assistant_id = $id),
                (SELECT COUNT(*) FROM messages m JOIN conversations cv ON cv.id = m.conversation_id WHERE cv.assistant_id = $id),
                (SELECT COUNT(*) FROM memories WHERE assistant_id = $id);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await r.ReadAsync(ct).ConfigureAwait(false);
        return new AssistantDataSummary(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM assistants WHERE id = $id;"; // conversations (and their messages) and memories cascade
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static VoiceStyle ReadStyle(string json) =>
        string.IsNullOrEmpty(json) ? VoiceStyle.Default : JsonSerializer.Deserialize<VoiceStyle>(json) ?? VoiceStyle.Default;
}
