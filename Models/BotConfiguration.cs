namespace DiscordOllamaBot.Models;

public class BotConfiguration
{
    public DiscordConfiguration Discord { get; set; } = new();
    public OllamaConfiguration Ollama { get; set; } = new();
    public ConversationConfiguration Conversation { get; set; } = new();
}

public class DiscordConfiguration
{
    public string Token { get; set; } = string.Empty;
}

public class OllamaConfiguration
{
    public string Endpoint { get; set; } = "http://localhost:11434";
    public string Model { get; set; } = "qwen3:4b";
    public string SystemPrompt { get; set; } = "You are a helpful Discord assistant.";
}

public class ConversationConfiguration
{
    public int MaxHistory { get; set; } = 20;
}
