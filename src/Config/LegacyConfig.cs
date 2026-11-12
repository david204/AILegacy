using System;
using System.Collections.Generic;
using System.IO;
using AILegacy.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AILegacy.Config
{
    public class ProviderConfig
    {
        [JsonProperty("name")] public string Name = "provider";
        [JsonProperty("enabled")] public bool Enabled = true;
        [JsonProperty("base_url")] public string BaseUrl = "";
        [JsonProperty("api_key")] public string ApiKey = "";
        [JsonProperty("model")] public string Model = "";
        [JsonProperty("max_tokens_field")] public string MaxTokensField = "max_tokens";
        [JsonProperty("json_mode")] public bool JsonMode = true;
        [JsonProperty("timeout_seconds")] public int TimeoutSeconds = 30;
        [JsonProperty("send_temperature")] public bool SendTemperature = true;
        [JsonProperty("extra")] public JObject Extra = new JObject();
        /// <summary>Peticiones por minuto que admite (0 = sin límite propio). Al llegar al cupo se usa el siguiente proveedor.</summary>
        [JsonProperty("rpm")] public int Rpm = 0;
        /// <summary>Precio en dólares por millón de tokens de entrada y de salida (0 = gratis o sin poner).</summary>
        [JsonProperty("price_in")] public double PriceIn = 0;
        [JsonProperty("price_out")] public double PriceOut = 0;
        /// <summary>¿Cuesta dinero? Sin poner, se deduce: tiene precio, o es un servicio que cobra siempre (OpenAI, DeepSeek, OpenRouter).</summary>
        [JsonProperty("paid")] public bool? Paid;
        /// <summary>Modelo de vectores («embeddings») de este proveedor. Vacío = el habitual del servicio; «-» = no usarlo para vectores.</summary>
        [JsonProperty("embedding_model")] public string EmbeddingModel = "";

        [JsonIgnore]
        public bool IsPaid
        {
            get
            {
                if (Paid.HasValue) return Paid.Value;
                if (PriceIn > 0 || PriceOut > 0) return true;
                string u = (BaseUrl ?? "").ToLowerInvariant();
                return u.Contains("api.openai.com") || u.Contains("deepseek.com") || u.Contains("openrouter.ai") || u.Contains("anthropic.com");
            }
        }

        [JsonIgnore]
        public bool IsUsable =>
            Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model)
            && !string.IsNullOrWhiteSpace(ApiKey) && !ApiKey.StartsWith("PON_AQUI", StringComparison.OrdinalIgnoreCase) && !ApiKey.StartsWith("PUT_YOUR", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// config.json: solo proveedores de IA (con sus keys). El resto de ajustes está en MCM.
    /// Se recarga solo cuando el archivo cambia.
    /// </summary>
    public class LegacyConfig
    {
        [JsonProperty("providers")] public List<ProviderConfig> Providers = new List<ProviderConfig>();

        private static LegacyConfig _current;
        private static DateTime _loadedStamp = DateTime.MinValue;
        private static string _mcmSig = null;
        private static readonly object Gate = new object();

        public static string LastLoadError { get; private set; }

        public static LegacyConfig Current
        {
            get
            {
                lock (Gate)
                {
                    try
                    {
                        var stamp = File.Exists(Paths.ConfigFile) ? File.GetLastWriteTimeUtc(Paths.ConfigFile) : DateTime.MinValue;
                        string sig = "";
                        try { sig = Settings.S.Get.McmProvidersSignature(); } catch { }
                        if (_current == null || stamp != _loadedStamp || sig != _mcmSig)
                        {
                            // Primero los proveedores escritos en MCM; después, si existe, los de config.json (para ajustes avanzados)
                            bool hasFile = File.Exists(Paths.ConfigFile);
                            var file = hasFile ? Load() : new LegacyConfig();
                            var merged = new LegacyConfig();
                            try
                            {
                                // Foto coherente de lo escrito en MCM: si lo estás editando justo ahora, se vuelve a leer
                                var mcm = Settings.S.Get.McmProviders();
                                string sig2 = Settings.S.Get.McmProvidersSignature();
                                if (sig2 != sig) { mcm = Settings.S.Get.McmProviders(); sig = Settings.S.Get.McmProvidersSignature(); }
                                merged.Providers.AddRange(mcm);
                            }
                            catch (Exception ex) { Log.Error("Proveedores de MCM", ex); }
                            int fromMcm = merged.Providers.FindAll(p => p.IsUsable).Count;
                            // Sin nombres repetidos (ni entre MCM y el archivo, ni dentro del archivo): el repetido se queda fuera
                            foreach (var p in file.Providers)
                            {
                                string n = (p.Name ?? "").Trim();
                                if (n.Length == 0) { n = "proveedor" + (merged.Providers.Count + 1); p.Name = n; }
                                if (merged.Providers.Exists(x => string.Equals((x.Name ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase)))
                                { Log.Warn($"Proveedor repetido en config.json, se ignora: {n}"); continue; }
                                merged.Providers.Add(p);
                            }
                            if (!hasFile) LastLoadError = fromMcm > 0 ? null : L.T("AIL_bb646884", "No hay ninguna clave: ponla en Opciones, Mod Options, AI Legacy, 0. Claves de IA.");
                            if (sig != _mcmSig || _current == null)
                                Log.Info($"Proveedores de IA: {fromMcm} utilizable(s) desde MCM, {file.Providers.FindAll(p => p.IsUsable).Count} desde config.json");
                            _current = merged;
                            _loadedStamp = stamp; _mcmSig = sig;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("No se pudo comprobar config.json", ex);
                        if (_current == null) _current = new LegacyConfig();
                    }
                    return _current;
                }
            }
        }

        private static LegacyConfig Load()
        {
            LastLoadError = null;
            if (!File.Exists(Paths.ConfigFile)) return new LegacyConfig();      // ya no hace falta: las claves pueden ir en MCM
            try
            {
                var cfg = JsonConvert.DeserializeObject<LegacyConfig>(File.ReadAllText(Paths.ConfigFile)) ?? new LegacyConfig();
                var usable = cfg.Providers.FindAll(p => p.IsUsable);
                Log.Info($"config.json cargado: {cfg.Providers.Count} proveedores, {usable.Count} utilizables ({string.Join(", ", usable.ConvertAll(p => p.Name + "/" + p.Model))})");
                return cfg;
            }
            catch (Exception ex)
            {
                LastLoadError = L.T("AIL_7fc31420", "config.json tiene un error de formato: ") + ex.Message;
                Log.Error(LastLoadError);
                return _current ?? new LegacyConfig();
            }
        }
    }
}
