# Discord Ollama Bot

A Discord chatbot that uses [Ollama](https://ollama.ai) to run AI models locally. Built with .NET 8 and Discord.NET.

## Features

- **@Mention responses** - Mention the bot in any channel to chat
- **Direct message support** - DM the bot without needing to @mention
- **Per-user conversation memory** - Each user has their own conversation context
- **Customizable personality** - Configure the system prompt to change bot behavior
- **Fully local** - No API costs, runs entirely on your machine

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Ollama](https://ollama.ai) installed and running
- A Discord bot token ([Discord Developer Portal](https://discord.com/developers/applications))

## Quick Start

1. **Clone the repository**
   ```bash
   git clone https://github.com/yourusername/DiscordOllamaBot.git
   cd DiscordOllamaBot
   ```

2. **Pull the Ollama model**
   ```bash
   ollama pull qwen3:4b
   ```

3. **Configure the bot**

   Edit `appsettings.json` with your Discord bot token:
   ```json
   {
     "Discord": {
       "Token": "YOUR_BOT_TOKEN"
     }
   }
   ```

4. **Enable Message Content Intent**

   In the [Discord Developer Portal](https://discord.com/developers/applications):
   - Select your application → Bot → Privileged Gateway Intents
   - Enable **Message Content Intent**

5. **Run the bot**
   ```bash
   dotnet run
   ```

## Configuration

| Setting | Default | Description |
|---------|---------|-------------|
| `Discord:Token` | - | Your Discord bot token (required) |
| `Ollama:Endpoint` | `http://localhost:11434` | Ollama API endpoint |
| `Ollama:Model` | `qwen3:4b` | Ollama model to use |
| `Ollama:SystemPrompt` | `You are a helpful Discord assistant...` | Bot personality/instructions |
| `Conversation:MaxHistory` | `20` | Max messages to remember per user |

## Project Structure

```
DiscordOllamaBot/
├── Program.cs                 # Entry point & DI setup
├── appsettings.json           # Configuration
├── Models/
│   ├── BotConfiguration.cs    # Config POCOs
│   └── ConversationContext.cs # User conversation state
├── Services/
│   ├── OllamaService.cs       # Ollama API integration
│   └── ConversationService.cs # Per-user history management
└── Handlers/
    └── MessageHandler.cs      # Discord message processing
```

## Usage

**In a server channel:**
```
@BotName What is the capital of France?
```

**In direct messages:**
```
What is the capital of France?
```

The bot remembers your conversation history, so you can have multi-turn conversations.

## Using Different Models

You can use any model available in Ollama. Update `appsettings.json`:

```json
{
  "Ollama": {
    "Model": "llama3:8b"
  }
}
```

Popular options:
- `qwen3:4b` - Fast, good for general chat
- `llama3:8b` - Balanced performance
- `mistral:7b` - Great reasoning capabilities
- `codellama:7b` - Optimized for code

## License

MIT
