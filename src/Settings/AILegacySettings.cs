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
        [SettingPropertyBool("{=AIL_7c7093eb}Usar este proveedor", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Los proveedores se prueban en orden (1, 2, 3, 4): si el primero falla o se queda sin cupo, se pasa al siguiente. Las claves se guardan en Documentos, en Mount and Blade II Bannerlord / Configs / ModSettings, no en la carpeta del mod.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_208da3de}Proveedor 1", GroupOrder = 0)]
        public bool P1Enabled { get; set; } = true;

        [SettingPropertyText("{=AIL_5794d85d}Servicio", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Escribe uno: gemini, openai, groq, deepseek, openrouter, ollama (local, sin clave) u otro (entonces rellena la dirección).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_208da3de}Proveedor 1")]
        public string P1Service { get; set; } = "gemini";

        [SettingPropertyText("{=AIL_4868da9a}Clave (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Tu clave de ese servicio. Se ve en claro en esta pantalla: no la enseñes en capturas.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_208da3de}Proveedor 1")]
        public string P1Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Modelo (vacío = el recomendado)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Nombre exacto del modelo. Vacío: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat o llama3.1 según el servicio.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_208da3de}Proveedor 1")]
        public string P1Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Dirección (solo si el servicio es «otro»)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}Dirección base compatible con OpenAI, p. ej. https://mi-servidor/v1. Para los servicios conocidos se pone sola.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_208da3de}Proveedor 1")]
        public string P1Url { get; set; } = "";

        [SettingPropertyBool("{=AIL_7c7093eb}Usar este proveedor", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Los proveedores se prueban en orden (1, 2, 3, 4): si el primero falla o se queda sin cupo, se pasa al siguiente. Las claves se guardan en Documentos, en Mount and Blade II Bannerlord / Configs / ModSettings, no en la carpeta del mod.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1f8da24b}Proveedor 2")]
        public bool P2Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Servicio", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Escribe uno: gemini, openai, groq, deepseek, openrouter, ollama (local, sin clave) u otro (entonces rellena la dirección).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1f8da24b}Proveedor 2")]
        public string P2Service { get; set; } = "openai";

        [SettingPropertyText("{=AIL_4868da9a}Clave (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Tu clave de ese servicio. Se ve en claro en esta pantalla: no la enseñes en capturas.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1f8da24b}Proveedor 2")]
        public string P2Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Modelo (vacío = el recomendado)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Nombre exacto del modelo. Vacío: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat o llama3.1 según el servicio.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1f8da24b}Proveedor 2")]
        public string P2Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Dirección (solo si el servicio es «otro»)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}Dirección base compatible con OpenAI, p. ej. https://mi-servidor/v1. Para los servicios conocidos se pone sola.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1f8da24b}Proveedor 2")]
        public string P2Url { get; set; } = "";

        [SettingPropertyBool("{=AIL_7c7093eb}Usar este proveedor", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Los proveedores se prueban en orden (1, 2, 3, 4): si el primero falla o se queda sin cupo, se pasa al siguiente. Las claves se guardan en Documentos, en Mount and Blade II Bannerlord / Configs / ModSettings, no en la carpeta del mod.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1e8da0b8}Proveedor 3")]
        public bool P3Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Servicio", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Escribe uno: gemini, openai, groq, deepseek, openrouter, ollama (local, sin clave) u otro (entonces rellena la dirección).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1e8da0b8}Proveedor 3")]
        public string P3Service { get; set; } = "";

        [SettingPropertyText("{=AIL_4868da9a}Clave (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Tu clave de ese servicio. Se ve en claro en esta pantalla: no la enseñes en capturas.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1e8da0b8}Proveedor 3")]
        public string P3Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Modelo (vacío = el recomendado)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Nombre exacto del modelo. Vacío: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat o llama3.1 según el servicio.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1e8da0b8}Proveedor 3")]
        public string P3Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Dirección (solo si el servicio es «otro»)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}Dirección base compatible con OpenAI, p. ej. https://mi-servidor/v1. Para los servicios conocidos se pone sola.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_1e8da0b8}Proveedor 3")]
        public string P3Url { get; set; } = "";

        [SettingPropertyBool("{=AIL_7c7093eb}Usar este proveedor", Order = 0, RequireRestart = false,
            HintText = "{=AIL_454f39f4}Los proveedores se prueban en orden (1, 2, 3, 4): si el primero falla o se queda sin cupo, se pasa al siguiente. Las claves se guardan en Documentos, en Mount and Blade II Bannerlord / Configs / ModSettings, no en la carpeta del mod.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_258dabbd}Proveedor 4")]
        public bool P4Enabled { get; set; } = false;

        [SettingPropertyText("{=AIL_5794d85d}Servicio", Order = 1, RequireRestart = false,
            HintText = "{=AIL_0504fb72}Escribe uno: gemini, openai, groq, deepseek, openrouter, ollama (local, sin clave) u otro (entonces rellena la dirección).")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_258dabbd}Proveedor 4")]
        public string P4Service { get; set; } = "";

        [SettingPropertyText("{=AIL_4868da9a}Clave (API key)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_1068028c}Tu clave de ese servicio. Se ve en claro en esta pantalla: no la enseñes en capturas.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_258dabbd}Proveedor 4")]
        public string P4Key { get; set; } = "";

        [SettingPropertyText("{=AIL_ad9e9552}Modelo (vacío = el recomendado)", Order = 3, RequireRestart = false,
            HintText = "{=AIL_18fd1956}Nombre exacto del modelo. Vacío: gemini-3.5-flash-lite, gpt-5.6-luna, llama-3.3-70b-versatile, deepseek-chat o llama3.1 según el servicio.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_258dabbd}Proveedor 4")]
        public string P4Model { get; set; } = "";

        [SettingPropertyText("{=AIL_eccf1cc9}Dirección (solo si el servicio es «otro»)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5e0df453}Dirección base compatible con OpenAI, p. ej. https://mi-servidor/v1. Para los servicios conocidos se pone sola.")]
        [SettingPropertyGroup("{=AIL_6c7f2b62}0. Claves de IA/{=AIL_258dabbd}Proveedor 4")]
        public string P4Url { get; set; } = "";

        /// <summary>Los proveedores escritos en MCM, en orden, ya con los detalles técnicos de cada servicio.</summary>
        public System.Collections.Generic.List<Config.ProviderConfig> McmProviders()
        {
            var list = new System.Collections.Generic.List<Config.ProviderConfig>();
            void Add(int n, bool on, string service, string key, string model, string url)
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
                list.Add(p);
            }
            Add(1, P1Enabled, P1Service, P1Key, P1Model, P1Url); Add(2, P2Enabled, P2Service, P2Key, P2Model, P2Url);
            Add(3, P3Enabled, P3Service, P3Key, P3Model, P3Url); Add(4, P4Enabled, P4Service, P4Key, P4Model, P4Url);
            return list;
        }

        /// <summary>Cambia cuando tocas algo de los proveedores en MCM (para recargarlos al momento).</summary>
        public string McmProvidersSignature() =>
            string.Join("|", P1Enabled, P1Service, P1Key, P1Model, P1Url, P2Enabled, P2Service, P2Key, P2Model, P2Url, P3Enabled, P3Service, P3Key, P3Model, P3Url, P4Enabled, P4Service, P4Key, P4Model, P4Url);

        // ------------------------------------------------------------ General
        [SettingPropertyDropdown("{=AIL_e250598b}Idioma de la interfaz (Interface language)", Order = -2, RequireRestart = false,
            HintText = "{=AIL_5fd4967b}Idioma de los menús, avisos y ventanas de AI Legacy. «Auto» usa el idioma del juego. Para ver este menú en el idioma nuevo, sal de las opciones y vuelve a entrar.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public MCM.Common.Dropdown<string> UiLanguage { get; set; } = new MCM.Common.Dropdown<string>(L.Names, 0);

        /// <summary>Código del idioma elegido: auto, es, en, de, fr, zh…</summary>
        public string UiLanguageCode()
        {
            int i = UiLanguage?.SelectedIndex ?? 0;
            return i >= 0 && i < L.Codes.Length ? L.Codes[i] : "auto";
        }

        [SettingPropertyText("{=AIL_e405f75e}Idioma de las respuestas de los NPC", Order = -1, RequireRestart = false,
            HintText = "{=AIL_500ac9b1}Vacío: el mismo idioma que la interfaz. Si quieres otro, escribe su nombre en inglés. Ej: Spanish (Spain), English, Catalan, Italian.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General", GroupOrder = 1)]
        public string ReplyLanguage { get; set; } = "";

        /// <summary>Idioma en que escribe la IA (nombre en inglés).</summary>
        public string Language => string.IsNullOrWhiteSpace(ReplyLanguage) ? L.EnglishName : ReplyLanguage.Trim();

        [SettingPropertyInteger("{=AIL_91d66f05}Frases mínimas por respuesta", 1, 6, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int ReplyMinSentences { get; set; } = 1;

        [SettingPropertyInteger("{=AIL_d22a5c23}Frases máximas por respuesta", 1, 12, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int ReplyMaxSentences { get; set; } = 4;

        [SettingPropertyBool("{=AIL_90796905}Charla libre con gente del pueblo", Order = 3, RequireRestart = false,
            HintText = "{=AIL_8201313c}Añade la charla libre a aldeanos, gente de la ciudad y de la taberna (no héroes).")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool EnableResidents { get; set; } = true;

        [SettingPropertyInteger("{=AIL_abf0a3ef}Nivel de clan para que te conozcan por fama", 0, 7, Order = 4, RequireRestart = false,
            HintText = "{=AIL_a3cd73e3}A partir de este nivel de clan, cualquier NPC sabe quién eres sin presentarte.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int FameTierKnownByName { get; set; } = 4;

        [SettingPropertyBool("{=AIL_62e94015}Pensamiento interno antes de responder", Order = 5, RequireRestart = false,
            HintText = "{=AIL_344592e1}El NPC razona en privado (qué sabe, qué quiere, si una acción tiene sentido) antes de hablar. Mejores respuestas, algo más lento.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool EnableThoughts { get; set; } = true;

        [SettingPropertyBool("{=AIL_758ef766}Conversación dentro del diálogo del juego", Order = 7, RequireRestart = false,
            HintText = "{=AIL_d279f208}Activado: hablas desde el cuadro de diálogo del juego (escribir → esperar → ver respuesta). Desactivado: ventana de chat propia.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool UseDialogStyle { get; set; } = true;

        [SettingPropertyBool("{=AIL_25c835cb}Usar asteriscos para gestos", Order = 8, RequireRestart = false, HintText = "{=AIL_1586f13c}Los NPC describen gestos entre *asteriscos*.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool UseAsterisks { get; set; } = true;

        [SettingPropertyBool("{=AIL_f8db9623}Botones en el mapa (Noticias / Memorias)", Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool ShowMapButtons { get; set; } = true;

        [SettingPropertyBool("{=AIL_53a2324c}Los compañeros te reconocen siempre", Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool CompanionsRecognize { get; set; } = true;

        [SettingPropertyBool("{=AIL_16443f4c}Los nobles de tu reino te reconocen", Order = 11, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool KingdomRecognize { get; set; } = true;

        [SettingPropertyBool("{=AIL_d2c3f48b}Incluir amigos y enemigos del NPC", Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool IncludeFriends { get; set; } = true;

        [SettingPropertyInteger("{=AIL_77262a54}Máximo de amigos en el prompt", 0, 15, Order = 13, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int MaxFriends { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_bce32dcd}Máximo de enemigos en el prompt", 0, 15, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public int MaxEnemies { get; set; } = 5;

        [SettingPropertyBool("{=AIL_08baf922}La memoria va con la partida guardada", Order = 15, RequireRestart = false,
            HintText = "{=AIL_68f3dc4c}Activado: al cargar una partida, los NPC recuerdan lo que sabían cuando la guardaste. Desactivado: recuerdan siempre lo último, aunque cargues una partida anterior.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool MemoryFollowsSave { get; set; } = true;

        [SettingPropertyBool("{=AIL_a9b74d1b}Quitar «Hablar» de la lista de personajes", Order = 17, RequireRestart = false,
            HintText = "{=AIL_cefb1dfd}En la lista de personajes de una ciudad, castillo o taberna solo queda «Visitar». Hay que ir hasta la persona y hablarle en la escena.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool BlockQuickTalk { get; set; } = true;

        [SettingPropertyBool("{=AIL_68a7d597}Modo de pruebas: NPC complacientes", Order = 16, RequireRestart = false,
            HintText = "{=AIL_e4267ade}SOLO PARA PROBAR. Los NPC aceptan casi cualquier petición razonable (seguirte, encargos, regalos, tropas) sin que tengas que ganarte antes su confianza. Desactívalo para jugar.")]
        [SettingPropertyGroup("{=AIL_dda0e8ca}1. General")]
        public bool TestCompliantMode { get; set; } = false;

        // ------------------------------------------------------------ Relación
        [SettingPropertyBool("{=AIL_b59cea89}Relación personal con cada NPC", Order = -1, RequireRestart = false,
            HintText = "{=AIL_2839a892}Activado: tu relación es con esa persona. Desactivado (como el juego): con un noble, tu relación es la del jefe de su clan.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public bool PersonalRelation { get; set; } = true;

        [SettingPropertyInteger("{=AIL_edd26e08}Cambio máximo de relación por respuesta", 0, 10, Order = 0, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance", GroupOrder = 2)]
        public int MaxRelationChangePerReply { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_1425de35}Cambio máximo de relación por conversación", 0, 50, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int MaxRelationChangePerConversation { get; set; } = 10;

        // ------------------------------------------------------------ Acciones
        [SettingPropertyBool("{=AIL_fd3f245a}Acciones activadas", Order = 0, RequireRestart = false,
            HintText = "{=AIL_1f1faf3a}Si se desactiva, los NPC solo hablan: no dan oro, no se unen, etc.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones", GroupOrder = 3)]
        public bool EnableActions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_256e88f0}Oro máximo por acción", 0, 1000000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int MaxGoldPerAction { get; set; } = 20000;

        [SettingPropertyBool("{=AIL_8ff3713b}Diplomacia (guerra / paz entre reyes)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_14250983}Un rey puede declararte la guerra o firmar la paz contigo si tú también eres rey.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowDiplomacy { get; set; } = true;

        [SettingPropertyBool("{=AIL_4ce0146b}Vasallaje y mercenarios", Order = 3, RequireRestart = false,
            HintText = "{=AIL_a30867c0}Un líder de clan puede jurarte lealtad o servirte como mercenario si eres rey.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowFealty { get; set; } = true;

        [SettingPropertyBool("{=AIL_26eb718e}Matrimonio", Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowMarriage { get; set; } = true;

        [SettingPropertyInteger("{=AIL_c509c21c}Relación mínima para aceptar matrimonio", 0, 100, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int MinRelationForMarriage { get; set; } = 30;

        [SettingPropertyBool("{=AIL_b85090ef}Órdenes en el mapa", Order = 6, RequireRestart = false,
            HintText = "{=AIL_65ab580e}Los NPC que mandan un grupo pueden aceptar seguirte, ir a un sitio, esperar, patrullar, atacar, asediar, saquear o unirse a tu ejército. También por carta.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool EnableMapOrders { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e28e2ffe}Duración normal de una orden (días)", 1, 30, Order = 7, RequireRestart = false,
            HintText = "{=AIL_904a67c2}Cuánto te siguen, esperan o patrullan si no se acuerda otra cosa. También lo que te esperan en un sitio al quedar contigo.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int OrderDefaultDays { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_12efd9a7}Duración máxima de una orden (días)", 1, 60, Order = 8, RequireRestart = false,
            HintText = "{=AIL_cb272b11}Mientras cumplen una orden no hacen su vida normal (no compran comida ni reclutan), así que no conviene alargarlo.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int OrderMaxDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_ac7ff171}Radio para atacar grupos enemigos", 5, 200, Order = 9, RequireRestart = false,
            HintText = "{=AIL_35e216d6}Distancia en el mapa a la que un NPC ve enemigos a los que puede ir a atacar si se lo pides.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int OrderAttackRadius { get; set; } = 40;

        [SettingPropertyBool("{=AIL_5da6bc58}Ejércitos (unirse al tuyo, reunir el suyo, dejarlo)", Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowArmies { get; set; } = true;

        // ------------------------------------------------------------ Confianza y romance
        [SettingPropertyBool("{=AIL_71329b44}Confianza (aparte de la relación)", Order = 2, RequireRestart = false,
            HintText = "{=AIL_f8007f6a}Cada NPC lleva la cuenta de cuánto se fía de ti. Sin confianza suficiente no cuenta secretos ni información delicada.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public bool EnableTrust { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4cef6d19}Cambio máximo de confianza por respuesta", 1, 20, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int MaxTrustChangePerReply { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_1e4baeec}Confianza necesaria para contar secretos", 0, 100, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int TrustForSecrets { get; set; } = 40;

        [SettingPropertyInteger("{=AIL_cd6fe758}Confianza que pierdes si te pilla una mentira", 0, 50, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int TrustLostPerLie { get; set; } = 10;

        [SettingPropertyBool("{=AIL_a4403b32}Romance", Order = 6, RequireRestart = false,
            HintText = "{=AIL_7d3d5b34}Nivel de romance con cada NPC: sube con el cortejo, se enfría si no os veis, y cuenta para el matrimonio.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public bool EnableRomance { get; set; } = true;

        [SettingPropertyBool("{=AIL_276e2745}Boda con compañeros, errantes y notables", Order = 7, RequireRestart = false,
            HintText = "{=AIL_94ae59ca}El juego solo deja casarse entre nobles. Con esto, un compañero, un errante o un notable al que hayas enamorado puede aceptar: entra antes en tu casa como noble y deja de ocupar sitio de compañero.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool MarryCommoners { get; set; } = true;

        [SettingPropertyBool("{=AIL_170fa0ef}Romance con personas casadas", Order = 7, RequireRestart = false,
            HintText = "{=AIL_9e5a3fd7}Apagado: quien está casado (o tú, si lo estás) no acepta otra boda. Encendido: puede aceptarla, y el matrimonio anterior termina.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool RomanceWithMarried { get; set; } = false;

        [SettingPropertyInteger("{=AIL_f0c471e9}Romance que impide que el juego le case con otro (0 = no impedir)", 0, 100, Order = 7, RequireRestart = false,
            HintText = "{=AIL_bdf82f15}A partir de este nivel de romance contigo, el juego no casa por su cuenta a ese personaje con otra persona.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int RomanceProtectsFromMarriage { get; set; } = 15;

        [SettingPropertyBool("{=AIL_09d60746}Romance entre personas del mismo sexo", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public bool RomanceSameSex { get; set; } = false;

        [SettingPropertyInteger("{=AIL_c5ca3b43}Cambio máximo de romance por respuesta", 1, 20, Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int MaxRomanceChangePerReply { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_d582953e}Romance necesario para aceptar matrimonio", 0, 100, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int RomanceForMarriage { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_635ff613}Días sin veros para que el romance se enfríe", 1, 120, Order = 10, RequireRestart = false,
            HintText = "{=AIL_a643593e}Pasados estos días sin hablar ni escribiros, el romance baja 1 punto al día (con tu cónyuge no baja de 40).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int RomanceDecayAfterDays { get; set; } = 14;

        [SettingPropertyBool("{=AIL_0ea1a4c8}Intimidad", Order = 11, RequireRestart = false,
            HintText = "{=AIL_49588f47}Con romance alto, el NPC puede aceptar pasar la noche contigo (nunca se describe). Entre cónyuges puede acabar en embarazo.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public bool AllowIntimacy { get; set; } = true;

        [SettingPropertyInteger("{=AIL_22b0d9b9}Romance necesario para la intimidad", 0, 100, Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int RomanceForIntimacy { get; set; } = 70;

        [SettingPropertyInteger("{=AIL_6b1ca542}Probabilidad de embarazo (%)", 0, 100, Order = 13, RequireRestart = false,
            HintText = "{=AIL_6ac2e619}Solo entre cónyuges (el juego necesita un padre conocido).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int PregnancyChancePercent { get; set; } = 25;

        [SettingPropertyBool("{=AIL_a1c0e5d1}Ánimo por causas", Order = 20, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d2}El ánimo de cada personaje sale de lo que le ha pasado, varias cosas a la vez: una batalla ganada o perdida, un torneo, estar preso o herido, un familiar o un amigo muerto, una guerra recién declarada, un parto. Cada una dura unos días. Apagado: solo cuenta cómo le dejó la última charla.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public bool EnableMoodCauses { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a1c0e5d3}Duración de las causas del ánimo (%)", 25, 400, Order = 21, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d4}100 = lo normal: un torneo se saborea 5 días, una batalla 3 o 4, el luto por un familiar dura 14 y por un amigo 10. 200 = el doble.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int MoodDurationPercent { get; set; } = 100;

        [SettingPropertyBool("{=AIL_a1c0e5d5}Ciclo femenino", Order = 22, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d6}Las mujeres de 13 a 47 años tienen ciclo: les cambia el ánimo según el día (a unas mucho y a otras casi nada) y cambia la probabilidad de embarazo, también la del juego. No hay ciclo durante el embarazo ni después del parto. Ella sabe en qué día está.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public bool EnableFemaleCycle { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a1c0e5d7}Días que dura el ciclo", 15, 40, Order = 23, RequireRestart = false,
            HintText = "{=AIL_a1c0e5d8}Días de campaña. Un año del juego tiene 84 días, así que 21 son cuatro ciclos al año.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int FemaleCycleDays { get; set; } = 21;

        [SettingPropertyBool("{=AIL_6746c998}Traspaso de tropas y prisioneros", Order = 11, RequireRestart = false,
            HintText = "{=AIL_c0668e57}Un NPC con grupo puede cederte soldados o prisioneros, o aceptar los que tú le ofrezcas.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowTransfers { get; set; } = true;

        [SettingPropertyInteger("{=AIL_86ee0cb9}Tropas máximas por traspaso", 1, 500, Order = 12, RequireRestart = false,
            HintText = "{=AIL_6a6bf769}Además, un NPC nunca cede más de un tercio de su grupo de una vez.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int MaxTroopsPerTransfer { get; set; } = 40;

        [SettingPropertyBool("{=AIL_3a0a45fd}Combate desde la charla", Order = 13, RequireRestart = false,
            HintText = "{=AIL_298b35b9}Peleas a puñetazos y duelos en las escenas, ataque y rendición de enemigos en el mapa, y llamar a la guardia.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowCombat { get; set; } = true;

        [SettingPropertyBool("{=AIL_731669e9}Gente del pueblo que se une a ti", Order = 14, RequireRestart = false,
            HintText = "{=AIL_f8fff764}Un vecino con el que has hablado de verdad puede dejar su vida y unirse a tu compañía: pasa a ser un personaje permanente, con su nombre, su carácter y su memoria. Su aspecto cambia al del nuevo personaje.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowPromotions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_ac508642}Frases mínimas habladas antes de que pueda unirse", 2, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int PromotionMinTurns { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_b40b4b3a}No repetir el mismo pago en (segundos)", 0, 900, Order = 13, RequireRestart = false,
            HintText = "{=AIL_800f61d9}A veces la IA repite en su siguiente respuesta un cobro o un pago que ya se hizo. El mismo importe, en el mismo sentido y con la misma persona no se vuelve a aplicar dentro de este tiempo. 0 = sin protección.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int GoldRepeatGuardSeconds { get; set; } = 240;

        [SettingPropertyBool("{=AIL_80912930}La guardia interviene", Order = 14, RequireRestart = false,
            HintText = "{=AIL_6d2b9329}Tras una pelea o un duelo en un asentamiento que no es tuyo, si hay un guardia cerca se acerca a pedirte cuentas y decide: dejarte ir, multarte o encerrarte.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowJustice { get; set; } = true;

        [SettingPropertyBool("{=AIL_d594c7d1}Pegar a cualquiera empieza una pelea", Order = 14, RequireRestart = false,
            HintText = "{=AIL_76fdb1de}En una ciudad, aldea o castillo, si sueltas un golpe con alguien justo delante empieza la pelea con él sin tener que hablarle: a puñetazos si vas con las manos vacías, en serio si llevas el arma fuera. Salen en su defensa sus parientes y, si hay acero, la guardia. Te lo guarda, sube tu mala fama y luego viene la guardia.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowStreetViolence { get; set; } = true;

        [SettingPropertyInteger("{=AIL_1c381bc9}Cuántos salen en defensa del agredido, como máximo", 0, 10, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int StreetFightHelpers { get; set; } = 4;

        [SettingPropertyBool("{=AIL_76ca0cb9}Pelea armada: la guardia llega por oleadas", Order = 14, RequireRestart = false,
            HintText = "{=AIL_73b83666}Si atacas a alguien con arma en una ciudad, los guardias cercanos van sumándose cada pocos segundos. Con varios caídos suena la alarma y acude toda la guardia.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool StreetGuardWaves { get; set; } = true;

        [SettingPropertyInteger("{=AIL_45d4f33e}Guardias por oleada", 1, 10, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int StreetGuardWaveSize { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_5689cbfa}Segundos entre oleadas", 3, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int StreetGuardWaveSeconds { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_eb1b8e7a}Guardias y defensores como máximo en una pelea", 2, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int StreetMaxGuards { get; set; } = 20;

        [SettingPropertyBool("{=AIL_c61991e6}Pelea armada: puedes llamar a tus tropas (Ctrl+X)", Order = 14, RequireRestart = false,
            HintText = "{=AIL_5b8786aa}Durante una pelea en la calle o en una aldea, Ctrl+X mete a diez de tus soldados y Ctrl+Alt+X a todos los que quepan. En interiores no entran. Los que caigan quedan heridos o muertos en tu partida.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool StreetSummonTroops { get; set; } = true;

        [SettingPropertyInteger("{=AIL_164cfae0}Soldados tuyos como máximo en una pelea de calle", 5, 100, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int StreetMaxOwnTroops { get; set; } = 30;

        [SettingPropertyBool("{=AIL_f264e0b3}Comercio de verdad con la gente del lugar", Order = 14, RequireRestart = false,
            HintText = "{=AIL_4a37fd4a}Mercaderes, artesanos, taberneros y aldeanos pueden venderte y comprarte mercancías en una charla, contra las existencias y los precios reales del mercado de ese lugar: cambian de manos las mercancías y el dinero.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowRealTrade { get; set; } = true;

        [SettingPropertyBool("{=AIL_109be563}Muerte por rol", Order = 14, RequireRestart = false,
            HintText = "{=AIL_cd7a8ef0}Un personaje puede morir como desenlace de una charla: si es tu prisionero y lo ejecutas diciéndolo claramente, si se quita la vida, o si se está muriendo de sus heridas. Muere de verdad en la partida.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowRoleplayDeath { get; set; } = true;

        [SettingPropertyBool("{=AIL_c949ab1f}Muerte por rol: en cualquier situación", Order = 14, RequireRestart = false,
            HintText = "{=AIL_c79b58e9}Apagado (recomendado): solo prisioneros tuyos y heridos. Encendido: la IA puede decidir la muerte de cualquier personaje si la escena lo pide, incluidos los de tu clan.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool RoleplayDeathAnywhere { get; set; } = false;

        [SettingPropertyBool("{=AIL_4f0d9e02}Los figurantes pueden pasar a ser personajes", Order = 14, RequireRestart = false,
            HintText = "{=AIL_7245c3fc}Un vecino de una escena, o uno de tus soldados, puede aceptar entrar a tu servicio como compañero (si te queda sitio) o quedar como alguien con nombre al que volver a encontrar. Conserva la cara, la edad y lo hablado contigo.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowPromotion { get; set; } = true;

        [SettingPropertyBool("{=AIL_b0a1e3a6}Hacen falta testigos", Order = 14, RequireRestart = false,
            HintText = "{=AIL_ced9d43e}Si pegas o matas a alguien sin que nadie vivo lo vea, nadie sabe que has sido tú: ni mala fama, ni guardia, ni tu nombre en las noticias. Si te ven pero no saben quién eres, se busca a «un forastero armado» y la mala fama es la mitad. Solo te señalan por tu nombre si algún testigo te conoce o si ya eres famoso.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool EnableWitnesses { get; set; } = true;

        [SettingPropertyInteger("{=AIL_8aab03a7}Testigos: hasta dónde se ve (metros)", 5, 80, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int WitnessRadius { get; set; } = 25;

        [SettingPropertyInteger("{=AIL_41faa86d}Testigos: nivel de clan a partir del cual todos te reconocen", 1, 7, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int WitnessFameTier { get; set; } = 3;

        [SettingPropertyBool("{=AIL_6e4f31b1}Los personajes pueden arrestarse entre ellos", Order = 14, RequireRestart = false,
            HintText = "{=AIL_3732e7fc}En una conversación de grupo en ciudad o castillo, un guardia o quien manda allí puede llevarse preso a otro personaje presente si hay motivo de peso (p. ej. le acusas de un crimen con pruebas a la vista). Queda preso de verdad, y te la guarda.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowNpcArrests { get; set; } = true;

        [SettingPropertyBool("{=AIL_fedffd7f}El señor del lugar te juzga", Order = 14, RequireRestart = false,
            HintText = "{=AIL_5b102155}Si la guardia te encierra fuera de tu feudo y tu reino, el señor del lugar dicta sentencia (soltarte, multa o días de mazmorra), cambia tu fama allí y lo que piensa de ti, y su sentencia se pregona como noticia. Hace falta que allí sepan quién eres.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowVerdict { get; set; } = true;

        [SettingPropertyInteger("{=AIL_ac30aa7a}Condena máxima (días de mazmorra)", 1, 60, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int VerdictMaxDays { get; set; } = 10;

        [SettingPropertyBool("{=AIL_15eec8c6}Frases durante las peleas", Order = 14, RequireRestart = false,
            HintText = "{=AIL_7aad4a7d}En peleas y duelos dentro de una escena, tu rival suelta frases según le va, y los mirones gritan. Una sola petición a la IA al empezar la pelea.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool EnableFightBanter { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3ba48d61}Distancia a la que un guardia ve una pelea (metros)", 5, 200, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int GuardHearingDistance { get; set; } = 35;

        [SettingPropertyInteger("{=AIL_148d1995}Distancia a la que un guardia oye que lo llaman (metros)", 5, 400, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int GuardCallDistance { get; set; } = 90;

        [SettingPropertyInteger("{=AIL_fde6aa22}Multa habitual: mínimo", 1, 100000, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int FineMin { get; set; } = 50;

        [SettingPropertyInteger("{=AIL_7f897c38}Multa habitual: máximo", 1, 100000, Order = 14, RequireRestart = false,
            HintText = "{=AIL_004eae62}El guardia puede pasarse hasta cinco veces de este valor si hubo sangre o le faltas al respeto.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int FineMax { get; set; } = 500;

        [SettingPropertyInteger("{=AIL_6184689e}Crimen por dejar plantado a un guardia", 0, 100, Order = 14, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int CrimeForWalkingAway { get; set; } = 10;

        [SettingPropertyBool("{=AIL_2da21a68}Los duelos a muerte matan de verdad", Order = 14, RequireRestart = false,
            HintText = "{=AIL_735ebb21}Si ganas un duelo a muerte contra un héroe, muere. Tú nunca mueres: quedas malherido.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowDuelDeath { get; set; } = true;

        [SettingPropertyBool("{=AIL_7a6f04b2}Tensión y amenaza", Order = 14, RequireRestart = false,
            HintText = "{=AIL_04d419f6}La tensión sube con insultos y amenazas y baja con los días. Con tensión alta el NPC puede pasar a la violencia o llamar a la guardia. También valora si le das miedo (tus tropas contra las suyas).")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public bool EnableTension { get; set; } = true;

        [SettingPropertyInteger("{=AIL_35049c49}Cambio máximo de tensión por respuesta", 1, 50, Order = 15, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int MaxTensionChangePerReply { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_bcd82606}Tensión necesaria para la violencia", 0, 100, Order = 16, RequireRestart = false,
            HintText = "{=AIL_c0e5f1de}A partir de aquí puede atacarte por su cuenta o llamar a la guardia. Los duelos y peleas pactados no lo necesitan.")]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int TensionForViolence { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_ab464755}Tensión que baja cada día", 1, 100, Order = 17, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_d770d7bc}2. Relación, confianza y romance")]
        public int TensionDecayPerDay { get; set; } = 15;

        [SettingPropertyBool("{=AIL_cae619f6}Encargos (misiones creadas por la IA)", Order = 15, RequireRestart = false,
            HintText = "{=AIL_745a654d}Los NPC pueden encargarte cosas, anotar tu progreso, darlas por cumplidas (con recompensa) o por fracasadas. Aparecen en el diario de misiones del juego. Ojo: una partida guardada con encargos necesita AI Legacy para cargar.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool EnableQuests { get; set; } = true;

        [SettingPropertyInteger("{=AIL_f039f8c1}Encargos activos a la vez", 1, 30, Order = 16, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int MaxActiveQuests { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_871af31c}Relación ganada al cumplir un encargo", 0, 20, Order = 17, RequireRestart = false,
            HintText = "{=AIL_3a0aca3f}Si fracasa o se pasa el plazo pierdes la mitad.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int QuestRelationReward { get; set; } = 4;

        [SettingPropertyBool("{=AIL_5454e867}Objetos, documentos y talleres", Order = 18, RequireRestart = false,
            HintText = "{=AIL_ba299376}Dar y recibir objetos, documentos de rol (cartas de presentación, contratos, salvoconductos) y comprar talleres hablando.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowItems { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0bfa167c}Valor máximo de los objetos que da un NPC de una vez", 0, 1000000, Order = 19, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int MaxItemValuePerAction { get; set; } = 5000;

        [SettingPropertyBool("{=AIL_da8482bd}Conceder y aceptar feudos", Order = 20, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowFiefs { get; set; } = true;

        [SettingPropertyBool("{=AIL_911ad5bc}Abdicar y ceder la corona", Order = 21, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowAbdication { get; set; } = true;

        [SettingPropertyBool("{=AIL_990436d2}Cambios de clan (unirse al tuyo, dejarlo)", Order = 22, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool AllowClanChanges { get; set; } = true;

        // ------------------------------------------------------------ Memoria
        [SettingPropertyInteger("{=AIL_1057a530}Turnos de historial en el prompt", 4, 60, Order = 0, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria", GroupOrder = 4)]
        public int HistoryTurnsInPrompt { get; set; } = 16;

        [SettingPropertyInteger("{=AIL_effea661}Resumir a partir de N turnos", 10, 200, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int SummarizeAfterTurns { get; set; } = 24;

        [SettingPropertyInteger("{=AIL_ba098098}Turnos literales tras resumir", 2, 40, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int KeepTurnsAfterSummary { get; set; } = 8;

        [SettingPropertyBool("{=AIL_5283e9ce}Los personajes recuerdan lo suyo entre ellos", Order = 3, RequireRestart = false,
            HintText = "{=AIL_17ca3c0a}Cada personaje con nombre guarda cómo se lleva con otros personajes y lo último que pasó entre ellos (sus charlas, un recado, un desplante delante de ti, un arresto). Lo usan cuando hablan entre sí, en grupo y contigo. Se guarda en su archivo, en «others».")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public bool EnableNpcBonds { get; set; } = true;

        [SettingPropertyBool("{=AIL_a2e05b46}Lo que pasa entre ellos cambia su relación en el juego", Order = 3, RequireRestart = false,
            HintText = "{=AIL_02e730b3}Cuando una charla deja a dos personajes claramente mejor o peor, su relación del juego sube o baja un punto.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public bool NpcBondsAffectRelation { get; set; } = true;

        [SettingPropertyInteger("{=AIL_f43bef55}Recuerdos que guarda de cada otro personaje", 1, 10, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int NpcBondNotes { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_34f22b4a}De cuántos personajes guarda recuerdo cada uno", 4, 60, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int NpcBondMaxPeople { get; set; } = 20;

        [SettingPropertyBool("{=AIL_daf17754}Completar fichas de personaje a medias", Order = 3, RequireRestart = false,
            HintText = "{=AIL_e1661a3b}Las fichas antiguas o importadas de AIInfluence traen solo personalidad, pasado y forma de hablar. Con esto, en la siguiente charla con ese personaje la IA escribe lo que falta (qué busca, su secreto, qué le mueve, valores, religión, reputación, frases de ejemplo) sin tocar lo que ya había.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public bool CompleteProfiles { get; set; } = true;

        [SettingPropertyBool("{=AIL_ad44fa7d}Recuerdo relevante de conversaciones antiguas", Order = 3, RequireRestart = false,
            HintText = "{=AIL_bc041dfb}Las frases que salen del historial al resumir se archivan. Al hablar, se buscan las que tienen que ver con lo que dices ahora (nombres, lugares, cosas concretas) y el personaje las recuerda palabra por palabra. No gasta peticiones.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public bool EnableRecall { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0d81b407}Recuerdos relevantes que se traen a cada respuesta", 1, 8, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int RecallCount { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_52769c73}Frases antiguas archivadas por personaje", 20, 2000, Order = 3, RequireRestart = false,
            HintText = "{=AIL_70a6b6c9}Cuántas frases viejas se guardan de cada personaje para poder recordarlas. Más = mejor memoria a largo plazo y archivos algo mayores.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int ArchiveTurns { get; set; } = 400;

        [SettingPropertyInteger("{=AIL_69c192a8}Recuerdos máximos por personaje", 3, 40, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int MaxNotesPerHero { get; set; } = 12;

        [SettingPropertyBool("{=AIL_6b899a92}Libro de memorias por páginas", Order = 4, RequireRestart = false,
            HintText = "{=AIL_ab26e5e4}Cada vez que se resume un tramo de vuestra historia, la IA escribe una página con título: se lee en el libro de Memorias.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public bool EnableMemoryPages { get; set; } = true;

        [SettingPropertyInteger("{=AIL_5c92a18a}Páginas máximas por personaje", 1, 100, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int MaxMemoryPages { get; set; } = 30;

        [SettingPropertyBool("{=AIL_5c31ae7f}Arquetipos de carácter", Order = 6, RequireRestart = false,
            HintText = "{=AIL_96004d45}Cada personaje nace de un molde (world_data\\archetypes.json) que fija su moral, cómo habla y cuánto cree. Editable.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public bool EnableArchetypes { get; set; } = true;

        [SettingPropertyBool("{=AIL_b7df35c1}Religión y costumbres de cada cultura", Order = 7, RequireRestart = false,
            HintText = "{=AIL_bd15ad6c}world_data\\religion.json y cultural_traditions.json: la fe y las costumbres (cortejo, hospitalidad, juramentos) de su pueblo. Editables.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public bool EnableReligion { get; set; } = true;

        [SettingPropertyBool("{=AIL_41386273}El carácter evoluciona", Order = 8, RequireRestart = false,
            HintText = "{=AIL_2fb56a84}Tras un suceso que le marca (una traición, una muerte, un voto, enamorarse), la IA retoca un poco su carácter y lo apunta en su pasado.")]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public bool EnableCharacterDevelopment { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3cb61993}Días mínimos entre cambios de carácter", 1, 120, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int DevelopmentMinDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_e99f2367}Campos que pueden cambiar de una vez", 1, 5, Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_370ba9dd}4. Memoria")]
        public int DevelopmentMaxFields { get; set; } = 2;

        // ------------------------------------------------------------ Noticias
        [SettingPropertyBool("{=AIL_e424c129}Noticias del mundo", Order = 0, RequireRestart = false,
            HintText = "{=AIL_30c8d104}Guerras, paces, batallas, asedios, muertes... Los NPC se enteran según importancia y cercanía.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. Noticias y rumores", GroupOrder = 5)]
        public bool EnableNews { get; set; } = true;

        [SettingPropertyBool("{=AIL_b430b5cf}Rumores de las conversaciones", Order = 1, RequireRestart = false,
            HintText = "{=AIL_a7c4d3a0}Lo que pasa en una charla puede correr de boca en boca.")]
        [SettingPropertyGroup("{=AIL_953a8770}5. Noticias y rumores")]
        public bool EnableRumors { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a013a921}Días que dura una noticia", 5, 365, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_953a8770}5. Noticias y rumores")]
        public int NewsLifetimeDays { get; set; } = 60;

        [SettingPropertyInteger("{=AIL_8b7f2016}Noticias máximas en el prompt", 0, 30, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_953a8770}5. Noticias y rumores")]
        public int MaxNewsInPrompt { get; set; } = 8;

        // ------------------------------------------------------------ Cartas
        [SettingPropertyBool("{=AIL_1b9a1bfb}Cartas de los NPC", Order = 0, RequireRestart = false,
            HintText = "{=AIL_7f30bf34}Personajes con los que tienes historia te escriben por su cuenta.")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Cartas", GroupOrder = 6)]
        public bool EnableLetters { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4695e872}Probabilidad diaria de carta (%)", 0, 100, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Cartas")]
        public int LetterChancePercent { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_d943da10}Días mínimos entre cartas", 1, 60, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Cartas")]
        public int MinDaysBetweenLetters { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_9d30d4f1}Coste del mensajero por unidad de distancia", 0, 100, Order = 3, RequireRestart = false,
            HintText = "{=AIL_0ebbc439}Denarios por unidad de distancia en el mapa. Las cartas se envían desde el distrito de tabernas.")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Cartas")]
        public int MessengerCostPerDistance { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_3fbb045d}Horas de viaje por unidad de distancia (x10)", 1, 100, Order = 4, RequireRestart = false,
            HintText = "{=AIL_95f8de9b}10 = 1 hora por unidad de distancia. Más lejos = tarda más en llegar (y en volver la respuesta).")]
        [SettingPropertyGroup("{=AIL_4bb74331}6. Cartas")]
        public int MessengerHoursPerDistanceX10 { get; set; } = 10;

        // ------------------------------------------------------------ IA
        [SettingPropertyInteger("{=AIL_0d832ced}Temperatura (x100)", 0, 150, Order = 0, RequireRestart = false,
            HintText = "{=AIL_e47fec25}Creatividad. 80 = 0.8. Algunos modelos (GPT-5) la ignoran.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. IA", GroupOrder = 7)]
        public int TemperatureX100 { get; set; } = 80;

        [SettingPropertyInteger("{=AIL_dcc9bf83}Tokens máximos por respuesta", 200, 4000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. IA")]
        public int MaxTokens { get; set; } = 900;

        [SettingPropertyButton("{=AIL_affcf5d2}Probar conexión con las IAs", Content = "{=AIL_f43d7a3d}Probar", Order = 2, RequireRestart = false,
            HintText = "{=AIL_8e88da8b}Prueba cada proveedor (los de «0. Claves de IA» y, si existe, los de config.json) y enseña el resultado en el registro del juego.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. IA")]
        public Action TestConnection { get; set; } = () => LlmClient.TestAllProviders();

        [SettingPropertyText("{=AIL_3865f0c2}Proveedor para héroes", Order = 4, RequireRestart = false,
            HintText = "{=AIL_a9aa8409}Nombre de un proveedor: el servicio que escribiste en «0. Claves de IA» (gemini, openai; el del hueco 2, 3 o 4 lleva su número: openai2) o uno de config.json. Vacío = la cadena normal. Si falla, se usa el resto.")]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. IA")]
        public string ProviderHeroes { get; set; } = "";

        [SettingPropertyText("{=AIL_4b7ed15a}Proveedor para gente del pueblo", Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. IA")]
        public string ProviderResidents { get; set; } = "";

        [SettingPropertyText("{=AIL_5fcbf671}Proveedor para cartas", Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. IA")]
        public string ProviderLetters { get; set; } = "";

        [SettingPropertyText("{=AIL_c5d6d5d7}Proveedor para resúmenes de memoria", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. IA")]
        public string ProviderSummaries { get; set; } = "";

        // ------------------------------------------------------------ Escena del mapa
        [SettingPropertyBool("{=AIL_20b8ba96}Escena del mapa: también con enemigos (parlamento)", Order = 20, RequireRestart = false,
            HintText = "{=AIL_011ed64e}Con un señor enemigo también puedes desmontar y hablar cara a cara. Al volver al mapa el encuentro sigue donde estaba (atacar, rendirte, que te deje ir), salvo que en la charla haya quedado resuelto.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool MapTalkWithEnemies { get; set; } = true;

        [SettingPropertyBool("{=AIL_fe9e2f63}Hablar en la escena desde el mapa", Order = 20, RequireRestart = false,
            HintText = "{=AIL_967570b4}Al encontrarte en el mapa con un señor con el que no estás en guerra, aparece la opción «Desmontemos y hablemos cara a cara»: se abre una escena del terreno con los dos a pie y vuestra gente detrás. Tab para volver al mapa.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool EnableMapTalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_401f90b1}Escena del mapa: acompañantes por bando", 0, 30, Order = 21, RequireRestart = false,
            HintText = "{=AIL_297f659a}Cuántos de cada partida aparecen detrás de su jefe (primero sus héroes, luego sus mejores tropas).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int MapTalkEscort { get; set; } = 8;

        // ------------------------------------------------------------ Batallas
        [SettingPropertyBool("{=AIL_3bdc9ea5}IA de táctica en batallas campales", Order = 0, RequireRestart = false,
            HintText = "{=AIL_39a19577}El jefe de cada bando que no mandas tú recibe cada cierto tiempo el estado de la batalla y da órdenes reales a sus formaciones, según su carácter y su pericia. Mientras decide, o si la IA falla, siguen las tácticas del juego. No actúa en asedios.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Batallas", GroupOrder = 14)]
        public bool EnableBattleAI { get; set; } = false;

        [SettingPropertyInteger("{=AIL_92a26c15}Segundos entre decisiones de cada jefe", 30, 300, Order = 1, RequireRestart = false,
            HintText = "{=AIL_458e1b5a}Cada decisión es una petición a la IA por bando. Si un bando pierde de golpe mucha gente, decide antes.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Batallas")]
        public int BattleAIInterval { get; set; } = 120;

        [SettingPropertyBool("{=AIL_df336f51}El enemigo manda con IA", Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Batallas")]
        public bool BattleAIEnemy { get; set; } = true;

        [SettingPropertyBool("{=AIL_b8b2988f}Tus aliados mandan con IA", Order = 3, RequireRestart = false,
            HintText = "{=AIL_7858cd57}El ejército aliado con jefe propio, y tu bando cuando no eres tú quien manda (las formaciones que llevas tú no se tocan).")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Batallas")]
        public bool BattleAIAllies { get; set; } = false;

        [SettingPropertyBool("{=AIL_7f5984a3}Órdenes con tus palabras (Alt+B)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_04c41d7c}En batalla, Alt+B: escribes la orden como la dirías («la caballería por la izquierda contra sus arqueros») y la IA la convierte en órdenes a tus formaciones. El juego se para mientras escribes.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Batallas")]
        public bool BattleAITextOrders { get; set; } = true;

        [SettingPropertyBool("{=AIL_76c8cc5d}Gritos de los jefes", Order = 5, RequireRestart = false,
            HintText = "{=AIL_26b70a49}Lo que cada jefe brama a los suyos sale en el registro (y sobre su cabeza si lo tienes cerca).")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Batallas")]
        public bool BattleAIShouts { get; set; } = true;

        [SettingPropertyBool("{=AIL_f8823100}Enseñar las órdenes de la IA", Order = 6, RequireRestart = false,
            HintText = "{=AIL_c829a044}Muestra en el registro qué ha ordenado cada jefe a cada formación.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Batallas")]
        public bool BattleAIShowOrders { get; set; } = true;

        [SettingPropertyText("{=AIL_17a3df8d}Proveedor para las batallas", Order = 7, RequireRestart = false,
            HintText = "{=AIL_506e56c2}Nombre del proveedor de config.json que se prefiere para las órdenes de batalla (vacío = el normal). Conviene uno rápido.")]
        [SettingPropertyGroup("{=AIL_46d0d1f8}E. Batallas")]
        public string ProviderBattle { get; set; } = "";

        // ------------------------------------------------------------ Escenas
        [SettingPropertyBool("{=AIL_3515a1a2}Texto sobre la cabeza de los personajes", Order = 0, RequireRestart = false,
            HintText = "{=AIL_2287fa82}En las escenas, lo que dicen los personajes entre ellos o lo que te gritan al pasar sale escrito sobre su cabeza.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas", GroupOrder = 13)]
        public bool EnableBubbles { get; set; } = true;

        [SettingPropertyBool("{=AIL_2808c06c}Charlas entre personajes en las escenas", Order = 1, RequireRestart = false,
            HintText = "{=AIL_4e6c313c}De vez en cuando, dos personajes que están cerca de ti hablan de lo que pasa: noticias, escasez, enfermedad, su señor. Gasta una petición corta a la IA cada vez.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool EnableAmbient { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4faa8fb4}Segundos entre charlas (aprox.)", 15, 900, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int AmbientIntervalSeconds { get; set; } = 45;

        [SettingPropertyInteger("{=AIL_637830f6}Distancia a la que oyes una charla (metros)", 4, 60, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int AmbientRadius { get; set; } = 20;

        [SettingPropertyBool("{=AIL_4df26a61}Lo que dicen sale también en el registro", Order = 1, RequireRestart = false,
            HintText = "{=AIL_af3d096e}Además de sobre su cabeza, las frases que se cruzan los personajes aparecen en el registro de mensajes (abajo a la izquierda).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool AmbientToLog { get; set; } = true;

        [SettingPropertyBool("{=AIL_131719b8}Quien quiere hablar, se acerca andando", Order = 3, RequireRestart = false,
            HintText = "{=AIL_2c243fac}Si los dos que van a charlar no están juntos, uno camina hasta el otro antes de empezar (también entre plantas, si hay camino).")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool AmbientWalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_beb1508e}Distancia máxima entre los dos que charlan (metros)", 2, 40, Order = 3, RequireRestart = false,
            HintText = "{=AIL_4f107a7c}En un salón los nobles están separados: con poco, nunca encuentran con quién hablar.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int AmbientPairDistance { get; set; } = 14;

        [SettingPropertyInteger("{=AIL_508eddc4}Longitud máxima de cada frase (caracteres)", 40, 240, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int AmbientMaxChars { get; set; } = 140;

        [SettingPropertyInteger("{=AIL_0ca5ee01}Frases como máximo en una charla entre personajes", 2, 30, Order = 4, RequireRestart = false,
            HintText = "{=AIL_5d9bde16}La IA escribe entre la mitad y este número, según dé de sí el tema. Es una sola petición, sea larga o corta.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int AmbientMaxLines { get; set; } = 10;

        [SettingPropertyBool("{=AIL_e37afdcd}La gente corriente también charla (en corto)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_5c19273c}Apagado: solo charlan entre sí quienes tienen nombre (señores, damas, notables, errantes). Encendido: vecinos, taberneros y demás también, pero dos o tres frases cortas.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool AmbientCommoners { get; set; } = true;

        [SettingPropertyBool("{=AIL_2da177ae}Los soldados y guardias también charlan (en corto)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_74efb871}Por defecto no: están de servicio. Siguen contestando si les hablas tú.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool AmbientGuards { get; set; } = true;

        [SettingPropertyBool("{=AIL_7a1b2cf4}Te dicen cosas al pasar", Order = 4, RequireRestart = false,
            HintText = "{=AIL_9b770e1c}Cuando caminas junto a alguien en una escena (guardias, vecinos, mercaderes, nobles que no te conocen), suelta algo de pasada. Gasta UNA petición por visita a cada sitio: se pide un lote de frases y se van usando. Apágalo si no quieres ese gasto.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool EnablePassBy { get; set; } = true;

        [SettingPropertyInteger("{=AIL_90bb89d1}Al pasar: segundos entre una frase y la siguiente", 4, 120, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int PassBySeconds { get; set; } = 14;

        [SettingPropertyInteger("{=AIL_0b677944}Al pasar: distancia a la que te hablan (metros)", 2, 12, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int PassByDistance { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_cd6bc920}Al pasar: longitud máxima de cada frase", 30, 160, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int PassByMaxChars { get; set; } = 80;

        [SettingPropertyInteger("{=AIL_8529201a}Al pasar: lotes extra si se acaban las frases", 0, 5, Order = 4, RequireRestart = false,
            HintText = "{=AIL_96933cf3}Cada lote extra es una petición más en esa visita. 0 = solo el primero.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int PassByRefills { get; set; } = 1;

        [SettingPropertyBool("{=AIL_3c35b946}Charlas entre nobles por turnos (más peticiones)", Order = 4, RequireRestart = false,
            HintText = "{=AIL_ff3da657}Apagado: cada charla entre personajes se escribe entera en una petición. Encendido: se pide por turnos y cada tanda sabe lo dicho antes; pueden cortarse el uno al otro y la charla se acaba sola si te metes. Gasta varias peticiones por charla.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool AmbientTurnByTurn { get; set; } = false;

        [SettingPropertyInteger("{=AIL_a9262a10}Por turnos: frases por petición", 1, 4, Order = 4, RequireRestart = false,
            HintText = "{=AIL_270c2dbd}1 = una petición por frase (lo más caro). 2 = la mitad de peticiones.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int AmbientTurnSize { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_9f48ba90}Frases como máximo cuando charla gente corriente", 2, 8, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int AmbientCommonerLines { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_3e132ba1}Grupo: segundos de silencio tuyo para que sigan ellos (0 = nunca)", 0, 180, Order = 5, RequireRestart = false,
            HintText = "{=AIL_f08739d1}Si no dices nada durante este tiempo, la IA decide si alguien del grupo sigue la conversación (te insiste, o se ponen a hablar entre ellos). Cada vez es una petición.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupSilenceSeconds { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_b4fc9c6e}Grupo: veces seguidas que siguen sin ti", 0, 6, Order = 5, RequireRestart = false,
            HintText = "{=AIL_26e90847}Cuántas veces seguidas pueden continuar la conversación sin que tú digas nada. Al hablar tú, la cuenta vuelve a empezar.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupSilenceFollowUps { get; set; } = 4;

        [SettingPropertyBool("{=AIL_7dd76d95}Grupo: se colocan en corro a tu alrededor", Order = 5, RequireRestart = false,
            HintText = "{=AIL_bd386578}Al abrir una conversación de grupo, los participantes caminan hasta formar un semicírculo delante de ti y te miran. Quien está sentado u ocupado se queda donde está.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool GroupCircle { get; set; } = true;

        [SettingPropertyBool("{=AIL_d79a206d}Grupo: cámara lenta mientras escribes", Order = 5, RequireRestart = false,
            HintText = "{=AIL_91d8da81}Encendido: al abrir la ventana de Alt+T la escena sigue, a cámara lenta. Apagado: el juego se detiene mientras escribes.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool GroupSlowMotion { get; set; } = true;

        [SettingPropertyBool("{=AIL_14d6bbb7}Grupo: los de alrededor pueden meterse", Order = 5, RequireRestart = false,
            HintText = "{=AIL_020f810a}Quien está cerca y oye lo que dices al grupo puede acercarse y entrar en la conversación si le va algo en ello. No gasta peticiones de más.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool GroupBystanders { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a33f15bf}Grupo: hasta dónde se te oye (metros)", 1, 20, Order = 5, RequireRestart = false,
            HintText = "{=AIL_d0722260}Distancia a la que alguien de fuera oye la conversación. Un grito (!) llega al doble; un susurro (*), solo a quien miras.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupHearRadius { get; set; } = 10;

        [SettingPropertyBool("{=AIL_32aaabb0}Gestos y caras al hablar", Order = 5, RequireRestart = false,
            HintText = "{=AIL_6fed9f7e}En las charlas de escena, quien habla pone la cara y hace el gesto de lo que siente (asentir, negar, dudar, enfadarse…). Quien está sentado, ocupado o andando solo cambia la cara.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool EnableGestures { get; set; } = true;

        [SettingPropertyBool("{=AIL_92d095e7}En el grupo, pueden ir a buscar a alguien", Order = 5, RequireRestart = false,
            HintText = "{=AIL_3a88a804}Si lo que dices en una conversación de grupo le da motivo, el personaje puede actuar: ir a hablar con otro y volver a contártelo, acompañarte, quedarse, guiarte hasta alguien, llamar a alguien para que venga, o marcharse.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool SceneErrands { get; set; } = true;

        [SettingPropertyInteger("{=AIL_90722e01}Silencio tras una charla (segundos)", 30, 900, Order = 4, RequireRestart = false,
            HintText = "{=AIL_c43fad42}Tiempo sin charlas nuevas después de que acabe una. Los mismos dos tardan el doble en volver a hablar.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int AmbientCooldownSeconds { get; set; } = 180;

        [SettingPropertyBool("{=AIL_05883eac}Quien te conoce te dice algo al pasar", Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool AmbientCallOuts { get; set; } = true;

        [SettingPropertyBool("{=AIL_7450a805}Conversación de grupo (Alt+T)", Order = 5, RequireRestart = false,
            HintText = "{=AIL_b7c8efe0}En una escena, Alt+T: hablas a todos los que tienes cerca y la IA decide quién contesta. Sus respuestas salen sobre sus cabezas y en el registro. Vuelve a pulsar Alt+T para seguir hablando.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool EnableGroupTalk { get; set; } = true;

        [SettingPropertyInteger("{=AIL_35cd4d87}Grupo: distancia para entrar (metros)", 2, 30, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupRadius { get; set; } = 5;

        [SettingPropertyInteger("{=AIL_2eff340e}Grupo: distancia a la que alguien sale (metros)", 3, 50, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupGraceRadius { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_a29fcb55}Grupo: participantes como máximo", 1, 12, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupMaxParticipants { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_3bcc8ae2}Grupo: respuestas como máximo por turno", 1, 6, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupMaxLines { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_7bd9a098}Grupo: longitud máxima de cada respuesta", 60, 400, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupMaxChars { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_f6cbdca6}Grupo: segundos sin hablar para que se deshaga", 30, 1800, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int GroupIdleSeconds { get; set; } = 240;

        [SettingPropertyBool("{=AIL_6e6f1630}Lo que dices se oye", Order = 6, RequireRestart = false,
            HintText = "{=AIL_d0cb85ca}Los personajes con nombre que están a pocos pasos cuando hablas con alguien en una escena oyen lo que dices, y lo recuerdan.")]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public bool EnableOverhearing { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a8cfe43b}Distancia a la que te oyen (metros)", 1, 30, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int OverhearRadius { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_c4dbb3ad}Distancia máxima a la que se ve el texto (metros)", 5, 100, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_3a41f7a5}D. Escenas")]
        public int BubbleMaxDistance { get; set; } = 25;

        // ------------------------------------------------------------ Enfermedades
        [SettingPropertyBool("{=AIL_68f76b26}Enfermedades", Order = 0, RequireRestart = false,
            HintText = "{=AIL_6a0ec00a}Brotes de temporada o por un evento: dañan al asentamiento, se contagian a los vecinos, enferman a tus tropas si entras, y se frenan con cuarentenas.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades", GroupOrder = 12)]
        public bool EnableDiseases { get; set; } = true;

        [SettingPropertyInteger("{=AIL_5e55369d}Brotes a la vez como máximo", 1, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public int DiseaseMaxSimultaneous { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_36b9fe81}Gravedad máxima", 1, 5, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public int DiseaseMaxSeverity { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_77ca23b7}Días mínimos entre brotes nuevos", 1, 200, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public int DiseaseMinDaysBetweenOutbreaks { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_b3d0b991}Probabilidad diaria de brote de temporada (por mil)", 0, 200, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public int DiseaseSeasonalChancePerMille { get; set; } = 25;

        [SettingPropertyInteger("{=AIL_e00fb19f}Contagio diario a un vecino por nivel de gravedad (por mil)", 0, 200, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public int DiseaseSpreadChancePerMille { get; set; } = 12;

        [SettingPropertyInteger("{=AIL_0361e655}Prosperidad perdida al día por nivel de gravedad", 0, 50, Order = 6, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public int DiseaseProsperityLossPerSeverity { get; set; } = 4;

        [SettingPropertyBool("{=AIL_c54dc4b5}Los eventos pueden traer enfermedades", Order = 7, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public bool DiseaseFromEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_eade4433}Tus tropas pueden enfermar", Order = 8, RequireRestart = false,
            HintText = "{=AIL_c6dc6d42}Al entrar o quedarte en un sitio con enfermedad, parte de tus soldados puede caer enferma (quedan como heridos).")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public bool DiseaseAffectsPlayerTroops { get; set; } = true;

        [SettingPropertyInteger("{=AIL_1b8ccb46}Probabilidad de que enfermen por nivel de gravedad (%)", 0, 30, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public int DiseaseTroopChancePerSeverity { get; set; } = 8;

        // ------------------------------------------------------------ Iniciativa y recados
        [SettingPropertyBool("{=AIL_875d42da}Los NPC se cuentan cosas entre ellos", Order = 0, RequireRestart = false,
            HintText = "{=AIL_39a88b5b}Lo que dices o haces delante de un NPC puede llegar a su señor, a su familia o a sus amigos, con retraso según la distancia. Lo que llega es su versión, no un hecho.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Iniciativa y recados", GroupOrder = 11)]
        public bool EnableInfoExchange { get; set; } = true;

        [SettingPropertyInteger("{=AIL_0a072878}Recados que recuerda cada NPC", 1, 40, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Iniciativa y recados")]
        public int MaxHeardPerHero { get; set; } = 10;

        [SettingPropertyBool("{=AIL_a815af23}Los NPC se te acercan en el mapa", Order = 2, RequireRestart = false,
            HintText = "{=AIL_cc079ac8}Un noble que ya te conoce y pasa cerca puede cabalgar hasta ti para hablar, si tiene motivo: un encargo abierto, algo que ha oído de ti, afecto o rencor. Te pregunta si le recibes.")]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Iniciativa y recados")]
        public bool EnableMapInitiative { get; set; } = true;

        [SettingPropertyInteger("{=AIL_3225c875}Probabilidad por hora de que alguien cercano se acerque (%)", 0, 100, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Iniciativa y recados")]
        public int MapInitiativeChancePercent { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_f42f2a6a}Días mínimos entre acercamientos", 0, 60, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Iniciativa y recados")]
        public int MapInitiativeMinDaysBetween { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_fcce1e3f}Días mínimos entre acercamientos del mismo NPC", 1, 120, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_884d0de2}B. Iniciativa y recados")]
        public int MapInitiativeDaysPerNpc { get; set; } = 7;

        // ------------------------------------------------------------ Mundo y diplomacia
        [SettingPropertyBool("{=AIL_8a4cc812}Eventos del mundo por IA", Order = 0, RequireRestart = false,
            HintText = "{=AIL_b057228e}Cada cierto tiempo la IA escribe sucesos nuevos a partir del estado real del mundo (política, guerra, economía, sociedad, misterios). Salen en Noticias y los NPC los conocen.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia", GroupOrder = 10)]
        public bool EnableWorldEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_c268feb6}Reacciones a lo que pasa de verdad", Order = 1, RequireRestart = false,
            HintText = "{=AIL_bd7a44d0}Cuando ocurre algo sonado en el juego (una guerra, una paz, una ciudad tomada, la muerte o captura de alguien importante, un rumor gordo salido de tus charlas), la IA escribe enseguida cómo lo recibe el mundo.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool ReactiveEvents { get; set; } = true;

        [SettingPropertyBool("{=AIL_77793b62}Hospital en las ciudades", Order = 1, RequireRestart = true,
            HintText = "{=AIL_f683eca5}En el menú de cada ciudad, «Visitar el hospital»: te informan de las enfermedades que corren, te curan a ti, a tus compañeros, a tus tropas heridas o enfermas y a tus prisioneros (pagando; más barato en ciudades prósperas), y venden prevención para que tus hombres enfermen menos durante unos días.")]
        [SettingPropertyGroup("{=AIL_513640bb}C. Enfermedades")]
        public bool EnableHospital { get; set; } = true;

        [SettingPropertyInteger("{=AIL_29e73294}Efecto máximo de un suceso sobre los precios de un mercado (%)", 0, 80, Order = 1, RequireRestart = false,
            HintText = "{=AIL_2fd476bb}Un suceso puede encarecer o abaratar todo el mercado de una ciudad o aldea mientras dura (escasez, pánico, abundancia). 0 = los sucesos no tocan los precios.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int EffectMaxPricePercent { get; set; } = 35;

        [SettingPropertyBool("{=AIL_94c1e5dd}Personajes únicos creados por la IA", Order = 1, RequireRestart = false,
            HintText = "{=AIL_ce8ae4a7}Cada cierto tiempo llega a una ciudad cercana alguien nuevo inventado por la IA para esta partida (un médico huido, una capitana mercenaria, un predicador…), con nombre, oficio, carácter y motivo. Vive como un errante: puedes buscarle y tomarle a tu servicio. Una petición por personaje.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool EnableUniqueCharacters { get; set; } = true;

        [SettingPropertyInteger("{=AIL_cf0af550}Días entre personajes únicos", 3, 120, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int UniqueCharacterDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_160a2e2f}Personajes únicos vivos como máximo", 1, 60, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int UniqueCharacterMax { get; set; } = 15;

        [SettingPropertyBool("{=AIL_98b07be9}Crónica al morir alguien que conocías", Order = 1, RequireRestart = false,
            HintText = "{=AIL_d49df131}Cuando muere un personaje con el que has hablado varias veces, la IA escribe su crónica (quién fue, qué hubo entre vosotros, cómo acabó). Sale en pantalla y queda en Noticias. Una petición por crónica.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool EnableChronicles { get; set; } = true;

        [SettingPropertyInteger("{=AIL_e3a1d50c}Crónica: charlas mínimas con el difunto", 1, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int ChronicleMinTalks { get; set; } = 3;

        [SettingPropertyBool("{=AIL_d6f2ec0b}Entrenamiento en la arena", Order = 1, RequireRestart = true,
            HintText = "{=AIL_b3ac06a3}En la arena de cada ciudad, «Entrenar a tus tropas»: pagas, pasan 8 horas y tus soldados sanos ganan experiencia (más los novatos). Alguno puede lesionarse. Cada arena tarda unos días en volver a admitiros.")]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public bool EnableArenaTraining { get; set; } = true;

        [SettingPropertyInteger("{=AIL_11a9a28f}Arena: experiencia base por soldado", 20, 2000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int ArenaTrainingXp { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_fd5decd8}Arena: coste (% del precio normal)", 10, 500, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int ArenaTrainingCostPercent { get; set; } = 100;

        [SettingPropertyInteger("{=AIL_d77a8668}Arena: probabilidad de lesión por soldado (%)", 0, 30, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int ArenaTrainingInjuryPercent { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_fde1c2b2}Arena: horas hasta poder repetir en la misma", 0, 720, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_e4f26c59}3. Acciones")]
        public int ArenaTrainingCooldownHours { get; set; } = 72;

        [SettingPropertyBool("{=AIL_4d649e87}Secretos nacidos de la historia", Order = 1, RequireRestart = false,
            HintText = "{=AIL_9b8bd5de}La IA inventa los secretos a partir de la gente real de cada reino (sus casas, bodas, deudas, rencores) y de los sucesos que van pasando. Cada uno trata de personas concretas y lo conocen unas pocas, que solo lo sueltan con confianza. Gasta una petición por reino, repartidas una por día de juego al empezar la partida.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool StorySecrets { get; set; } = true;

        [SettingPropertyInteger("{=AIL_2504fc22}Secretos de origen por reino", 1, 10, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int StorySecretsPerKingdom { get; set; } = 4;

        [SettingPropertyInteger("{=AIL_da62d72b}Secretos vivos como máximo en la partida", 10, 400, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int StorySecretsMax { get; set; } = 120;

        [SettingPropertyInteger("{=AIL_df85af91}Secretos que un personaje tiene presentes al hablar", 1, 10, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int StorySecretsInPrompt { get; set; } = 4;

        [SettingPropertyBool("{=AIL_e3c37efa}Además, secretos de fábrica (world_data\\secrets.json)", Order = 1, RequireRestart = false,
            HintText = "{=AIL_5a6d8aee}Apagado por defecto. Encendido: también se reparten a dados los secretos genéricos escritos en ese archivo (puedes poner ahí los tuyos).")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool PresetSecrets { get; set; } = false;

        [SettingPropertyBool("{=AIL_9e2fda6f}Los sucesos afectan a los clanes", Order = 1, RequireRestart = false,
            HintText = "{=AIL_e9f870fb}Un suceso puede manchar o ensalzar el nombre de un clan concreto (también el tuyo): sus ingresos bajan o suben un tanto por ciento durante unos días. Se ve en el desglose de ingresos del clan y en Noticias.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool EnableClanEffects { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d02abd4c}Efecto máximo sobre los ingresos de un clan (%)", 5, 60, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int ClanEffectMaxPercent { get; set; } = 30;

        [SettingPropertyBool("{=AIL_96226daa}Lo que haces y dices da que hablar", Order = 1, RequireRestart = false,
            HintText = "{=AIL_750e804a}Una agresión tuya en la calle, un muerto, un arresto, o un rumor de peso que eches a rodar en una charla, se convierten en un suceso del mundo con su noticia (y con efectos en el lugar si hubo sangre). Cuenta solo lo que los testigos pudieron saber. Es una petición a la IA por suceso.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool IncidentEvents { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d53be13f}Importancia mínima para provocar una reacción", 1, 4, Order = 1, RequireRestart = false,
            HintText = "{=AIL_303700aa}1 = cualquier suceso local, 2 = asuntos de la corte, 3 = lo que sacude un reino, 4 = solo lo que sacude el mundo.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int ReactiveMinImportance { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_586abbc0}Horas de juego mínimas entre reacciones", 1, 240, Order = 1, RequireRestart = false,
            HintText = "{=AIL_6a2934ef}Si pasan varias cosas seguidas, se juntan en una sola reacción.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int ReactiveMinHoursBetween { get; set; } = 8;

        [SettingPropertyBool("{=AIL_292485a4}Sucesos de fondo (sin que haya pasado nada)", Order = 1, RequireRestart = false,
            HintText = "{=AIL_613c43b2}Además de las reacciones, cada cierto tiempo la IA escribe lo que se cuece por su cuenta: intrigas, escasez, presagios, malestar.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool BackgroundEvents { get; set; } = true;

        [SettingPropertyInteger("{=AIL_18b82bcb}Sucesos de fondo: cada cuántos días (aprox.)", 1, 100, Order = 1, RequireRestart = false,
            HintText = "{=AIL_d43bd671}No cae como un reloj: el plazo real varía entre el 60 % y el 140 % de este valor.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int WorldEventsIntervalDays { get; set; } = 7;

        [SettingPropertyInteger("{=AIL_c0d9e15c}Eventos por tanda", 1, 4, Order = 2, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int WorldEventsPerGeneration { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_072e561a}Longitud mínima de un evento (caracteres)", 80, 800, Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int WorldEventMinLength { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_5b79eb8c}Longitud máxima de un evento (caracteres)", 200, 1400, Order = 4, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int WorldEventMaxLength { get; set; } = 600;

        [SettingPropertyInteger("{=AIL_dd736c20}Días que un evento sigue «en el aire»", 5, 120, Order = 5, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int WorldEventLifespanDays { get; set; } = 30;

        [SettingPropertyInteger("{=AIL_b32fb45c}Avisar en pantalla a partir de importancia", 1, 5, Order = 6, RequireRestart = false,
            HintText = "{=AIL_580cc0e5}1 = todos, 4 = solo los que sacuden el mundo, 5 = nunca.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int WorldEventNotifyImportance { get; set; } = 2;

        [SettingPropertyBool("{=AIL_c69c977f}Efectos económicos de los eventos", Order = 7, RequireRestart = false,
            HintText = "{=AIL_44600a27}Un evento puede cambiar cada día, durante un tiempo, la prosperidad, la comida, la seguridad y la lealtad de un asentamiento real.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool EnableEconomicEffects { get; set; } = true;

        [SettingPropertyInteger("{=AIL_d37eb730}Efecto máximo: prosperidad al día", 0, 200, Order = 8, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int EffectMaxProsperityPerDay { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_0696ca62}Efecto máximo: comida al día", 0, 100, Order = 9, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int EffectMaxFoodPerDay { get; set; } = 8;

        [SettingPropertyInteger("{=AIL_b8d972a1}Efecto máximo: seguridad al día", 0, 20, Order = 10, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int EffectMaxSecurityPerDay { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_b700fc3c}Efecto máximo: lealtad al día", 0, 20, Order = 11, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int EffectMaxLoyaltyPerDay { get; set; } = 2;

        [SettingPropertyInteger("{=AIL_fc7ff1ee}Efecto máximo: duración (días)", 1, 120, Order = 12, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int EffectMaxDays { get; set; } = 20;

        [SettingPropertyBool("{=AIL_d86408df}Diplomacia de reyes por IA (sustituye a la del juego)", Order = 20, RequireRestart = false,
            HintText = "{=AIL_6c0f99f2}Los clanes del juego dejan de proponer guerras, paces, alianzas y comercio por su cuenta. Lo deciden los reyes con declaraciones públicas. Necesita los eventos del mundo activados.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool EnableDiplomacy { get; set; } = true;

        [SettingPropertyInteger("{=AIL_4ae4ed78}Días mínimos entre declaraciones de un mismo rey", 1, 60, Order = 21, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int MinDaysBetweenStatements { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_abd77d03}Días en que un evento puede provocar declaraciones", 1, 60, Order = 22, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int EngagementDays { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_0e7aa239}Probabilidad diaria de contestar una propuesta (%)", 5, 100, Order = 23, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int ResponsePressurePercent { get; set; } = 40;

        [SettingPropertyInteger("{=AIL_46407e9d}Días que dura una propuesta sin respuesta", 5, 120, Order = 24, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int ProposalLifetimeDays { get; set; } = 30;

        [SettingPropertyInteger("{=AIL_ab45cede}Longitud mínima de una declaración", 80, 800, Order = 25, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int StatementMinLength { get; set; } = 200;

        [SettingPropertyInteger("{=AIL_9ba9f600}Longitud máxima de una declaración", 200, 1600, Order = 26, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int StatementMaxLength { get; set; } = 700;

        [SettingPropertyInteger("{=AIL_0d849eb1}Cambio máximo de postura entre coronas por declaración", 1, 50, Order = 27, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int MaxStanceChange { get; set; } = 15;

        [SettingPropertyInteger("{=AIL_b57ad932}Tributo diario máximo", 10, 20000, Order = 28, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int MaxDailyTribute { get; set; } = 2000;

        [SettingPropertyInteger("{=AIL_e9d88202}Reparaciones máximas", 100, 2000000, Order = 29, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int MaxReparations { get; set; } = 100000;

        [SettingPropertyInteger("{=AIL_ffcd4d39}Impuesto de la corona: mínimo (%)", -50, 0, Order = 30, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int KingdomTaxMinPercent { get; set; } = -10;

        [SettingPropertyInteger("{=AIL_9ff27d57}Impuesto de la corona: máximo (%)", 0, 100, Order = 31, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int KingdomTaxMaxPercent { get; set; } = 30;

        [SettingPropertyBool("{=AIL_cddce6e0}Los reyes pueden declararte la guerra si reinas", Order = 32, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool AllowAiWarOnPlayer { get; set; } = true;

        [SettingPropertyBool("{=AIL_76e631ee}Tu rey puede expulsarte o confiscarte un feudo", Order = 33, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool AllowAiExpelPlayer { get; set; } = false;

        [SettingPropertyBool("{=AIL_015c810d}Leyes proclamadas por los reyes", Order = 34, RequireRestart = false,
            HintText = "{=AIL_ef8b7c72}Un rey puede crear una ley propia con efectos pequeños y diarios en todas sus ciudades y castillos (lealtad, prosperidad, seguridad, comida), y derogarla.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool EnableKingdomLaws { get; set; } = true;

        [SettingPropertyInteger("{=AIL_a12df7a9}Leyes en vigor por reino", 1, 10, Order = 35, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int MaxLawsPerKingdom { get; set; } = 3;

        [SettingPropertyBool("{=AIL_5abeb44e}Rebeliones de clanes", Order = 36, RequireRestart = false,
            HintText = "{=AIL_cbd20bc6}Un evento puede hacer que un clan vasallo se alce contra su corona (nunca el tuyo ni el clan reinante), si de verdad se lleva mal con su rey.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public bool AllowClanRebellions { get; set; } = true;

        [SettingPropertyInteger("{=AIL_db9bfa6a}Relación máxima con su rey para rebelarse", -100, 20, Order = 37, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int RebellionMaxRelation { get; set; } = -15;

        [SettingPropertyInteger("{=AIL_84f97df8}Cansancio de guerra: recuperación al día en paz (x10)", 0, 100, Order = 40, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int FatigueRecoveryPerDayX10 { get; set; } = 10;

        [SettingPropertyInteger("{=AIL_7948ed38}Cansancio de guerra: por día y guerra abierta (x10)", 0, 100, Order = 41, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int FatiguePerWarDayX10 { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_b7d8f762}Cansancio de guerra: por batalla perdida (x10)", 0, 200, Order = 42, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int FatiguePerBattleLostX10 { get; set; } = 20;

        [SettingPropertyInteger("{=AIL_7c23f3b8}Cansancio de guerra: por noble muerto", 0, 50, Order = 43, RequireRestart = false,
            HintText = "{=AIL_47dd15e0}Un noble capturado cuenta un tercio.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int FatiguePerLordKilled { get; set; } = 3;

        [SettingPropertyInteger("{=AIL_0e578525}Cansancio de guerra: por ciudad o castillo perdido", 0, 50, Order = 44, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int FatiguePerSettlementLost { get; set; } = 6;

        [SettingPropertyInteger("{=AIL_a4b23033}Segundos reales mínimos entre peticiones del mundo", 5, 600, Order = 50, RequireRestart = false,
            HintText = "{=AIL_2924e794}Para no gastar cupo cuando avanzas el tiempo deprisa.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public int WorldAiMinSecondsBetweenCalls { get; set; } = 25;

        [SettingPropertyText("{=AIL_74ca054c}Proveedor para el mundo y la diplomacia", Order = 51, RequireRestart = false,
            HintText = "{=AIL_e2132343}Nombre de un proveedor de config.json. Vacío = la cadena normal.")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public string ProviderWorld { get; set; } = "";

        [SettingPropertyButton("{=AIL_6a025b78}Generar eventos ahora", Content = "{=AIL_63621315}Generar", Order = 52, RequireRestart = false,
            HintText = "{=AIL_67d30a9a}Con una partida cargada: pide ya una tanda de eventos (aparecen cuando vuelves al mapa).")]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public Action ForceWorldEvents { get; set; } = () => Later(() => World.WorldSim.GenerateEvents(true));

        [SettingPropertyButton("{=AIL_4eb0fc2d}Hacer una declaración (si reinas)", Content = "{=AIL_c7acc8b1}Declarar", Order = 53, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_f2caa92d}A. Mundo y diplomacia")]
        public Action PlayerDeclaration { get; set; } = () => Later(World.Diplomacy.PlayerStatement);

        // ------------------------------------------------------------ Editor
        [SettingPropertyButton("{=AIL_1cb10851}Abrir el editor de AI Legacy", Content = "{=AIL_5a216c13}Abrir", Order = 0, RequireRestart = false,
            HintText = "{=AIL_ab8aaa3e}Abre en tu navegador el editor: mundo, secretos, NPC (personalidad, memoria, instrucciones), eventos y copias. Necesita una partida cargada.")]
        [SettingPropertyGroup("{=AIL_1d38423b}9. Editor", GroupOrder = 9)]
        public Action OpenEditor { get; set; } = () => Web.EditorServer.OpenInBrowser();

        [SettingPropertyInteger("{=AIL_2fc52b1a}Puerto del editor", 1024, 65000, Order = 1, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_1d38423b}9. Editor")]
        public int EditorPort { get; set; } = 17890;

        [SettingPropertyBool("{=AIL_7ff37c4e}Guardar petición/respuesta en logs", Order = 3, RequireRestart = false)]
        [SettingPropertyGroup("{=AIL_a96fe2c6}7. IA")]
        public bool DebugLogBodies { get; set; } = false;

        // ------------------------------------------------------------ Importar
        [SettingPropertyButton("{=AIL_31311890}Importar memorias de AIInfluence", Content = "{=AIL_faa80647}Importar", Order = 0, RequireRestart = false,
            HintText = "{=AIL_d4db6271}Con una partida cargada: trae de AIInfluence la memoria, personalidad, recuerdos e instrucciones de los NPC de ESTA campaña. No sobrescribe a quien ya tenga memoria en AI Legacy.")]
        [SettingPropertyGroup("{=AIL_6cf8197b}8. Importar", GroupOrder = 8)]
        public Action ImportAIInfluence { get; set; } = () => Import.AIInfluenceImporter.ImportCurrentCampaign();

        [SettingPropertyButton("{=AIL_376f0803}Deshacer la importación", Content = "{=AIL_bdd858a5}Quitar", Order = 1, RequireRestart = false,
            HintText = "{=AIL_20e63156}Quita de esta campaña las memorias que vinieron de AIInfluence (por ejemplo, si importaste las de otra partida). Los archivos se apartan a una subcarpeta, no se borran.")]
        [SettingPropertyGroup("{=AIL_6cf8197b}8. Importar")]
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
