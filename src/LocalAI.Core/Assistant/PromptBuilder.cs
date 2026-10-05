using System.Globalization;
using System.Text;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Assistant;

/// <summary>Assembles the model input: system prompt + relevant memories + recent history within a token budget.</summary>
public sealed class PromptBuilder(IOptions<LocalAiOptions> options, AssistantContext assistant)
{
    private readonly AssistantOptions _options = options.Value.Assistant;

    public static string LanguageName(string? code) => code switch
    {
        "pt" => "Brazilian Portuguese",
        "it" => "Italian",
        "en" => "English",
        "es" => "Spanish",
        "fr" => "French",
        "de" => "German",
        null or "" => "",
        _ => code,
    };

    /// <summary>Rough token estimate (≈3.5 chars/token for European languages); only used for budgeting.</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length / 3.5) + 4;

    public IReadOnlyList<ChatMessage> Build(
        IReadOnlyList<StoredMessage> history,
        string userText,
        IReadOnlyList<ScoredMemory> memories,
        bool spoken,
        DateTimeOffset now)
    {
        var profile = assistant.Current;
        var system = new StringBuilder(_options.SystemPrompt.Replace("{name}", profile?.Name ?? "Assistant", StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(profile?.StylePrompt))
            system.Append("\n\nPersonality and style defined by the user:\n").Append(profile.StylePrompt);
        system.Append(CultureInfo.InvariantCulture, $"\nCurrent local date/time: {now:yyyy-MM-dd HH:mm} ({now:dddd}).");

        if (memories.Count > 0)
        {
            system.Append("\n\nLong-term memory about the user (use only when relevant; do not mention it unprompted):");
            foreach (var m in memories) system.Append("\n- ").Append(m.Memory.Content);
        }

        var language = LanguageName(profile?.Language);
        system.Append(language.Length > 0
            ? $"\n\nAlways reply in {language}, even when the user writes or speaks another language. Never switch languages."
            : "\n\nReply in the same language as the user's last message.");

        if (spoken) system.Append("\n\n").Append(_options.VoiceStyleHint);

        var messages = new List<ChatMessage> { new(ChatRole.System, system.ToString()) };

        var budget = _options.HistoryTokenBudget - EstimateTokens(userText);
        var selected = new List<ChatMessage>();
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            if (m.Role == ChatRole.System || string.IsNullOrWhiteSpace(m.Content)) continue;
            budget -= EstimateTokens(m.Content);
            if (budget < 0) break;
            selected.Add(new ChatMessage(m.Role, m.Content));
        }
        selected.Reverse();

        // Chat templates expect alternating roles starting with the user.
        while (selected.Count > 0 && selected[0].Role != ChatRole.User) selected.RemoveAt(0);
        messages.AddRange(MergeConsecutive(selected));

        // Models tend to follow the language of the last message over the system prompt, so repeat the rule there.
        // This reminder is only sent to the model; the stored message is unchanged.
        var userTurn = language.Length > 0 ? $"{userText}\n\n(Reply in {language}.)" : userText;
        if (messages[^1].Role == ChatRole.User)
            messages[^1] = messages[^1] with { Content = messages[^1].Content + "\n\n" + userTurn };
        else
            messages.Add(new ChatMessage(ChatRole.User, userTurn));
        return messages;
    }

    private static IEnumerable<ChatMessage> MergeConsecutive(List<ChatMessage> list)
    {
        ChatMessage? pending = null;
        foreach (var m in list)
        {
            if (pending != null && pending.Role == m.Role)
            {
                pending = pending with { Content = pending.Content + "\n\n" + m.Content };
                continue;
            }
            if (pending != null) yield return pending;
            pending = m;
        }
        if (pending != null) yield return pending;
    }
}
