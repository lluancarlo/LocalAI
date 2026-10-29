using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAI.Core.Assistants;
using LocalAI.Core.Desktop;
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
        cmd.CommandText = "SELECT id, name, style_prompt, model_id, voice_id, language, voice_style, created_at, hotkey, avatar, behavior, hearing, speaker FROM assistants ORDER BY id;";
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
                FromUnixMs(r.GetInt64(7)),
                HotkeyGesture.TryParse(r.GetString(8), out var hotkey) ? hotkey : null,
                r.GetString(9),
                ReadBehavior(r.GetString(10)))
            {
                Hearing = r.GetInt64(11) != 0,
                Speaker = r.GetInt64(12) != 0,
            });
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
            INSERT INTO assistants(name, style_prompt, model_id, voice_id, language, voice_style, created_at, avatar, behavior)
            VALUES ($name, $style, $model, $voice, $language, $voiceStyle, $at, $avatar, $behavior) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$avatar", assistant.Avatar);
        var behavior = assistant.Behavior ?? AssistantBehavior.Default;
        cmd.Parameters.AddWithValue("$behavior", JsonSerializer.Serialize(behavior, BehaviorJson));
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
            assistant.Language, voiceStyle, FromUnixMs(ToUnixMs(now)), Avatar: assistant.Avatar, Behavior: behavior);
    }

    public async Task SetAvatarAsync(long id, string avatar, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE assistants SET avatar = $avatar WHERE id = $id;";
        cmd.Parameters.AddWithValue("$avatar", avatar);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetSensesAsync(long id, bool hearing, bool speaker, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE assistants SET hearing = $h, speaker = $s WHERE id = $id;";
        cmd.Parameters.AddWithValue("$h", hearing ? 1 : 0);
        cmd.Parameters.AddWithValue("$s", speaker ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetBehaviorAsync(long id, AssistantBehavior behavior, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE assistants SET behavior = $behavior WHERE id = $id;";
        cmd.Parameters.AddWithValue("$behavior", JsonSerializer.Serialize(behavior, BehaviorJson));
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task SetHotkeyAsync(long id, HotkeyGesture? hotkey, CancellationToken ct = default)
    {
        await using var c = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE assistants SET hotkey = $hotkey WHERE id = $id;";
        cmd.Parameters.AddWithValue("$hotkey", hotkey?.ToString() ?? "");
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
                (SELECT COUNT(*) FROM vault_notes WHERE assistant_id = $id);
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

    // Choices are stored by name ("Precise"), so reordering them never changes a saved assistant.
    private static readonly JsonSerializerOptions BehaviorJson = new() { Converters = { new JsonStringEnumConverter() } };

    private static AssistantBehavior ReadBehavior(string json)
    {
        if (string.IsNullOrEmpty(json)) return AssistantBehavior.Default;
        try { return JsonSerializer.Deserialize<AssistantBehavior>(json, BehaviorJson) ?? AssistantBehavior.Default; }
        catch (JsonException) { return AssistantBehavior.Default; }
    }

    private static VoiceStyle ReadStyle(string json) =>
        string.IsNullOrEmpty(json) ? VoiceStyle.Default : JsonSerializer.Deserialize<VoiceStyle>(json) ?? VoiceStyle.Default;
}
