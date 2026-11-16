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
        /// <summary>Requests per minute allowed (0 = no limit). Once it's reached, the next provider is used.</summary>
        [JsonProperty("rpm")] public int Rpm = 0;
        /// <summary>USD per million input and output tokens (0 = free or not set).</summary>
        [JsonProperty("price_in")] public double PriceIn = 0;
        [JsonProperty("price_out")] public double PriceOut = 0;
        /// <summary>When unset, it's inferred: the provider has a price, or it's a service that always charges.</summary>
        [JsonProperty("paid")] public bool? Paid;
        /// <summary>Embedding model for this provider. Empty = the service's usual one, "-" = don't use it for embeddings.</summary>
        [JsonProperty("embedding_model")] public string EmbeddingModel = "";
        /// <summary>How hard the model reasons: "" (off, nothing sent), low, medium, high or max. LlmClient sends it in the
        /// form the endpoint understands (reasoning_effort, OpenRouter's reasoning object, Anthropic's thinking budget).</summary>
        [JsonProperty("reasoning_effort")] public string ReasoningEffort = "";

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
    /// config.json only holds AI providers and their keys. Everything else lives in MCM.
    /// Reloaded when the file or the MCM provider fields change.
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
                            // MCM providers first, then config.json ones if the file exists (for advanced setups)
                            bool hasFile = File.Exists(Paths.ConfigFile);
                            var file = hasFile ? Load() : new LegacyConfig();
                            var merged = new LegacyConfig();
                            try
                            {
                                // Re-read if the MCM fields changed mid-read (the player is editing them right now)
                                var mcm = Settings.S.Get.McmProviders();
                                string sig2 = Settings.S.Get.McmProvidersSignature();
                                if (sig2 != sig) { mcm = Settings.S.Get.McmProviders(); sig = Settings.S.Get.McmProvidersSignature(); }
                                merged.Providers.AddRange(mcm);
                            }
                            catch (Exception ex) { Log.Error("MCM providers", ex); }
                            int fromMcm = merged.Providers.FindAll(p => p.IsUsable).Count;
                            // Names must be unique across MCM and the file; a duplicate is skipped
                            foreach (var p in file.Providers)
                            {
                                string n = (p.Name ?? "").Trim();
                                if (n.Length == 0) { n = "proveedor" + (merged.Providers.Count + 1); p.Name = n; }
                                if (merged.Providers.Exists(x => string.Equals((x.Name ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase)))
                                { Log.Warn($"Duplicate provider in config.json, ignored: {n}"); continue; }
                                merged.Providers.Add(p);
                            }
                            if (!hasFile) LastLoadError = fromMcm > 0 ? null : L.T("AIL_bb646884", "No hay ninguna clave: ponla en Opciones, Mod Options, AI Legacy, 0. Claves de IA.");
                            if (sig != _mcmSig || _current == null)
                                Log.Info($"AI providers: {fromMcm} usable from MCM, {file.Providers.FindAll(p => p.IsUsable).Count} from config.json");
                            _current = merged;
                            _loadedStamp = stamp; _mcmSig = sig;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Could not check config.json", ex);
                        if (_current == null) _current = new LegacyConfig();
                    }
                    return _current;
                }
            }
        }

        private static LegacyConfig Load()
        {
            LastLoadError = null;
            if (!File.Exists(Paths.ConfigFile)) return new LegacyConfig();      // the file is optional, keys can go in MCM
            try
            {
                var cfg = JsonConvert.DeserializeObject<LegacyConfig>(File.ReadAllText(Paths.ConfigFile)) ?? new LegacyConfig();
                var usable = cfg.Providers.FindAll(p => p.IsUsable);
                Log.Info($"config.json loaded: {cfg.Providers.Count} providers, {usable.Count} usable ({string.Join(", ", usable.ConvertAll(p => p.Name + "/" + p.Model))})");
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
