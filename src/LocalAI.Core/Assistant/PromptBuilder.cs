using System.Globalization;
using System.Text;
using LocalAI.Configuration;
using LocalAI.Core.Assistants;
using LocalAI.Core.Conversations;
using LocalAI.Core.Llm;
using LocalAI.Core.Memory.Vault;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Assistant;

/// <summary>Assembles the model input: system prompt + memory (profile and note passages) + recent history within a token budget.</summary>
/// <remarks>
/// llama-server reuses its cache for the start of the prompt that did not change since the last turn. So the system
/// message holds only what rarely changes, the history starts at the same message from turn to turn, and what changes
/// every turn (date and time, recalled passages) goes in the last user message.
/// </remarks>
public sealed class PromptBuilder(IOptions<LocalAiOptions> options, AssistantContext assistant)
{
    private readonly AssistantOptions _options = options.Value.Assistant;

    /// <summary>Tokens left free besides the reply: the chat template's tokens and the error of the estimate.</summary>
    private const int ContextMarginTokens = 256;

    public const string ContextStart = "[Context from the app]";
    public const string ContextEnd = "[End of context]";

    private readonly Lock _gate = new();
    /// <summary>Where the history of the last prompt started: (conversation, message id).</summary>
    private (long ConversationId, long MessageId)? _historyStart;

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

    /// <summary>
    /// A user turn as the model sees it: the text the user selected in another application (if any) quoted before
    /// what they said or typed.
    /// </summary>
    public static string WithSelection(string userText, string? selectedText) =>
        string.IsNullOrWhiteSpace(selectedText)
            ? userText
            : $"Text I selected on my screen:\n\"\"\"\n{selectedText.Trim()}\n\"\"\"\n\n{userText}";

    /// <param name="selectedText">Text the user selected in another application for this message (Read selection).</param>
    /// <param name="userLanguage">
    /// The language the user wrote or spoke this message in (ISO 639-1), when known: the reply is in that language,
    /// whatever the assistant's voice. The voice speaks every language.
    /// </param>
    /// <param name="contextTokens">The model's context size (0 = unknown): the history is cut so the prompt and the reply fit.</param>
    /// <param name="replyTokens">Tokens kept free for the reply.</param>
    public IReadOnlyList<ChatMessage> Build(
        IReadOnlyList<StoredMessage> history,
        string userText,
        MemoryContext memory,
        bool spoken,
        DateTimeOffset now,
        string? selectedText = null,
        string? userLanguage = null,
        int contextTokens = 0,
        int replyTokens = 0)
    {
        var system = BuildSystem(memory, spoken);
        var userTurn = BuildUserTurn(userText, memory, now, selectedText, LanguageName(userLanguage));

        var budget = _options.HistoryTokenBudget;
        if (contextTokens > 0)
        {
            budget = Math.Min(budget,
                contextTokens - replyTokens - EstimateTokens(system) - EstimateTokens(userTurn) - ContextMarginTokens);
        }
        var selected = SelectHistory(history, Math.Max(0, budget));

        // Chat templates expect alternating roles starting with the user.
        while (selected.Count > 0 && selected[0].Role != ChatRole.User) selected.RemoveAt(0);
        var messages = new List<ChatMessage> { new(ChatRole.System, system) };
        messages.AddRange(MergeConsecutive(selected));

        if (messages[^1].Role == ChatRole.User)
            messages[^1] = messages[^1] with { Content = messages[^1].Content + "\n\n" + userTurn };
        else
            messages.Add(new ChatMessage(ChatRole.User, userTurn));
        return messages;
    }

    /// <summary>The system message: only what stays the same from turn to turn.</summary>
    private string BuildSystem(MemoryContext memory, bool spoken)
    {
        var profile = assistant.Current;
        var system = new StringBuilder(_options.SystemPrompt.Replace("{name}", profile?.Name ?? "Assistant", StringComparison.Ordinal));
        // The identity note says who the assistant is (the user may have edited it); until it exists, the style itself.
        if (!string.IsNullOrWhiteSpace(memory.Identity))
            system.Append("\n\nYour identity note (who you are and how you act; follow it):\n").Append(memory.Identity.Trim());
        else if (!string.IsNullOrWhiteSpace(profile?.StylePrompt))
            system.Append("\n\nPersonality and style defined by the user:\n").Append(profile.StylePrompt);

        if (!string.IsNullOrWhiteSpace(memory.Profile))
            system.Append("\n\nWhat you know about the user (your memory; use only when relevant, do not mention it unprompted):\n")
                .Append(memory.Profile.Trim());

        system.Append("\n\nThe user's message starts with a context block from the app (the current date and time, and notes " +
                      "from past conversations that may be relevant). The user did not write it: use it when it helps, do not " +
                      "mention it unprompted.");
        // Replies follow the user: the language of their last message (text or voice), never the assistant's voice.
        system.Append("\n\nAlways reply in the same language as the user's last message (text or speech), even if earlier " +
                      "messages were in another language.");

        if (spoken) system.Append("\n\n").Append(_options.VoiceStyleHint);
        return system.ToString();
    }

    /// <summary>The user's message as the model sees it: the context of this turn, then what the user selected and said.</summary>
    private static string BuildUserTurn(string userText, MemoryContext memory, DateTimeOffset now, string? selectedText, string language)
    {
        var turn = new StringBuilder(ContextStart).Append('\n');
        turn.Append(CultureInfo.InvariantCulture, $"Current local date/time: {now:yyyy-MM-dd HH:mm} ({now:dddd}).");
        if (memory.Passages.Count > 0)
        {
            turn.Append("\nNotes from past conversations that may be relevant (when you use one, you can name it as [[title]]):");
            foreach (var p in memory.Passages)
                turn.Append("\n- [[").Append(p.NoteTitle).Append("]] ").Append(p.Heading).Append(": ").Append(p.Text.ReplaceLineEndings(" "));
        }
        turn.Append('\n').Append(ContextEnd).Append("\n\n").Append(WithSelection(userText, selectedText));
        // Models tend to follow the language of the last message over the system prompt, so repeat the rule there.
        // This reminder is only sent to the model; the stored message is unchanged.
        if (language.Length > 0) turn.Append("\n\n(Reply in ").Append(language).Append(".)");
        return turn.ToString();
    }

    /// <summary>
    /// The newest messages that fit <paramref name="budget"/>. The start of the last prompt is kept while everything
    /// after it fits; when it no longer does, the history is cut to three quarters of the budget, so the start then
    /// stays put for the next turns again (a start that moves every turn makes llama-server read the whole history again).
    /// </summary>
    private List<ChatMessage> SelectHistory(IReadOnlyList<StoredMessage> history, int budget)
    {
        var usable = history.Where(m => m.Role != ChatRole.System && !string.IsNullOrWhiteSpace(m.Content)).ToList();
        var contents = usable.Select(m => m.Role == ChatRole.User ? WithSelection(m.Content, m.SelectedText) : m.Content).ToList();
        var costs = contents.Select(EstimateTokens).ToList();

        var start = -1;
        lock (_gate)
        {
            if (_historyStart is { } previous && usable.Count > 0 && usable[0].ConversationId == previous.ConversationId &&
                usable[0].Id <= previous.MessageId)
            {
                var index = usable.FindIndex(m => m.Id >= previous.MessageId);
                if (index >= 0 && costs.Skip(index).Sum() <= budget) start = index;
            }
            if (start < 0)
            {
                var target = budget * 3 / 4;
                var used = 0;
                start = usable.Count;
                while (start > 0 && used + costs[start - 1] <= target) used += costs[--start];
                _historyStart = start < usable.Count ? (usable[start].ConversationId, usable[start].Id) : null;
            }
        }
        return usable.Skip(start).Select((m, i) => new ChatMessage(m.Role, contents[start + i])).ToList();
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
