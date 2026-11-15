using System;
using AILegacy.Core;
using AILegacy.Llm;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace AILegacy.Settings
{
    /// <summary>In-game settings menu (Options > Mod Options > AI Legacy).</summary>
    public class AILegacySettings : AttributeGlobalSettings<AILegacySettings>
    {
        public override string Id => "AILegacy_v1";
        public override string DisplayName => "AI Legacy";
        public override string FolderName => "AILegacy";
        public override string FormatType => "json2";

        // MCM buttons run on the main thread on the next tick
        private static void Later(System.Action a) => Core.MainThread.Post(a);

        // ------------------------------------------------------------ AI keys and providers (saved by MCM, outside the mod folder)
        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1 to 6): if the first one fails or runs out of quota, the next one is used. Slots 1 to 4 come filled in for Gemini, OpenAI, DeepSeek and Ollama as examples: add your key and switch them on, or change anything. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1", GroupOrder = 0)]
        public bool P1Enabled { get; set; } = true;

        [SettingPropertyText("{=AIL_5794d85d}Service", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Type one: gemini, openai, groq, deepseek, openrouter, ollama (local, no key) or other (then fill in the address).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public string P1Service { get; set; } = "gemini";

        [SettingPropertyText("{=AIL_4868da9a}Key (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Your key for that service. It is shown in plain text on this screen: don't show it in screenshots.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public string P1Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Model (empty = the recommended one)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Exact name of the model. Empty: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat or llama3.1 depending on the service.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public string P1Model { get; set; } = "gemini-3.5-flash-lite";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, without /chat/completions at the end (the mod adds it), e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public string P1Url { get; set; } = "https://generativelanguage.googleapis.com/v1beta/openai";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public float P1PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public float P1PriceOut { get; set; } = 0f;

        [SettingPropertyText("{=AIL_e7b1c4d1}Vector model (empty = automatic, - = none)", Order = 7, RequireRestart = false,
            HintText = "{=AIL_e7b1c4d2}Model this provider uses for recall by meaning (embeddings). Empty: the usual one of the service if it has one (embedding-3 with GLM), or the one in 4. Memory. Type - so that this provider is never asked for vectors.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public string P1Embed { get; set; } = "gemini-embedding-001";

        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1 to 6): if the first one fails or runs out of quota, the next one is used. Slots 1 to 4 come filled in for Gemini, OpenAI, DeepSeek and Ollama as examples: add your key and switch them on, or change anything. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public bool P2Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Service", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Type one: gemini, openai, groq, deepseek, openrouter, ollama (local, no key) or other (then fill in the address).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public string P2Service { get; set; } = "openai";

        [SettingPropertyText("{=AIL_4868da9a}Key (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Your key for that service. It is shown in plain text on this screen: don't show it in screenshots.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public string P2Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Model (empty = the recommended one)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Exact name of the model. Empty: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat or llama3.1 depending on the service.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public string P2Model { get; set; } = "gpt-5.6-luna";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, without /chat/completions at the end (the mod adds it), e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public string P2Url { get; set; } = "https://api.openai.com/v1";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public float P2PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public float P2PriceOut { get; set; } = 0f;

        [SettingPropertyText("{=AIL_e7b1c4d1}Vector model (empty = automatic, - = none)", Order = 7, RequireRestart = false,
            HintText = "{=AIL_e7b1c4d2}Model this provider uses for recall by meaning (embeddings). Empty: the usual one of the service if it has one (embedding-3 with GLM), or the one in 4. Memory. Type - so that this provider is never asked for vectors.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public string P2Embed { get; set; } = "text-embedding-3-small";

        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1 to 6): if the first one fails or runs out of quota, the next one is used. Slots 1 to 4 come filled in for Gemini, OpenAI, DeepSeek and Ollama as examples: add your key and switch them on, or change anything. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public bool P3Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Service", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Type one: gemini, openai, groq, deepseek, openrouter, ollama (local, no key) or other (then fill in the address).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Service { get; set; } = "deepseek";

        [SettingPropertyText("{=AIL_4868da9a}Key (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Your key for that service. It is shown in plain text on this screen: don't show it in screenshots.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Model (empty = the recommended one)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Exact name of the model. Empty: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat or llama3.1 depending on the service.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Model { get; set; } = "deepseek-chat";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, without /chat/completions at the end (the mod adds it), e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Url { get; set; } = "https://api.deepseek.com/v1";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public float P3PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public float P3PriceOut { get; set; } = 0f;

        [SettingPropertyText("{=AIL_e7b1c4d1}Vector model (empty = automatic, - = none)", Order = 7, RequireRestart = false,
            HintText = "{=AIL_e7b1c4d2}Model this provider uses for recall by meaning (embeddings). Empty: the usual one of the service if it has one (embedding-3 with GLM), or the one in 4. Memory. Type - so that this provider is never asked for vectors.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Embed { get; set; } = "";

        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1 to 6): if the first one fails or runs out of quota, the next one is used. Slots 1 to 4 come filled in for Gemini, OpenAI, DeepSeek and Ollama as examples: add your key and switch them on, or change anything. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public bool P4Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Service", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Type one: gemini, openai, groq, deepseek, openrouter, ollama (local, no key) or other (then fill in the address).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Service { get; set; } = "ollama";

        [SettingPropertyText("{=AIL_4868da9a}Key (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Your key for that service. It is shown in plain text on this screen: don't show it in screenshots.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Model (empty = the recommended one)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Exact name of the model. Empty: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat or llama3.1 depending on the service.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Model { get; set; } = "llama3.1";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, without /chat/completions at the end (the mod adds it), e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Url { get; set; } = "http://localhost:11434/v1";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public float P4PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public float P4PriceOut { get; set; } = 0f;

        [SettingPropertyText("{=AIL_e7b1c4d1}Vector model (empty = automatic, - = none)", Order = 7, RequireRestart = false,
            HintText = "{=AIL_e7b1c4d2}Model this provider uses for recall by meaning (embeddings). Empty: the usual one of the service if it has one (embedding-3 with GLM), or the one in 4. Memory. Type - so that this provider is never asked for vectors.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Embed { get; set; } = "nomic-embed-text";

        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1 to 6): if the first one fails or runs out of quota, the next one is used. Slots 1 to 4 come filled in for Gemini, OpenAI, DeepSeek and Ollama as examples: add your key and switch them on, or change anything. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d5}Provider 5")]
        public bool P5Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Service", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Type one: gemini, openai, groq, deepseek, openrouter, ollama (local, no key) or other (then fill in the address).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d5}Provider 5")]
        public string P5Service { get; set; } = "other";

        [SettingPropertyText("{=AIL_4868da9a}Key (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Your key for that service. It is shown in plain text on this screen: don't show it in screenshots.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d5}Provider 5")]
        public string P5Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Model (empty = the recommended one)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Exact name of the model. Empty: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat or llama3.1 depending on the service.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d5}Provider 5")]
        public string P5Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, without /chat/completions at the end (the mod adds it), e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d5}Provider 5")]
        public string P5Url { get; set; } = "";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d5}Provider 5")]
        public float P5PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d5}Provider 5")]
        public float P5PriceOut { get; set; } = 0f;

        [SettingPropertyText("{=AIL_e7b1c4d1}Vector model (empty = automatic, - = none)", Order = 7, RequireRestart = false,
            HintText = "{=AIL_e7b1c4d2}Model this provider uses for recall by meaning (embeddings). Empty: the usual one of the service if it has one (embedding-3 with GLM), or the one in 4. Memory. Type - so that this provider is never asked for vectors.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d5}Provider 5")]
        public string P5Embed { get; set; } = "";

        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1 to 6): if the first one fails or runs out of quota, the next one is used. Slots 1 to 4 come filled in for Gemini, OpenAI, DeepSeek and Ollama as examples: add your key and switch them on, or change anything. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d6}Provider 6")]
        public bool P6Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Service", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Type one: gemini, openai, groq, deepseek, openrouter, ollama (local, no key) or other (then fill in the address).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d6}Provider 6")]
        public string P6Service { get; set; } = "other";

        [SettingPropertyText("{=AIL_4868da9a}Key (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Your key for that service. It is shown in plain text on this screen: don't show it in screenshots.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d6}Provider 6")]
        public string P6Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Model (empty = the recommended one)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Exact name of the model. Empty: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat or llama3.1 depending on the service.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d6}Provider 6")]
        public string P6Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, without /chat/completions at the end (the mod adds it), e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d6}Provider 6")]
        public string P6Url { get; set; } = "";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d6}Provider 6")]
        public float P6PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d6}Provider 6")]
        public float P6PriceOut { get; set; } = 0f;

        [SettingPropertyText("{=AIL_e7b1c4d1}Vector model (empty = automatic, - = none)", Order = 7, RequireRestart = false,
            HintText = "{=AIL_e7b1c4d2}Model this provider uses for recall by meaning (embeddings). Empty: the usual one of the service if it has one (embedding-3 with GLM), or the one in 4. Memory. Type - so that this provider is never asked for vectors.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_e7b1c4d6}Provider 6")]
        public string P6Embed { get; set; } = "";

        // service, address, model, vector model
        private static readonly string[][] Presets =
        {
            new[] { "gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.5-flash-lite", "gemini-embedding-001" },
            new[] { "openai", "https://api.openai.com/v1", "gpt-5.6-luna", "text-embedding-3-small" },
            new[] { "deepseek", "https://api.deepseek.com/v1", "deepseek-chat", "" },
            new[] { "ollama", "http://localhost:11434/v1", "llama3.1", "nomic-embed-text" },
        };

        /// <summary>Providers set in MCM, in order, with each service's URL and quirks filled in.</summary>
        public System.Collections.Generic.List<Config.ProviderConfig> McmProviders()
        {
            var list = new System.Collections.Generic.List<Config.ProviderConfig>();
            void Add(int n, bool on, string service, string key, string model, string url, float priceIn, float priceOut, string embed)
            {
                service = (service ?? "").Trim().ToLowerInvariant(); key = (key ?? "").Trim(); model = (model ?? "").Trim(); url = (url ?? "").Trim();
                if (!on || service.Length == 0) return;
                embed = (embed ?? "").Trim();
                // The slots come pre-filled. If the service is changed and the old preset values stay, drop them
                string canon = service == "google" ? "gemini" : service == "chatgpt" ? "openai" : service == "local" ? "ollama" : service;
                foreach (var k in Presets)
                {
                    if (k[0] == canon) continue;
                    if (string.Equals(url.TrimEnd('/'), k[1], StringComparison.OrdinalIgnoreCase)) url = "";
                    if (string.Equals(model, k[2], StringComparison.OrdinalIgnoreCase)) model = "";
                    if (k[3].Length > 0 && string.Equals(embed, k[3], StringComparison.OrdinalIgnoreCase)) embed = "";
                }
                var p = new Config.ProviderConfig { Name = service + (n > 1 ? n.ToString() : ""), Enabled = true, ApiKey = key, Model = model, BaseUrl = url, TimeoutSeconds = 30 };
                switch (service)
                {
                    case "gemini": case "google":
                        p.BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai"; if (model.Length == 0) p.Model = "gemini-3.5-flash-lite";
                        if (p.Model.IndexOf("lite", StringComparison.OrdinalIgnoreCase) < 0) p.Extra["reasoning_effort"] = "none";
                        break;
                    case "openai": case "chatgpt":
                        p.BaseUrl = "https://api.openai.com/v1"; if (model.Length == 0) p.Model = "gpt-5.6-luna";
                        p.MaxTokensField = "max_completion_tokens"; p.SendTemperature = false; p.Extra["reasoning_effort"] = "none";
                        break;
                    case "groq": p.BaseUrl = "https://api.groq.com/openai/v1"; if (model.Length == 0) p.Model = "llama-3.3-70b-versatile"; break;
                    case "deepseek": p.BaseUrl = "https://api.deepseek.com/v1"; if (model.Length == 0) p.Model = "deepseek-chat"; break;
                    case "openrouter": p.BaseUrl = "https://openrouter.ai/api/v1"; break;
                    case "ollama": case "local":
                        if (url.Length == 0) p.BaseUrl = "http://localhost:11434/v1"; if (model.Length == 0) p.Model = "llama3.1";
                        if (key.Length == 0) p.ApiKey = "local"; p.TimeoutSeconds = 90;
                        break;
                }
                // A typed address always wins (own servers or "other")
                if (url.Length > 0) p.BaseUrl = url;
                p.PriceIn = Math.Max(0f, priceIn); p.PriceOut = Math.Max(0f, priceOut);
                p.EmbeddingModel = embed;
                list.Add(p);
            }
            Add(1, P1Enabled, P1Service, P1Key, P1Model, P1Url, P1PriceIn, P1PriceOut, P1Embed); Add(2, P2Enabled, P2Service, P2Key, P2Model, P2Url, P2PriceIn, P2PriceOut, P2Embed);
            Add(3, P3Enabled, P3Service, P3Key, P3Model, P3Url, P3PriceIn, P3PriceOut, P3Embed); Add(4, P4Enabled, P4Service, P4Key, P4Model, P4Url, P4PriceIn, P4PriceOut, P4Embed);
            Add(5, P5Enabled, P5Service, P5Key, P5Model, P5Url, P5PriceIn, P5PriceOut, P5Embed); Add(6, P6Enabled, P6Service, P6Key, P6Model, P6Url, P6PriceIn, P6PriceOut, P6Embed);
            return list;
        }

        /// <summary>Changes when any provider field changes in MCM, so they get reloaded right away.</summary>
        public string McmProvidersSignature() =>
            string.Join("|", P1Enabled, P1Service, P1Key, P1Model, P1Url, P2Enabled, P2Service, P2Key, P2Model, P2Url, P3Enabled, P3Service, P3Key, P3Model, P3Url, P4Enabled, P4Service, P4Key, P4Model, P4Url,
                P1PriceIn, P1PriceOut, P2PriceIn, P2PriceOut, P3PriceIn, P3PriceOut, P4PriceIn, P4PriceOut, P1Embed, P2Embed, P3Embed, P4Embed,
                P5Enabled, P5Service, P5Key, P5Model, P5Url, P5PriceIn, P5PriceOut, P5Embed, P6Enabled, P6Service, P6Key, P6Model, P6Url, P6PriceIn, P6PriceOut, P6Embed);

        // ------------------------------------------------------------ Images
        [SettingPropertyDropdown("{=AIL_e7b1c4f0}Where pictures come from", Order = 0, RequireRestart = false,
            HintText = "{=AIL_e7b1c4f1}Each chapter of the memory book can have a picture. Free uses a public service that needs no key: only a description of the scene is sent to it. OpenAI uses the OpenAI provider you set up in 0. AI Keys and costs money per picture. Local needs Stable Diffusion running on your PC with its API on. Off = no pictures.")]
        [SettingPropertyGroup("{=AIL_e7b1c4f2}F. Pictures", GroupOrder = 15)]
        public MCM.Common.Dropdown<string> ImageSource { get; set; } = new MCM.Common.Dropdown<string>(World.SceneImages.SourceNames, World.SceneImages.Free);

        [SettingPropertyBool("{=AIL_e7b1c4f3}Draw each new chapter by itself", Order = 1, RequireRestart = false,
            HintText = "{=AIL_e7b1c4f4}When a new chapter is written, its picture is made in the background. Off: only when you press the button in the book. Each picture takes one short AI request for the description.")]
        [SettingPropertyGroup("{=AIL_e7b1c4f2}F. Pictures", GroupOrder = 15)]
        public bool ImageAuto { get; set; } = true;

        [SettingPropertyText("{=AIL_e7b1c4f5}Style of the pictures", Order = 2, RequireRestart = false,
            HintText = "{=AIL_e7b1c4f6}Added to every picture description, in English.")]
        [SettingPropertyGroup("{=AIL_e7b1c4f2}F. Pictures", GroupOrder = 15)]
        public string ImageStyle { get; set; } = "Painterly medieval illustration, oil on canvas, muted earthy colours, soft natural light, detailed, no text, no watermark";

        [SettingPropertyText("{=AIL_e7b1c4f7}Local Stable Diffusion address", Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e7b1c4f2}F. Pictures", GroupOrder = 15)]
        public string ImageLocalUrl { get; set; } = "http://127.0.0.1:7860";

        [SettingPropertyText("{=AIL_e7b1c4f8}OpenAI image model", Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e7b1c4f2}F. Pictures", GroupOrder = 15)]
        public string ImageOpenAiModel { get; set; } = "gpt-image-1";

        // ------------------------------------------------------------ General
        [SettingPropertyDropdown("{=AIL_e250598b}Interface language", Order = -2, RequireRestart = false,
            HintText = "{=AIL_5fd4967b}Language of AI Legacy's menus, notices and windows. \"Auto\" uses the game's language. To see this menu in the new language, leave the options and come back in.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public MCM.Common.Dropdown<string> UiLanguage { get; set; } = new MCM.Common.Dropdown<string>(L.Names, 0);

        /// <summary>Selected language code: auto, es, en, de, fr, zh...</summary>
        public string UiLanguageCode()
        {
            int i = UiLanguage?.SelectedIndex ?? 0;
            return i >= 0 && i < L.Codes.Length ? L.Codes[i] : "auto";
        }

        [SettingPropertyText("{=AIL_e405f75e}Language of NPC replies", Order = -1, RequireRestart = false,
            HintText = "{=AIL_500ac9b1}Empty: the same language as the interface. If you want another one, type its name in English. E.g.: Spanish (Spain), English, Catalan, Italian.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public string ReplyLanguage { get; set; } = "";

        /// <summary>Language the AI writes in (English name).</summary>
        public string Language => string.IsNullOrWhiteSpace(ReplyLanguage) ? L.EnglishName : ReplyLanguage.Trim();

        [SettingPropertyInteger("{=AIL_91d66f05}Minimum sentences per reply", 1, 6, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int ReplyMinSentences { get; set; } = 1;

        [SettingPropertyInteger("{=AIL_d22a5c23}Maximum sentences per reply", 1, 12, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int ReplyMaxSentences { get; set; } = 4;

        [SettingPropertyBool("{=AIL_90796905}Free chat with townsfolk", Order = 3, RequireRestart = false,
            HintText = "{=AIL_8201313c}Adds free chat to villagers, townspeople and tavern folk (not heroes).")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool EnableResidents { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c6c8}Townsfolk you talked to keep their face", Order = 3, RequireRestart = false,
            HintText = "{=AIL_e7b1c6c9}The game draws a new random face for every townsman each time you enter a place. With this on, a villager, townsman or tavern guest you have talked to comes back with the same face, build, age, clothes and name on later visits, and remembers you after saving and loading. Before, their memory lasted only until you closed the game.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool UniqueResidentFaces { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c6ca}Townsfolk remembered per campaign", 10, 300, Order = 3, RequireRestart = false,
            HintText = "{=AIL_e7b1c6cb}When there are more, the ones you talked to longest ago are forgotten. They are saved with the world file, so a very high number makes that file bigger.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int ResidentsRemembered { get; set; } = 60;

        [SettingPropertyBool("{=AIL_e7b1c6d2}Own music in conversations", Order = 40, RequireRestart = false,
            HintText = "{=AIL_e7b1c6d3}The mod ships no music. Put your own .ogg files in Modules\\AILegacy\\music, in the folders talk, tense, romance, sad and victory. While you talk to someone through the mod, a track from the folder that fits the moment plays (tension, romance and the mood they show decide it; an empty folder falls back to talk) and the game's music is turned down until the talk ends. It sounds at the game's voice volume.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool EnableTalkMusic { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c6d4}Own music: game music volume meanwhile (%)", 0, 100, Order = 41, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int TalkMusicGameVolumePercent { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_e7b1c6d5}Own music: tension from which the tense folder plays", 1, 100, Order = 42, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int TalkMusicTenseFrom { get; set; } = 50;

        [SettingPropertyInteger("{=AIL_e7b1c6d6}Own music: romance from which the romance folder plays", 1, 100, Order = 43, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int TalkMusicRomanceFrom { get; set; } = 40;

        [SettingPropertyInteger("{=AIL_abf0a3ef}Clan tier at which people know you by reputation", 0, 7, Order = 4, RequireRestart = false,
            HintText = "{=AIL_a3cd73e3}From this clan tier on, any NPC knows who you are without an introduction.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int FameTierKnownByName { get; set; } = 4;

        [SettingPropertyBool("{=AIL_62e94015}Inner thought before replying", Order = 5, RequireRestart = false,
            HintText = "{=AIL_344592e1}The NPC reasons in private (what they know, what they want, whether an action makes sense) before speaking. Better replies, slightly slower.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool EnableThoughts { get; set; } = true;

        [SettingPropertyBool("{=AIL_758ef766}Conversation inside the game's dialogue", Order = 7, RequireRestart = false,
            HintText = "{=AIL_d279f208}On: you talk from the game's dialogue box (type → wait → see reply). Off: separate chat window.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool UseDialogStyle { get; set; } = true;

        [SettingPropertyBool("{=AIL_a7e1d3c1}Show the reply as soon as it arrives", Order = 8, RequireRestart = false,
            HintText = "{=AIL_a7e1d3c2}When the answer arrives it appears in the dialogue box by itself, without pressing \"See their reply\". Off: you press it yourself.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool AutoShowReply { get; set; } = true;

        [SettingPropertyBool("{=AIL_a7e1d3c3}Sound when the reply arrives", Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool ReplyReadySound { get; set; } = true;

        [SettingPropertyBool("{=AIL_e6b1c010}Activities in towns and villages", Order = 9, RequireRestart = false,
            HintText = "{=AIL_e6b1c011}Two options in the town and village menus that take a few hours, with the game's waiting bar: ask around to hear what people are saying (one AI request), and spend the evening with your men (morale, and trust from companions).")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool EnableActivities { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e6b1c012}Evening with your men: denars per man", 1, 20, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int EveningCostPerMan { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_e6b1c013}Evening with your men: morale gained", 1, 20, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int EveningMorale { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_e6b1c014}Evening with your men: days before you can repeat it", 1, 30, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int EveningCooldownDays { get; set; } = 3;

        [SettingPropertyBool("{=AIL_d5a0e010}Bars next to the name in conversations", Order = 8, RequireRestart = false,
            HintText = "{=AIL_d5a0e011}In the game's conversation screen, next to the character's name: relation, trust and romance as bars, and a warning if they suspect you lie. With this on, the line of figures under each reply is not shown.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool ShowNameBars { get; set; } = true;

        [SettingPropertyBool("{=AIL_d5a0e012}History button in conversations", Order = 8, RequireRestart = false,
            HintText = "{=AIL_d5a0e013}A button next to the name opens, over the dialogue box, what you and that character have said to each other, with a scrollbar.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool ShowDialogHistory { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d5a0e014}History: lines shown", 5, 200, Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int DialogHistoryLines { get; set; } = 60;

        [SettingPropertyBool("{=AIL_a7e1d3c4}Relation, trust and romance under each reply", Order = 8, RequireRestart = false,
            HintText = "{=AIL_a7e1d3c5}Under each answer, one line with where you stand with that character right now: relation, trust, tension, romance, and how many times they have caught you lying.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool ShowDialogStats { get; set; } = true;

        [SettingPropertyBool("{=AIL_25c835cb}Use asterisks for gestures", Order = 8, RequireRestart = false, HintText = "{=AIL_1586f13c}NPCs describe gestures between *asterisks*.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool UseAsterisks { get; set; } = true;

        [SettingPropertyBool("{=AIL_f8db9623}Buttons on the map (News / Memories)", Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool ShowMapButtons { get; set; } = true;

        [SettingPropertyBool("{=AIL_53a2324c}Companions always recognize you", Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool CompanionsRecognize { get; set; } = true;

        [SettingPropertyBool("{=AIL_16443f4c}The nobles of your kingdom recognize you", Order = 11, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool KingdomRecognize { get; set; } = true;

        [SettingPropertyBool("{=AIL_d2c3f48b}Include the NPC's friends and enemies", Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool IncludeFriends { get; set; } = true;

        [SettingPropertyInteger("{=AIL_77262a54}Maximum friends in the prompt", 0, 15, Order = 13, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int MaxFriends { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_bce32dcd}Maximum enemies in the prompt", 0, 15, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public int MaxEnemies { get; set; } = 5;

        [SettingPropertyBool("{=AIL_08baf922}Memory goes with the saved game", Order = 15, RequireRestart = false,
            HintText = "{=AIL_68f3dc4c}On: when you load a saved game, NPCs remember what they knew when you saved it. Off: they always remember the latest, even if you load an earlier saved game.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool MemoryFollowsSave { get; set; } = true;

        [SettingPropertyBool("{=AIL_a9b74d1b}Remove \"Talk\" from the character list", Order = 17, RequireRestart = false,
            HintText = "{=AIL_cefb1dfd}In the character list of a town, castle or tavern only \"Visit\" remains. You have to go to the person and talk to them in the scene.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool BlockQuickTalk { get; set; } = true;

        [SettingPropertyBool("{=AIL_68a7d597}Test mode: compliant NPCs", Order = 16, RequireRestart = false,
            HintText = "{=AIL_e4267ade}FOR TESTING ONLY. NPCs accept almost any reasonable request (following you, tasks, gifts, troops) without you having to earn their trust first. Turn it off to play.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public bool TestCompliantMode { get; set; } = false;

        // ------------------------------------------------------------ Relation
        [SettingPropertyBool("{=AIL_b59cea89}Personal relation with each NPC", Order = -1, RequireRestart = false,
            HintText = "{=AIL_2839a892}On: your relation is with that person. Off (like the game): with a noble, your relation is that of the head of their clan.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool PersonalRelation { get; set; } = true;

        [SettingPropertyInteger("{=AIL_edd26e08}Maximum relation change per reply", 0, 10, Order = 0, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int MaxRelationChangePerReply { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_1425de35}Maximum relation change per conversation", 0, 50, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int MaxRelationChangePerConversation { get; set; } = 10;

        // ------------------------------------------------------------ Actions
        [SettingPropertyBool("{=AIL_fd3f245a}Enabled actions", Order = 0, RequireRestart = false,
            HintText = "{=AIL_1f1faf3a}If turned off, NPCs only talk: they don't give gold, don't join you, etc.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool EnableActions { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c79f}Catch deeds that are only said", Order = 23, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a0}On: when a character says in a talk that he does something an action carries out (swears fealty, hands over a castle or gold, marries you, joins you, makes peace, frees someone) but uses no action, the mod notes for him that it did not happen, so his memory keeps it as words, and on his next turn asks him once to do it properly or say why not. It reads the reply itself and makes no extra request to the AI. It only understands English and Spanish replies. Off: nothing is checked.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool CatchEmptyPromises { get; set; } = true;

        [SettingPropertyInteger("{=AIL_256e88f0}Maximum gold per action", 0, 1000000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int MaxGoldPerAction { get; set; } = 20000;

        [SettingPropertyBool("{=AIL_8ff3713b}Diplomacy (war / peace between kings)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_14250983}A king can declare war on you or make peace with you if you are also a king.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowDiplomacy { get; set; } = true;

        [SettingPropertyBool("{=AIL_4ce0146b}Vassalage and mercenaries", Order = 3, RequireRestart = false,
            HintText = "{=AIL_a30867c0}A clan leader can swear fealty to you or serve you as a mercenary if you are a king.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowFealty { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c783}Prisoners can be forced into deals", Order = 3, RequireRestart = false,
            HintText = "{=AIL_e7b1c784}On: a lord you hold prisoner can, under threat, swear fealty with his clan, serve you as a mercenary, renounce his kingdom or sign a town or castle over to you from his cell, even if he dislikes you (a captive ruler can sign over any fief of his realm). What he swears leaves him free, resentful and trusting you less. Off: a prisoner can only pay, make peace if he is a ruler, be released or die.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowPrisonerDeals { get; set; } = true;

        [SettingPropertyBool("{=AIL_26eb718e}Marriage", Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowMarriage { get; set; } = true;

        [SettingPropertyInteger("{=AIL_c509c21c}Minimum relation to accept marriage", 0, 100, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int MinRelationForMarriage { get; set; } = 30;

        [SettingPropertyBool("{=AIL_b85090ef}Orders on the map", Order = 6, RequireRestart = false,
            HintText = "{=AIL_65ab580e}NPCs who lead a party can agree to follow you, go somewhere, wait, patrol, attack, besiege, raid or join your army. Also by letter.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool EnableMapOrders { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e28e2ffe}Normal duration of an order (days)", 1, 30, Order = 7, RequireRestart = false,
            HintText = "{=AIL_904a67c2}How long they follow you, wait or patrol if nothing else is agreed. Also how long they wait for you somewhere when you arrange to meet.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int OrderDefaultDays { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_12efd9a7}Maximum duration of an order (days)", 1, 60, Order = 8, RequireRestart = false,
            HintText = "{=AIL_cb272b11}While carrying out an order they don't go about their normal life (they don't buy food or recruit), so it is best not to drag it out.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int OrderMaxDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_ac7ff171}Radius for attacking enemy parties", 5, 200, Order = 9, RequireRestart = false,
            HintText = "{=AIL_35e216d6}Distance on the map at which an NPC sees enemies they can go and attack if you ask them to.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int OrderAttackRadius { get; set; } = 40;

        [SettingPropertyBool("{=AIL_5da6bc58}Armies (join yours, gather their own, leave it)", Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowArmies { get; set; } = true;

        // ------------------------------------------------------------ Trust and romance
        [SettingPropertyBool("{=AIL_71329b44}Trust (separate from relation)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_f8007f6a}Each NPC keeps track of how much they trust you. Without enough trust they don't share secrets or sensitive information.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool EnableTrust { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4cef6d19}Maximum trust change per reply", 1, 20, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int MaxTrustChangePerReply { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_1e4baeec}Trust needed to share secrets", 0, 100, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int TrustForSecrets { get; set; } = 40;

        [SettingPropertyInteger("{=AIL_e7b1c4da}Trust needed to talk about sensitive matters", 0, 100, Order = 4, RequireRestart = false,
            HintText = "{=AIL_e7b1c4db}Below this they keep plans, troop movements and intrigues to themselves. Real secrets need the higher value above.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int TrustForSensitive { get; set; } = 25;

        [SettingPropertyBool("{=AIL_e7b1c4f9}A whole band can join you", Order = 30, RequireRestart = false,
            HintText = "{=AIL_e7b1c4fa}The leader of a bandit party you talk to on the map can decide to join your party with all their men, if you make them a real offer. Their men, their captives and their purse come with them.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowBandRecruit { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c4dc}Relation gained (% of what the AI gives)", 10, 100, Order = 4, RequireRestart = false,
            HintText = "{=AIL_e7b1c4dd}Liking is harder to win than to lose: gains are scaled by this and losses always count in full. 100 = the same both ways.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int RelationGainPercent { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_cd6fe758}Trust you lose if caught in a lie", 0, 50, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int TrustLostPerLie { get; set; } = 10;

        [SettingPropertyBool("{=AIL_a4403b32}Romance", Order = 6, RequireRestart = false,
            HintText = "{=AIL_7d3d5b34}Romance level with each NPC: it rises with courtship, cools if you don't see each other, and counts toward marriage.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool EnableRomance { get; set; } = true;

        [SettingPropertyBool("{=AIL_276e2745}Wedding with companions, wanderers and notables", Order = 7, RequireRestart = false,
            HintText = "{=AIL_94ae59ca}The game only allows marriage between nobles. With this on, a companion, wanderer or notable you have won over can accept: they first join your household as a noble and stop taking up a companion slot.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool MarryCommoners { get; set; } = true;

        [SettingPropertyBool("{=AIL_170fa0ef}Romance with married people", Order = 7, RequireRestart = false,
            HintText = "{=AIL_9e5a3fd7}Off: someone who is married (or you, if you are) doesn't accept another wedding. On: they can accept it, and the previous marriage ends.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool RomanceWithMarried { get; set; } = false;

        [SettingPropertyInteger("{=AIL_f0c471e9}Romance that stops the game from marrying them to someone else (0 = don't stop)", 0, 100, Order = 7, RequireRestart = false,
            HintText = "{=AIL_bdf82f15}From this level of romance with you, the game does not marry that character off to someone else on its own.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int RomanceProtectsFromMarriage { get; set; } = 15;

        [SettingPropertyBool("{=AIL_09d60746}Romance between people of the same sex", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool RomanceSameSex { get; set; } = false;

        [SettingPropertyBool("{=AIL_e7b1c50d}Courtships between other characters", Order = 40, RequireRestart = false,
            HintText = "{=AIL_e7b1c50e}Unmarried nobles who share a hall or an army and suit each other may begin to court. It grows while they stay near, cools when they are apart and can end in a wedding. They and the people around them know about it and talk about it. A character who likes you may also make the first move. No AI requests.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool EnableNpcRomance { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c50f}Daily chance that a new courtship begins (%)", 0, 100, Order = 41, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int NpcRomanceChancePercent { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_e7b1c510}Courtships at the same time, at most", 1, 40, Order = 42, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int NpcRomanceMax { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_e7b1c511}Days of courtship before they can marry", 1, 200, Order = 43, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int NpcRomanceMinDays { get; set; } = 20;

        /// <summary>How far a courtship has to go before the wedding.</summary>
        public int NpcRomanceForMarriage => 80;

        [SettingPropertyInteger("{=AIL_c5ca3b43}Maximum romance change per reply", 1, 20, Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int MaxRomanceChangePerReply { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_d582953e}Romance needed to accept marriage", 0, 100, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int RomanceForMarriage { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_635ff613}Days without seeing each other before romance cools", 1, 120, Order = 10, RequireRestart = false,
            HintText = "{=AIL_a643593e}After this many days without talking or writing to each other, romance drops 1 point per day (with your spouse it doesn't drop below 40).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int RomanceDecayAfterDays { get; set; } = 14;

        [SettingPropertyBool("{=AIL_0ea1a4c8}Intimacy", Order = 11, RequireRestart = false,
            HintText = "{=AIL_49588f47}With high romance, the NPC may agree to spend the night with you (never described). Between spouses it can end in pregnancy.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool AllowIntimacy { get; set; } = true;

        [SettingPropertyInteger("{=AIL_22b0d9b9}Romance needed for intimacy", 0, 100, Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int RomanceForIntimacy { get; set; } = 70;

        [SettingPropertyInteger("{=AIL_6b1ca542}Pregnancy chance (%)", 0, 100, Order = 13, RequireRestart = false,
            HintText = "{=AIL_6ac2e619}Only between spouses (the game needs a known father).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int PregnancyChancePercent { get; set; } = 25;

        [SettingPropertyBool("{=AIL_a1c0e5d1}Mood from causes", Order = 20, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d2}Each character's mood comes from what has happened to them, several things at once: a battle won or lost, a tournament, being a prisoner or wounded, a dead relative or friend, a war just declared, a birth. Each one lasts a few days. Off: only how the last talk left them counts.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool EnableMoodCauses { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a1c0e5d3}How long mood causes last (%)", 25, 400, Order = 21, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d4}100 = normal: a tournament win is savoured for 5 days, a battle for 3 or 4, mourning lasts 14 days for family and 10 for a friend. 200 = twice as long.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int MoodDurationPercent { get; set; } = 100;

        [SettingPropertyBool("{=AIL_a1c0e5d5}Female cycle", Order = 22, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d6}Women aged 13 to 47 have a cycle: it changes their mood depending on the day (a lot for some, hardly at all for others) and the chance of pregnancy, the game's own included. No cycle during pregnancy or after giving birth. She knows what day she is on.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool EnableFemaleCycle { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a1c0e5d7}Cycle length in days", 10, 40, Order = 23, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d8}Campaign days. A game year has 84 days, so 21 means four cycles a year.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int FemaleCycleDays { get; set; } = 21;

        [SettingPropertyBool("{=AIL_6746c998}Handing over troops and prisoners", Order = 11, RequireRestart = false,
            HintText = "{=AIL_c0668e57}An NPC with a party can give you soldiers or prisoners, or accept the ones you offer.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowTransfers { get; set; } = true;

        [SettingPropertyInteger("{=AIL_86ee0cb9}Maximum troops per handover", 1, 500, Order = 12, RequireRestart = false,
            HintText = "{=AIL_6a6bf769}Besides, an NPC never gives up more than a third of their party at once.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int MaxTroopsPerTransfer { get; set; } = 40;

        [SettingPropertyBool("{=AIL_3a0a45fd}Combat from the chat", Order = 13, RequireRestart = false,
            HintText = "{=AIL_298b35b9}Fistfights and duels in scenes, enemies attacking and surrendering on the map, and calling the guard.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowCombat { get; set; } = true;

        [SettingPropertyBool("{=AIL_731669e9}Townsfolk who join you", Order = 14, RequireRestart = false,
            HintText = "{=AIL_f8fff764}A villager you have really talked to can give up their life and join your company: they become a permanent character, with their own name, personality and memory. Their appearance changes to that of the new character.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowPromotions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_ac508642}Minimum sentences spoken before they can join", 2, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int PromotionMinTurns { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_b40b4b3a}Don't repeat the same payment within (seconds)", 0, 900, Order = 13, RequireRestart = false,
            HintText = "{=AIL_800f61d9}Sometimes the AI repeats a charge or payment that was already made in its next reply. The same amount, in the same direction and with the same person is not applied again within this time. 0 = no protection.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int GoldRepeatGuardSeconds { get; set; } = 240;

        [SettingPropertyBool("{=AIL_80912930}The guard steps in", Order = 14, RequireRestart = false,
            HintText = "{=AIL_6d2b9329}After a fight or duel in a settlement that is not yours, if a guard is nearby they come over to call you to account and decide: let you go, fine you or lock you up.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowJustice { get; set; } = true;

        [SettingPropertyBool("{=AIL_d594c7d1}Hitting anyone starts a fight", Order = 14, RequireRestart = false,
            HintText = "{=AIL_76fdb1de}In a town, village or castle, if you throw a punch at someone right in front of you, the fight with them starts without you having to talk to them: with fists if you are empty-handed, for real if you have your weapon drawn. Their relatives come to their defense and, if steel is drawn, so does the guard. They hold it against you, your bad reputation rises, and then the guard comes.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowStreetViolence { get; set; } = true;

        [SettingPropertyInteger("{=AIL_1c381bc9}How many come to the defense of the attacked person, at most", 0, 10, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int StreetFightHelpers { get; set; } = 4;

        [SettingPropertyBool("{=AIL_76ca0cb9}Armed fight: the guard arrives in waves", Order = 14, RequireRestart = false,
            HintText = "{=AIL_73b83666}If you attack someone with a weapon in a town, nearby guards join in every few seconds. With several down, the alarm sounds and the whole guard comes.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool StreetGuardWaves { get; set; } = true;

        [SettingPropertyInteger("{=AIL_45d4f33e}Guards per wave", 1, 10, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int StreetGuardWaveSize { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_5689cbfa}Seconds between waves", 3, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int StreetGuardWaveSeconds { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_eb1b8e7a}Maximum guards and defenders in a fight", 2, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int StreetMaxGuards { get; set; } = 20;

        [SettingPropertyBool("{=AIL_c61991e6}Armed fight: you can call your troops (Ctrl+X)", Order = 14, RequireRestart = false,
            HintText = "{=AIL_5b8786aa}During a fight in the street or in a village, Ctrl+X brings in ten of your soldiers and Ctrl+Alt+X as many as fit. They don't enter indoors. Those who fall are left wounded or dead in your party.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool StreetSummonTroops { get; set; } = true;

        [SettingPropertyInteger("{=AIL_164cfae0}Maximum of your soldiers in a street fight", 5, 100, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int StreetMaxOwnTroops { get; set; } = 30;

        [SettingPropertyBool("{=AIL_f264e0b3}Real trade with the people of the place", Order = 14, RequireRestart = false,
            HintText = "{=AIL_4a37fd4a}Merchants, craftsmen, innkeepers and villagers can sell to you and buy from you goods in a chat, against the real stock and prices of that place's market: goods and money really change hands.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowRealTrade { get; set; } = true;

        [SettingPropertyBool("{=AIL_109be563}Roleplay death", Order = 14, RequireRestart = false,
            HintText = "{=AIL_cd7a8ef0}A character can die as the outcome of a chat: if they are your prisoner and you execute them by saying so clearly, if they take their own life, or if they are dying of their wounds. They really die in the saved game.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowRoleplayDeath { get; set; } = true;

        [SettingPropertyBool("{=AIL_c949ab1f}Roleplay death: in any situation", Order = 14, RequireRestart = false,
            HintText = "{=AIL_c79b58e9}Off (recommended): only your prisoners and the wounded. On: the AI can decide the death of any character if the scene calls for it, including those of your clan.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool RoleplayDeathAnywhere { get; set; } = false;

        [SettingPropertyBool("{=AIL_4f0d9e02}Extras can become characters", Order = 14, RequireRestart = false,
            HintText = "{=AIL_7245c3fc}A villager in a scene, or one of your soldiers, can agree to enter your service as a companion (if you have room) or remain as a named person you can find again. They keep their face, age and what you have talked about.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowPromotion { get; set; } = true;

        [SettingPropertyBool("{=AIL_b0a1e3a6}Witnesses are required", Order = 14, RequireRestart = false,
            HintText = "{=AIL_ced9d43e}If you hit or kill someone without anyone alive seeing it, nobody knows it was you: no bad reputation, no guard, no name in the news. If they see you but don't know who you are, they look for \"an armed stranger\" and the bad reputation is halved. They only name you if a witness knows you or if you are already famous.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool EnableWitnesses { get; set; } = true;

        [SettingPropertyInteger("{=AIL_8aab03a7}Witnesses: how far they can see (meters)", 5, 80, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int WitnessRadius { get; set; } = 25;

        [SettingPropertyInteger("{=AIL_41faa86d}Witnesses: clan tier from which everyone recognizes you", 1, 7, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int WitnessFameTier { get; set; } = 3;

        [SettingPropertyBool("{=AIL_6e4f31b1}Characters can arrest each other", Order = 14, RequireRestart = false,
            HintText = "{=AIL_3732e7fc}In a group conversation in a town or castle, a guard or whoever is in charge there can take another character present into custody if there is a serious reason (e.g. you accuse them of a crime with evidence in plain sight). They are really imprisoned, and they hold it against you.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowNpcArrests { get; set; } = true;

        [SettingPropertyBool("{=AIL_fedffd7f}The lord of the place judges you", Order = 14, RequireRestart = false,
            HintText = "{=AIL_5b102155}If the guard locks you up outside your fief and your kingdom, the lord of the place passes sentence (release, fine or days in the dungeon), changes your reputation there and what they think of you, and their sentence is announced as news. They need to know who you are there.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowVerdict { get; set; } = true;

        [SettingPropertyInteger("{=AIL_ac30aa7a}Maximum sentence (days in the dungeon)", 1, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int VerdictMaxDays { get; set; } = 10;

        [SettingPropertyBool("{=AIL_15eec8c6}Lines during fights", Order = 14, RequireRestart = false,
            HintText = "{=AIL_7aad4a7d}In fights and duels within a scene, your opponent says lines depending on how it's going, and onlookers shout. Just one request to the AI when the fight starts.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool EnableFightBanter { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3ba48d61}Distance at which a guard sees a fight (meters)", 5, 200, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int GuardHearingDistance { get; set; } = 35;

        [SettingPropertyInteger("{=AIL_148d1995}Distance at which a guard hears a call (meters)", 5, 400, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int GuardCallDistance { get; set; } = 90;

        [SettingPropertyInteger("{=AIL_fde6aa22}Usual fine: minimum", 1, 100000, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int FineMin { get; set; } = 50;

        [SettingPropertyInteger("{=AIL_7f897c38}Usual fine: maximum", 1, 100000, Order = 14, RequireRestart = false,
            HintText = "{=AIL_004eae62}The guard can go up to five times over this value if there was blood or you disrespect them.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int FineMax { get; set; } = 500;

        [SettingPropertyInteger("{=AIL_6184689e}Crime for walking out on a guard", 0, 100, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int CrimeForWalkingAway { get; set; } = 10;

        [SettingPropertyBool("{=AIL_2da21a68}Duels to the death really kill", Order = 14, RequireRestart = false,
            HintText = "{=AIL_735ebb21}If you win a duel to the death against a hero, they die. You never die: you are left badly wounded.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowDuelDeath { get; set; } = true;

        [SettingPropertyBool("{=AIL_7a6f04b2}Tension and threat", Order = 14, RequireRestart = false,
            HintText = "{=AIL_04d419f6}Tension rises with insults and threats and drops with the days. With high tension the NPC can turn to violence or call the guard. They also gauge whether you frighten them (your troops against theirs).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public bool EnableTension { get; set; } = true;

        [SettingPropertyInteger("{=AIL_35049c49}Maximum tension change per reply", 1, 50, Order = 15, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int MaxTensionChangePerReply { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_bcd82606}Tension needed for violence", 0, 100, Order = 16, RequireRestart = false,
            HintText = "{=AIL_c0e5f1de}From here on they can attack you on their own or call the guard. Agreed duels and fights don't need it.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int TensionForViolence { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_ab464755}Tension that drops each day", 1, 100, Order = 17, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int TensionDecayPerDay { get; set; } = 15;

        [SettingPropertyBool("{=AIL_cae619f6}Tasks (quests created by the AI)", Order = 15, RequireRestart = false,
            HintText = "{=AIL_745a654d}NPCs can give you tasks, note your progress, and mark them fulfilled (with reward) or failed. They appear in the game's quest log. Careful: a saved game with tasks needs AI Legacy to load.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool EnableQuests { get; set; } = true;

        [SettingPropertyInteger("{=AIL_f039f8c1}Active tasks at once", 1, 30, Order = 16, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int MaxActiveQuests { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_871af31c}Relation gained when completing a task", 0, 20, Order = 17, RequireRestart = false,
            HintText = "{=AIL_3a0aca3f}If you fail or the deadline passes, you lose half.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int QuestRelationReward { get; set; } = 4;

        [SettingPropertyBool("{=AIL_5454e867}Items, documents and workshops", Order = 18, RequireRestart = false,
            HintText = "{=AIL_ba299376}Giving and receiving items, roleplay documents (letters of introduction, contracts, safe-conducts) and buying workshops by talking.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowItems { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0bfa167c}Maximum value of the items an NPC gives at once", 0, 1000000, Order = 19, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int MaxItemValuePerAction { get; set; } = 5000;

        [SettingPropertyBool("{=AIL_da8482bd}Grant and accept fiefs", Order = 20, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowFiefs { get; set; } = true;

        [SettingPropertyBool("{=AIL_911ad5bc}Abdicate and hand over the crown", Order = 21, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowAbdication { get; set; } = true;

        [SettingPropertyBool("{=AIL_990436d2}Clan changes (join yours, leave it)", Order = 22, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AllowClanChanges { get; set; } = true;

        // ------------------------------------------------------------ Memory
        [SettingPropertyInteger("{=AIL_1057a530}History turns in the prompt", 4, 60, Order = 0, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int HistoryTurnsInPrompt { get; set; } = 16;

        [SettingPropertyBool("{=AIL_d4b8a2f1}Recall by meaning (RAG)", Order = 21, RequireRestart = false,
            HintText = "{=AIL_d4b8a2f2}Besides searching by words, your shared history is searched for moments that relate to what you say even if you use other words (\"that promise\" finds \"I will help you take the fortress\"). It uses the vectors of a provider you already have (Gemini gives them free; OpenAI charges cents). If none provides them, the word search carries on.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableRag { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d4b8a2f3}History chunks retrieved", 1, 10, Order = 22, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int RagTopK { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_d4b8a2f4}Lines per chunk", 2, 20, Order = 23, RequireRestart = false,
            HintText = "{=AIL_d4b8a2f5}How many consecutive lines form one chunk of history when indexing it. If you change it, each character's index rebuilds itself.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int RagChunkSize { get; set; } = 6;

        [SettingPropertyBool("{=AIL_d4b8a2f6}Actions by topic", Order = 24, RequireRestart = false,
            HintText = "{=AIL_d4b8a2f7}Actions that depend on what is being discussed (orders, trade, realm) are only explained in full when relevant; the rest are named on one line and still work. Saves tokens on every line. Needs the vectors of recall by meaning.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableActionRag { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d4b8a2f8}Actions explained in full", 1, 20, Order = 25, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int ActionRagTopK { get; set; } = 7;

        [SettingPropertyText("{=AIL_d4b8a2f9}Vector model (empty = automatic)", Order = 26, RequireRestart = false,
            HintText = "{=AIL_d4b8a2fa}For providers whose own vector model field is empty and whose service the mod does not know. Empty: gemini-embedding-001 with Gemini, text-embedding-3-small with OpenAI, mistral-embed with Mistral, embedding-3 with GLM (Chinese endpoint only), nomic-embed-text with Ollama; DeepSeek, Groq and international GLM do not give vectors. Type another only if your provider uses a different name.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public string EmbeddingModel { get; set; } = "";

        /// <summary>History chunks kept per character in the index. The oldest get dropped.</summary>
        public int RagMaxChunks => 600;

        [SettingPropertyBool("{=AIL_c3a7f1e9}Facts about what is named", Order = 20, RequireRestart = false,
            HintText = "{=AIL_c3a7f1ea}If a place, a house, a realm, a person or a trade good is named in the talk, its public facts go into that request (where it is, whose it is, what it is worth), and on the map also the parties in sight. The character knows what you mean without loading the whole world every time.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableMentions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_effea661}Summarize after N turns", 10, 200, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int SummarizeAfterTurns { get; set; } = 24;

        [SettingPropertyInteger("{=AIL_ba098098}Verbatim turns kept after summarizing", 2, 40, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int KeepTurnsAfterSummary { get; set; } = 8;

        [SettingPropertyBool("{=AIL_5283e9ce}Characters remember their own dealings with each other", Order = 3, RequireRestart = false,
            HintText = "{=AIL_17ca3c0a}Each named character keeps track of how they get along with other characters and the latest thing that happened between them (their chats, an errand, a snub in front of you, an arrest). They use it when they talk to each other, in groups and with you. It is stored in their file, under \"others\".")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableNpcBonds { get; set; } = true;

        [SettingPropertyBool("{=AIL_a2e05b46}What happens between them changes their relation in the game", Order = 3, RequireRestart = false,
            HintText = "{=AIL_02e730b3}When a chat leaves two characters clearly better or worse off, their in-game relation goes up or down by one point.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool NpcBondsAffectRelation { get; set; } = true;

        [SettingPropertyInteger("{=AIL_f43bef55}Memories kept of each other character", 1, 10, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int NpcBondNotes { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_34f22b4a}How many characters each one keeps memories of", 4, 60, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int NpcBondMaxPeople { get; set; } = 20;

        [SettingPropertyBool("{=AIL_daf17754}Complete half-filled character sheets", Order = 3, RequireRestart = false,
            HintText = "{=AIL_e1661a3b}Old character sheets, or ones imported from AIInfluence, only come with personality, past and way of speaking. With this on, in the next chat with that character the AI writes what is missing (what they seek, their secret, what drives them, values, religion, reputation, example phrases) without touching what was already there.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool CompleteProfiles { get; set; } = true;

        [SettingPropertyBool("{=AIL_ad44fa7d}Relevant recall of old conversations", Order = 3, RequireRestart = false,
            HintText = "{=AIL_bc041dfb}Sentences that leave the history when summarizing are archived. When you talk, the ones related to what you are saying now (names, places, specific things) are looked up and the character remembers them word for word. It doesn't use extra requests.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableRecall { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0d81b407}Relevant memories brought into each reply", 1, 8, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int RecallCount { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_52769c73}Old sentences archived per character", 20, 2000, Order = 3, RequireRestart = false,
            HintText = "{=AIL_70a6b6c9}How many old sentences are kept for each character so they can be recalled. More = better long-term memory and somewhat larger files.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int ArchiveTurns { get; set; } = 400;

        [SettingPropertyInteger("{=AIL_69c192a8}Maximum memories per character", 3, 40, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int MaxNotesPerHero { get; set; } = 12;

        [SettingPropertyBool("{=AIL_6b899a92}Book of memories by pages", Order = 4, RequireRestart = false,
            HintText = "{=AIL_ab26e5e4}Each time a stretch of your shared story is summarized, the AI writes a titled page: it can be read in the Book of memories.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableMemoryPages { get; set; } = true;

        [SettingPropertyInteger("{=AIL_5c92a18a}Maximum pages per character", 1, 100, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int MaxMemoryPages { get; set; } = 30;

        [SettingPropertyBool("{=AIL_5c31ae7f}Personality archetypes", Order = 6, RequireRestart = false,
            HintText = "{=AIL_96004d45}Each character is born from a mold (world_data\\archetypes.json) that sets their morals, how they speak and how much they believe. Editable.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableArchetypes { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7df35c1}Religion and customs of each culture", Order = 7, RequireRestart = false,
            HintText = "{=AIL_bd15ad6c}world_data\\religion.json and cultural_traditions.json: the faith and customs (courtship, hospitality, oaths) of their people. Editable.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableReligion { get; set; } = true;

        [SettingPropertyBool("{=AIL_41386273}Personality evolves", Order = 8, RequireRestart = false,
            HintText = "{=AIL_2fb56a84}After an event that marks them (a betrayal, a death, a vow, falling in love), the AI slightly adjusts their personality and notes it in their past.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public bool EnableCharacterDevelopment { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3cb61993}Minimum days between personality changes", 1, 120, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int DevelopmentMinDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_e99f2367}Fields that can change at once", 1, 5, Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int DevelopmentMaxFields { get; set; } = 2;

        // ------------------------------------------------------------ News
        [SettingPropertyBool("{=AIL_e424c129}World news", Order = 0, RequireRestart = false,
            HintText = "{=AIL_30c8d104}Wars, peace treaties, battles, sieges, deaths... NPCs find out depending on importance and proximity.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors", GroupOrder = 5)]
        public bool EnableNews { get; set; } = true;

        [SettingPropertyBool("{=AIL_b430b5cf}Rumors from conversations", Order = 1, RequireRestart = false,
            HintText = "{=AIL_a7c4d3a0}What happens in a chat can spread by word of mouth.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors", GroupOrder = 5)]
        public bool EnableRumors { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a013a921}Days a piece of news lasts", 5, 365, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors", GroupOrder = 5)]
        public int NewsLifetimeDays { get; set; } = 60;

        [SettingPropertyBool("{=AIL_d2e7a4c5}News travels with distance", Order = 5, RequireRestart = false,
            HintText = "{=AIL_d2e7a4c6}What happens far away takes days to arrive, and small matters never get there. Nobles hear from further away than commoners, rulers from further still, and friends or enemies of those involved always find out. Off: everyone in the realm knows at once.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors", GroupOrder = 5)]
        public bool RealisticNewsSpread { get; set; } = true;

        public static readonly string[] PrisonerNewsNames = { "All captures", "Notable only (rulers and heads of a kingdom's own clans)", "Rulers only" };

        [SettingPropertyDropdown("{=AIL_e7b1c73b}Prisoner news kept", Order = 6, RequireRestart = false,
            HintText = "{=AIL_e7b1c73c}Lords are captured all the time, and the news fills up with it. Notable only: the capture of a ruler (news for the whole world) and of the head of a clan sworn to a kingdom (news for that kingdom). Leaders of mercenary and minor clans and ordinary nobles are left out. All: those are kept too, as local talk that never provokes a reaction. Either way the captives of one battle make a single piece of news, and what you capture or who captures you is always told.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors", GroupOrder = 5)]
        public MCM.Common.Dropdown<string> PrisonerNews { get; set; } = new MCM.Common.Dropdown<string>(PrisonerNewsNames, 1);

        /// <summary>0 = all, 1 = notable only, 2 = rulers only.</summary>
        public int PrisonerNewsMode()
        {
            try { return Math.Max(0, Math.Min(2, PrisonerNews?.SelectedIndex ?? 1)); } catch { return 1; }
        }

        [SettingPropertyInteger("{=AIL_8b7f2016}Maximum news in the prompt", 0, 30, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors", GroupOrder = 5)]
        public int MaxNewsInPrompt { get; set; } = 8;

        // ------------------------------------------------------------ Letters
        [SettingPropertyBool("{=AIL_1b9a1bfb}Letters from NPCs", Order = 0, RequireRestart = false,
            HintText = "{=AIL_7f30bf34}Characters you have history with write to you on their own.")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters", GroupOrder = 6)]
        public bool EnableLetters { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4695e872}Daily chance of a letter (%)", 0, 100, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters", GroupOrder = 6)]
        public int LetterChancePercent { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_d943da10}Minimum days between letters", 1, 60, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters", GroupOrder = 6)]
        public int MinDaysBetweenLetters { get; set; } = 4;

        [SettingPropertyBool("{=AIL_e7b1c64e}A letter arrives as a map notice", Order = 5, RequireRestart = false,
            HintText = "{=AIL_e7b1c64f}A letter from a character shows as one of the round notices at the right of the map; click it to read and answer it, right click to discard it. Notices last until you leave the game (the letter stays in their memory either way). Off: the letter opens in a window as it arrives.")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters", GroupOrder = 6)]
        public bool LetterMapNotification { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c650}Also open the letter at once", Order = 6, RequireRestart = false,
            HintText = "{=AIL_e7b1c651}With the map notice on, the letter also opens in a window as it arrives, as it did before.")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters", GroupOrder = 6)]
        public bool LetterPopup { get; set; } = false;

        [SettingPropertyInteger("{=AIL_9d30d4f1}Messenger cost per distance unit", 0, 100, Order = 3, RequireRestart = false,
            HintText = "{=AIL_0ebbc439}Denars per distance unit on the map. Letters are sent from the tavern district.")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters", GroupOrder = 6)]
        public int MessengerCostPerDistance { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_3fbb045d}Travel hours per distance unit (x10)", 1, 100, Order = 4, RequireRestart = false,
            HintText = "{=AIL_95f8de9b}10 = 1 hour per distance unit. Farther = takes longer to arrive (and for the reply to come back).")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters", GroupOrder = 6)]
        public int MessengerHoursPerDistanceX10 { get; set; } = 10;

        // ------------------------------------------------------------ AI
        [SettingPropertyInteger("{=AIL_0d832ced}Temperature (x100)", 0, 150, Order = 0, RequireRestart = false,
            HintText = "{=AIL_e47fec25}Creativity. 80 = 0.8. Some models (GPT-5) ignore it.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public int TemperatureX100 { get; set; } = 80;

        [SettingPropertyInteger("{=AIL_dcc9bf83}Maximum tokens per reply", 200, 4000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public int MaxTokens { get; set; } = 900;

        [SettingPropertyButton("{=AIL_affcf5d2}Test connection to the AIs", Content = "{=AIL_f43d7a3d}Test", Order = 2, RequireRestart = false,
            HintText = "{=AIL_8e88da8b}Tests each provider (those in \"0. AI Keys\" and, if present, those in config.json) and shows the result in the game's log.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public Action TestConnection { get; set; } = () => LlmClient.TestAllProviders();

        [SettingPropertyText("{=AIL_3865f0c2}Provider for heroes", Order = 4, RequireRestart = false,
            HintText = "{=AIL_a9aa8409}Name of a provider: the service you typed in \"0. AI Keys\" (gemini, openai; the one in slot 2, 3 or 4 carries its number: openai2) or one from config.json. Empty = the normal chain. If it fails, the rest are used.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public string ProviderHeroes { get; set; } = "";

        [SettingPropertyText("{=AIL_4b7ed15a}Provider for townsfolk", Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public string ProviderResidents { get; set; } = "";

        [SettingPropertyText("{=AIL_5fcbf671}Provider for letters", Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public string ProviderLetters { get; set; } = "";

        [SettingPropertyText("{=AIL_c5d6d5d7}Provider for memory summaries", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public string ProviderSummaries { get; set; } = "";

        // ------------------------------------------------------------ Map scene
        [SettingPropertyBool("{=AIL_20b8ba96}Map scene: also with enemies (parley)", Order = 20, RequireRestart = false,
            HintText = "{=AIL_011ed64e}With an enemy lord you can also dismount and talk face to face. When you return to the map, the encounter continues where it was (attack, surrender, let you go), unless it was resolved in the chat.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool MapTalkWithEnemies { get; set; } = true;

        [SettingPropertyBool("{=AIL_fe9e2f63}Talk in the scene from the map", Order = 20, RequireRestart = false,
            HintText = "{=AIL_967570b4}When you meet a lord on the map you are not at war with, the option \"Let's dismount and talk face to face\" appears: a scene of the terrain opens with the two of you on foot and your people behind you. Tab to return to the map.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool EnableMapTalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_401f90b1}Map scene: companions per side", 0, 30, Order = 21, RequireRestart = false,
            HintText = "{=AIL_297f659a}How many from each party appear behind their leader (first their heroes, then their best troops).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int MapTalkEscort { get; set; } = 8;

        // ------------------------------------------------------------ Battles
        [SettingPropertyBool("{=AIL_3bdc9ea5}Battle tactics AI in field battles", Order = 0, RequireRestart = false,
            HintText = "{=AIL_39a19577}The leader of each side you don't command periodically receives the state of the battle and gives real orders to their formations, according to their personality and skill. While they decide, or if the AI fails, the game's own tactics continue. It doesn't act in sieges.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public bool EnableBattleAI { get; set; } = false;

        [SettingPropertyInteger("{=AIL_92a26c15}Seconds between each leader's decisions", 30, 300, Order = 1, RequireRestart = false,
            HintText = "{=AIL_458e1b5a}Each decision is one request to the AI per side. If a side suddenly loses many people, it decides sooner.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public int BattleAIInterval { get; set; } = 120;

        [SettingPropertyBool("{=AIL_df336f51}The enemy commands with AI", Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public bool BattleAIEnemy { get; set; } = true;

        [SettingPropertyBool("{=AIL_b8b2988f}Your allies command with AI", Order = 3, RequireRestart = false,
            HintText = "{=AIL_7858cd57}The allied army with its own leader, and your side when you are not the one in command (the formations you lead are not touched).")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public bool BattleAIAllies { get; set; } = false;

        [SettingPropertyBool("{=AIL_7f5984a3}Orders in your own words (Alt+B)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_04c41d7c}In battle, Alt+B: you write the order as you would say it (\"cavalry on the left against their archers\") and the AI turns it into orders for your formations. The game pauses while you type.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public bool BattleAITextOrders { get; set; } = true;

        [SettingPropertyBool("{=AIL_76c8cc5d}Leaders' shouts", Order = 5, RequireRestart = false,
            HintText = "{=AIL_26b70a49}What each leader bellows at their men appears in the log (and above their head if they are near).")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public bool BattleAIShouts { get; set; } = true;

        [SettingPropertyBool("{=AIL_f8823100}Show the AI's orders", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c829a044}Shows in the log what each leader has ordered each formation.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public bool BattleAIShowOrders { get; set; } = true;

        [SettingPropertyText("{=AIL_17a3df8d}Provider for battles", Order = 7, RequireRestart = false,
            HintText = "{=AIL_506e56c2}Name of the config.json provider preferred for battle orders (empty = the normal one). A fast one is best.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public string ProviderBattle { get; set; } = "";

        // ------------------------------------------------------------ Scenes
        [SettingPropertyBool("{=AIL_3515a1a2}Text above characters' heads", Order = 0, RequireRestart = false,
            HintText = "{=AIL_2287fa82}In scenes, what characters say to each other or shout at you in passing appears written above their heads.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool EnableBubbles { get; set; } = true;

        [SettingPropertyBool("{=AIL_2808c06c}Chats between characters in the scenes", Order = 1, RequireRestart = false,
            HintText = "{=AIL_4e6c313c}From time to time, two characters near you talk about what is going on: news, shortages, disease, their lord. Uses one short request to the AI each time.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool EnableAmbient { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4faa8fb4}Seconds between chats (approx.)", 15, 900, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int AmbientIntervalSeconds { get; set; } = 45;

        [SettingPropertyInteger("{=AIL_637830f6}Distance at which you hear a chat (meters)", 4, 60, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int AmbientRadius { get; set; } = 20;

        [SettingPropertyBool("{=AIL_4df26a61}What they say also appears in the log", Order = 1, RequireRestart = false,
            HintText = "{=AIL_af3d096e}Besides above their heads, the sentences characters exchange appear in the message log (bottom left).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool AmbientToLog { get; set; } = true;

        [SettingPropertyBool("{=AIL_131719b8}Whoever wants to talk walks over", Order = 3, RequireRestart = false,
            HintText = "{=AIL_2c243fac}If the two who are going to chat are not together, one walks over to the other before starting (also between floors, if there is a path).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool AmbientWalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_beb1508e}Maximum distance between the two chatting (meters)", 2, 40, Order = 3, RequireRestart = false,
            HintText = "{=AIL_4f107a7c}In a hall the nobles are spread apart: with a low value they never find anyone to talk to.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int AmbientPairDistance { get; set; } = 14;

        [SettingPropertyInteger("{=AIL_508eddc4}Maximum length of each sentence (characters)", 40, 240, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int AmbientMaxChars { get; set; } = 140;

        [SettingPropertyInteger("{=AIL_0ca5ee01}Maximum sentences in a chat between characters", 2, 30, Order = 4, RequireRestart = false,
            HintText = "{=AIL_5d9bde16}The AI writes between half of this number and this number, depending on what the topic allows. It is a single request, long or short.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int AmbientMaxLines { get; set; } = 10;

        [SettingPropertyBool("{=AIL_e37afdcd}Ordinary people also chat (briefly)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5c19273c}Off: only named characters (lords, ladies, notables, wanderers) chat among themselves. On: villagers, innkeepers and others too, but just two or three short sentences.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool AmbientCommoners { get; set; } = true;

        [SettingPropertyBool("{=AIL_2da177ae}Soldiers and guards also chat (briefly)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_74efb871}Off by default: they are on duty. They still answer if you talk to them.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool AmbientGuards { get; set; } = true;

        [SettingPropertyBool("{=AIL_7a1b2cf4}They say things to you in passing", Order = 4, RequireRestart = false,
            HintText = "{=AIL_9b770e1c}When you walk next to someone in a scene (guards, villagers, merchants, nobles who don't know you), they say something in passing. Uses ONE request per visit to each place: a batch of sentences is requested and used up gradually. Turn it off if you don't want that cost.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool EnablePassBy { get; set; } = true;

        [SettingPropertyInteger("{=AIL_90bb89d1}In passing: seconds between one sentence and the next", 4, 120, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int PassBySeconds { get; set; } = 14;

        [SettingPropertyInteger("{=AIL_0b677944}In passing: distance at which they speak to you (meters)", 2, 12, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int PassByDistance { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_cd6bc920}In passing: maximum length of each sentence", 30, 160, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int PassByMaxChars { get; set; } = 80;

        [SettingPropertyInteger("{=AIL_8529201a}In passing: extra batches when sentences run out", 0, 5, Order = 4, RequireRestart = false,
            HintText = "{=AIL_96933cf3}Each extra batch is one more request on that visit. 0 = only the first.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int PassByRefills { get; set; } = 1;

        [SettingPropertyBool("{=AIL_3c35b946}Turn-based chats between nobles (more requests)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_ff3da657}Off: each chat between characters is written whole in one request. On: it is requested in turns and each round knows what was said before; they can interrupt each other and the chat ends on its own if you step in. Uses several requests per chat.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool AmbientTurnByTurn { get; set; } = false;

        [SettingPropertyInteger("{=AIL_a9262a10}Turn-based: sentences per request", 1, 4, Order = 4, RequireRestart = false,
            HintText = "{=AIL_270c2dbd}1 = one request per sentence (the most expensive). 2 = half the requests.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int AmbientTurnSize { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_9f48ba90}Maximum sentences when ordinary people chat", 2, 8, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int AmbientCommonerLines { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_3e132ba1}Group: seconds of your silence before they continue (0 = never)", 0, 180, Order = 5, RequireRestart = false,
            HintText = "{=AIL_f08739d1}If you say nothing during this time, the AI decides whether someone in the group continues the conversation (pushes you, or they start talking among themselves). Each time is one request.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupSilenceSeconds { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_b4fc9c6e}Group: consecutive times they continue without you", 0, 6, Order = 5, RequireRestart = false,
            HintText = "{=AIL_26e90847}How many times in a row they can continue the conversation without you saying anything. When you speak, the count starts over.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupSilenceFollowUps { get; set; } = 4;

        [SettingPropertyBool("{=AIL_7dd76d95}Group: they form a circle around you", Order = 5, RequireRestart = false,
            HintText = "{=AIL_bd386578}When opening a group conversation, the participants walk until they form a semicircle in front of you and look at you. Anyone seated or busy stays where they are.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool GroupCircle { get; set; } = true;

        [SettingPropertyBool("{=AIL_d79a206d}Group: slow motion while you type", Order = 5, RequireRestart = false,
            HintText = "{=AIL_91d8da81}On: when you open the Alt+T window the scene goes on, in slow motion. Off: the game pauses while you type.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool GroupSlowMotion { get; set; } = true;

        [SettingPropertyBool("{=AIL_14d6bbb7}Group: bystanders can chime in", Order = 5, RequireRestart = false,
            HintText = "{=AIL_020f810a}Anyone nearby who hears what you say to the group can walk over and join the conversation if they have a stake in it. Doesn't use extra requests.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool GroupBystanders { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a33f15bf}Group: how far you can be heard (meters)", 1, 20, Order = 5, RequireRestart = false,
            HintText = "{=AIL_d0722260}Distance at which someone outside hears the conversation. A shout (!) reaches twice as far; a whisper (*), only whoever you are looking at.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupHearRadius { get; set; } = 10;

        [SettingPropertyBool("{=AIL_32aaabb0}Gestures and faces while talking", Order = 5, RequireRestart = false,
            HintText = "{=AIL_6fed9f7e}In scene chats, whoever speaks makes the face and gesture of what they feel (nodding, denying, doubting, getting angry…). Anyone seated, busy or walking only changes their face.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool EnableGestures { get; set; } = true;

        [SettingPropertyBool("{=AIL_92d095e7}In the group, they can go and fetch someone", Order = 5, RequireRestart = false,
            HintText = "{=AIL_3a88a804}If what you say in a group conversation gives them reason to, the character can act: go talk to someone else and come back to tell you, accompany you, stay, guide you to someone, call someone over, or leave.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool SceneErrands { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c647}Whoever walks with you follows you through doors", Order = 5, RequireRestart = false,
            HintText = "{=AIL_e7b1c648}A named character who is walking with you comes along when you go through a door to the tavern, the hall or the streets of the same settlement, and a character can offer to take you to one of those places. Nameless townsfolk stop at the door. Off: whoever follows you is left behind at every door.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool FollowAcrossDoors { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c6cc}Lords practise in the arena", Order = 6, RequireRestart = false,
            HintText = "{=AIL_e7b1c6cd}Some nobles staying in a town spend a few hours of the morning or the afternoon at the arena instead of the keep. Enter the arena to find them there and talk to them. They stand among the people there: they do not join the practice fights. Not while a tournament is on.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool LordsTrainInArena { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c6ce}Lords in the arena: chance per lord and day (%)", 0, 100, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int LordsInArenaChancePercent { get; set; } = 35;

        [SettingPropertyBool("{=AIL_e7b1c649}In a fight, their helpers can give up or change sides", Order = 5, RequireRestart = false,
            HintText = "{=AIL_e7b1c64a}Those who fight beside your opponent can lower their weapon and step back, or turn on them, when the fight goes badly, if it fits who they are. Decided in the same request as the fight lines, so it needs \"Lines during fights\" on. Never in a fist fight or a friendly duel.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool FightHelpersWaver { get; set; } = true;

        [SettingPropertyInteger("{=AIL_90722e01}Silence after a chat (seconds)", 30, 900, Order = 4, RequireRestart = false,
            HintText = "{=AIL_c43fad42}Time without new chats after one ends. The same two take twice as long to talk again.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int AmbientCooldownSeconds { get; set; } = 180;

        [SettingPropertyBool("{=AIL_05883eac}Those who know you say something in passing", Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool AmbientCallOuts { get; set; } = true;

        [SettingPropertyBool("{=AIL_7450a805}Group conversation (Alt+T)", Order = 5, RequireRestart = false,
            HintText = "{=AIL_b7c8efe0}In a scene, Alt+T: you talk to everyone near you and the AI decides who answers. Their replies appear above their heads and in the log. Press Alt+T again to keep talking.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool EnableGroupTalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_35cd4d87}Group: distance to join (meters)", 2, 30, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupRadius { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_2eff340e}Group: distance at which someone leaves (meters)", 3, 50, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupGraceRadius { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_a29fcb55}Group: maximum participants", 1, 12, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupMaxParticipants { get; set; } = 6;

        [SettingPropertyBool("{=AIL_c3e1a904}Group: named characters can use every action", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3e1a905}In a group talk, lords and other named characters can do what they could do alone with you: give gold, swear fealty, join you, challenge you. Off: only scene actions, as before. It makes the group request longer.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool GroupFullActions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3bcc8ae2}Group: maximum replies per turn", 1, 6, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupMaxLines { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_7bd9a098}Group: maximum length of each reply", 60, 400, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupMaxChars { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_f6cbdca6}Group: seconds without talking before it breaks up", 30, 1800, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int GroupIdleSeconds { get; set; } = 240;

        [SettingPropertyBool("{=AIL_6e6f1630}What you say is overheard", Order = 6, RequireRestart = false,
            HintText = "{=AIL_d0cb85ca}Named characters who are a few steps away when you talk to someone in a scene overhear what you say, and remember it.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool EnableOverhearing { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a8cfe43b}Distance at which they hear you (meters)", 1, 30, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int OverhearRadius { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_e7b1c4de}Days an overheard line is remembered (0 = always)", 0, 120, Order = 6, RequireRestart = false,
            HintText = "{=AIL_e7b1c4df}What a character caught in passing fades after this many days. Up to 4.5 meters they hear every word; further away they catch only part of it.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int OverheardLifespanDays { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_c4dbb3ad}Maximum distance at which the text is visible (meters)", 5, 100, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public int BubbleMaxDistance { get; set; } = 25;

        // ------------------------------------------------------------ Diseases
        [SettingPropertyBool("{=AIL_68f76b26}Diseases", Order = 0, RequireRestart = false,
            HintText = "{=AIL_6a0ec00a}Outbreaks by season or from an event: they damage the settlement, spread to neighbors, sicken your troops if you enter, and are slowed by quarantines.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public bool EnableDiseases { get; set; } = true;

        [SettingPropertyInteger("{=AIL_5e55369d}Maximum simultaneous outbreaks", 1, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public int DiseaseMaxSimultaneous { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_36b9fe81}Maximum severity", 1, 5, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public int DiseaseMaxSeverity { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_77ca23b7}Minimum days between new outbreaks", 1, 200, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public int DiseaseMinDaysBetweenOutbreaks { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_b3d0b991}Daily chance of a seasonal outbreak (per thousand)", 0, 200, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public int DiseaseSeasonalChancePerMille { get; set; } = 25;

        [SettingPropertyInteger("{=AIL_e00fb19f}Daily spread to a neighbor per severity level (per thousand)", 0, 200, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public int DiseaseSpreadChancePerMille { get; set; } = 12;

        [SettingPropertyInteger("{=AIL_0361e655}Prosperity lost per day per severity level", 0, 50, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public int DiseaseProsperityLossPerSeverity { get; set; } = 4;

        [SettingPropertyBool("{=AIL_c54dc4b5}Events can bring diseases", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public bool DiseaseFromEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_eade4433}Your troops can fall ill", Order = 8, RequireRestart = false,
            HintText = "{=AIL_c6dc6d42}When entering or staying in a place with disease, some of your soldiers may fall ill (they are left as wounded).")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public bool DiseaseAffectsPlayerTroops { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7d2f020}Quarantine closes the gates", Order = 20, RequireRestart = false,
            HintText = "{=AIL_b7d2f021}While a quarantine lasts, lords, caravans and villagers stay away, no tournament is called, and the guard stops you at the gate. Owners and enemies at war are not stopped.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public bool QuarantineClosesGates { get; set; } = true;

        [SettingPropertyInteger("{=AIL_b7d2f022}Bribe to enter a quarantined place, per severity level (0 = no way in)", 0, 5000, Order = 21, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public int QuarantineBribeGold { get; set; } = 300;

        [SettingPropertyInteger("{=AIL_1b8ccb46}Chance of falling ill per severity level (%)", 0, 30, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public int DiseaseTroopChancePerSeverity { get; set; } = 8;

        // People and parties fall ill: limits of what a disease may do
        [SettingPropertyFloatingInteger("{=AIL_e7b1c611}Limits: highest spread rate of a disease", 0.1f, 1f, "0.00", Order = 30, RequireRestart = false,
            HintText = "{=AIL_e7b1c612}How catching a disease can be at most, whether the event that brought it says so or it is worked out from its severity. It scales every contagion chance below.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b1}1. Limits", GroupOrder = 1)]
        public float DiseaseMaxSpreadRate { get; set; } = 0.8f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c613}Limits: highest daily chance of dying (0 = nobody dies of disease)", 0f, 1f, "0.00", Order = 31, RequireRestart = false,
            HintText = "{=AIL_e7b1c614}Only serious diseases (severity 3 or more) kill: surely when they run their full course untreated, and with this daily risk at most from the halfway point. At 0 no hero and no soldier dies of disease.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b1}1. Limits", GroupOrder = 1)]
        public float DiseaseMaxDeathChance { get; set; } = 0.3f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c615}Limits: lowest combat multiplier", 0.1f, 1f, "0.00", Order = 32, RequireRestart = false,
            HintText = "{=AIL_e7b1c616}The weakest a sick fighter can get: damage, weapon speed and, by half, movement in a fight. 1 = disease never weakens anyone in combat.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b1}1. Limits", GroupOrder = 1)]
        public float DiseaseMinCombatModifier { get; set; } = 0.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c617}Limits: lowest map speed multiplier", 0.1f, 1f, "0.00", Order = 33, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b1}1. Limits", GroupOrder = 1)]
        public float DiseaseMinMapSpeedModifier { get; set; } = 0.5f;

        [SettingPropertyInteger("{=AIL_e7b1c618}Limits: largest morale penalty", -50, 0, Order = 34, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b1}1. Limits", GroupOrder = 1)]
        public int DiseaseMaxMoralePenalty { get; set; } = -30;

        [SettingPropertyInteger("{=AIL_e7b1c619}Limits: largest penalty to fighting skills", -50, 0, Order = 35, RequireRestart = false,
            HintText = "{=AIL_e7b1c61a}Points a sick hero loses, at worst, in weapon skills, Riding and Athletics while fighting in a scene.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b1}1. Limits", GroupOrder = 1)]
        public int DiseaseMaxPhysicalSkillPenalty { get; set; } = -30;

        // How it is caught
        [SettingPropertyFloatingInteger("{=AIL_e7b1c61b}Contagion: inside a stricken place (every 6 hours)", 0f, 0.3f, "0.000", Order = 40, RequireRestart = false,
            HintText = "{=AIL_e7b1c61c}Base chance, every 6 hours, that a hero staying in a town, castle or village with an outbreak catches it. Severity, crowding, the spread rate and a good healer change it.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b2}2. Contagion", GroupOrder = 2)]
        public float SettlementInfectionBaseChance { get; set; } = 0.05f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c61d}Contagion: walking the streets of a stricken place (per minute)", 0f, 0.3f, "0.000", Order = 41, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b2}2. Contagion", GroupOrder = 2)]
        public float MissionInfectionBaseChance { get; set; } = 0.08f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c61e}Contagion: a leader from their own sick soldiers (daily)", 0f, 0.5f, "0.000", Order = 42, RequireRestart = false,
            HintText = "{=AIL_e7b1c61f}Daily chance when every soldier is sick; with fewer sick it shrinks in proportion. It applies to you, your companions and every lord.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b2}2. Contagion", GroupOrder = 2)]
        public float PlayerFromTroopsBaseChance { get; set; } = 0.175f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c620}Contagion: in the lord's hall, per sick lord present", 0f, 0.1f, "0.000", Order = 43, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b2}2. Contagion", GroupOrder = 2)]
        public float LordHallInfectionChancePerLord { get; set; } = 0.025f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c621}Contagion: multiplier inside a quarantined place", 0f, 1f, "0.00", Order = 44, RequireRestart = false,
            HintText = "{=AIL_e7b1c622}With the gates shut and the sick kept apart, those inside catch it less. 1 = quarantine does not help those inside; 0 = nobody inside catches it.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b2}2. Contagion", GroupOrder = 2)]
        public float QuarantineInfectionReduction { get; set; } = 0.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c623}Contagion: multiplier to and from prisoners (0 = off)", 0f, 1f, "0.00", Order = 45, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b2}2. Contagion", GroupOrder = 2)]
        public float PrisonerSpreadModifier { get; set; } = 0.5f;

        // Seasonal illnesses
        [SettingPropertyBool("{=AIL_e7b1c624}Seasonal illnesses on the road", Order = 50, RequireRestart = false,
            HintText = "{=AIL_e7b1c625}Mild illnesses (a cold, a camp cough, the ague) for heroes and soldiers travelling the map. More likely in winter, in rain or snow, and at sea. They pass in two weeks and weaken a little meanwhile.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public bool EnableSeasonalDiseases { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c626}Seasonal: base chance (every 6 hours)", 0.0005f, 0.1f, "0.0000", Order = 51, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a1}Chance, every 6 hours, that a hero or a band travelling the map catches something mild. The season and the weather below are added to it, and the total never goes above the highest chance. Recommended: 0.001 to 0.003.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalDiseaseBaseChance { get; set; } = 0.002f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c627}Seasonal: highest chance after all additions", 0.01f, 0.5f, "0.000", Order = 52, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a2}The chance never goes above this, however many additions pile up (winter, a blizzard, at sea). Recommended: 0.02 to 0.05.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalDiseaseMaxChance { get; set; } = 0.04f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c628}Seasonal: added in winter", 0f, 0.2f, "0.0000", Order = 53, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a3}Added to the base chance during that season. Recommended: 0 to 0.005, with winter the highest.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalWinterBonus { get; set; } = 0.005f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c629}Seasonal: added in autumn", 0f, 0.2f, "0.0000", Order = 54, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a3}Added to the base chance during that season. Recommended: 0 to 0.005, with winter the highest.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalAutumnBonus { get; set; } = 0.003f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c62a}Seasonal: added in spring", 0f, 0.2f, "0.0000", Order = 55, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a3}Added to the base chance during that season. Recommended: 0 to 0.005, with winter the highest.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalSpringBonus { get; set; } = 0.002f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c62b}Seasonal: added in summer", 0f, 0.1f, "0.0000", Order = 56, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a3}Added to the base chance during that season. Recommended: 0 to 0.005, with winter the highest.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalSummerBonus { get; set; } = 0.001f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c62c}Seasonal: added in light rain", 0f, 0.1f, "0.0000", Order = 57, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a4}Added to the base chance while the party is in that weather or at sea. Recommended: 0 to 0.004.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalWeatherLightRainBonus { get; set; } = 0.0012f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c62d}Seasonal: added in heavy rain or a storm", 0f, 0.15f, "0.0000", Order = 58, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a4}Added to the base chance while the party is in that weather or at sea. Recommended: 0 to 0.004.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalWeatherHeavyRainBonus { get; set; } = 0.002f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c62e}Seasonal: added in snowfall", 0f, 0.15f, "0.0000", Order = 59, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a4}Added to the base chance while the party is in that weather or at sea. Recommended: 0 to 0.004.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalWeatherSnowyBonus { get; set; } = 0.002f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c62f}Seasonal: added in a blizzard", 0f, 0.2f, "0.0000", Order = 60, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a4}Added to the base chance while the party is in that weather or at sea. Recommended: 0 to 0.004.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalWeatherBlizzardBonus { get; set; } = 0.003f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c630}Seasonal: added on wet ground", 0f, 0.1f, "0.0000", Order = 61, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a4}Added to the base chance while the party is in that weather or at sea. Recommended: 0 to 0.004.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalWeatherWetBonus { get; set; } = 0.001f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c631}Seasonal: added aboard ship at sea", 0f, 0.15f, "0.0000", Order = 62, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a4}Added to the base chance while the party is in that weather or at sea. Recommended: 0 to 0.004.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public float SeasonalShipAtSeaBonus { get; set; } = 0.002f;

        [SettingPropertyInteger("{=AIL_e7b1c63a}Seasonal: days of immunity after getting over one (0 = none)", 0, 60, Order = 63, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a5}After getting over a seasonal illness, a hero cannot catch another one for this many days. Recommended: 14 to 30.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public int SeasonalPostRecoveryImmunityDays { get; set; } = 21;

        [SettingPropertyInteger("{=AIL_e7b1c7a6}Seasonal: most cases at once in the campaign (0 = no limit)", 0, 300, Order = 64, RequireRestart = false,
            HintText = "{=AIL_e7b1c7a7}Seasonal illnesses alive at the same time across the whole map, counting each sick hero and each sick band once. At the limit no lord or caravan catches a new one until someone recovers; you and your party still can. Outbreaks in towns do not count. Recommended: 20 to 60.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b3}3. Seasonal illnesses", GroupOrder = 3)]
        public int SeasonalMaxLiveCases { get; set; } = 40;

        // Treatment and healing
        [SettingPropertyFloatingInteger("{=AIL_e7b1c632}Treatment: base cost in the hospital", 50f, 500f, "0", Order = 70, RequireRestart = false,
            HintText = "{=AIL_e7b1c633}Treating a sick hero costs this, times the severity of the disease, times the price level of the town. Lords pay the same when they ride in sick.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public float DiseaseTreatmentBaseCost { get; set; } = 100f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c634}Treatment: cost multiplier for soldiers and prisoners", 0.1f, 2f, "0.00", Order = 71, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public float DiseaseTreatmentTroopMultiplier { get; set; } = 0.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c635}Treatment: price multiplier of prevention", 0.5f, 3f, "0.00", Order = 72, RequireRestart = false,
            HintText = "{=AIL_e7b1c6f5}Multiplies the price of the hospital's prevention and of the healing herbs it sells.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public float DiseasePreventionHerbCostMultiplier { get; set; } = 1f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c636}Treatment: share of the surgeon's Medicine that reaches the soldiers", 0.5f, 1f, "0.00", Order = 73, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public float DiseaseMedicineSkillForTroopsMultiplier { get; set; } = 0.75f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c637}Speed at which an untreated disease worsens", 0.25f, 3f, "0.00", Order = 74, RequireRestart = false,
            HintText = "{=AIL_e7b1c638}1 = a mild disease takes about three months to reach its worst and a plague under three weeks. Treatment, or a surgeon good enough for that disease, turns it back.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public float DiseaseProgressionMultiplier { get; set; } = 1f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c639}Speed of recovery under treatment", 0.25f, 3f, "0.00", Order = 75, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public float DiseaseRecoveryMultiplier { get; set; } = 1f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c63b}Share of its remaining days an outbreak passes on when it jumps to another place", 0.25f, 1f, "0.00", Order = 76, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public float DiseaseSpreadInheritFactor { get; set; } = 0.75f;

        // Lords who ride off to be treated
        [SettingPropertyBool("{=AIL_e7b1c6e0}AI parties ride to be treated", Order = 80, RequireRestart = false,
            HintText = "{=AIL_e7b1c6e1}A lord whose party is sick enough rides to the nearest friendly town or castle within reach, pays its physicians, rests there and then goes back to his own affairs. Never a party in an army, in a battle or a siege, or doing something you asked of it.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b5}5. AI parties and garrisons", GroupOrder = 5)]
        public bool DiseaseAiSeekTreatment { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c6e2}AI treatment: urgency needed to set out (3 = any illness, 10 = only the gravest)", 3f, 10f, "0.0", Order = 81, RequireRestart = false,
            HintText = "{=AIL_e7b1c6e3}Urgency grows with how far the illness has gone and how severe it is: 3 at the first symptoms, 10 for a plague halfway through its course. At 5 a lord sets out for a mild illness once it reaches a quarter of its course, and at once for a deadly one.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b5}5. AI parties and garrisons", GroupOrder = 5)]
        public float DiseaseAiTreatmentMinUrgency { get; set; } = 5f;

        [SettingPropertyInteger("{=AIL_e7b1c6e4}AI treatment: share of sick soldiers that counts (%)", 1, 100, Order = 82, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b5}5. AI parties and garrisons", GroupOrder = 5)]
        public int DiseaseAiTreatmentMinTroopShare { get; set; } = 10;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c6e5}AI treatment: farthest they will ride (days)", 0.5f, 10f, "0.0", Order = 83, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b5}5. AI parties and garrisons", GroupOrder = 5)]
        public float DiseaseAiTreatmentMaxDays { get; set; } = 3f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c6e6}AI treatment: days they rest where they are treated", 0.5f, 7f, "0.0", Order = 84, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b5}5. AI parties and garrisons", GroupOrder = 5)]
        public float DiseaseAiTreatmentStayDays { get; set; } = 2f;

        // Herbs, garrisons, tooltips, deaths, debt
        [SettingPropertyBool("{=AIL_e7b1c6e7}Healing herbs", Order = 85, RequireRestart = false,
            HintText = "{=AIL_e7b1c6e8}Five qualities of herbs sold in the hospitals, better ones in richer towns and the best only in a capital. They are goods in your inventory: use them anywhere (Alt+H on the map, or in the hospital) to treat your sick or to make your party catch less for some days.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public bool EnableHealingHerbs { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c6e9}Herbs: treat your sick with them automatically", Order = 86, RequireRestart = false,
            HintText = "{=AIL_e7b1c6ea}Each day, your sick heroes and soldiers with no treatment get the herbs you carry: the best ones for the grave cases, the plainest for the rest. Off: only when you use them yourself.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public bool HerbsAutoUse { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c6eb}Garrisons and militias fall ill", Order = 87, RequireRestart = false,
            HintText = "{=AIL_e7b1c6ec}Where there is an outbreak, the garrison and the militia may catch it. It spreads among them, the physicians of the place see to them every few days, and a serious disease left to run kills some each day. It shows in the settlement's tooltip and in the militia's daily change.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b5}5. AI parties and garrisons", GroupOrder = 5)]
        public bool DiseaseGarrisonsFallIll { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c6ed}Garrisons: days between the physicians' rounds", 1, 30, Order = 88, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b5}5. AI parties and garrisons", GroupOrder = 5)]
        public int DiseaseForceCareEveryDays { get; set; } = 4;

        [SettingPropertyBool("{=AIL_e7b1c6ee}Disease in the map tooltips", Order = 89, RequireRestart = false,
            HintText = "{=AIL_e7b1c6ef}Hovering a party on the map lists who is ill in it: its leader, its soldiers and its prisoners. Hold the key for more information to see how far each illness has gone. A settlement shows its outbreak and its sick garrison and militia.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public bool DiseaseMapTooltips { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c6f0}Deaths name the disease", Order = 90, RequireRestart = false,
            HintText = "{=AIL_e7b1c6f1}When a hero dies of a disease, the notice, the encyclopedia and the obituary say which, instead of old age.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public bool DiseaseDeathTexts { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c6f2}Treatment on the clan's account", Order = 91, RequireRestart = false,
            HintText = "{=AIL_e7b1c6f3}A member of your clan treated away from you who cannot pay in full leaves the rest owed: it is charged with your clan's next daily expenses, as a line of its own. In the hospital you too can have your sick treated on account when you are short.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public bool DiseaseTreatmentDebt { get; set; } = true;

        // ------------------------------------------------------------ Initiative and errands
        [SettingPropertyBool("{=AIL_875d42da}NPCs tell each other things", Order = 0, RequireRestart = false,
            HintText = "{=AIL_39a88b5b}What you say or do in front of an NPC can reach their lord, their family or their friends, delayed according to distance. What arrives is their version, not a fact.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public bool EnableInfoExchange { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0a072878}Errands each NPC remembers", 1, 40, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public int MaxHeardPerHero { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_e7b1c515}Days they remember what they were told (0 = always)", 0, 365, Order = 2, RequireRestart = false,
            HintText = "{=AIL_e7b1c516}What reached a character through someone else fades after this many days. What they only overheard has its own, shorter setting in D. Scenes.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public int HeardLifespanDays { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_e7b1c517}Hands a message can pass through", 0, 4, Order = 2, RequireRestart = false,
            HintText = "{=AIL_e7b1c518}Whoever receives word may pass it on to their own people: a public message to a couple of them, a private one sometimes, a secret only if they are the kind to break a confidence. Each one knows who told them and where it began. 0 = it stops with the first person.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public int ErrandMaxHops { get; set; } = 2;

        [SettingPropertyBool("{=AIL_e7b1c519}Messages can be answered", Order = 2, RequireRestart = false,
            HintText = "{=AIL_e7b1c51a}When a character sends word and needs a reply, the one who receives it sends their answer back by the same road, and the first can tell you when you next speak. One short AI request per answer.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public bool ErrandAnswers { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c51b}Answers per day, at most", 1, 30, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public int ErrandAnswersPerDay { get; set; } = 4;

        [SettingPropertyBool("{=AIL_a815af23}NPCs approach you on the map", Order = 2, RequireRestart = false,
            HintText = "{=AIL_cc079ac8}A noble who already knows you and passes nearby may ride up to you to talk, if they have a reason: an open task, something they heard about you, affection or resentment. They ask whether you will receive them.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public bool EnableMapInitiative { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3225c875}Hourly chance that someone nearby approaches (%)", 0, 100, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public int MapInitiativeChancePercent { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_f42f2a6a}Minimum days between approaches", 0, 60, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public int MapInitiativeMinDaysBetween { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_fcce1e3f}Minimum days between approaches by the same NPC", 1, 120, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public int MapInitiativeDaysPerNpc { get; set; } = 7;

        // ------------------------------------------------------------ World and diplomacy
        [SettingPropertyBool("{=AIL_8a4cc812}AI world events", Order = 0, RequireRestart = false,
            HintText = "{=AIL_b057228e}From time to time the AI writes new events based on the real state of the world (politics, war, economy, society, mysteries). They appear in News and NPCs know about them.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool EnableWorldEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_c268feb6}Reactions to what really happens", Order = 1, RequireRestart = false,
            HintText = "{=AIL_bd7a44d0}When something big happens in the game (a war, a peace, a captured town, the death or capture of someone important, a big rumor from your chats), the AI immediately writes how the world takes it.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool ReactiveEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_77793b62}Hospital in the towns", Order = 1, RequireRestart = true,
            HintText = "{=AIL_f683eca5}In each town's menu, \"Visit the hospital\": they tell you about the diseases going around, treat you, your companions, your wounded or sick troops and your prisoners (for a fee; cheaper in prosperous towns), and sell prevention so your men fall ill less for a few days.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases/{=AIL_e7b1c7b4}4. Treatment and healing", GroupOrder = 4)]
        public bool EnableHospital { get; set; } = true;

        [SettingPropertyInteger("{=AIL_29e73294}Maximum effect of an event on a market's prices (%)", 0, 80, Order = 1, RequireRestart = false,
            HintText = "{=AIL_2fd476bb}An event can raise or lower the prices of an entire town or village market while it lasts (shortage, panic, abundance). 0 = events don't touch prices.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int EffectMaxPricePercent { get; set; } = 35;

        [SettingPropertyBool("{=AIL_94c1e5dd}Unique characters created by the AI", Order = 1, RequireRestart = false,
            HintText = "{=AIL_ce8ae4a7}From time to time someone new, invented by the AI for this saved game, arrives in a nearby town (a fugitive physician, a mercenary captain, a preacher…), with name, trade, personality and motive. They live like a wanderer: you can look for them and take them into your service. One request per character.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool EnableUniqueCharacters { get; set; } = true;

        [SettingPropertyInteger("{=AIL_cf0af550}Days between unique characters", 3, 120, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int UniqueCharacterDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_160a2e2f}Maximum living unique characters", 1, 60, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int UniqueCharacterMax { get; set; } = 15;

        [SettingPropertyBool("{=AIL_98b07be9}Chronicle when someone you knew dies", Order = 1, RequireRestart = false,
            HintText = "{=AIL_d49df131}When a character you have talked to several times dies, the AI writes their chronicle (who they were, what there was between you, how it ended). It appears on screen and stays in News. One request per chronicle.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool EnableChronicles { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e3a1d50c}Chronicle: minimum chats with the deceased", 1, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int ChronicleMinTalks { get; set; } = 3;

        [SettingPropertyBool("{=AIL_d6f2ec0b}Training in the arena", Order = 1, RequireRestart = true,
            HintText = "{=AIL_b3ac06a3}In each town's arena, \"Train your troops\": you pay, 8 hours pass and your healthy soldiers gain experience (more for novices). Some may get injured. Each arena takes a few days to admit you again.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool EnableArenaTraining { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c6d9}AI lords train their troops in arenas", Order = 1, RequireRestart = false,
            HintText = "{=AIL_e7b1c6da}A lord resting in a town whose party is mostly low-tier troops pays the arena from his own gold and his healthy soldiers gain experience, with the same price, experience and injury chance as your own arena training, and the same wait before training again. Not your clan's parties. Needs “Training in the arena” on.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool AiLordsTrainTroops { get; set; } = true;

        [SettingPropertyInteger("{=AIL_11a9a28f}Arena: base experience per soldier", 20, 2000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int ArenaTrainingXp { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_fd5decd8}Arena: cost (% of the normal price)", 10, 500, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int ArenaTrainingCostPercent { get; set; } = 100;

        [SettingPropertyInteger("{=AIL_d77a8668}Arena: injury chance per soldier (%)", 0, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int ArenaTrainingInjuryPercent { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_fde1c2b2}Arena: hours until you can repeat in the same one", 0, 720, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public int ArenaTrainingCooldownHours { get; set; } = 72;

        [SettingPropertyBool("{=AIL_4d649e87}Secrets born from history", Order = 1, RequireRestart = false,
            HintText = "{=AIL_9b8bd5de}The AI invents the secrets from the real people of each kingdom (their houses, weddings, debts, grudges) and from the events as they happen. Each one concerns specific people and is known by a few, who only let it out with trust. Uses one request per kingdom, spread one per game day at the start of the saved game.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool StorySecrets { get; set; } = true;

        [SettingPropertyInteger("{=AIL_2504fc22}Origin secrets per kingdom", 1, 10, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int StorySecretsPerKingdom { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_da62d72b}Maximum living secrets in the saved game", 10, 400, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int StorySecretsMax { get; set; } = 120;

        [SettingPropertyInteger("{=AIL_df85af91}Secrets a character has in mind when talking", 1, 10, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int StorySecretsInPrompt { get; set; } = 4;

        [SettingPropertyBool("{=AIL_e3c37efa}Plus factory secrets (world_data\\secrets.json)", Order = 1, RequireRestart = false,
            HintText = "{=AIL_5a6d8aee}Off by default. On: the generic secrets written in that file are also handed out at random (you can put your own there).")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool PresetSecrets { get; set; } = false;

        [SettingPropertyBool("{=AIL_9e2fda6f}Events affect clans", Order = 1, RequireRestart = false,
            HintText = "{=AIL_e9f870fb}An event can stain or exalt the name of a specific clan (yours too): its income drops or rises by a percentage for a few days. It shows in the clan's income breakdown and in News.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool EnableClanEffects { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d02abd4c}Maximum effect on a clan's income (%)", 5, 60, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int ClanEffectMaxPercent { get; set; } = 30;

        [SettingPropertyBool("{=AIL_96226daa}What you do and say gets people talking", Order = 1, RequireRestart = false,
            HintText = "{=AIL_750e804a}An aggression by you in the street, a death, an arrest, or a weighty rumor you set rolling in a chat, becomes a world event with its news (and with effects on the place if there was blood). It only tells what the witnesses could have known. It is one request to the AI per event.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool IncidentEvents { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d53be13f}Minimum importance to provoke a reaction", 1, 4, Order = 1, RequireRestart = false,
            HintText = "{=AIL_303700aa}1 = any local event, 2 = court matters, 3 = what shakes a kingdom, 4 = only what shakes the world.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int ReactiveMinImportance { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_586abbc0}Minimum game hours between reactions", 1, 240, Order = 1, RequireRestart = false,
            HintText = "{=AIL_6a2934ef}If several things happen in a row, they are combined into a single reaction.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int ReactiveMinHoursBetween { get; set; } = 8;

        [SettingPropertyBool("{=AIL_292485a4}Background events (when nothing has happened)", Order = 1, RequireRestart = false,
            HintText = "{=AIL_613c43b2}Besides reactions, from time to time the AI writes what is brewing on its own: intrigues, shortages, omens, unrest.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool BackgroundEvents { get; set; } = true;

        [SettingPropertyInteger("{=AIL_18b82bcb}Background events: every how many days (approx.)", 1, 100, Order = 1, RequireRestart = false,
            HintText = "{=AIL_d43bd671}It doesn't fall like clockwork: the actual interval varies between 60 % and 140 % of this value.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int WorldEventsIntervalDays { get; set; } = 7;

        [SettingPropertyInteger("{=AIL_c0d9e15c}Events per batch", 1, 4, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int WorldEventsPerGeneration { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_072e561a}Minimum length of an event (characters)", 80, 800, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int WorldEventMinLength { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_5b79eb8c}Maximum length of an event (characters)", 200, 1400, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int WorldEventMaxLength { get; set; } = 600;

        [SettingPropertyInteger("{=AIL_dd736c20}Days an event stays \"up in the air\"", 5, 120, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int WorldEventLifespanDays { get; set; } = 30;

        [SettingPropertyInteger("{=AIL_b32fb45c}Notify on screen from importance", 1, 5, Order = 6, RequireRestart = false,
            HintText = "{=AIL_580cc0e5}1 = all, 4 = only those that shake the world, 5 = never.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int WorldEventNotifyImportance { get; set; } = 2;

        [SettingPropertyBool("{=AIL_c69c977f}Economic effects of events", Order = 7, RequireRestart = false,
            HintText = "{=AIL_44600a27}An event can change, each day for a while, the prosperity, food, security and loyalty of a real settlement.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool EnableEconomicEffects { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d37eb730}Maximum effect: prosperity per day", 0, 200, Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int EffectMaxProsperityPerDay { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_0696ca62}Maximum effect: food per day", 0, 100, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int EffectMaxFoodPerDay { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_b8d972a1}Maximum effect: security per day", 0, 20, Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int EffectMaxSecurityPerDay { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_b700fc3c}Maximum effect: loyalty per day", 0, 20, Order = 11, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int EffectMaxLoyaltyPerDay { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_fc7ff1ee}Maximum effect: duration (days)", 1, 120, Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int EffectMaxDays { get; set; } = 20;

        [SettingPropertyBool("{=AIL_d86408df}AI diplomacy of kings (replaces the game's own)", Order = 16, RequireRestart = false,
            HintText = "{=AIL_6c0f99f2}The game's clans stop proposing wars, peace, alliances and trade on their own. Kings decide them through public declarations. Requires world events to be enabled.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool EnableDiplomacy { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c512}Only rulers decide wars and pacts between AI kingdoms", Order = 3, RequireRestart = false,
            HintText = "{=AIL_e7b1c513}Stops the game's own votes, call-to-war agreements and other mods from declaring war, making peace, sealing an alliance or a trade agreement between two AI kingdoms: only their rulers' declarations do. An alliance that runs out is no longer renewed by a vote; it ends, and the rulers may propose it again. Wars that come from what happens in the world (an attack, a rebellion, a new kingdom) and anything involving your own kingdom are not touched.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool BlockForeignWarPeace { get; set; } = true;

        [SettingPropertyInteger("{=AIL_c3e1a901}Kings who may speak out per day", 1, 8, Order = 17, RequireRestart = false,
            HintText = "{=AIL_c3e1a902}When something big happens, several kings can speak about it. They take turns, each one knowing what the others said. Every declaration is one AI request.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int MaxStatementsPerDay { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_c3e1a903}Game hours between one king's declaration and the next", 1, 24, Order = 18, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int HoursBetweenStatements { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_4ae4ed78}Minimum days between declarations by the same king", 1, 60, Order = 19, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int MinDaysBetweenStatements { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_e7b1c758}Minimum days between declarations in the whole world (0 = no limit)", 0, 30, Order = 21, RequireRestart = false,
            HintText = "{=AIL_e7b1c759}After any AI king speaks, no other AI king does until this many days have passed, however much the AI wants them to. It also means one declaration at a time. With 0 only the limits per day and per king above apply. Your own declarations are never held back. Answers to a pending proposal are not held back by this, so that proposals do not run out unanswered. Recommended: 1 or 2; 0 turns it off.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int MinDaysBetweenAnyStatements { get; set; } = 1;

        [SettingPropertyInteger("{=AIL_abd77d03}Days during which an event can provoke declarations", 1, 60, Order = 22, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int EngagementDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_0e7aa239}Daily chance of answering a proposal (%)", 5, 100, Order = 23, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int ResponsePressurePercent { get; set; } = 40;

        [SettingPropertyInteger("{=AIL_46407e9d}Days an unanswered proposal lasts", 5, 120, Order = 24, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int ProposalLifetimeDays { get; set; } = 30;

        [SettingPropertyInteger("{=AIL_ab45cede}Minimum length of a declaration", 80, 800, Order = 25, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int StatementMinLength { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_9ba9f600}Maximum length of a declaration", 200, 1600, Order = 26, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int StatementMaxLength { get; set; } = 700;

        [SettingPropertyInteger("{=AIL_0d849eb1}Maximum shift in stance between crowns per declaration", 1, 50, Order = 27, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int MaxStanceChange { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_b57ad932}Maximum daily tribute", 10, 20000, Order = 28, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int MaxDailyTribute { get; set; } = 2000;

        [SettingPropertyInteger("{=AIL_e9d88202}Maximum reparations", 100, 2000000, Order = 29, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int MaxReparations { get; set; } = 100000;

        [SettingPropertyInteger("{=AIL_ffcd4d39}Crown tax: minimum (%)", -50, 0, Order = 30, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int KingdomTaxMinPercent { get; set; } = -10;

        [SettingPropertyInteger("{=AIL_9ff27d57}Crown tax: maximum (%)", 0, 100, Order = 31, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int KingdomTaxMaxPercent { get; set; } = 30;

        [SettingPropertyBool("{=AIL_cddce6e0}Kings can declare war on you if you rule", Order = 32, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool AllowAiWarOnPlayer { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c73d}Check what the chronicler and the kings write against the world", Order = 33, RequireRestart = false,
            HintText = "{=AIL_e7b1c73e}Before an event or a king's declaration is published, the mod checks it against the game: a lord placed in a kingdom he does not belong to, a mercenary called a vassal, a war or an alliance that does not exist, a king claiming another crown's lord as his own. What contradicts the world is thrown away and the reason goes to the log. Reactions to real events may only involve the kingdoms those events touch. Off: everything is published as written.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool FactCheckEvents { get; set; } = true;

        // The house rules. They bind AI kings only, never the kingdom the player rules.
        [SettingPropertyBool("{=AIL_e7b1c73f}New campaigns start with every kingdom at peace", Order = 0, RequireRestart = false,
            HintText = "{=AIL_e7b1c740}Only when a new campaign is created, once, after the game has set up its starting wars: every war between two kingdoms ends. Never on a campaign that already exists and never when loading. Set it before starting the campaign. If a truce length is set below, it counts from that first day. Recommended: off for the game's usual start, on for a calmer first year.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public bool PeaceAtCampaignStart { get; set; } = false;

        [SettingPropertyInteger("{=AIL_e7b1c741}House rule: most kingdoms one kingdom may fight at once (0 = no limit)", 0, 10, Order = 1, RequireRestart = false,
            HintText = "{=AIL_e7b1c742}An AI king may not declare war if his kingdom, or the kingdom he aims at, is already at war with this many: no piling on. It binds AI kings only. You are never stopped from declaring war yourself, but the rule protects you too: no AI king will declare war on you once you are at the limit. Wars that start by themselves (a rebellion, a new kingdom) are not stopped. Recommended: 2 or 3 to prevent pile-ons; 0 leaves it off.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public int MaxWarsPerKingdom { get; set; } = 0;

        [SettingPropertyInteger("{=AIL_e7b1c743}House rule: days of truce after a peace (0 = none)", 0, 336, Order = 2, RequireRestart = false,
            HintText = "{=AIL_e7b1c744}After two kingdoms make peace, no AI king of either may declare war on the other until this many days have passed. A year of Calradia is 84 days. It binds AI kings only, in their declarations and when you talk to them: you may break a truce yourself. Recommended: 21 to 84 (84 is one year); 0 turns it off.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public int MinTruceDays { get; set; } = 42;

        [SettingPropertyInteger("{=AIL_e7b1c750}Forced peace after this many days of a stalemate war (0 = off)", 0, 840, Order = 3, RequireRestart = false,
            HintText = "{=AIL_e7b1c751}When two AI kingdoms have been at war this long and no town or castle has changed hands between them in all that time, the war ends by itself, with a news item: nobody can keep it up. One such peace a day at most. A year of Calradia is 84 days. Never applied to a kingdom you rule. Kings are told how close each of their wars is to this. Afterwards neither of the two may declare war on the other for 84 days, or for the truce set above if that is longer. Recommended: 126 to 252 (a year and a half to three years); 0 turns it off.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public int ForcedPeaceStalemateDays { get; set; } = 168;

        [SettingPropertyInteger("{=AIL_e7b1c745}House rule: most alliances per kingdom (0 = no limit)", 0, 10, Order = 4, RequireRestart = false,
            HintText = "{=AIL_e7b1c746}An AI king will not propose or accept an alliance if his kingdom or the other one already has this many. It binds AI kings only: what you propose or accept as ruler is never blocked, but an AI king at his limit will not ally with you. Recommended: 1 or 2; 0 leaves it off.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public int MaxAlliancesPerKingdom { get; set; } = 0;

        [SettingPropertyBool("{=AIL_e7b1c752}A new alliance replaces the old one", Order = 5, RequireRestart = false,
            HintText = "{=AIL_e7b1c753}Only matters with a limit on alliances. Off: an AI king at his limit cannot make a new alliance. On: he can, and when the new alliance is sealed the oldest one of each AI kingdom that was at its limit is then ended, with a news item naming the ally left behind. Your own alliances are never ended this way. Recommended: off.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public bool AllianceReplacesOldest { get; set; } = false;

        [SettingPropertyInteger("{=AIL_e7b1c747}House rule: most trade agreements per kingdom (0 = no limit)", 0, 10, Order = 6, RequireRestart = false,
            HintText = "{=AIL_e7b1c748}An AI king will not propose or accept a trade agreement if his kingdom or the other one already has this many. It binds AI kings only, the same way as the limit on alliances. Recommended: 2 to 4; 0 leaves it off.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public int MaxTradePactsPerKingdom { get; set; } = 0;

        [SettingPropertyBool("{=AIL_e7b1c754}AI kings may offer alliances and trade agreements to a kingdom you rule", Order = 7, RequireRestart = false,
            HintText = "{=AIL_e7b1c755}Off: no AI king proposes an alliance or a trade agreement to the kingdom you rule; you can still propose them yourself and they answer. Peace offers and demands are not affected. While you are only a vassal nothing changes. Recommended: on.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public bool AiOffersPactsToPlayer { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c756}Days before a refused proposal can be made again (0 = no wait)", 0, 336, Order = 8, RequireRestart = false,
            HintText = "{=AIL_e7b1c757}After a proposal of peace, alliance or trade between two kingdoms is refused, or runs out unanswered, no AI king may make the same kind of proposal between those two until this many days have passed. It binds AI kings only: you may insist. A year of Calradia is 84 days. Recommended: 21 to 84; 0 turns it off.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c74f}Campaign rules", GroupOrder = 10)]
        public int RefusedProposalCooldownDays { get; set; } = 42;

        [SettingPropertyBool("{=AIL_b7d2f023}An expelled clan is banished", Order = 60, RequireRestart = false,
            HintText = "{=AIL_b7d2f024}A clan cast out of a kingdom cannot swear to it, defect to it or serve it as mercenaries until its ruler pardons it. Applies to you too.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool BanishedClansCannotReturn { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7d2f025}Banishment ends when the kingdom changes ruler", Order = 61, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool BanishmentEndsWithRuler { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7d2f026}Keep landless clans you know alive", Order = 62, RequireRestart = false,
            HintText = "{=AIL_b7d2f027}The game dissolves a clan with no kingdom and no land after a while, killing its members. With this on, it spares banished clans and clans with people you have talked to.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool KeepKnownClansAlive { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c6cf}Landless clans patrol", Order = 63, RequireRestart = false,
            HintText = "{=AIL_e7b1c6d0}Parties of clans with no fief that are standing idle (minor factions, dispossessed nobles, clans the mod keeps alive) patrol for a few days around a settlement of their kingdom or employer, or the nearest one that is not hostile if they serve nobody, then rest a day so they can resupply. It uses the mod's own map orders. Hungry or depleted parties are left alone, and anything you agree with them comes first.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool LandlessClansPatrol { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c6d1}Landless clans: days per patrol", 1, 7, Order = 64, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int LandlessPatrolDays { get; set; } = 2;

        [SettingPropertyBool("{=AIL_76e631ee}Your king can expel you or confiscate a fief from you", Order = 33, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool AllowAiExpelPlayer { get; set; } = false;

        [SettingPropertyBool("{=AIL_015c810d}Laws proclaimed by kings", Order = 34, RequireRestart = false,
            HintText = "{=AIL_ef8b7c72}In a declaration, a ruler can create a law of their own and repeal it. Its effects (on settlements, clans or parties) go through the game's own calculations and show in the tooltips with the law's name.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool EnableKingdomLaws { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a12df7a9}Laws in force per kingdom", 1, 10, Order = 35, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int MaxLawsPerKingdom { get; set; } = 3;

        [SettingPropertyBool("{=AIL_e7b1c5da}Rulers can adopt the game's own policies", Order = 0, RequireRestart = false,
            HintText = "{=AIL_e7b1c5db}In a declaration, a ruler can adopt or abolish one of the kingdom policies of the game. It takes effect at once, with no vote, and shows in Kingdom › Policies.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public bool EnableNativePolicies { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c5dc}Show created laws in Kingdom › Policies", Order = 1, RequireRestart = false,
            HintText = "{=AIL_e7b1c5dd}The laws created by the ruler of your kingdom are listed among its active policies, with their text and effects. They cannot be voted on there.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public bool LawsInPoliciesScreen { get; set; } = true;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5b3}Loyalty per day: min", -10f, 0f, "0.00", Order = 10, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawLoyaltyMin { get; set; } = -2f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5b4}Loyalty per day: max", 0f, 10f, "0.00", Order = 11, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawLoyaltyMax { get; set; } = 2f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5b5}Prosperity per day: min", -10f, 0f, "0.00", Order = 12, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawProsperityMin { get; set; } = -1.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5b6}Prosperity per day: max", 0f, 10f, "0.00", Order = 13, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawProsperityMax { get; set; } = 1.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5b7}Security per day: min", -10f, 0f, "0.00", Order = 14, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawSecurityMin { get; set; } = -1.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5b8}Security per day: max", 0f, 10f, "0.00", Order = 15, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawSecurityMax { get; set; } = 1.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5b9}Food per day: min", -10f, 0f, "0.00", Order = 16, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawFoodMin { get; set; } = -3f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5ba}Food per day: max", 0f, 10f, "0.00", Order = 17, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawFoodMax { get; set; } = 3f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5bb}Militia per day: min", -10f, 0f, "0.00", Order = 18, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawMilitiaMin { get; set; } = -1.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5bc}Militia per day: max", 0f, 10f, "0.00", Order = 19, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawMilitiaMax { get; set; } = 1.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5bd}Village hearths per day: min", -5f, 0f, "0.00", Order = 20, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawHearthMin { get; set; } = -0.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5be}Village hearths per day: max", 0f, 5f, "0.00", Order = 21, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawHearthMax { get; set; } = 0.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5bf}Workshop production (%): min", -50f, 0f, "0", Order = 22, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawWorkshopMin { get; set; } = -15f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c0}Workshop production (%): max", 0f, 50f, "0", Order = 23, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawWorkshopMax { get; set; } = 15f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c1}Veteran militia chance (%): min", -50f, 0f, "0", Order = 24, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawVeteransMin { get; set; } = -20f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c2}Veteran militia chance (%): max", 0f, 50f, "0", Order = 25, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawVeteransMax { get; set; } = 20f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c3}Construction speed (%): min", -50f, 0f, "0", Order = 26, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawConstructionMin { get; set; } = -25f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c4}Construction speed (%): max", 0f, 50f, "0", Order = 27, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawConstructionMax { get; set; } = 25f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c5}Recruit replenishment (%): min", -50f, 0f, "0", Order = 28, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawRecruitmentMin { get; set; } = -25f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c6}Recruit replenishment (%): max", 0f, 50f, "0", Order = 29, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawRecruitmentMax { get; set; } = 25f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c7}Tax income (%): min", -50f, 0f, "0", Order = 30, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawTaxMin { get; set; } = -15f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c8}Tax income (%): max", 0f, 50f, "0", Order = 31, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawTaxMax { get; set; } = 15f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5c9}Garrison wages (%): min", -50f, 0f, "0", Order = 32, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawGarrisonWagesMin { get; set; } = -15f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5ca}Garrison wages (%): max", 0f, 50f, "0", Order = 33, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawGarrisonWagesMax { get; set; } = 15f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5cb}Clan influence per day: min", -10f, 0f, "0.00", Order = 34, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawInfluenceMin { get; set; } = -1.5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5cc}Clan influence per day: max", 0f, 10f, "0.00", Order = 35, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawInfluenceMax { get; set; } = 2f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5cd}Clan income (%): min", -50f, 0f, "0", Order = 36, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawClanIncomeMin { get; set; } = -10f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5ce}Clan income (%): max", 0f, 50f, "0", Order = 37, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawClanIncomeMax { get; set; } = 10f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5cf}Party size: min", -100f, 0f, "0", Order = 38, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawPartySizeMin { get; set; } = -20f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d0}Party size: max", 0f, 150f, "0", Order = 39, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawPartySizeMax { get; set; } = 40f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d1}Troop wages (%): min", -50f, 0f, "0", Order = 40, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawWagesMin { get; set; } = -15f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d2}Troop wages (%): max", 0f, 50f, "0", Order = 41, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawWagesMax { get; set; } = 15f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d3}Party morale: min", -30f, 0f, "0.0", Order = 42, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawMoraleMin { get; set; } = -5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d4}Party morale: max", 0f, 30f, "0.0", Order = 43, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawMoraleMax { get; set; } = 5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d5}Party speed (%): min", -25f, 0f, "0", Order = 44, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawSpeedMin { get; set; } = -5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d6}Party speed (%): max", 0f, 25f, "0", Order = 45, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawSpeedMax { get; set; } = 5f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d7}Army influence cost (%): min", -50f, 0f, "0", Order = 46, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawArmyCostMin { get; set; } = -30f;

        [SettingPropertyFloatingInteger("{=AIL_e7b1c5d8}Army influence cost (%): max", 0f, 50f, "0", Order = 47, RequireRestart = false, HintText = "{=AIL_e7b1c5de}Hard limit for the laws rulers create: a value outside this range is cut down to it, also for laws already in force. The AI is told the range.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy/{=AIL_e7b1c5d9}Laws", GroupOrder = 10)]
        public float LawArmyCostMax { get; set; } = 30f;

        [SettingPropertyBool("{=AIL_5abeb44e}Clan rebellions", Order = 36, RequireRestart = false,
            HintText = "{=AIL_cbd20bc6}An event can make a vassal clan rise against its crown (never yours nor the ruling clan), if it truly gets along badly with its king.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool AllowClanRebellions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_db9bfa6a}Maximum relation with their king to rebel", -100, 20, Order = 37, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int RebellionMaxRelation { get; set; } = -15;

        [SettingPropertyInteger("{=AIL_84f97df8}War weariness: recovery per day at peace (x10)", 0, 100, Order = 40, RequireRestart = false,
            HintText = "{=AIL_e7b1c799}What a kingdom at peace with every other kingdom recovers each day, in tenths: 20 is 2 points a day. Nothing is recovered while any war is open. Recommended: 10 to 30.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int FatigueRecoveryPerDayX10 { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_7948ed38}War weariness: per day and open war (x10)", 0, 100, Order = 41, RequireRestart = false,
            HintText = "{=AIL_e7b1c75a}The base amount. Each war weighs more the longer it has lasted (half as much again per year, up to three times), and every war beyond the first makes all of them a quarter heavier: a kingdom fighting many at once wears out fast. Recommended: 2 to 5 (0.2 to 0.5 points a day).")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int FatiguePerWarDayX10 { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_b7d8f762}War weariness: per lost battle (x10)", 0, 200, Order = 42, RequireRestart = false,
            HintText = "{=AIL_e7b1c79a}Extra weariness for the side that loses a battle against a kingdom it is at war with, on top of the men it lost. A defeat with 250 men lost or more counts in full, a smaller one in proportion. Battles against bandits, caravans, villagers or anyone the kingdom is not at war with never count. In tenths: 20 is 2 points. Recommended: 10 to 30.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int FatiguePerBattleLostX10 { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_e7b1c797}War weariness: per 100 men lost in battle (x10)", 0, 100, Order = 43, RequireRestart = false,
            HintText = "{=AIL_e7b1c798}Every man a kingdom loses (killed, wounded or routed) in a battle against a kingdom it is at war with wears it down, whether it wins or loses. In tenths per 100 men: 15 means 1.5 points for every 100 men lost. Recommended: 10 to 25.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int FatiguePerHundredCasualtiesX10 { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_7c23f3b8}War weariness: per dead noble", 0, 50, Order = 44, RequireRestart = false,
            HintText = "{=AIL_47dd15e0}Only when the noble is killed by a kingdom at war with his own: in battle or executed. Deaths from age, illness or childbirth do not count. A noble captured by that enemy counts as a third, given back when he goes free. Recommended: 2 to 5.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int FatiguePerLordKilled { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_0e578525}War weariness: per lost town or castle", 0, 50, Order = 45, RequireRestart = false,
            HintText = "{=AIL_e7b1c79b}Only when the town or castle is taken by force by a kingdom at war with its owner. One handed over as a term of peace, granted, or lost to a rebellion does not count. Recommended: 4 to 10.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int FatiguePerSettlementLost { get; set; } = 6;

        [SettingPropertyBool("{=AIL_e7b1c75b}War-weary kings speak up about their wars", Order = 46, RequireRestart = false,
            HintText = "{=AIL_e7b1c75c}A king whose realm is weary, or who is fighting several kingdoms at once, takes a turn to decide what to do about his wars even when nothing else calls for a declaration: the wearier and the more wars, the likelier. Without this a king only speaks when an event or a proposal calls him, and wars can go on for years with nobody asking for peace. One AI request per turn, within the limits on declarations above. Recommended: on.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool WearyKingsSeekPeace { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a4b23033}Minimum real seconds between world requests", 5, 600, Order = 50, RequireRestart = false,
            HintText = "{=AIL_2924e794}So as not to spend quota when you advance time quickly.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public int WorldAiMinSecondsBetweenCalls { get; set; } = 25;

        [SettingPropertyText("{=AIL_74ca054c}Provider for the world and diplomacy", Order = 51, RequireRestart = false,
            HintText = "{=AIL_e2132343}Name of a config.json provider. Empty = the normal chain.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public string ProviderWorld { get; set; } = "";

        [SettingPropertyButton("{=AIL_6a025b78}Generate events now", Content = "{=AIL_63621315}Generate", Order = 52, RequireRestart = false,
            HintText = "{=AIL_67d30a9a}With a saved game loaded: requests a batch of events right now (they appear when you return to the map).")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public Action ForceWorldEvents { get; set; } = () => Later(() => World.WorldSim.GenerateEvents(true));

        [SettingPropertyButton("{=AIL_4eb0fc2d}Make a declaration (if you rule)", Content = "{=AIL_c7acc8b1}Declare", Order = 53, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public Action PlayerDeclaration { get; set; } = () => Later(World.Diplomacy.PlayerStatement);

        // ------------------------------------------------------------ Keys
        [SettingPropertyText("{=AIL_e7b1c653}Talk to the group", Order = 0, RequireRestart = false,
            HintText = "{=AIL_e7b1c654}Write the shortcut as text: Alt+T, Ctrl+Shift+F5, F9. Modifiers: Alt, Ctrl, Shift. Keys: a letter, a digit, F1 to F12, Space, Tab, Insert, Home, PageUp, Numpad0... Empty turns the shortcut off; text that can't be read counts as the default. Menu texts elsewhere still name the default keys.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General/{=AIL_e7b1c652}Keys", GroupOrder = 1)]
        public string KeyGroupTalk { get; set; } = "Alt+T";

        [SettingPropertyText("{=AIL_e7b1c655}End the group talk", Order = 1, RequireRestart = false,
            HintText = "{=AIL_e7b1c654}Write the shortcut as text: Alt+T, Ctrl+Shift+F5, F9. Modifiers: Alt, Ctrl, Shift. Keys: a letter, a digit, F1 to F12, Space, Tab, Insert, Home, PageUp, Numpad0... Empty turns the shortcut off; text that can't be read counts as the default. Menu texts elsewhere still name the default keys.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General/{=AIL_e7b1c652}Keys", GroupOrder = 1)]
        public string KeyGroupEnd { get; set; } = "Alt+G";

        [SettingPropertyText("{=AIL_e7b1c656}Battle orders in your own words", Order = 2, RequireRestart = false,
            HintText = "{=AIL_e7b1c654}Write the shortcut as text: Alt+T, Ctrl+Shift+F5, F9. Modifiers: Alt, Ctrl, Shift. Keys: a letter, a digit, F1 to F12, Space, Tab, Insert, Home, PageUp, Numpad0... Empty turns the shortcut off; text that can't be read counts as the default. Menu texts elsewhere still name the default keys.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General/{=AIL_e7b1c652}Keys", GroupOrder = 1)]
        public string KeyBattleOrders { get; set; } = "Alt+B";

        [SettingPropertyText("{=AIL_e7b1c657}Fight: yield", Order = 3, RequireRestart = false,
            HintText = "{=AIL_e7b1c654}Write the shortcut as text: Alt+T, Ctrl+Shift+F5, F9. Modifiers: Alt, Ctrl, Shift. Keys: a letter, a digit, F1 to F12, Space, Tab, Insert, Home, PageUp, Numpad0... Empty turns the shortcut off; text that can't be read counts as the default. Menu texts elsewhere still name the default keys.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General/{=AIL_e7b1c652}Keys", GroupOrder = 1)]
        public string KeyYield { get; set; } = "Alt+Y";

        [SettingPropertyText("{=AIL_e7b1c658}Fight: ask for a truce", Order = 4, RequireRestart = false,
            HintText = "{=AIL_e7b1c654}Write the shortcut as text: Alt+T, Ctrl+Shift+F5, F9. Modifiers: Alt, Ctrl, Shift. Keys: a letter, a digit, F1 to F12, Space, Tab, Insert, Home, PageUp, Numpad0... Empty turns the shortcut off; text that can't be read counts as the default. Menu texts elsewhere still name the default keys.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General/{=AIL_e7b1c652}Keys", GroupOrder = 1)]
        public string KeyTruce { get; set; } = "Alt+P";

        [SettingPropertyText("{=AIL_e7b1c659}Fight: call ten of your men", Order = 5, RequireRestart = false,
            HintText = "{=AIL_e7b1c654}Write the shortcut as text: Alt+T, Ctrl+Shift+F5, F9. Modifiers: Alt, Ctrl, Shift. Keys: a letter, a digit, F1 to F12, Space, Tab, Insert, Home, PageUp, Numpad0... Empty turns the shortcut off; text that can't be read counts as the default. Menu texts elsewhere still name the default keys.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General/{=AIL_e7b1c652}Keys", GroupOrder = 1)]
        public string KeySummonTen { get; set; } = "Ctrl+X";

        [SettingPropertyText("{=AIL_e7b1c65a}Fight: call all the men that fit", Order = 6, RequireRestart = false,
            HintText = "{=AIL_e7b1c654}Write the shortcut as text: Alt+T, Ctrl+Shift+F5, F9. Modifiers: Alt, Ctrl, Shift. Keys: a letter, a digit, F1 to F12, Space, Tab, Insert, Home, PageUp, Numpad0... Empty turns the shortcut off; text that can't be read counts as the default. Menu texts elsewhere still name the default keys.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General/{=AIL_e7b1c652}Keys", GroupOrder = 1)]
        public string KeySummonAll { get; set; } = "Ctrl+Alt+X";

        [SettingPropertyText("{=AIL_e7b1c6f4}Use healing herbs (on the map)", Order = 7, RequireRestart = false,
            HintText = "{=AIL_e7b1c654}Write the shortcut as text: Alt+T, Ctrl+Shift+F5, F9. Modifiers: Alt, Ctrl, Shift. Keys: a letter, a digit, F1 to F12, Space, Tab, Insert, Home, PageUp, Numpad0... Empty turns the shortcut off; text that can't be read counts as the default. Menu texts elsewhere still name the default keys.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General/{=AIL_e7b1c652}Keys", GroupOrder = 1)]
        public string KeyHerbs { get; set; } = "Alt+H";

        // ------------------------------------------------------------ Editor
        [SettingPropertyButton("{=AIL_1cb10851}Open the AI Legacy editor", Content = "{=AIL_5a216c13}Open", Order = 0, RequireRestart = false,
            HintText = "{=AIL_ab8aaa3e}Opens the editor in your browser: world, secrets, NPCs (personality, memory, instructions), events and backups. Requires a saved game to be loaded.")]
        [SettingPropertyGroup("{=AIL_1d38423b}9. Editor", GroupOrder = 9)]
        public Action OpenEditor { get; set; } = () => Web.EditorServer.OpenInBrowser();

        [SettingPropertyInteger("{=AIL_2fc52b1a}Editor port", 1024, 65000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_1d38423b}9. Editor", GroupOrder = 9)]
        public int EditorPort { get; set; } = 17890;

        [SettingPropertyBool("{=AIL_c3a7f1e5}Background tasks on free providers only", Order = 10, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e6}Ambient talk, world events, summaries and other tasks you are not waiting for never use a paid provider while a free one exists. The paid one is kept in reserve for dialogue.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public bool BackgroundOnFreeProviders { get; set; } = true;

        [SettingPropertyInteger("{=AIL_c3a7f1e7}Daily spending cap (US cents, 0 = no cap)", 0, 5000, Order = 11, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e8}Once that amount is reached in a day (real day, not game day) paid providers stop being used until tomorrow. Only providers with a price set under 0. AI keys are counted.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public int DailySpendCapCents { get; set; } = 0;

        [SettingPropertyBool("{=AIL_e7b1c63c}Show the reply as it is written (streaming)", Order = 12, RequireRestart = false,
            HintText = "{=AIL_e7b1c63d}The character's line grows in the dialogue box while the AI writes it, instead of appearing all at once. In a group talk, the first line grows over its speaker's head. If a provider does not support it, the mod goes back to the whole reply with that provider on its own.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public bool StreamReplies { get; set; } = true;

        [SettingPropertyBool("{=AIL_e7b1c7c8}Flag a mistake of the AI", Order = 15, RequireRestart = false,
            HintText = "{=AIL_e7b1c7c9}Adds «Flag a mistake of the AI» to the conversation (and /error in the chat window). You say what the last reply got wrong, the AI reviews that exact reply and, when it was a real mistake, proposes one short lesson. Nothing is saved until you accept it. Saved lessons go into every conversation from then on. Each review is one extra AI request. /lecciones lists them and /borrarleccion deletes one.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public bool EnablePlayerLessons { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e7b1c7ca}Lessons kept at most", 5, 50, Order = 16, RequireRestart = false,
            HintText = "{=AIL_e7b1c7cb}When a new lesson goes past this number, the oldest one is dropped. Every lesson makes each conversation request a little longer. Recommended: 10 to 20.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public int PlayerLessonsMax { get; set; } = 20;

        [SettingPropertyBool("{=AIL_e7b1c7cc}Lessons shared by every campaign", Order = 17, RequireRestart = false,
            HintText = "{=AIL_e7b1c7cd}Off: each campaign keeps its own lessons, saved with it. On: one list for every campaign, in the file lessons.txt in the mod folder, which you can also edit by hand (one lesson per line). Switching does not move lessons from one list to the other. Recommended: off.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public bool PlayerLessonsGlobal { get; set; } = false;

        [SettingPropertyBool("{=AIL_e7b1c64c}Dialogue box shows only their words", Order = 13, RequireRestart = false,
            HintText = "{=AIL_e7b1c64d}The dialogue box shows only what the character says, without your own line above it and without their name in front. Turn it on if you use a voice mod that reads the dialogue box aloud, so it does not read your line too. Your line is still in the History panel.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public bool DialogOnlyTheirWords { get; set; } = false;

        [SettingPropertyBool("{=AIL_7ff37c4e}Save request/response in logs", Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public bool DebugLogBodies { get; set; } = false;

        [SettingPropertyBool("{=AIL_e7b1c6d7}Performance meter (to the log)", Order = 14, RequireRestart = false,
            HintText = "{=AIL_e7b1c6d8}While on, the mod times its own work on every frame and each of its Harmony patches, and writes a table sorted by total time to logs\\ailegacy.log every 60 seconds. The console command ailegacy.perf prints it at once. Nothing is shown on screen. Leave it off unless you are looking for what slows the game down.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public bool PerformanceMeter { get; set; } = false;

        // ------------------------------------------------------------ Import
        [SettingPropertyButton("{=AIL_31311890}Import memories from AIInfluence", Content = "{=AIL_faa80647}Import", Order = 0, RequireRestart = false,
            HintText = "{=AIL_d4db6271}With a saved game loaded: brings in from AIInfluence the memory, personality, memories and instructions of the NPCs of THIS campaign. It doesn't overwrite anyone who already has memory in AI Legacy.")]
        [SettingPropertyGroup("{=AIL_6cf8197b}8. Import", GroupOrder = 8)]
        public Action ImportAIInfluence { get; set; } = () => Import.AIInfluenceImporter.ImportCurrentCampaign();

        [SettingPropertyButton("{=AIL_376f0803}Undo the import", Content = "{=AIL_bdd858a5}Remove", Order = 1, RequireRestart = false,
            HintText = "{=AIL_20e63156}Removes from this campaign the memories that came from AIInfluence (for example, if you imported those of another saved game). The files are moved to a subfolder, not deleted.")]
        [SettingPropertyGroup("{=AIL_6cf8197b}8. Import", GroupOrder = 8)]
        public Action UndoImportAIInfluence { get; set; } = () => Import.AIInfluenceImporter.UndoImport();
    }

    /// <summary>Settings access that falls back to defaults if MCM isn't ready yet.</summary>
    internal static class S
    {
        private static readonly AILegacySettings Defaults = new AILegacySettings();

        public static AILegacySettings Get
        {
            get
            {
                try { return AILegacySettings.Instance ?? Defaults; }
                catch (Exception ex) { Log.Warn("MCM no disponible, uso valores por defecto: " + ex.Message); return Defaults; }
            }
        }
    }
}
