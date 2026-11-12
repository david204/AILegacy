using System;
using AILegacy.Core;
using AILegacy.Llm;
using MCM.Abstractions.Attributes;
using MCM.Abstractions.Attributes.v2;
using MCM.Abstractions.Base.Global;

namespace AILegacy.Settings
{
    /// <summary>Menú de ajustes en el juego (Opciones → Mod Options → AI Legacy).</summary>
    public class AILegacySettings : AttributeGlobalSettings<AILegacySettings>
    {
        public override string Id => "AILegacy_v1";
        public override string DisplayName => "AI Legacy";
        public override string FolderName => "AILegacy";
        public override string FormatType => "json2";

        // Botones de MCM: se ejecutan en el hilo principal en el siguiente tick
        private static void Later(System.Action a) => Core.MainThread.Post(a);

        // ------------------------------------------------------------ Claves y proveedores de IA (en MCM, fuera de la carpeta del mod)
        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1, 2, 3, 4): if the first one fails or runs out of quota, the next one is used. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
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
        public string P1Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public string P1Url { get; set; } = "";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public float P1PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_208da3de}Provider 1")]
        public float P1PriceOut { get; set; } = 0f;

        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1, 2, 3, 4): if the first one fails or runs out of quota, the next one is used. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
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
        public string P2Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public string P2Url { get; set; } = "";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public float P2PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1f8da24b}Provider 2")]
        public float P2PriceOut { get; set; } = 0f;

        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1, 2, 3, 4): if the first one fails or runs out of quota, the next one is used. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public bool P3Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Service", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Type one: gemini, openai, groq, deepseek, openrouter, ollama (local, no key) or other (then fill in the address).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Service { get; set; } = "";

        [SettingPropertyText("{=AIL_4868da9a}Key (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Your key for that service. It is shown in plain text on this screen: don't show it in screenshots.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Model (empty = the recommended one)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Exact name of the model. Empty: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat or llama3.1 depending on the service.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public string P3Url { get; set; } = "";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public float P3PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_1e8da0b8}Provider 3")]
        public float P3PriceOut { get; set; } = 0f;

        [SettingPropertyBool("{=AIL_7c7093eb}Use this provider", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Providers are tried in order (1, 2, 3, 4): if the first one fails or runs out of quota, the next one is used. Keys are saved in Documents, in Mount and Blade II Bannerlord / Configs / ModSettings, not in the mod folder.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public bool P4Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Service", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Type one: gemini, openai, groq, deepseek, openrouter, ollama (local, no key) or other (then fill in the address).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Service { get; set; } = "";

        [SettingPropertyText("{=AIL_4868da9a}Key (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Your key for that service. It is shown in plain text on this screen: don't show it in screenshots.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Model (empty = the recommended one)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Exact name of the model. Empty: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat or llama3.1 depending on the service.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Address (only if the service is \"other\")", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}OpenAI-compatible base address, e.g. https://my-server/v1. For the known services it is filled in automatically.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public string P4Url { get; set; } = "";

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e2}Input price ($ per million tokens)", 0f, 200f, "0.00", Order = 5, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public float P4PriceIn { get; set; } = 0f;

        [SettingPropertyFloatingInteger("{=AIL_c3a7f1e4}Output price ($ per million tokens)", 0f, 400f, "0.00", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e3}What this provider charges per million tokens. 0 = free. Used to work out spending (shown in the editor, Service tab) and for the daily cap; it changes nothing else.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. AI Keys/{=AIL_258dabbd}Provider 4")]
        public float P4PriceOut { get; set; } = 0f;

        /// <summary>Los proveedores escritos en MCM, en orden, ya con los detalles técnicos de cada servicio.</summary>
        public System.Collections.Generic.List<Config.ProviderConfig> McmProviders()
        {
            var list = new System.Collections.Generic.List<Config.ProviderConfig>();
            void Add(int n, bool on, string service, string key, string model, string url, float priceIn, float priceOut)
            {
                service = (service ?? "").Trim().ToLowerInvariant(); key = (key ?? "").Trim(); model = (model ?? "").Trim(); url = (url ?? "").Trim();
                if (!on || service.Length == 0) return;
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
                // Si escribes una dirección, manda la tuya (para servidores propios o «otro»)
                if (url.Length > 0) p.BaseUrl = url;
                p.PriceIn = Math.Max(0f, priceIn); p.PriceOut = Math.Max(0f, priceOut);
                list.Add(p);
            }
            Add(1, P1Enabled, P1Service, P1Key, P1Model, P1Url, P1PriceIn, P1PriceOut); Add(2, P2Enabled, P2Service, P2Key, P2Model, P2Url, P2PriceIn, P2PriceOut);
            Add(3, P3Enabled, P3Service, P3Key, P3Model, P3Url, P3PriceIn, P3PriceOut); Add(4, P4Enabled, P4Service, P4Key, P4Model, P4Url, P4PriceIn, P4PriceOut);
            return list;
        }

        /// <summary>Cambia cuando tocas algo de los proveedores en MCM (para recargarlos al momento).</summary>
        public string McmProvidersSignature() =>
            string.Join("|", P1Enabled, P1Service, P1Key, P1Model, P1Url, P2Enabled, P2Service, P2Key, P2Model, P2Url, P3Enabled, P3Service, P3Key, P3Model, P3Url, P4Enabled, P4Service, P4Key, P4Model, P4Url,
                P1PriceIn, P1PriceOut, P2PriceIn, P2PriceOut, P3PriceIn, P3PriceOut, P4PriceIn, P4PriceOut);

        // ------------------------------------------------------------ General
        [SettingPropertyDropdown("{=AIL_e250598b}Interface language", Order = -2, RequireRestart = false,
            HintText = "{=AIL_5fd4967b}Language of AI Legacy's menus, notices and windows. \"Auto\" uses the game's language. To see this menu in the new language, leave the options and come back in.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public MCM.Common.Dropdown<string> UiLanguage { get; set; } = new MCM.Common.Dropdown<string>(L.Names, 0);

        /// <summary>Código del idioma elegido: auto, es, en, de, fr, zh…</summary>
        public string UiLanguageCode()
        {
            int i = UiLanguage?.SelectedIndex ?? 0;
            return i >= 0 && i < L.Codes.Length ? L.Codes[i] : "auto";
        }

        [SettingPropertyText("{=AIL_e405f75e}Language of NPC replies", Order = -1, RequireRestart = false,
            HintText = "{=AIL_500ac9b1}Empty: the same language as the interface. If you want another one, type its name in English. E.g.: Spanish (Spain), English, Catalan, Italian.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public string ReplyLanguage { get; set; } = "";

        /// <summary>Idioma en que escribe la IA (nombre en inglés).</summary>
        public string Language => string.IsNullOrWhiteSpace(ReplyLanguage) ? L.EnglishName : ReplyLanguage.Trim();

        [SettingPropertyInteger("{=AIL_91d66f05}Minimum sentences per reply", 1, 6, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int ReplyMinSentences { get; set; } = 1;

        [SettingPropertyInteger("{=AIL_d22a5c23}Maximum sentences per reply", 1, 12, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int ReplyMaxSentences { get; set; } = 4;

        [SettingPropertyBool("{=AIL_90796905}Free chat with townsfolk", Order = 3, RequireRestart = false,
            HintText = "{=AIL_8201313c}Adds free chat to villagers, townspeople and tavern folk (not heroes).")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool EnableResidents { get; set; } = true;

        [SettingPropertyInteger("{=AIL_abf0a3ef}Clan tier at which people know you by reputation", 0, 7, Order = 4, RequireRestart = false,
            HintText = "{=AIL_a3cd73e3}From this clan tier on, any NPC knows who you are without an introduction.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int FameTierKnownByName { get; set; } = 4;

        [SettingPropertyBool("{=AIL_62e94015}Inner thought before replying", Order = 5, RequireRestart = false,
            HintText = "{=AIL_344592e1}The NPC reasons in private (what they know, what they want, whether an action makes sense) before speaking. Better replies, slightly slower.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool EnableThoughts { get; set; } = true;

        [SettingPropertyBool("{=AIL_758ef766}Conversation inside the game's dialogue", Order = 7, RequireRestart = false,
            HintText = "{=AIL_d279f208}On: you talk from the game's dialogue box (type → wait → see reply). Off: separate chat window.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool UseDialogStyle { get; set; } = true;

        [SettingPropertyBool("{=AIL_a7e1d3c1}Show the reply as soon as it arrives", Order = 8, RequireRestart = false,
            HintText = "{=AIL_a7e1d3c2}When the answer arrives it appears in the dialogue box by itself, without pressing \"See their reply\". Off: you press it yourself.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool AutoShowReply { get; set; } = true;

        [SettingPropertyBool("{=AIL_a7e1d3c3}Sound when the reply arrives", Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool ReplyReadySound { get; set; } = true;

        [SettingPropertyBool("{=AIL_a7e1d3c4}Relation, trust and romance under each reply", Order = 8, RequireRestart = false,
            HintText = "{=AIL_a7e1d3c5}Under each answer, one line with where you stand with that character right now: relation, trust, tension, romance, and how many times they have caught you lying.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool ShowDialogStats { get; set; } = true;

        [SettingPropertyBool("{=AIL_25c835cb}Use asterisks for gestures", Order = 8, RequireRestart = false, HintText = "{=AIL_1586f13c}NPCs describe gestures between *asterisks*.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool UseAsterisks { get; set; } = true;

        [SettingPropertyBool("{=AIL_f8db9623}Buttons on the map (News / Memories)", Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool ShowMapButtons { get; set; } = true;

        [SettingPropertyBool("{=AIL_53a2324c}Companions always recognize you", Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool CompanionsRecognize { get; set; } = true;

        [SettingPropertyBool("{=AIL_16443f4c}The nobles of your kingdom recognize you", Order = 11, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool KingdomRecognize { get; set; } = true;

        [SettingPropertyBool("{=AIL_d2c3f48b}Include the NPC's friends and enemies", Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool IncludeFriends { get; set; } = true;

        [SettingPropertyInteger("{=AIL_77262a54}Maximum friends in the prompt", 0, 15, Order = 13, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int MaxFriends { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_bce32dcd}Maximum enemies in the prompt", 0, 15, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int MaxEnemies { get; set; } = 5;

        [SettingPropertyBool("{=AIL_08baf922}Memory goes with the saved game", Order = 15, RequireRestart = false,
            HintText = "{=AIL_68f3dc4c}On: when you load a saved game, NPCs remember what they knew when you saved it. Off: they always remember the latest, even if you load an earlier saved game.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool MemoryFollowsSave { get; set; } = true;

        [SettingPropertyBool("{=AIL_a9b74d1b}Remove \"Talk\" from the character list", Order = 17, RequireRestart = false,
            HintText = "{=AIL_cefb1dfd}In the character list of a town, castle or tavern only \"Visit\" remains. You have to go to the person and talk to them in the scene.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool BlockQuickTalk { get; set; } = true;

        [SettingPropertyBool("{=AIL_68a7d597}Test mode: compliant NPCs", Order = 16, RequireRestart = false,
            HintText = "{=AIL_e4267ade}FOR TESTING ONLY. NPCs accept almost any reasonable request (following you, tasks, gifts, troops) without you having to earn their trust first. Turn it off to play.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool TestCompliantMode { get; set; } = false;

        // ------------------------------------------------------------ Relación
        [SettingPropertyBool("{=AIL_b59cea89}Personal relation with each NPC", Order = -1, RequireRestart = false,
            HintText = "{=AIL_2839a892}On: your relation is with that person. Off (like the game): with a noble, your relation is that of the head of their clan.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public bool PersonalRelation { get; set; } = true;

        [SettingPropertyInteger("{=AIL_edd26e08}Maximum relation change per reply", 0, 10, Order = 0, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance", GroupOrder = 2)]
        public int MaxRelationChangePerReply { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_1425de35}Maximum relation change per conversation", 0, 50, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int MaxRelationChangePerConversation { get; set; } = 10;

        // ------------------------------------------------------------ Acciones
        [SettingPropertyBool("{=AIL_fd3f245a}Enabled actions", Order = 0, RequireRestart = false,
            HintText = "{=AIL_1f1faf3a}If turned off, NPCs only talk: they don't give gold, don't join you, etc.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions", GroupOrder = 3)]
        public bool EnableActions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_256e88f0}Maximum gold per action", 0, 1000000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int MaxGoldPerAction { get; set; } = 20000;

        [SettingPropertyBool("{=AIL_8ff3713b}Diplomacy (war / peace between kings)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_14250983}A king can declare war on you or make peace with you if you are also a king.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowDiplomacy { get; set; } = true;

        [SettingPropertyBool("{=AIL_4ce0146b}Vassalage and mercenaries", Order = 3, RequireRestart = false,
            HintText = "{=AIL_a30867c0}A clan leader can swear fealty to you or serve you as a mercenary if you are a king.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowFealty { get; set; } = true;

        [SettingPropertyBool("{=AIL_26eb718e}Marriage", Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowMarriage { get; set; } = true;

        [SettingPropertyInteger("{=AIL_c509c21c}Minimum relation to accept marriage", 0, 100, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int MinRelationForMarriage { get; set; } = 30;

        [SettingPropertyBool("{=AIL_b85090ef}Orders on the map", Order = 6, RequireRestart = false,
            HintText = "{=AIL_65ab580e}NPCs who lead a party can agree to follow you, go somewhere, wait, patrol, attack, besiege, raid or join your army. Also by letter.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool EnableMapOrders { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e28e2ffe}Normal duration of an order (days)", 1, 30, Order = 7, RequireRestart = false,
            HintText = "{=AIL_904a67c2}How long they follow you, wait or patrol if nothing else is agreed. Also how long they wait for you somewhere when you arrange to meet.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int OrderDefaultDays { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_12efd9a7}Maximum duration of an order (days)", 1, 60, Order = 8, RequireRestart = false,
            HintText = "{=AIL_cb272b11}While carrying out an order they don't go about their normal life (they don't buy food or recruit), so it is best not to drag it out.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int OrderMaxDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_ac7ff171}Radius for attacking enemy parties", 5, 200, Order = 9, RequireRestart = false,
            HintText = "{=AIL_35e216d6}Distance on the map at which an NPC sees enemies they can go and attack if you ask them to.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int OrderAttackRadius { get; set; } = 40;

        [SettingPropertyBool("{=AIL_5da6bc58}Armies (join yours, gather their own, leave it)", Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowArmies { get; set; } = true;

        // ------------------------------------------------------------ Confianza y romance
        [SettingPropertyBool("{=AIL_71329b44}Trust (separate from relation)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_f8007f6a}Each NPC keeps track of how much they trust you. Without enough trust they don't share secrets or sensitive information.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public bool EnableTrust { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4cef6d19}Maximum trust change per reply", 1, 20, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int MaxTrustChangePerReply { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_1e4baeec}Trust needed to share secrets", 0, 100, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int TrustForSecrets { get; set; } = 40;

        [SettingPropertyInteger("{=AIL_cd6fe758}Trust you lose if caught in a lie", 0, 50, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int TrustLostPerLie { get; set; } = 10;

        [SettingPropertyBool("{=AIL_a4403b32}Romance", Order = 6, RequireRestart = false,
            HintText = "{=AIL_7d3d5b34}Romance level with each NPC: it rises with courtship, cools if you don't see each other, and counts toward marriage.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public bool EnableRomance { get; set; } = true;

        [SettingPropertyBool("{=AIL_276e2745}Wedding with companions, wanderers and notables", Order = 7, RequireRestart = false,
            HintText = "{=AIL_94ae59ca}The game only allows marriage between nobles. With this on, a companion, wanderer or notable you have won over can accept: they first join your household as a noble and stop taking up a companion slot.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool MarryCommoners { get; set; } = true;

        [SettingPropertyBool("{=AIL_170fa0ef}Romance with married people", Order = 7, RequireRestart = false,
            HintText = "{=AIL_9e5a3fd7}Off: someone who is married (or you, if you are) doesn't accept another wedding. On: they can accept it, and the previous marriage ends.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool RomanceWithMarried { get; set; } = false;

        [SettingPropertyInteger("{=AIL_f0c471e9}Romance that stops the game from marrying them to someone else (0 = don't stop)", 0, 100, Order = 7, RequireRestart = false,
            HintText = "{=AIL_bdf82f15}From this level of romance with you, the game does not marry that character off to someone else on its own.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int RomanceProtectsFromMarriage { get; set; } = 15;

        [SettingPropertyBool("{=AIL_09d60746}Romance between people of the same sex", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public bool RomanceSameSex { get; set; } = false;

        [SettingPropertyInteger("{=AIL_c5ca3b43}Maximum romance change per reply", 1, 20, Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int MaxRomanceChangePerReply { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_d582953e}Romance needed to accept marriage", 0, 100, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int RomanceForMarriage { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_635ff613}Days without seeing each other before romance cools", 1, 120, Order = 10, RequireRestart = false,
            HintText = "{=AIL_a643593e}After this many days without talking or writing to each other, romance drops 1 point per day (with your spouse it doesn't drop below 40).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int RomanceDecayAfterDays { get; set; } = 14;

        [SettingPropertyBool("{=AIL_0ea1a4c8}Intimacy", Order = 11, RequireRestart = false,
            HintText = "{=AIL_49588f47}With high romance, the NPC may agree to spend the night with you (never described). Between spouses it can end in pregnancy.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public bool AllowIntimacy { get; set; } = true;

        [SettingPropertyInteger("{=AIL_22b0d9b9}Romance needed for intimacy", 0, 100, Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int RomanceForIntimacy { get; set; } = 70;

        [SettingPropertyInteger("{=AIL_6b1ca542}Pregnancy chance (%)", 0, 100, Order = 13, RequireRestart = false,
            HintText = "{=AIL_6ac2e619}Only between spouses (the game needs a known father).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int PregnancyChancePercent { get; set; } = 25;

        [SettingPropertyBool("{=AIL_a1c0e5d1}Mood from causes", Order = 20, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d2}Each character's mood comes from what has happened to them, several things at once: a battle won or lost, a tournament, being a prisoner or wounded, a dead relative or friend, a war just declared, a birth. Each one lasts a few days. Off: only how the last talk left them counts.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public bool EnableMoodCauses { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a1c0e5d3}How long mood causes last (%)", 25, 400, Order = 21, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d4}100 = normal: a tournament win is savoured for 5 days, a battle for 3 or 4, mourning lasts 14 days for family and 10 for a friend. 200 = twice as long.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int MoodDurationPercent { get; set; } = 100;

        [SettingPropertyBool("{=AIL_a1c0e5d5}Female cycle", Order = 22, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d6}Women aged 13 to 47 have a cycle: it changes their mood depending on the day (a lot for some, hardly at all for others) and the chance of pregnancy, the game's own included. No cycle during pregnancy or after giving birth. She knows what day she is on.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public bool EnableFemaleCycle { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a1c0e5d7}Cycle length in days", 10, 40, Order = 23, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d8}Campaign days. A game year has 84 days, so 21 means four cycles a year.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int FemaleCycleDays { get; set; } = 21;

        [SettingPropertyBool("{=AIL_6746c998}Handing over troops and prisoners", Order = 11, RequireRestart = false,
            HintText = "{=AIL_c0668e57}An NPC with a party can give you soldiers or prisoners, or accept the ones you offer.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowTransfers { get; set; } = true;

        [SettingPropertyInteger("{=AIL_86ee0cb9}Maximum troops per handover", 1, 500, Order = 12, RequireRestart = false,
            HintText = "{=AIL_6a6bf769}Besides, an NPC never gives up more than a third of their party at once.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int MaxTroopsPerTransfer { get; set; } = 40;

        [SettingPropertyBool("{=AIL_3a0a45fd}Combat from the chat", Order = 13, RequireRestart = false,
            HintText = "{=AIL_298b35b9}Fistfights and duels in scenes, enemies attacking and surrendering on the map, and calling the guard.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowCombat { get; set; } = true;

        [SettingPropertyBool("{=AIL_731669e9}Townsfolk who join you", Order = 14, RequireRestart = false,
            HintText = "{=AIL_f8fff764}A villager you have really talked to can give up their life and join your company: they become a permanent character, with their own name, personality and memory. Their appearance changes to that of the new character.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowPromotions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_ac508642}Minimum sentences spoken before they can join", 2, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int PromotionMinTurns { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_b40b4b3a}Don't repeat the same payment within (seconds)", 0, 900, Order = 13, RequireRestart = false,
            HintText = "{=AIL_800f61d9}Sometimes the AI repeats a charge or payment that was already made in its next reply. The same amount, in the same direction and with the same person is not applied again within this time. 0 = no protection.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int GoldRepeatGuardSeconds { get; set; } = 240;

        [SettingPropertyBool("{=AIL_80912930}The guard steps in", Order = 14, RequireRestart = false,
            HintText = "{=AIL_6d2b9329}After a fight or duel in a settlement that is not yours, if a guard is nearby they come over to call you to account and decide: let you go, fine you or lock you up.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowJustice { get; set; } = true;

        [SettingPropertyBool("{=AIL_d594c7d1}Hitting anyone starts a fight", Order = 14, RequireRestart = false,
            HintText = "{=AIL_76fdb1de}In a town, village or castle, if you throw a punch at someone right in front of you, the fight with them starts without you having to talk to them: with fists if you are empty-handed, for real if you have your weapon drawn. Their relatives come to their defense and, if steel is drawn, so does the guard. They hold it against you, your bad reputation rises, and then the guard comes.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowStreetViolence { get; set; } = true;

        [SettingPropertyInteger("{=AIL_1c381bc9}How many come to the defense of the attacked person, at most", 0, 10, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int StreetFightHelpers { get; set; } = 4;

        [SettingPropertyBool("{=AIL_76ca0cb9}Armed fight: the guard arrives in waves", Order = 14, RequireRestart = false,
            HintText = "{=AIL_73b83666}If you attack someone with a weapon in a town, nearby guards join in every few seconds. With several down, the alarm sounds and the whole guard comes.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool StreetGuardWaves { get; set; } = true;

        [SettingPropertyInteger("{=AIL_45d4f33e}Guards per wave", 1, 10, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int StreetGuardWaveSize { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_5689cbfa}Seconds between waves", 3, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int StreetGuardWaveSeconds { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_eb1b8e7a}Maximum guards and defenders in a fight", 2, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int StreetMaxGuards { get; set; } = 20;

        [SettingPropertyBool("{=AIL_c61991e6}Armed fight: you can call your troops (Ctrl+X)", Order = 14, RequireRestart = false,
            HintText = "{=AIL_5b8786aa}During a fight in the street or in a village, Ctrl+X brings in ten of your soldiers and Ctrl+Alt+X as many as fit. They don't enter indoors. Those who fall are left wounded or dead in your party.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool StreetSummonTroops { get; set; } = true;

        [SettingPropertyInteger("{=AIL_164cfae0}Maximum of your soldiers in a street fight", 5, 100, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int StreetMaxOwnTroops { get; set; } = 30;

        [SettingPropertyBool("{=AIL_f264e0b3}Real trade with the people of the place", Order = 14, RequireRestart = false,
            HintText = "{=AIL_4a37fd4a}Merchants, craftsmen, innkeepers and villagers can sell to you and buy from you goods in a chat, against the real stock and prices of that place's market: goods and money really change hands.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowRealTrade { get; set; } = true;

        [SettingPropertyBool("{=AIL_109be563}Roleplay death", Order = 14, RequireRestart = false,
            HintText = "{=AIL_cd7a8ef0}A character can die as the outcome of a chat: if they are your prisoner and you execute them by saying so clearly, if they take their own life, or if they are dying of their wounds. They really die in the saved game.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowRoleplayDeath { get; set; } = true;

        [SettingPropertyBool("{=AIL_c949ab1f}Roleplay death: in any situation", Order = 14, RequireRestart = false,
            HintText = "{=AIL_c79b58e9}Off (recommended): only your prisoners and the wounded. On: the AI can decide the death of any character if the scene calls for it, including those of your clan.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool RoleplayDeathAnywhere { get; set; } = false;

        [SettingPropertyBool("{=AIL_4f0d9e02}Extras can become characters", Order = 14, RequireRestart = false,
            HintText = "{=AIL_7245c3fc}A villager in a scene, or one of your soldiers, can agree to enter your service as a companion (if you have room) or remain as a named person you can find again. They keep their face, age and what you have talked about.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowPromotion { get; set; } = true;

        [SettingPropertyBool("{=AIL_b0a1e3a6}Witnesses are required", Order = 14, RequireRestart = false,
            HintText = "{=AIL_ced9d43e}If you hit or kill someone without anyone alive seeing it, nobody knows it was you: no bad reputation, no guard, no name in the news. If they see you but don't know who you are, they look for \"an armed stranger\" and the bad reputation is halved. They only name you if a witness knows you or if you are already famous.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool EnableWitnesses { get; set; } = true;

        [SettingPropertyInteger("{=AIL_8aab03a7}Witnesses: how far they can see (meters)", 5, 80, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int WitnessRadius { get; set; } = 25;

        [SettingPropertyInteger("{=AIL_41faa86d}Witnesses: clan tier from which everyone recognizes you", 1, 7, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int WitnessFameTier { get; set; } = 3;

        [SettingPropertyBool("{=AIL_6e4f31b1}Characters can arrest each other", Order = 14, RequireRestart = false,
            HintText = "{=AIL_3732e7fc}In a group conversation in a town or castle, a guard or whoever is in charge there can take another character present into custody if there is a serious reason (e.g. you accuse them of a crime with evidence in plain sight). They are really imprisoned, and they hold it against you.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowNpcArrests { get; set; } = true;

        [SettingPropertyBool("{=AIL_fedffd7f}The lord of the place judges you", Order = 14, RequireRestart = false,
            HintText = "{=AIL_5b102155}If the guard locks you up outside your fief and your kingdom, the lord of the place passes sentence (release, fine or days in the dungeon), changes your reputation there and what they think of you, and their sentence is announced as news. They need to know who you are there.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowVerdict { get; set; } = true;

        [SettingPropertyInteger("{=AIL_ac30aa7a}Maximum sentence (days in the dungeon)", 1, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int VerdictMaxDays { get; set; } = 10;

        [SettingPropertyBool("{=AIL_15eec8c6}Lines during fights", Order = 14, RequireRestart = false,
            HintText = "{=AIL_7aad4a7d}In fights and duels within a scene, your opponent says lines depending on how it's going, and onlookers shout. Just one request to the AI when the fight starts.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool EnableFightBanter { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3ba48d61}Distance at which a guard sees a fight (meters)", 5, 200, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int GuardHearingDistance { get; set; } = 35;

        [SettingPropertyInteger("{=AIL_148d1995}Distance at which a guard hears a call (meters)", 5, 400, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int GuardCallDistance { get; set; } = 90;

        [SettingPropertyInteger("{=AIL_fde6aa22}Usual fine: minimum", 1, 100000, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int FineMin { get; set; } = 50;

        [SettingPropertyInteger("{=AIL_7f897c38}Usual fine: maximum", 1, 100000, Order = 14, RequireRestart = false,
            HintText = "{=AIL_004eae62}The guard can go up to five times over this value if there was blood or you disrespect them.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int FineMax { get; set; } = 500;

        [SettingPropertyInteger("{=AIL_6184689e}Crime for walking out on a guard", 0, 100, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int CrimeForWalkingAway { get; set; } = 10;

        [SettingPropertyBool("{=AIL_2da21a68}Duels to the death really kill", Order = 14, RequireRestart = false,
            HintText = "{=AIL_735ebb21}If you win a duel to the death against a hero, they die. You never die: you are left badly wounded.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowDuelDeath { get; set; } = true;

        [SettingPropertyBool("{=AIL_7a6f04b2}Tension and threat", Order = 14, RequireRestart = false,
            HintText = "{=AIL_04d419f6}Tension rises with insults and threats and drops with the days. With high tension the NPC can turn to violence or call the guard. They also gauge whether you frighten them (your troops against theirs).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public bool EnableTension { get; set; } = true;

        [SettingPropertyInteger("{=AIL_35049c49}Maximum tension change per reply", 1, 50, Order = 15, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int MaxTensionChangePerReply { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_bcd82606}Tension needed for violence", 0, 100, Order = 16, RequireRestart = false,
            HintText = "{=AIL_c0e5f1de}From here on they can attack you on their own or call the guard. Agreed duels and fights don't need it.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int TensionForViolence { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_ab464755}Tension that drops each day", 1, 100, Order = 17, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relation, trust and romance")]
        public int TensionDecayPerDay { get; set; } = 15;

        [SettingPropertyBool("{=AIL_cae619f6}Tasks (quests created by the AI)", Order = 15, RequireRestart = false,
            HintText = "{=AIL_745a654d}NPCs can give you tasks, note your progress, and mark them fulfilled (with reward) or failed. They appear in the game's quest log. Careful: a saved game with tasks needs AI Legacy to load.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool EnableQuests { get; set; } = true;

        [SettingPropertyInteger("{=AIL_f039f8c1}Active tasks at once", 1, 30, Order = 16, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int MaxActiveQuests { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_871af31c}Relation gained when completing a task", 0, 20, Order = 17, RequireRestart = false,
            HintText = "{=AIL_3a0aca3f}If you fail or the deadline passes, you lose half.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int QuestRelationReward { get; set; } = 4;

        [SettingPropertyBool("{=AIL_5454e867}Items, documents and workshops", Order = 18, RequireRestart = false,
            HintText = "{=AIL_ba299376}Giving and receiving items, roleplay documents (letters of introduction, contracts, safe-conducts) and buying workshops by talking.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowItems { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0bfa167c}Maximum value of the items an NPC gives at once", 0, 1000000, Order = 19, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int MaxItemValuePerAction { get; set; } = 5000;

        [SettingPropertyBool("{=AIL_da8482bd}Grant and accept fiefs", Order = 20, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowFiefs { get; set; } = true;

        [SettingPropertyBool("{=AIL_911ad5bc}Abdicate and hand over the crown", Order = 21, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowAbdication { get; set; } = true;

        [SettingPropertyBool("{=AIL_990436d2}Clan changes (join yours, leave it)", Order = 22, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool AllowClanChanges { get; set; } = true;

        // ------------------------------------------------------------ Memoria
        [SettingPropertyInteger("{=AIL_1057a530}History turns in the prompt", 4, 60, Order = 0, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory", GroupOrder = 4)]
        public int HistoryTurnsInPrompt { get; set; } = 16;

        [SettingPropertyBool("{=AIL_d4b8a2f1}Recall by meaning (RAG)", Order = 21, RequireRestart = false,
            HintText = "{=AIL_d4b8a2f2}Besides searching by words, your shared history is searched for moments that relate to what you say even if you use other words (\"that promise\" finds \"I will help you take the fortress\"). It uses the vectors of a provider you already have (Gemini gives them free; OpenAI charges cents). If none provides them, the word search carries on.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableRag { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d4b8a2f3}History chunks retrieved", 1, 10, Order = 22, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int RagTopK { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_d4b8a2f4}Lines per chunk", 2, 20, Order = 23, RequireRestart = false,
            HintText = "{=AIL_d4b8a2f5}How many consecutive lines form one chunk of history when indexing it. If you change it, each character's index rebuilds itself.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int RagChunkSize { get; set; } = 6;

        [SettingPropertyBool("{=AIL_d4b8a2f6}Actions by topic", Order = 24, RequireRestart = false,
            HintText = "{=AIL_d4b8a2f7}Actions that depend on what is being discussed (orders, trade, realm) are only explained in full when relevant; the rest are named on one line and still work. Saves tokens on every line. Needs the vectors of recall by meaning.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableActionRag { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d4b8a2f8}Actions explained in full", 1, 20, Order = 25, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int ActionRagTopK { get; set; } = 7;

        [SettingPropertyText("{=AIL_d4b8a2f9}Vector model (empty = automatic)", Order = 26, RequireRestart = false,
            HintText = "{=AIL_d4b8a2fa}Empty: gemini-embedding-001 with Gemini, text-embedding-3-small with OpenAI, nomic-embed-text with Ollama. Type another only if your provider uses a different name.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public string EmbeddingModel { get; set; } = "";

        /// <summary>Trozos de historia que se conservan por personaje en el índice (los más viejos salen).</summary>
        public int RagMaxChunks => 600;

        [SettingPropertyBool("{=AIL_c3a7f1e9}Facts about what is named", Order = 20, RequireRestart = false,
            HintText = "{=AIL_c3a7f1ea}If a place, a house, a realm, a person or a trade good is named in the talk, its public facts go into that request (where it is, whose it is, what it is worth), and on the map also the parties in sight. The character knows what you mean without loading the whole world every time.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableMentions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_effea661}Summarize after N turns", 10, 200, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int SummarizeAfterTurns { get; set; } = 24;

        [SettingPropertyInteger("{=AIL_ba098098}Verbatim turns kept after summarizing", 2, 40, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int KeepTurnsAfterSummary { get; set; } = 8;

        [SettingPropertyBool("{=AIL_5283e9ce}Characters remember their own dealings with each other", Order = 3, RequireRestart = false,
            HintText = "{=AIL_17ca3c0a}Each named character keeps track of how they get along with other characters and the latest thing that happened between them (their chats, an errand, a snub in front of you, an arrest). They use it when they talk to each other, in groups and with you. It is stored in their file, under \"others\".")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableNpcBonds { get; set; } = true;

        [SettingPropertyBool("{=AIL_a2e05b46}What happens between them changes their relation in the game", Order = 3, RequireRestart = false,
            HintText = "{=AIL_02e730b3}When a chat leaves two characters clearly better or worse off, their in-game relation goes up or down by one point.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool NpcBondsAffectRelation { get; set; } = true;

        [SettingPropertyInteger("{=AIL_f43bef55}Memories kept of each other character", 1, 10, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int NpcBondNotes { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_34f22b4a}How many characters each one keeps memories of", 4, 60, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int NpcBondMaxPeople { get; set; } = 20;

        [SettingPropertyBool("{=AIL_daf17754}Complete half-filled character sheets", Order = 3, RequireRestart = false,
            HintText = "{=AIL_e1661a3b}Old character sheets, or ones imported from AIInfluence, only come with personality, past and way of speaking. With this on, in the next chat with that character the AI writes what is missing (what they seek, their secret, what drives them, values, religion, reputation, example phrases) without touching what was already there.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool CompleteProfiles { get; set; } = true;

        [SettingPropertyBool("{=AIL_ad44fa7d}Relevant recall of old conversations", Order = 3, RequireRestart = false,
            HintText = "{=AIL_bc041dfb}Sentences that leave the history when summarizing are archived. When you talk, the ones related to what you are saying now (names, places, specific things) are looked up and the character remembers them word for word. It doesn't use extra requests.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableRecall { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0d81b407}Relevant memories brought into each reply", 1, 8, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int RecallCount { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_52769c73}Old sentences archived per character", 20, 2000, Order = 3, RequireRestart = false,
            HintText = "{=AIL_70a6b6c9}How many old sentences are kept for each character so they can be recalled. More = better long-term memory and somewhat larger files.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int ArchiveTurns { get; set; } = 400;

        [SettingPropertyInteger("{=AIL_69c192a8}Maximum memories per character", 3, 40, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int MaxNotesPerHero { get; set; } = 12;

        [SettingPropertyBool("{=AIL_6b899a92}Book of memories by pages", Order = 4, RequireRestart = false,
            HintText = "{=AIL_ab26e5e4}Each time a stretch of your shared story is summarized, the AI writes a titled page: it can be read in the Book of memories.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableMemoryPages { get; set; } = true;

        [SettingPropertyInteger("{=AIL_5c92a18a}Maximum pages per character", 1, 100, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int MaxMemoryPages { get; set; } = 30;

        [SettingPropertyBool("{=AIL_5c31ae7f}Personality archetypes", Order = 6, RequireRestart = false,
            HintText = "{=AIL_96004d45}Each character is born from a mold (world_data\\archetypes.json) that sets their morals, how they speak and how much they believe. Editable.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableArchetypes { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7df35c1}Religion and customs of each culture", Order = 7, RequireRestart = false,
            HintText = "{=AIL_bd15ad6c}world_data\\religion.json and cultural_traditions.json: the faith and customs (courtship, hospitality, oaths) of their people. Editable.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableReligion { get; set; } = true;

        [SettingPropertyBool("{=AIL_41386273}Personality evolves", Order = 8, RequireRestart = false,
            HintText = "{=AIL_2fb56a84}After an event that marks them (a betrayal, a death, a vow, falling in love), the AI slightly adjusts their personality and notes it in their past.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public bool EnableCharacterDevelopment { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3cb61993}Minimum days between personality changes", 1, 120, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int DevelopmentMinDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_e99f2367}Fields that can change at once", 1, 5, Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memory")]
        public int DevelopmentMaxFields { get; set; } = 2;

        // ------------------------------------------------------------ Noticias
        [SettingPropertyBool("{=AIL_e424c129}World news", Order = 0, RequireRestart = false,
            HintText = "{=AIL_30c8d104}Wars, peace treaties, battles, sieges, deaths... NPCs find out depending on importance and proximity.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors", GroupOrder = 5)]
        public bool EnableNews { get; set; } = true;

        [SettingPropertyBool("{=AIL_b430b5cf}Rumors from conversations", Order = 1, RequireRestart = false,
            HintText = "{=AIL_a7c4d3a0}What happens in a chat can spread by word of mouth.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors")]
        public bool EnableRumors { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a013a921}Days a piece of news lasts", 5, 365, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors")]
        public int NewsLifetimeDays { get; set; } = 60;

        [SettingPropertyBool("{=AIL_d2e7a4c5}News travels with distance", Order = 5, RequireRestart = false,
            HintText = "{=AIL_d2e7a4c6}What happens far away takes days to arrive, and small matters never get there. Nobles hear from further away than commoners, rulers from further still, and friends or enemies of those involved always find out. Off: everyone in the realm knows at once.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors")]
        public bool RealisticNewsSpread { get; set; } = true;

        [SettingPropertyInteger("{=AIL_8b7f2016}Maximum news in the prompt", 0, 30, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_953a8770}5. News and rumors")]
        public int MaxNewsInPrompt { get; set; } = 8;

        // ------------------------------------------------------------ Cartas
        [SettingPropertyBool("{=AIL_1b9a1bfb}Letters from NPCs", Order = 0, RequireRestart = false,
            HintText = "{=AIL_7f30bf34}Characters you have history with write to you on their own.")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters", GroupOrder = 6)]
        public bool EnableLetters { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4695e872}Daily chance of a letter (%)", 0, 100, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters")]
        public int LetterChancePercent { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_d943da10}Minimum days between letters", 1, 60, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters")]
        public int MinDaysBetweenLetters { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_9d30d4f1}Messenger cost per distance unit", 0, 100, Order = 3, RequireRestart = false,
            HintText = "{=AIL_0ebbc439}Denars per distance unit on the map. Letters are sent from the tavern district.")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters")]
        public int MessengerCostPerDistance { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_3fbb045d}Travel hours per distance unit (x10)", 1, 100, Order = 4, RequireRestart = false,
            HintText = "{=AIL_95f8de9b}10 = 1 hour per distance unit. Farther = takes longer to arrive (and for the reply to come back).")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Letters")]
        public int MessengerHoursPerDistanceX10 { get; set; } = 10;

        // ------------------------------------------------------------ IA
        [SettingPropertyInteger("{=AIL_0d832ced}Temperature (x100)", 0, 150, Order = 0, RequireRestart = false,
            HintText = "{=AIL_e47fec25}Creativity. 80 = 0.8. Some models (GPT-5) ignore it.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI", GroupOrder = 7)]
        public int TemperatureX100 { get; set; } = 80;

        [SettingPropertyInteger("{=AIL_dcc9bf83}Maximum tokens per reply", 200, 4000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public int MaxTokens { get; set; } = 900;

        [SettingPropertyButton("{=AIL_affcf5d2}Test connection to the AIs", Content = "{=AIL_f43d7a3d}Test", Order = 2, RequireRestart = false,
            HintText = "{=AIL_8e88da8b}Tests each provider (those in \"0. AI Keys\" and, if present, those in config.json) and shows the result in the game's log.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public Action TestConnection { get; set; } = () => LlmClient.TestAllProviders();

        [SettingPropertyText("{=AIL_3865f0c2}Provider for heroes", Order = 4, RequireRestart = false,
            HintText = "{=AIL_a9aa8409}Name of a provider: the service you typed in \"0. AI Keys\" (gemini, openai; the one in slot 2, 3 or 4 carries its number: openai2) or one from config.json. Empty = the normal chain. If it fails, the rest are used.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public string ProviderHeroes { get; set; } = "";

        [SettingPropertyText("{=AIL_4b7ed15a}Provider for townsfolk", Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public string ProviderResidents { get; set; } = "";

        [SettingPropertyText("{=AIL_5fcbf671}Provider for letters", Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public string ProviderLetters { get; set; } = "";

        [SettingPropertyText("{=AIL_c5d6d5d7}Provider for memory summaries", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public string ProviderSummaries { get; set; } = "";

        // ------------------------------------------------------------ Escena del mapa
        [SettingPropertyBool("{=AIL_20b8ba96}Map scene: also with enemies (parley)", Order = 20, RequireRestart = false,
            HintText = "{=AIL_011ed64e}With an enemy lord you can also dismount and talk face to face. When you return to the map, the encounter continues where it was (attack, surrender, let you go), unless it was resolved in the chat.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool MapTalkWithEnemies { get; set; } = true;

        [SettingPropertyBool("{=AIL_fe9e2f63}Talk in the scene from the map", Order = 20, RequireRestart = false,
            HintText = "{=AIL_967570b4}When you meet a lord on the map you are not at war with, the option \"Let's dismount and talk face to face\" appears: a scene of the terrain opens with the two of you on foot and your people behind you. Tab to return to the map.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool EnableMapTalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_401f90b1}Map scene: companions per side", 0, 30, Order = 21, RequireRestart = false,
            HintText = "{=AIL_297f659a}How many from each party appear behind their leader (first their heroes, then their best troops).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int MapTalkEscort { get; set; } = 8;

        // ------------------------------------------------------------ Batallas
        [SettingPropertyBool("{=AIL_3bdc9ea5}Battle tactics AI in field battles", Order = 0, RequireRestart = false,
            HintText = "{=AIL_39a19577}The leader of each side you don't command periodically receives the state of the battle and gives real orders to their formations, according to their personality and skill. While they decide, or if the AI fails, the game's own tactics continue. It doesn't act in sieges.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles", GroupOrder = 14)]
        public bool EnableBattleAI { get; set; } = false;

        [SettingPropertyInteger("{=AIL_92a26c15}Seconds between each leader's decisions", 30, 300, Order = 1, RequireRestart = false,
            HintText = "{=AIL_458e1b5a}Each decision is one request to the AI per side. If a side suddenly loses many people, it decides sooner.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles")]
        public int BattleAIInterval { get; set; } = 120;

        [SettingPropertyBool("{=AIL_df336f51}The enemy commands with AI", Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles")]
        public bool BattleAIEnemy { get; set; } = true;

        [SettingPropertyBool("{=AIL_b8b2988f}Your allies command with AI", Order = 3, RequireRestart = false,
            HintText = "{=AIL_7858cd57}The allied army with its own leader, and your side when you are not the one in command (the formations you lead are not touched).")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles")]
        public bool BattleAIAllies { get; set; } = false;

        [SettingPropertyBool("{=AIL_7f5984a3}Orders in your own words (Alt+B)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_04c41d7c}In battle, Alt+B: you write the order as you would say it (\"cavalry on the left against their archers\") and the AI turns it into orders for your formations. The game pauses while you type.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles")]
        public bool BattleAITextOrders { get; set; } = true;

        [SettingPropertyBool("{=AIL_76c8cc5d}Leaders' shouts", Order = 5, RequireRestart = false,
            HintText = "{=AIL_26b70a49}What each leader bellows at their men appears in the log (and above their head if they are near).")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles")]
        public bool BattleAIShouts { get; set; } = true;

        [SettingPropertyBool("{=AIL_f8823100}Show the AI's orders", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c829a044}Shows in the log what each leader has ordered each formation.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles")]
        public bool BattleAIShowOrders { get; set; } = true;

        [SettingPropertyText("{=AIL_17a3df8d}Provider for battles", Order = 7, RequireRestart = false,
            HintText = "{=AIL_506e56c2}Name of the config.json provider preferred for battle orders (empty = the normal one). A fast one is best.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Battles")]
        public string ProviderBattle { get; set; } = "";

        // ------------------------------------------------------------ Escenas
        [SettingPropertyBool("{=AIL_3515a1a2}Text above characters' heads", Order = 0, RequireRestart = false,
            HintText = "{=AIL_2287fa82}In scenes, what characters say to each other or shout at you in passing appears written above their heads.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes", GroupOrder = 13)]
        public bool EnableBubbles { get; set; } = true;

        [SettingPropertyBool("{=AIL_2808c06c}Chats between characters in the scenes", Order = 1, RequireRestart = false,
            HintText = "{=AIL_4e6c313c}From time to time, two characters near you talk about what is going on: news, shortages, disease, their lord. Uses one short request to the AI each time.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool EnableAmbient { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4faa8fb4}Seconds between chats (approx.)", 15, 900, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int AmbientIntervalSeconds { get; set; } = 45;

        [SettingPropertyInteger("{=AIL_637830f6}Distance at which you hear a chat (meters)", 4, 60, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int AmbientRadius { get; set; } = 20;

        [SettingPropertyBool("{=AIL_4df26a61}What they say also appears in the log", Order = 1, RequireRestart = false,
            HintText = "{=AIL_af3d096e}Besides above their heads, the sentences characters exchange appear in the message log (bottom left).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool AmbientToLog { get; set; } = true;

        [SettingPropertyBool("{=AIL_131719b8}Whoever wants to talk walks over", Order = 3, RequireRestart = false,
            HintText = "{=AIL_2c243fac}If the two who are going to chat are not together, one walks over to the other before starting (also between floors, if there is a path).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool AmbientWalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_beb1508e}Maximum distance between the two chatting (meters)", 2, 40, Order = 3, RequireRestart = false,
            HintText = "{=AIL_4f107a7c}In a hall the nobles are spread apart: with a low value they never find anyone to talk to.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int AmbientPairDistance { get; set; } = 14;

        [SettingPropertyInteger("{=AIL_508eddc4}Maximum length of each sentence (characters)", 40, 240, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int AmbientMaxChars { get; set; } = 140;

        [SettingPropertyInteger("{=AIL_0ca5ee01}Maximum sentences in a chat between characters", 2, 30, Order = 4, RequireRestart = false,
            HintText = "{=AIL_5d9bde16}The AI writes between half of this number and this number, depending on what the topic allows. It is a single request, long or short.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int AmbientMaxLines { get; set; } = 10;

        [SettingPropertyBool("{=AIL_e37afdcd}Ordinary people also chat (briefly)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5c19273c}Off: only named characters (lords, ladies, notables, wanderers) chat among themselves. On: villagers, innkeepers and others too, but just two or three short sentences.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool AmbientCommoners { get; set; } = true;

        [SettingPropertyBool("{=AIL_2da177ae}Soldiers and guards also chat (briefly)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_74efb871}Off by default: they are on duty. They still answer if you talk to them.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool AmbientGuards { get; set; } = true;

        [SettingPropertyBool("{=AIL_7a1b2cf4}They say things to you in passing", Order = 4, RequireRestart = false,
            HintText = "{=AIL_9b770e1c}When you walk next to someone in a scene (guards, villagers, merchants, nobles who don't know you), they say something in passing. Uses ONE request per visit to each place: a batch of sentences is requested and used up gradually. Turn it off if you don't want that cost.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool EnablePassBy { get; set; } = true;

        [SettingPropertyInteger("{=AIL_90bb89d1}In passing: seconds between one sentence and the next", 4, 120, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int PassBySeconds { get; set; } = 14;

        [SettingPropertyInteger("{=AIL_0b677944}In passing: distance at which they speak to you (meters)", 2, 12, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int PassByDistance { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_cd6bc920}In passing: maximum length of each sentence", 30, 160, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int PassByMaxChars { get; set; } = 80;

        [SettingPropertyInteger("{=AIL_8529201a}In passing: extra batches when sentences run out", 0, 5, Order = 4, RequireRestart = false,
            HintText = "{=AIL_96933cf3}Each extra batch is one more request on that visit. 0 = only the first.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int PassByRefills { get; set; } = 1;

        [SettingPropertyBool("{=AIL_3c35b946}Turn-based chats between nobles (more requests)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_ff3da657}Off: each chat between characters is written whole in one request. On: it is requested in turns and each round knows what was said before; they can interrupt each other and the chat ends on its own if you step in. Uses several requests per chat.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool AmbientTurnByTurn { get; set; } = false;

        [SettingPropertyInteger("{=AIL_a9262a10}Turn-based: sentences per request", 1, 4, Order = 4, RequireRestart = false,
            HintText = "{=AIL_270c2dbd}1 = one request per sentence (the most expensive). 2 = half the requests.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int AmbientTurnSize { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_9f48ba90}Maximum sentences when ordinary people chat", 2, 8, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int AmbientCommonerLines { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_3e132ba1}Group: seconds of your silence before they continue (0 = never)", 0, 180, Order = 5, RequireRestart = false,
            HintText = "{=AIL_f08739d1}If you say nothing during this time, the AI decides whether someone in the group continues the conversation (pushes you, or they start talking among themselves). Each time is one request.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupSilenceSeconds { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_b4fc9c6e}Group: consecutive times they continue without you", 0, 6, Order = 5, RequireRestart = false,
            HintText = "{=AIL_26e90847}How many times in a row they can continue the conversation without you saying anything. When you speak, the count starts over.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupSilenceFollowUps { get; set; } = 4;

        [SettingPropertyBool("{=AIL_7dd76d95}Group: they form a circle around you", Order = 5, RequireRestart = false,
            HintText = "{=AIL_bd386578}When opening a group conversation, the participants walk until they form a semicircle in front of you and look at you. Anyone seated or busy stays where they are.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool GroupCircle { get; set; } = true;

        [SettingPropertyBool("{=AIL_d79a206d}Group: slow motion while you type", Order = 5, RequireRestart = false,
            HintText = "{=AIL_91d8da81}On: when you open the Alt+T window the scene goes on, in slow motion. Off: the game pauses while you type.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool GroupSlowMotion { get; set; } = true;

        [SettingPropertyBool("{=AIL_14d6bbb7}Group: bystanders can chime in", Order = 5, RequireRestart = false,
            HintText = "{=AIL_020f810a}Anyone nearby who hears what you say to the group can walk over and join the conversation if they have a stake in it. Doesn't use extra requests.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool GroupBystanders { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a33f15bf}Group: how far you can be heard (meters)", 1, 20, Order = 5, RequireRestart = false,
            HintText = "{=AIL_d0722260}Distance at which someone outside hears the conversation. A shout (!) reaches twice as far; a whisper (*), only whoever you are looking at.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupHearRadius { get; set; } = 10;

        [SettingPropertyBool("{=AIL_32aaabb0}Gestures and faces while talking", Order = 5, RequireRestart = false,
            HintText = "{=AIL_6fed9f7e}In scene chats, whoever speaks makes the face and gesture of what they feel (nodding, denying, doubting, getting angry…). Anyone seated, busy or walking only changes their face.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool EnableGestures { get; set; } = true;

        [SettingPropertyBool("{=AIL_92d095e7}In the group, they can go and fetch someone", Order = 5, RequireRestart = false,
            HintText = "{=AIL_3a88a804}If what you say in a group conversation gives them reason to, the character can act: go talk to someone else and come back to tell you, accompany you, stay, guide you to someone, call someone over, or leave.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool SceneErrands { get; set; } = true;

        [SettingPropertyInteger("{=AIL_90722e01}Silence after a chat (seconds)", 30, 900, Order = 4, RequireRestart = false,
            HintText = "{=AIL_c43fad42}Time without new chats after one ends. The same two take twice as long to talk again.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int AmbientCooldownSeconds { get; set; } = 180;

        [SettingPropertyBool("{=AIL_05883eac}Those who know you say something in passing", Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool AmbientCallOuts { get; set; } = true;

        [SettingPropertyBool("{=AIL_7450a805}Group conversation (Alt+T)", Order = 5, RequireRestart = false,
            HintText = "{=AIL_b7c8efe0}In a scene, Alt+T: you talk to everyone near you and the AI decides who answers. Their replies appear above their heads and in the log. Press Alt+T again to keep talking.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool EnableGroupTalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_35cd4d87}Group: distance to join (meters)", 2, 30, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupRadius { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_2eff340e}Group: distance at which someone leaves (meters)", 3, 50, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupGraceRadius { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_a29fcb55}Group: maximum participants", 1, 12, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupMaxParticipants { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_3bcc8ae2}Group: maximum replies per turn", 1, 6, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupMaxLines { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_7bd9a098}Group: maximum length of each reply", 60, 400, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupMaxChars { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_f6cbdca6}Group: seconds without talking before it breaks up", 30, 1800, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int GroupIdleSeconds { get; set; } = 240;

        [SettingPropertyBool("{=AIL_6e6f1630}What you say is overheard", Order = 6, RequireRestart = false,
            HintText = "{=AIL_d0cb85ca}Named characters who are a few steps away when you talk to someone in a scene overhear what you say, and remember it.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public bool EnableOverhearing { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a8cfe43b}Distance at which they hear you (meters)", 1, 30, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int OverhearRadius { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_c4dbb3ad}Maximum distance at which the text is visible (meters)", 5, 100, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Scenes")]
        public int BubbleMaxDistance { get; set; } = 25;

        // ------------------------------------------------------------ Enfermedades
        [SettingPropertyBool("{=AIL_68f76b26}Diseases", Order = 0, RequireRestart = false,
            HintText = "{=AIL_6a0ec00a}Outbreaks by season or from an event: they damage the settlement, spread to neighbors, sicken your troops if you enter, and are slowed by quarantines.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases", GroupOrder = 12)]
        public bool EnableDiseases { get; set; } = true;

        [SettingPropertyInteger("{=AIL_5e55369d}Maximum simultaneous outbreaks", 1, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public int DiseaseMaxSimultaneous { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_36b9fe81}Maximum severity", 1, 5, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public int DiseaseMaxSeverity { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_77ca23b7}Minimum days between new outbreaks", 1, 200, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public int DiseaseMinDaysBetweenOutbreaks { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_b3d0b991}Daily chance of a seasonal outbreak (per thousand)", 0, 200, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public int DiseaseSeasonalChancePerMille { get; set; } = 25;

        [SettingPropertyInteger("{=AIL_e00fb19f}Daily spread to a neighbor per severity level (per thousand)", 0, 200, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public int DiseaseSpreadChancePerMille { get; set; } = 12;

        [SettingPropertyInteger("{=AIL_0361e655}Prosperity lost per day per severity level", 0, 50, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public int DiseaseProsperityLossPerSeverity { get; set; } = 4;

        [SettingPropertyBool("{=AIL_c54dc4b5}Events can bring diseases", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public bool DiseaseFromEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_eade4433}Your troops can fall ill", Order = 8, RequireRestart = false,
            HintText = "{=AIL_c6dc6d42}When entering or staying in a place with disease, some of your soldiers may fall ill (they are left as wounded).")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public bool DiseaseAffectsPlayerTroops { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7d2f020}Quarantine closes the gates", Order = 20, RequireRestart = false,
            HintText = "{=AIL_b7d2f021}While a quarantine lasts, lords, caravans and villagers stay away, no tournament is called, and the guard stops you at the gate. Owners and enemies at war are not stopped.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public bool QuarantineClosesGates { get; set; } = true;

        [SettingPropertyInteger("{=AIL_b7d2f022}Bribe to enter a quarantined place, per severity level (0 = no way in)", 0, 5000, Order = 21, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public int QuarantineBribeGold { get; set; } = 300;

        [SettingPropertyInteger("{=AIL_1b8ccb46}Chance of falling ill per severity level (%)", 0, 30, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public int DiseaseTroopChancePerSeverity { get; set; } = 8;

        // ------------------------------------------------------------ Iniciativa y recados
        [SettingPropertyBool("{=AIL_875d42da}NPCs tell each other things", Order = 0, RequireRestart = false,
            HintText = "{=AIL_39a88b5b}What you say or do in front of an NPC can reach their lord, their family or their friends, delayed according to distance. What arrives is their version, not a fact.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands", GroupOrder = 11)]
        public bool EnableInfoExchange { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0a072878}Errands each NPC remembers", 1, 40, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands")]
        public int MaxHeardPerHero { get; set; } = 10;

        [SettingPropertyBool("{=AIL_a815af23}NPCs approach you on the map", Order = 2, RequireRestart = false,
            HintText = "{=AIL_cc079ac8}A noble who already knows you and passes nearby may ride up to you to talk, if they have a reason: an open task, something they heard about you, affection or resentment. They ask whether you will receive them.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands")]
        public bool EnableMapInitiative { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3225c875}Hourly chance that someone nearby approaches (%)", 0, 100, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands")]
        public int MapInitiativeChancePercent { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_f42f2a6a}Minimum days between approaches", 0, 60, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands")]
        public int MapInitiativeMinDaysBetween { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_fcce1e3f}Minimum days between approaches by the same NPC", 1, 120, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Initiative and errands")]
        public int MapInitiativeDaysPerNpc { get; set; } = 7;

        // ------------------------------------------------------------ Mundo y diplomacia
        [SettingPropertyBool("{=AIL_8a4cc812}AI world events", Order = 0, RequireRestart = false,
            HintText = "{=AIL_b057228e}From time to time the AI writes new events based on the real state of the world (politics, war, economy, society, mysteries). They appear in News and NPCs know about them.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy", GroupOrder = 10)]
        public bool EnableWorldEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_c268feb6}Reactions to what really happens", Order = 1, RequireRestart = false,
            HintText = "{=AIL_bd7a44d0}When something big happens in the game (a war, a peace, a captured town, the death or capture of someone important, a big rumor from your chats), the AI immediately writes how the world takes it.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool ReactiveEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_77793b62}Hospital in the towns", Order = 1, RequireRestart = true,
            HintText = "{=AIL_f683eca5}In each town's menu, \"Visit the hospital\": they tell you about the diseases going around, treat you, your companions, your wounded or sick troops and your prisoners (for a fee; cheaper in prosperous towns), and sell prevention so your men fall ill less for a few days.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Diseases")]
        public bool EnableHospital { get; set; } = true;

        [SettingPropertyInteger("{=AIL_29e73294}Maximum effect of an event on a market's prices (%)", 0, 80, Order = 1, RequireRestart = false,
            HintText = "{=AIL_2fd476bb}An event can raise or lower the prices of an entire town or village market while it lasts (shortage, panic, abundance). 0 = events don't touch prices.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int EffectMaxPricePercent { get; set; } = 35;

        [SettingPropertyBool("{=AIL_94c1e5dd}Unique characters created by the AI", Order = 1, RequireRestart = false,
            HintText = "{=AIL_ce8ae4a7}From time to time someone new, invented by the AI for this saved game, arrives in a nearby town (a fugitive physician, a mercenary captain, a preacher…), with name, trade, personality and motive. They live like a wanderer: you can look for them and take them into your service. One request per character.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool EnableUniqueCharacters { get; set; } = true;

        [SettingPropertyInteger("{=AIL_cf0af550}Days between unique characters", 3, 120, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int UniqueCharacterDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_160a2e2f}Maximum living unique characters", 1, 60, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int UniqueCharacterMax { get; set; } = 15;

        [SettingPropertyBool("{=AIL_98b07be9}Chronicle when someone you knew dies", Order = 1, RequireRestart = false,
            HintText = "{=AIL_d49df131}When a character you have talked to several times dies, the AI writes their chronicle (who they were, what there was between you, how it ended). It appears on screen and stays in News. One request per chronicle.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool EnableChronicles { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e3a1d50c}Chronicle: minimum chats with the deceased", 1, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int ChronicleMinTalks { get; set; } = 3;

        [SettingPropertyBool("{=AIL_d6f2ec0b}Training in the arena", Order = 1, RequireRestart = true,
            HintText = "{=AIL_b3ac06a3}In each town's arena, \"Train your troops\": you pay, 8 hours pass and your healthy soldiers gain experience (more for novices). Some may get injured. Each arena takes a few days to admit you again.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public bool EnableArenaTraining { get; set; } = true;

        [SettingPropertyInteger("{=AIL_11a9a28f}Arena: base experience per soldier", 20, 2000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int ArenaTrainingXp { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_fd5decd8}Arena: cost (% of the normal price)", 10, 500, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int ArenaTrainingCostPercent { get; set; } = 100;

        [SettingPropertyInteger("{=AIL_d77a8668}Arena: injury chance per soldier (%)", 0, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int ArenaTrainingInjuryPercent { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_fde1c2b2}Arena: hours until you can repeat in the same one", 0, 720, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Actions")]
        public int ArenaTrainingCooldownHours { get; set; } = 72;

        [SettingPropertyBool("{=AIL_4d649e87}Secrets born from history", Order = 1, RequireRestart = false,
            HintText = "{=AIL_9b8bd5de}The AI invents the secrets from the real people of each kingdom (their houses, weddings, debts, grudges) and from the events as they happen. Each one concerns specific people and is known by a few, who only let it out with trust. Uses one request per kingdom, spread one per game day at the start of the saved game.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool StorySecrets { get; set; } = true;

        [SettingPropertyInteger("{=AIL_2504fc22}Origin secrets per kingdom", 1, 10, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int StorySecretsPerKingdom { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_da62d72b}Maximum living secrets in the saved game", 10, 400, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int StorySecretsMax { get; set; } = 120;

        [SettingPropertyInteger("{=AIL_df85af91}Secrets a character has in mind when talking", 1, 10, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int StorySecretsInPrompt { get; set; } = 4;

        [SettingPropertyBool("{=AIL_e3c37efa}Plus factory secrets (world_data\\secrets.json)", Order = 1, RequireRestart = false,
            HintText = "{=AIL_5a6d8aee}Off by default. On: the generic secrets written in that file are also handed out at random (you can put your own there).")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool PresetSecrets { get; set; } = false;

        [SettingPropertyBool("{=AIL_9e2fda6f}Events affect clans", Order = 1, RequireRestart = false,
            HintText = "{=AIL_e9f870fb}An event can stain or exalt the name of a specific clan (yours too): its income drops or rises by a percentage for a few days. It shows in the clan's income breakdown and in News.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool EnableClanEffects { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d02abd4c}Maximum effect on a clan's income (%)", 5, 60, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int ClanEffectMaxPercent { get; set; } = 30;

        [SettingPropertyBool("{=AIL_96226daa}What you do and say gets people talking", Order = 1, RequireRestart = false,
            HintText = "{=AIL_750e804a}An aggression by you in the street, a death, an arrest, or a weighty rumor you set rolling in a chat, becomes a world event with its news (and with effects on the place if there was blood). It only tells what the witnesses could have known. It is one request to the AI per event.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool IncidentEvents { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d53be13f}Minimum importance to provoke a reaction", 1, 4, Order = 1, RequireRestart = false,
            HintText = "{=AIL_303700aa}1 = any local event, 2 = court matters, 3 = what shakes a kingdom, 4 = only what shakes the world.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int ReactiveMinImportance { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_586abbc0}Minimum game hours between reactions", 1, 240, Order = 1, RequireRestart = false,
            HintText = "{=AIL_6a2934ef}If several things happen in a row, they are combined into a single reaction.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int ReactiveMinHoursBetween { get; set; } = 8;

        [SettingPropertyBool("{=AIL_292485a4}Background events (when nothing has happened)", Order = 1, RequireRestart = false,
            HintText = "{=AIL_613c43b2}Besides reactions, from time to time the AI writes what is brewing on its own: intrigues, shortages, omens, unrest.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool BackgroundEvents { get; set; } = true;

        [SettingPropertyInteger("{=AIL_18b82bcb}Background events: every how many days (approx.)", 1, 100, Order = 1, RequireRestart = false,
            HintText = "{=AIL_d43bd671}It doesn't fall like clockwork: the actual interval varies between 60 % and 140 % of this value.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int WorldEventsIntervalDays { get; set; } = 7;

        [SettingPropertyInteger("{=AIL_c0d9e15c}Events per batch", 1, 4, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int WorldEventsPerGeneration { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_072e561a}Minimum length of an event (characters)", 80, 800, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int WorldEventMinLength { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_5b79eb8c}Maximum length of an event (characters)", 200, 1400, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int WorldEventMaxLength { get; set; } = 600;

        [SettingPropertyInteger("{=AIL_dd736c20}Days an event stays \"up in the air\"", 5, 120, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int WorldEventLifespanDays { get; set; } = 30;

        [SettingPropertyInteger("{=AIL_b32fb45c}Notify on screen from importance", 1, 5, Order = 6, RequireRestart = false,
            HintText = "{=AIL_580cc0e5}1 = all, 4 = only those that shake the world, 5 = never.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int WorldEventNotifyImportance { get; set; } = 2;

        [SettingPropertyBool("{=AIL_c69c977f}Economic effects of events", Order = 7, RequireRestart = false,
            HintText = "{=AIL_44600a27}An event can change, each day for a while, the prosperity, food, security and loyalty of a real settlement.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool EnableEconomicEffects { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d37eb730}Maximum effect: prosperity per day", 0, 200, Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int EffectMaxProsperityPerDay { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_0696ca62}Maximum effect: food per day", 0, 100, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int EffectMaxFoodPerDay { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_b8d972a1}Maximum effect: security per day", 0, 20, Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int EffectMaxSecurityPerDay { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_b700fc3c}Maximum effect: loyalty per day", 0, 20, Order = 11, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int EffectMaxLoyaltyPerDay { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_fc7ff1ee}Maximum effect: duration (days)", 1, 120, Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int EffectMaxDays { get; set; } = 20;

        [SettingPropertyBool("{=AIL_d86408df}AI diplomacy of kings (replaces the game's own)", Order = 20, RequireRestart = false,
            HintText = "{=AIL_6c0f99f2}The game's clans stop proposing wars, peace, alliances and trade on their own. Kings decide them through public declarations. Requires world events to be enabled.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool EnableDiplomacy { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4ae4ed78}Minimum days between declarations by the same king", 1, 60, Order = 21, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int MinDaysBetweenStatements { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_abd77d03}Days during which an event can provoke declarations", 1, 60, Order = 22, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int EngagementDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_0e7aa239}Daily chance of answering a proposal (%)", 5, 100, Order = 23, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int ResponsePressurePercent { get; set; } = 40;

        [SettingPropertyInteger("{=AIL_46407e9d}Days an unanswered proposal lasts", 5, 120, Order = 24, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int ProposalLifetimeDays { get; set; } = 30;

        [SettingPropertyInteger("{=AIL_ab45cede}Minimum length of a declaration", 80, 800, Order = 25, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int StatementMinLength { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_9ba9f600}Maximum length of a declaration", 200, 1600, Order = 26, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int StatementMaxLength { get; set; } = 700;

        [SettingPropertyInteger("{=AIL_0d849eb1}Maximum shift in stance between crowns per declaration", 1, 50, Order = 27, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int MaxStanceChange { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_b57ad932}Maximum daily tribute", 10, 20000, Order = 28, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int MaxDailyTribute { get; set; } = 2000;

        [SettingPropertyInteger("{=AIL_e9d88202}Maximum reparations", 100, 2000000, Order = 29, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int MaxReparations { get; set; } = 100000;

        [SettingPropertyInteger("{=AIL_ffcd4d39}Crown tax: minimum (%)", -50, 0, Order = 30, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int KingdomTaxMinPercent { get; set; } = -10;

        [SettingPropertyInteger("{=AIL_9ff27d57}Crown tax: maximum (%)", 0, 100, Order = 31, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int KingdomTaxMaxPercent { get; set; } = 30;

        [SettingPropertyBool("{=AIL_cddce6e0}Kings can declare war on you if you rule", Order = 32, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool AllowAiWarOnPlayer { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7d2f023}An expelled clan is banished", Order = 60, RequireRestart = false,
            HintText = "{=AIL_b7d2f024}A clan cast out of a kingdom cannot swear to it, defect to it or serve it as mercenaries until its ruler pardons it. Applies to you too.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool BanishedClansCannotReturn { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7d2f025}Banishment ends when the kingdom changes ruler", Order = 61, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool BanishmentEndsWithRuler { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7d2f026}Keep landless clans you know alive", Order = 62, RequireRestart = false,
            HintText = "{=AIL_b7d2f027}The game dissolves a clan with no kingdom and no land after a while, killing its members. With this on, it spares banished clans and clans with people you have talked to.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool KeepKnownClansAlive { get; set; } = true;

        [SettingPropertyBool("{=AIL_76e631ee}Your king can expel you or confiscate a fief from you", Order = 33, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool AllowAiExpelPlayer { get; set; } = false;

        [SettingPropertyBool("{=AIL_015c810d}Laws proclaimed by kings", Order = 34, RequireRestart = false,
            HintText = "{=AIL_ef8b7c72}A king can create a law of their own with small daily effects in all their towns and castles (loyalty, prosperity, security, food), and repeal it.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool EnableKingdomLaws { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a12df7a9}Laws in force per kingdom", 1, 10, Order = 35, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int MaxLawsPerKingdom { get; set; } = 3;

        [SettingPropertyBool("{=AIL_5abeb44e}Clan rebellions", Order = 36, RequireRestart = false,
            HintText = "{=AIL_cbd20bc6}An event can make a vassal clan rise against its crown (never yours nor the ruling clan), if it truly gets along badly with its king.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public bool AllowClanRebellions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_db9bfa6a}Maximum relation with their king to rebel", -100, 20, Order = 37, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int RebellionMaxRelation { get; set; } = -15;

        [SettingPropertyInteger("{=AIL_84f97df8}War weariness: recovery per day at peace (x10)", 0, 100, Order = 40, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int FatigueRecoveryPerDayX10 { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_7948ed38}War weariness: per day and open war (x10)", 0, 100, Order = 41, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int FatiguePerWarDayX10 { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_b7d8f762}War weariness: per lost battle (x10)", 0, 200, Order = 42, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int FatiguePerBattleLostX10 { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_7c23f3b8}War weariness: per dead noble", 0, 50, Order = 43, RequireRestart = false,
            HintText = "{=AIL_47dd15e0}A captured noble counts as a third.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int FatiguePerLordKilled { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_0e578525}War weariness: per lost town or castle", 0, 50, Order = 44, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int FatiguePerSettlementLost { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_a4b23033}Minimum real seconds between world requests", 5, 600, Order = 50, RequireRestart = false,
            HintText = "{=AIL_2924e794}So as not to spend quota when you advance time quickly.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public int WorldAiMinSecondsBetweenCalls { get; set; } = 25;

        [SettingPropertyText("{=AIL_74ca054c}Provider for the world and diplomacy", Order = 51, RequireRestart = false,
            HintText = "{=AIL_e2132343}Name of a config.json provider. Empty = the normal chain.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public string ProviderWorld { get; set; } = "";

        [SettingPropertyButton("{=AIL_6a025b78}Generate events now", Content = "{=AIL_63621315}Generate", Order = 52, RequireRestart = false,
            HintText = "{=AIL_67d30a9a}With a saved game loaded: requests a batch of events right now (they appear when you return to the map).")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public Action ForceWorldEvents { get; set; } = () => Later(() => World.WorldSim.GenerateEvents(true));

        [SettingPropertyButton("{=AIL_4eb0fc2d}Make a declaration (if you rule)", Content = "{=AIL_c7acc8b1}Declare", Order = 53, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. World and diplomacy")]
        public Action PlayerDeclaration { get; set; } = () => Later(World.Diplomacy.PlayerStatement);

        // ------------------------------------------------------------ Editor
        [SettingPropertyButton("{=AIL_1cb10851}Open the AI Legacy editor", Content = "{=AIL_5a216c13}Open", Order = 0, RequireRestart = false,
            HintText = "{=AIL_ab8aaa3e}Opens the editor in your browser: world, secrets, NPCs (personality, memory, instructions), events and backups. Requires a saved game to be loaded.")]
        [SettingPropertyGroup("{=AIL_1d38423b}9. Editor", GroupOrder = 9)]
        public Action OpenEditor { get; set; } = () => Web.EditorServer.OpenInBrowser();

        [SettingPropertyInteger("{=AIL_2fc52b1a}Editor port", 1024, 65000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_1d38423b}9. Editor")]
        public int EditorPort { get; set; } = 17890;

        [SettingPropertyBool("{=AIL_c3a7f1e5}Background tasks on free providers only", Order = 10, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e6}Ambient talk, world events, summaries and other tasks you are not waiting for never use a paid provider while a free one exists. The paid one is kept in reserve for dialogue.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public bool BackgroundOnFreeProviders { get; set; } = true;

        [SettingPropertyInteger("{=AIL_c3a7f1e7}Daily spending cap (US cents, 0 = no cap)", 0, 5000, Order = 11, RequireRestart = false,
            HintText = "{=AIL_c3a7f1e8}Once that amount is reached in a day (real day, not game day) paid providers stop being used until tomorrow. Only providers with a price set under 0. AI keys are counted.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public int DailySpendCapCents { get; set; } = 0;

        [SettingPropertyBool("{=AIL_7ff37c4e}Save request/response in logs", Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. AI")]
        public bool DebugLogBodies { get; set; } = false;

        // ------------------------------------------------------------ Importar
        [SettingPropertyButton("{=AIL_31311890}Import memories from AIInfluence", Content = "{=AIL_faa80647}Import", Order = 0, RequireRestart = false,
            HintText = "{=AIL_d4db6271}With a saved game loaded: brings in from AIInfluence the memory, personality, memories and instructions of the NPCs of THIS campaign. It doesn't overwrite anyone who already has memory in AI Legacy.")]
        [SettingPropertyGroup("{=AIL_6cf8197b}8. Import", GroupOrder = 8)]
        public Action ImportAIInfluence { get; set; } = () => Import.AIInfluenceImporter.ImportCurrentCampaign();

        [SettingPropertyButton("{=AIL_376f0803}Undo the import", Content = "{=AIL_bdd858a5}Remove", Order = 1, RequireRestart = false,
            HintText = "{=AIL_20e63156}Removes from this campaign the memories that came from AIInfluence (for example, if you imported those of another saved game). The files are moved to a subfolder, not deleted.")]
        [SettingPropertyGroup("{=AIL_6cf8197b}8. Import")]
        public Action UndoImportAIInfluence { get; set; } = () => Import.AIInfluenceImporter.UndoImport();
    }

    /// <summary>Acceso cómodo a los ajustes, con valores por defecto si MCM aún no está listo.</summary>
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
