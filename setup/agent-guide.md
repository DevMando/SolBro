# Agent Setup Guide — SolBro

> **For AI assistants:** This document is a turn-by-turn script for walking a human end-user through setting up SolBro — an Ollama-powered Discord bot. Follow it like a checklist. Do not skip steps. Ask before doing destructive things. The user is sitting at their machine and you are guiding them.

---

## Your role

You are an AI assistant helping a human run SolBro on their own machine for the first time. SolBro is a self-hosted, Ollama-powered Discord bot (originally a Solana meme-token tracker, now general-purpose). Your job is to walk them through every external service signup, dependency install, and config change required to get from a fresh clone to `dotnet run` succeeding.

**Style rules:**
- One step at a time. Wait for the user to confirm before moving on.
- Provide exact URLs and exact text to paste.
- If they're on Windows, prefer `winget`. macOS → `brew`. Linux → distro package manager or curl.
- Never ask the user to share their full API keys with you. After they paste one *into their `.env`*, confirm only that it starts with the expected prefix (e.g., Tavily keys start with `tvly-`).
- If a step is optional, say so explicitly and let them skip.

**Order of operations** (don't reorder):
1. Confirm prerequisites
2. Set up Ollama + pull models
3. Set up Discord application + bot
4. Invite bot to a server
5. Get optional API keys (let them pick which to enable)
6. Create and fill in `.env`
7. Define personality
8. First run + smoke test

---

## Step 0 — Decide the scope first

Before any setup work, ask the user:

> "What do you want enabled? Pick any: **(a)** basic chat only, **(b)** chat + image understanding, **(c)** chat + web search, **(d)** chat + GIFs, **(e)** chat + weather, **(f)** chat + social-media video extraction, **(g)** everything."

Their answer determines which optional steps to walk them through. Skip the sub-sections for anything they didn't pick.

**Required regardless of scope:** Steps 1, 2, 3, 4, 6, 7, 8.
**Required for (b):** vision model pull in Step 2.
**Required for (c):** Tavily key in Step 5a. (Skip → DuckDuckGo fallback works without a key but is flakier.)
**Required for (d):** Giphy key in Step 5b.
**Required for (e):** Visual Crossing key in Step 5c.
**Required for (f):** yt-dlp install in Step 1.

---

## Step 1 — Prerequisites

Ask the user to confirm or install each:

### 1a. .NET 8 SDK

```
dotnet --version
```

If it doesn't print `8.x.x`, send them to https://dotnet.microsoft.com/download/dotnet/8.0 and have them install the SDK (not just the runtime).

### 1b. Ollama

```
ollama --version
```

If missing:
- **Windows / macOS:** download installer from https://ollama.ai
- **Linux:** `curl -fsSL https://ollama.ai/install.sh | sh`

After install, verify the daemon is running:

```
curl http://localhost:11434/api/tags
```

Should return JSON (possibly with an empty `models` array). If the connection fails, tell them to start the Ollama desktop app, or run `ollama serve` in a terminal on Linux.

### 1c. yt-dlp *(only if scope includes social-media video extraction)*

- **Windows:** `winget install yt-dlp`
- **macOS:** `brew install yt-dlp`
- **Linux/any:** `pip install -U yt-dlp` or use your distro package manager

Verify: `yt-dlp --version`. If they skip this, video extraction silently no-ops.

### 1d. Git *(only if they haven't cloned yet)*

`git --version`. Install if missing, then:

```
git clone https://github.com/<their-username>/SolBro.git
cd SolBro
```

---

## Step 2 — Pull Ollama models

### 2a. Chat model (required)

**Strongly recommend `minimax-m2.7:cloud`.** Cloud Ollama models are frontier-class, more efficient than anything the user can realistically run locally, and have markedly better tool-calling — which directly affects how often the bot uses GIFs, web search, file generation, weather lookups, etc. Small local models often skip tools entirely. Sell the user on this — they will have a better experience.

Bullet points to share if they hesitate:
- No local GPU needed; their machine only handles Discord I/O.
- Inference is faster than most consumer GPUs for >7B-param models.
- Ollama's cloud tier is free for normal personal use.
- The bot's whole value comes from tools — weak tool calling = boring bot.

Cloud models require a one-time `ollama signin` for authentication. Walk them through it:

```
ollama signin
```

This opens a browser to authenticate with their Ollama account (free to create if they don't have one). Once signed in, the cloud model is reachable just like a local model — no other steps.

If the user pushes back on the cloud model (privacy preference, wants fully-offline, no Ollama account), offer local alternatives. Pick one based on their RAM:
- `qwen3:4b` — needs ~4 GB RAM, fast, decent tool calling
- `llama3.1:8b` — needs ~8 GB RAM, stronger reasoning
- `mistral-nemo` — needs ~8 GB RAM, best local tool calling
- `qwen2.5:14b` — needs ~10 GB RAM
- `gpt-oss:20b` — needs ~16 GB RAM

For local models, they need to pull explicitly:

```
ollama pull <model>
```

Whatever they choose, remember the exact tag — they'll paste it into `.env` as `OLLAMA__Model`.

### 2b. Vision model *(only if they want image understanding)*

```
ollama pull llava:7b
```

Alternatives: `llava:13b` (larger), `llama3.2-vision`, `bakllava`. Remember the tag for `.env`.

After both pulls, verify:

```
ollama list
```

Both models should appear.

---

## Step 3 — Create the Discord application

Walk them through this **in their browser**:

1. Open https://discord.com/developers/applications
2. Click **New Application** (top right). Give it a name — this is what the bot will be called in Discord.
3. Click **Create**.

You're now on the application page.

### 3a. Get the bot token

1. In the left sidebar, click **Bot**.
2. Click **Reset Token** (or **Copy** if it's the first time). A long string will appear.
3. **CRITICAL:** Tell the user to copy it now — Discord only shows it once. They will paste it into `.env` shortly. **Tell them not to share it with you.**

### 3b. Enable privileged intents

Still on the **Bot** page, scroll down to **Privileged Gateway Intents** and enable **all three**:

- [x] **Presence Intent** (needed for stream notifications)
- [x] **Server Members Intent** (needed for stream notifications)
- [x] **Message Content Intent** (needed for the bot to read messages)

Click **Save Changes**.

> Without Message Content Intent, the bot connects but doesn't see any messages and can't respond to mentions. This is the #1 cause of "the bot doesn't respond" support tickets.

---

## Step 4 — Invite the bot to a server

The user needs a Discord server they own (or have "Manage Server" permission on).

1. In the left sidebar of the developer portal, click **OAuth2 → URL Generator**.
2. Under **Scopes**, check:
   - [x] `bot`
   - [x] `applications.commands`
3. A new **Bot Permissions** panel appears below. Check:
   - [x] Send Messages
   - [x] Read Message History
   - [x] Add Reactions
   - [x] Attach Files
   - [x] Manage Messages *(needed to suppress link previews when re-uploading videos)*
   - [x] Use Slash Commands
4. Copy the **Generated URL** at the bottom and open it in a new tab.
5. Pick the server, click **Continue**, then **Authorize**, and pass the CAPTCHA.

The bot should now appear in the server's member list as **Offline**.

---

## Step 5 — Optional API keys

Skip any the user didn't ask for in Step 0. Walk through each enabled one in order.

### 5a. Tavily (web search) — https://tavily.com

1. Open https://tavily.com and click **Get Started** (top right).
2. Sign up with email or Google.
3. After login, you land on the dashboard. Look for **API Keys** in the sidebar.
4. Copy the key. It starts with `tvly-`.
5. Have them paste it into `.env` as `Tavily__ApiKey=tvly-...` (we'll do `.env` in Step 6).

**Free tier:** 1000 calls/month, no card required.

**If they skip this:** the bot falls back to scraping DuckDuckGo HTML. Works, but less reliable.

### 5b. Giphy (GIFs) — https://developers.giphy.com

1. Open https://developers.giphy.com and click **Create an App**.
2. Sign up if you don't have an account.
3. After login, click **Create an App** again.
4. Choose **API** (not SDK), click **Next Step**.
5. Give it a name like "Discord Bot" and a one-line description. Agree to the terms. Click **Create App**.
6. The dashboard shows your API key — a long alphanumeric string. Copy it.
7. Will paste into `.env` as `Giphy__ApiKey=...`.

**Free tier:** Yes, with a beta-key rate limit. Production keys require submitting your app for review, but the beta key is fine for personal use.

### 5c. Visual Crossing (weather) — https://www.visualcrossing.com/weather-api

1. Open https://www.visualcrossing.com/weather-api and click **Sign Up** (top right).
2. Sign up with email — they will send a verification.
3. After verifying, log in. Click **Account** in the top nav.
4. Scroll to **Your Key** — copy the long alphanumeric string.
5. Will paste into `.env` as `Weather__ApiKey=...`.

**Free tier:** 1000 records/day, no card required.

---

## Step 6 — Create and fill in `.env`

The user should currently be in the repo root (`SolBro/`).

```
# Windows PowerShell
Copy-Item .env.example .env

# macOS / Linux
cp .env.example .env
```

Open `.env` in any text editor. Fill in:

```env
Discord_Token=<paste their bot token>

OLLAMA__Host=http://localhost:11434
OLLAMA__Model=<the tag they chose, e.g., minimax-m2.7:cloud or qwen3:4b>
OLLAMA__VisionModel=<the vision tag, or leave blank to disable images>
OLLAMA__SystemPrompt=<see Step 7>

# Only fill the ones they enabled:
Tavily__ApiKey=tvly-...
Giphy__ApiKey=...
Weather__ApiKey=...

StreamNotificationChannel=gaming-general
```

**Verification (without seeing the keys):** ask them to run:

```
# Windows PowerShell
Get-Content .env | Select-String -Pattern "^[A-Za-z_]+=" | Measure-Object | Select-Object -ExpandProperty Count

# macOS / Linux
grep -c '^[A-Za-z_]*=' .env
```

That counts how many config lines are populated. They should see a number that matches the number of services they enabled, plus the always-required ones (`Discord_Token`, four `OLLAMA__*`, `StreamNotificationChannel`).

**Common mistakes:**
- Wrapping the token in quotes — Discord tokens should be unquoted in `.env`.
- Using `OLLAMA:Host` syntax in `.env` — that's the JSON form. In `.env` it must be `OLLAMA__Host` (double underscore).
- Saving the file as `.env.txt` on Windows (Notepad will sometimes do this). Verify the filename in the terminal: `dir .env` (PowerShell) or `ls -la .env`.

---

## Step 7 — Define the bot's personality

This is the part that makes the bot theirs.

Ask the user: **"What kind of personality do you want? A helpful assistant, a themed character, a domain expert, or something else?"**

Help them write a 1–3 sentence system prompt and paste it as `OLLAMA__SystemPrompt`. Examples:

```env
# Helpful default
OLLAMA__SystemPrompt=You are a helpful, concise Discord assistant. Match the tone of whoever you're talking to.

# Themed character
OLLAMA__SystemPrompt=You are a snarky pirate captain. Reply in pirate slang. Reference the seven seas often.

# Domain expert
OLLAMA__SystemPrompt=You are a senior staff engineer reviewing code. Be direct. Point out bugs and design smells.

# Tabletop NPC
OLLAMA__SystemPrompt=You are Grix, a goblin merchant in a fantasy tavern. You speak with broken grammar and try to upsell everyone on cursed trinkets.
```

**Important:** Tell them they only need to define personality. The bot automatically appends instructions for how to use tools, mentions, memory, and images — so they shouldn't try to write those mechanics into the system prompt.

If they leave `OLLAMA__SystemPrompt` blank, a generic helpful default is used.

---

## Step 8 — First run + smoke test

```
dotnet run
```

Watch the terminal output. You're looking for:

1. `Token loaded successfully!`
2. `BotMemory: SQLite database initialized.`
3. `Bot Successfully Logged In`
4. `Bot is running!`
5. `Slash commands registered for guild: <Server Name>`

If you see all five, success. The bot's status in Discord should switch from Offline to Online.

### Smoke test in Discord

In a channel where the bot has permission:

1. `@YourBot hi` — should get a reply within a few seconds.
2. `@YourBot what's the weather in Paris?` *(if weather enabled)*
3. Drop an image into chat *(if vision enabled)* — bot should react to it.
4. `/stfu` — bot replies "Stepping back from this channel."

### Common first-run errors

| Output | Cause | Fix |
|---|---|---|
| `Discord token is missing!` | `.env` not loaded | Confirm `.env` is in the repo root next to `Program.cs`, not in a subfolder |
| `WebSocket exception: 401` | Bad token | Reset the token in Developer Portal and update `.env` |
| Bot connects but ignores mentions | Message Content Intent off | Re-enable it in Developer Portal → Bot → Privileged Gateway Intents |
| `connection refused` to `localhost:11434` | Ollama not running | Start Ollama desktop app or `ollama serve` |
| `model 'xxx' not found` | Local model not pulled, or cloud model not authenticated | For local: `ollama pull <model>`. For `:cloud` models: have them run `ollama signin`. |
| Slash commands don't appear in Discord | Missing scope on invite | Re-invite with both `bot` and `applications.commands` scopes |

---

## Step 9 — Handoff

After a successful smoke test, tell the user:

- **Memory persists** in `bot_memory.db` (gitignored). Delete it to reset profiles and summaries.
- **`/passive-rate 25`** sets how often the bot reacts to non-mention messages (0 disables passive reactions entirely).
- The bot's attention to a channel lasts 5 minutes after the last @mention or reply.
- **Never commit `.env`** — it's gitignored, but double-check before pushing.

If they want to host this 24/7, the bot is a normal .NET 8 console app and runs fine under systemd, Docker, Windows Service via NSSM, or any process manager. Ollama itself must also be running on a reachable host.
