# AI Legacy

AI Legacy is an open-source mod for Mount & Blade II: Bannerlord. Characters in Calradia talk, remember and act through a language model that runs on your own API keys. You choose the provider and pay it directly, or run a local model and pay nothing.

> Work in progress. I build and play it on Bannerlord v1.4.8 with War Sails. A lot of the systems are new and haven't been tested much, so expect bugs and please report them.

## Features

Conversation:
- Write freely to any lord, lady, companion, notable, guard or townsperson, on the campaign map or inside a scene.
- Each character gets a generated personality, backstory, way of speaking, goals and secrets, and remembers what you said and did in that campaign.
- NPCs also remember each other. They hold grudges, make friends and pass gossip along.
- Group conversations with several characters at once. You can whisper or shout, and people standing nearby may overhear.
- NPCs chat among themselves, make remarks when you walk past and sometimes come up to talk to you first.

Actions with consequences:
- A conversation can change the game. Gold and items change hands, relations go up or down, and characters can follow you, escort someone, join your party, marry you, duel you, arrest you, call the guards or release prisoners.
- Attack someone in the street and the witnesses will remember it. If you get arrested, a lord decides your sentence.
- NPCs can give you tasks, and they show up in the game's quest journal.
- Letters and messengers.

The world:
- Rulers declare war, make peace, form alliances, demand tribute and make public statements, all decided by the model.
- World events affect the economy and politics of towns and clans. A news window lists them.
- Rumours and secrets spread from person to person.
- Diseases, hospitals and quarantines.
- An optional AI commander for enemy and allied formations in battle, and spoken orders for your own troops.

Tools:
- About 280 settings in MCM. Every system can be turned off if you want to save tokens.
- A web editor, opened from the game, to edit characters, memories, world lore, events and every prompt template.
- An optional one-click import of character memories from an existing AIInfluence campaign.

## Languages

The interface is available in English, Spanish, German, French, Italian, Brazilian Portuguese, Russian and Simplified Chinese.

You can change it in `Options → Mod Options → AI Legacy → 1. General → Interface language`. *Auto* uses the game's language. NPCs reply in the interface language unless you type a different one in the setting below it (any language your model can write).

Translations are in `Module/lang/<code>.json`, as plain `"Spanish source text": "translation"` pairs. To add a language, copy `en.json`, translate the values and add the code and name to `Codes` and `Names` in `src/Core/L.cs`. Missing entries fall back to English. Most translations were machine-assisted, so corrections are very welcome.

## Requirements

- Mount & Blade II: Bannerlord v1.4.x
- [Harmony](https://www.nexusmods.com/mountandblade2bannerlord/mods/2006), [ButterLib](https://www.nexusmods.com/mountandblade2bannerlord/mods/2018), [UIExtenderEx](https://www.nexusmods.com/mountandblade2bannerlord/mods/2102) and [Mod Configuration Menu (MCM)](https://www.nexusmods.com/mountandblade2bannerlord/mods/612), loaded before AI Legacy
- An API key for an OpenAI-compatible service (Google Gemini, OpenAI, Groq, DeepSeek...) or a local model running in Ollama
- The Microsoft Edge WebView2 runtime for the editor window. Windows 10 and 11 already have it.

## Install

1. Download a release and extract the `AILegacy` folder into `<Bannerlord>\Modules\`.
2. Enable Harmony, ButterLib, UIExtenderEx, MCM and AI Legacy in the launcher.
3. In game, go to `Options → Mod Options → AI Legacy → 0. AI keys`. Enable a provider, type the service name and paste your key. You can set up to four providers. They are tried in order, so a free one makes a good fallback.
4. Load a campaign and talk to someone.

MCM stores your keys in `Documents\Mount and Blade II Bannerlord\Configs\ModSettings`, outside the mod folder. There is also an optional `config.json` for advanced provider options (see `Module/config.example.json`).

How much it costs depends on you. Every system that calls the model has its own switch and limits in MCM. NPCs talking among themselves uses the most tokens, so lower it or turn it off if you want to spend less.

## Building from source

You need the .NET SDK (any recent version) and the game installed with the four mods listed above.

```
git clone https://github.com/david204/AILegacy.git "<Bannerlord>\AILegacy"
cd "<Bannerlord>\AILegacy"
dotnet build -c Release
```

The project expects to sit in a folder directly inside the game directory, and it writes the built mod to `<Bannerlord>\Modules\AILegacy`. If you cloned it somewhere else, pass the game path:

```
dotnet build -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Mount & Blade II Bannerlord"
```

Close the game and the launcher before building. Otherwise the DLL is locked and can't be replaced.

| Path | Contents |
|---|---|
| `src/` | C# source |
| `Module/` | Files shipped with the mod: `SubModule.xml`, UI prefabs, the web editor, language files and the default world lore |
| `libs/webview2/` | Microsoft WebView2 SDK binaries used by the editor window |

Code comments are in Spanish. The prompts sent to the model are in English.

## Privacy

The mod sends conversation text and game context (names, places and events from your campaign) to the AI provider you configure, and nowhere else. It has no telemetry.

## License

MIT. See [LICENSE](LICENSE), which also has the third-party notices.

AI Legacy is an unofficial fan mod and isn't affiliated with TaleWorlds Entertainment.
