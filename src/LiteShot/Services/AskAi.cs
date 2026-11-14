namespace LiteShot;

/// <summary>A chat model on OpenRouter that can read images.</summary>
internal sealed record ChatModel(string Id, string Label, string Note);

/// <summary>
/// Ask AI: a conversation about a screenshot with a language model, through SyncNode → OpenRouter and the user's own
/// keys. The screenshot rides along with the first question, as a JPEG inside the request; it's never posted publicly.
/// </summary>
internal sealed class AskAi
{
    public const string DefaultModel = "anthropic/claude-sonnet-5.5";

    /// <summary>Models that read images, all on OpenRouter; any other OpenRouter model ID works too.</summary>
    public static readonly ChatModel[] Models =
    {
        new(DefaultModel, "Claude Sonnet 5.5", "Anthropic · sharp and careful, the default"),
        new("anthropic/claude-haiku-5.5", "Claude Haiku 5.5", "Anthropic · fast and inexpensive"),
        new("anthropic/claude-fable-5.2", "Claude Fable 5.2", "Anthropic · the most capable, costs more"),
        new("openai/gpt-6.1-terra", "GPT-6.1 Terra", "OpenAI"),
        new("google/gemini-4-hydrogen", "Gemini 4 Hydrogen", "Google"),
        new("x-ai/grok-4.7", "Grok 4.7", "xAI"),
    };

    public static readonly string[] Suggestions =
    {
        "What's in this screenshot?", "Summarize the text", "Explain this error and how to fix it", "Translate it to English",
        "Write a reply to this", "What should I do next?",
    };

    const string SystemPrompt =
        "You are the assistant inside LiteShot, a screenshot app. The user took a screenshot and is asking about it. " +
        "Answer from what the screenshot shows; say so when something can't be seen or read. Be clear and concise. " +
        "Write plain text with short paragraphs or simple '-' lists; don't use tables or headings.";

    /// <summary>The model chosen in Settings, or the default.</summary>
    public static string ModelId => AppSettings.Current.AiChatModel is { } m && m.Trim().Length > 0 ? m.Trim() : DefaultModel;

    public static string LabelFor(string id) => Models.FirstOrDefault(m => m.Id == id)?.Label ?? id;

    readonly string imageUri;
    readonly List<object> messages = new() { new { role = "system", content = SystemPrompt } };

    public AskAi(Bitmap screenshot) => imageUri = AiService.ToDataUri(screenshot, 1600);

    public int Turns => messages.Count(m => m is Turn { Role: "user" } or UserWithImage);

    /// <summary>Asks one question with the conversation so far and returns the answer; a failed turn leaves no trace.</summary>
    public async Task<string> AskAsync(string question, string apiKey, string model, CancellationToken ct)
    {
        object user = messages.Count == 1 ? new UserWithImage(question, imageUri) : new Turn("user", question);
        messages.Add(user);
        try
        {
            string answer = await SyncNodeClient.ChatAsync(apiKey, model, messages.Select(Wire).ToList(), ct);
            messages.Add(new Turn("assistant", answer));
            return answer;
        }
        catch
        {
            messages.Remove(user);
            throw;
        }
    }

    sealed record Turn(string Role, string Text);
    sealed record UserWithImage(string Text, string ImageUri);

    /// <summary>OpenRouter's message shape: plain text, or text and the picture as parts.</summary>
    static object Wire(object m) => m switch
    {
        Turn t => new { role = t.Role, content = t.Text },
        UserWithImage u => new
        {
            role = "user",
            content = new object[]
            {
                new { type = "text", text = u.Text },
                new { type = "image_url", image_url = new { url = u.ImageUri } },
            },
        },
        _ => m,
    };

    /// <summary>Models answer in Markdown; the chat shows plain text, so the marks come off and bullets become dots.</summary>
    public static string Plain(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            string l = line.TrimEnd();
            string trimmed = l.TrimStart();
            int indent = l.Length - trimmed.Length;
            if (trimmed.StartsWith('#')) trimmed = trimmed.TrimStart('#').TrimStart();
            if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ ")) trimmed = "• " + trimmed[2..];
            if (trimmed.StartsWith("```")) return null;
            trimmed = trimmed.Replace("**", "").Replace("__", "").Replace("`", "");
            return new string(' ', Math.Min(indent, 6)) + trimmed;
        }).Where(l => l != null);
        return string.Join("\n", lines).Trim();
    }
}
