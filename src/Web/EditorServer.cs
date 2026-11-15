using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using AILegacy.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace AILegacy.Web
{
    /// <summary>
    /// Browser editor (http://localhost:PORT/). Serves Modules\AILegacy\web\editor.html
    /// plus a small JSON API. Anything that touches the game runs on the main thread.
    /// Security: loopback requests only, Host/Origin must be localhost or 127.0.0.1 with our port,
    /// and the API needs the session token (X-AIL-Token header) embedded in the page.
    /// </summary>
    internal static class EditorServer
    {
        private static HttpListener _http;
        private static Thread _thread;
        private static int _port;
        // Per-session token. It's embedded in the served page and the page sends it on every /api/* call.
        // Other sites open in the browser can't read our page (different origin), so they never see it.
        private static string _token;
        private const string TokenHeader = "X-AIL-Token";
        private const string TokenMarker = "__AIL_TOKEN__";

        public static string Url => $"http://localhost:{_port}/";

        private static string NewToken()
        {
            var bytes = new byte[24];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        public static void OpenInBrowser()
        {
            MainThread.Post(() =>
            {
                if (Campaign.Current == null) { Info(L.T("AIL_9713e4ab", "Carga una partida para usar el editor.")); return; }
                if (!EnsureStarted()) return;
                EditorWindow.Show(Url);
            });
        }

        public static bool EnsureStarted()
        {
            int port = S.Get.EditorPort;
            if (_http != null && _http.IsListening && port == _port) return true;
            Stop();
            try
            {
                _port = port;
                _token = NewToken();
                // Local only: localhost, plus 127.0.0.1 if Windows allows it without admin rights
                try
                {
                    _http = new HttpListener();
                    _http.Prefixes.Add(Url);
                    _http.Prefixes.Add($"http://127.0.0.1:{_port}/");
                    _http.Start();
                }
                catch (HttpListenerException)
                {
                    try { _http?.Close(); } catch { }
                    _http = new HttpListener();
                    _http.Prefixes.Add(Url);
                    _http.Start();
                }
                _thread = new Thread(Loop) { IsBackground = true, Name = "AILegacyEditor" };
                _thread.Start();
                Log.Info("Editor web en " + Url);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo arrancar el editor web", ex);
                Info(L.F("AIL_4f6f8296", $"No se pudo abrir el puerto {port}. Cambia el puerto en MCM → AI Legacy → Editor.\n{ex.Message}"));
                _http = null;
                return false;
            }
        }

        public static void Stop()
        {
            EditorWindow.Close();
            try { _http?.Stop(); _http?.Close(); } catch { }
            _http = null;
        }

        private static void Loop()
        {
            while (_http != null && _http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _http.GetContext(); } catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request; var res = ctx.Response;
            try
            {
                // Loopback only, Host must be localhost/127.0.0.1:port (blocks DNS rebinding), same for Origin if present
                if (!Allowed(req, out string why))
                {
                    Reject($"({why}) {req.HttpMethod} {req.Url?.AbsolutePath}");
                    Send(res, 403, JsonConvert.SerializeObject(new { error = "forbidden" }), "application/json; charset=utf-8");
                    return;
                }
                string path = req.Url.AbsolutePath.TrimEnd('/');
                if (path == "/favicon.ico") { Send(res, 204, "", "text/plain"); return; }
                if (path == "" || path == "/index.html")
                {
                    var file = Path.Combine(Paths.ModuleDir, "web", "editor.html");
                    var html = File.Exists(file) ? File.ReadAllText(file, Encoding.UTF8).Replace(TokenMarker, _token) : "<h1>Falta web/editor.html</h1>";
                    Send(res, 200, html, "text/html; charset=utf-8");
                    return;
                }
                if (path.StartsWith("/api/") && req.HttpMethod == "POST") Memory.SaveFiles.Dirty();
                // Every API call needs the session token
                if (!path.StartsWith("/api/") || !TokenOk(req.Headers[TokenHeader]))
                {
                    Reject($"(sin ficha válida) {req.HttpMethod} {path}");
                    Send(res, 403, JsonConvert.SerializeObject(new { error = "forbidden" }), "application/json; charset=utf-8");
                    return;
                }
                string body = "";
                if (req.HasEntityBody) using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = r.ReadToEnd();
                var data = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);

                object result = MainThread.Invoke(() => Api(path, req.QueryString, data));
                Send(res, 200, JsonConvert.SerializeObject(result), "application/json; charset=utf-8");
            }
            catch (Exception ex)
            {
                Log.Error("Editor web: " + req.Url, ex);
                try { Send(res, 500, JsonConvert.SerializeObject(new { error = ex.GetBaseException().Message }), "application/json; charset=utf-8"); } catch { }
            }
        }

        private static int _rejected;

        /// <summary>Logs rejected requests, only the first ones, so a pushy site can't flood the log.</summary>
        private static void Reject(string what)
        {
            int n = Interlocked.Increment(ref _rejected);
            if (n <= 20) Log.Warn("Editor web: petición rechazada " + what);
            else if (n == 21) Log.Warn("Editor web: más peticiones rechazadas; ya no se apuntan");
        }

        private static bool Allowed(HttpListenerRequest req, out string why)
        {
            why = null;
            var remote = req.RemoteEndPoint?.Address;
            if (remote == null || !IPAddress.IsLoopback(remote)) { why = "no es este equipo: " + remote; return false; }
            string host = (req.Headers["Host"] ?? "").Trim();
            if (!IsOwnAuthority(host)) { why = "Host " + host; return false; }
            string origin = req.Headers["Origin"];
            if (origin != null)
            {
                Uri o;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out o) || o.Scheme != Uri.UriSchemeHttp || !IsOwnAuthority(o.Authority)) { why = "Origin " + origin; return false; }
            }
            return true;
        }

        /// <summary>"localhost:port" or "127.0.0.1:port" (no port only when it's 80).</summary>
        private static bool IsOwnAuthority(string a)
        {
            if (string.IsNullOrEmpty(a)) return false;
            foreach (var h in new[] { "localhost", "127.0.0.1" })
            {
                if (string.Equals(a, $"{h}:{_port}", StringComparison.OrdinalIgnoreCase)) return true;
                if (_port == 80 && string.Equals(a, h, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Constant-time token compare (doesn't stop at the first mismatch).</summary>
        private static bool TokenOk(string got)
        {
            var want = _token;
            if (string.IsNullOrEmpty(want) || string.IsNullOrEmpty(got) || got.Length != want.Length) return false;
            int diff = 0;
            for (int i = 0; i < want.Length; i++) diff |= want[i] ^ got[i];
            return diff == 0;
        }

        private static void Send(HttpListenerResponse res, int code, string text, string type)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.StatusCode = code;
            res.ContentType = type;
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Frame-Options"] = "DENY";          // no framing by other sites
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }

        // ================================================================ API (main thread)

        private static object Api(string path, System.Collections.Specialized.NameValueCollection q, JObject d)
        {
            if (Campaign.Current == null) throw new Exception(L.T("AIL_9189b76c", "No hay ninguna partida cargada."));
            string wd = Path.Combine(Paths.ModuleDir, "world_data");
            switch (path)
            {
                case "/api/state":
                {
                    World.Lore.EnsureFiles();
                    var byId = new Dictionary<string, Hero>();
                    foreach (var h in Hero.AllAliveHeroes) byId[h.StringId] = h;
                    foreach (var h in Hero.DeadOrDisabledHeroes) if (!byId.ContainsKey(h.StringId)) byId[h.StringId] = h;
                    var favs = new HashSet<string>(WorldSim.State.EditorFavorites ?? new List<string>());
                    // The world files as the game reads them now: this campaign's copy if it has one, else the module's
                    var files = new Dictionary<string, object>();
                    foreach (var n in WorldFiles.Names)
                        files[n] = new { text = ReadText(WorldFiles.Resolve(n), n == WorldFiles.World ? "" : n == "religion.json" || n == "cultural_traditions.json" ? "{}" : "[]"), own = WorldFiles.HasOwn(n) };
                    // Names for the hero ids the facts and secrets point at
                    var names = new Dictionary<string, string>();
                    foreach (var n in new[] { "info.json", "secrets.json" })
                    {
                        try
                        {
                            foreach (var e in JsonConvert.DeserializeObject<List<KnowledgeEntry>>(ReadText(WorldFiles.Resolve(n), "[]")) ?? new List<KnowledgeEntry>())
                                foreach (var id in (e.About ?? new List<string>()).Concat(e.Who ?? new List<string>()))
                                    if (id != null && byId.TryGetValue(id, out var hh)) names[id] = hh.Name.ToString();
                        }
                        catch { }
                    }
                    return new
                    {
                        campaign = Campaign.Current.UniqueGameId,
                        player = Hero.MainHero.Name.ToString(),
                        files,
                        hero_names = names,
                        player_description = WorldSim.State.PlayerDescription ?? "",
                        player_family = WorldSim.State.PlayerFamily ?? "",
                        kingdoms = Campaign.Current.Kingdoms.Where(k => !k.IsEliminated).Select(k => new { id = k.StringId, name = k.Name.ToString() }),
                        npcs = MemoryStore.AllHeroes.Select(m =>
                        {
                            byId.TryGetValue(m.HeroId ?? "", out var h);
                            var k = h?.MapFaction as Kingdom;
                            return new
                            {
                                id = m.HeroId, name = m.HeroName, talks = m.ConversationCount, profile = m.Profile != null && !m.Profile.IsEmpty,
                                kingdom = k?.StringId ?? "", clan = h?.Clan?.Name?.ToString() ?? "",
                                in_game = h != null, alive = h != null && h.IsAlive, prisoner = h != null && h.IsPrisoner,
                                romance = m.Romance, fav = favs.Contains(m.HeroId ?? ""),
                            };
                        }).OrderBy(x => x.name),
                        news = NewsStore.All.OrderByDescending(n => n.Day).Take(300).Select(n => new { n.Id, n.Type, n.Text, n.Importance, days = (int)(CampaignTime.Now.ToDays - n.Day), n.Kingdoms }),
                    };
                }

                // ---------------------------------------------------------------- world files of this campaign
                case "/api/worlddata/save":
                {
                    string name = (string)d["name"] ?? "";
                    // Only the listed names exist for this route, so a name can't point anywhere else
                    if (!WorldFiles.IsKnown(name)) throw new Exception(L.T("AIL_61be9174", "Archivo no válido."));
                    string text = CheckWorldFile(name, (string)d["text"] ?? "", out int count);
                    var p = WorldFiles.CampaignPath(name);
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    WriteText(p, text);
                    World.Lore.Reload();
                    Log.Info("Editor: " + name + " guardado para esta partida");
                    return new { ok = true, count };
                }

                case "/api/worlddata/copy":
                {
                    World.Lore.EnsureFiles();
                    var dir = WorldFiles.CampaignDir;
                    Directory.CreateDirectory(dir);
                    int copied = 0;
                    foreach (var n in WorldFiles.Names)
                    {
                        string from = WorldFiles.DefaultPath(n), to = WorldFiles.CampaignPath(n);
                        if (File.Exists(to) || !File.Exists(from)) continue;    // never over a copy the campaign already has
                        File.Copy(from, to);
                        copied++;
                    }
                    World.Lore.Reload();
                    return new { ok = true, copied };
                }

                case "/api/worlddata/reset":
                {
                    string name = (string)d["name"] ?? "";
                    if (!WorldFiles.IsKnown(name)) throw new Exception(L.T("AIL_61be9174", "Archivo no válido."));
                    var p = WorldFiles.CampaignPath(name);
                    if (File.Exists(p))
                    {
                        // Kept next to it as .bak, the game stops reading it
                        File.Copy(p, p + ".bak", true);
                        File.Delete(p);
                    }
                    World.Lore.Reload();
                    return new { ok = true };
                }

                case "/api/player/save":
                {
                    string Cut(string s) { s = (s ?? "").Trim(); return s.Length > 3000 ? s.Substring(0, 3000) : s; }
                    WorldSim.State.PlayerDescription = Cut((string)d["description"]);
                    WorldSim.State.PlayerFamily = Cut((string)d["family"]);
                    SaveFiles.Flush(heroes: false);
                    return new { ok = true };
                }

                case "/api/favorite":
                {
                    string id = (string)d["id"] ?? "";
                    var list = WorldSim.State.EditorFavorites ?? (WorldSim.State.EditorFavorites = new List<string>());
                    list.RemoveAll(x => x == id);
                    if ((bool?)d["on"] == true && MemoryStore.Find(id) != null) list.Add(id);
                    SaveFiles.Flush(heroes: false);
                    return new { ok = true };
                }

                // ---------------------------------------------------------------- copies kept by the mod
                case "/api/backups":
                    return SaveFiles.ListBackups(Campaign.Current.UniqueGameId);

                case "/api/backups/restore":
                {
                    // Names only: SaveFiles resolves them inside the campaign's own backup folders and refuses the rest
                    var safety = SaveFiles.RestoreBackup(Campaign.Current.UniqueGameId, (string)d["kind"] ?? "", (string)d["name"] ?? "", out string err);
                    if (safety == null) throw new Exception(err ?? L.T("AIL_61be9174", "Archivo no válido."));
                    return new { ok = true, backup = safety, npcs = MemoryStore.Count };
                }

                case "/api/heroes":
                {
                    var term = (q["q"] ?? "").Trim().ToLowerInvariant();
                    return Hero.AllAliveHeroes.Where(h => (h.IsLord || h.IsNotable || h.IsWanderer || h.IsPlayerCompanion) && h != Hero.MainHero
                                                          && (term.Length == 0 || h.Name.ToString().ToLowerInvariant().Contains(term)))
                        .Take(40).Select(h => new { id = h.StringId, name = h.Name.ToString(), clan = h.Clan?.Name?.ToString() ?? "" });
                }

                case "/api/npc":
                {
                    var id = q["id"] ?? (string)d["id"];
                    var hero = Hero.Find(id);
                    var m = MemoryStore.Find(id) ?? (hero != null ? MemoryStore.Get(id, hero.Name.ToString()) : null);
                    if (m == null) throw new Exception("No existe ese personaje.");
                    return new
                    {
                        id = m.HeroId, name = m.HeroName,
                        info = hero == null ? "" : L.F("AIL_622157b0", $"{hero.Clan?.Name} · {(hero.MapFaction?.Name?.ToString() ?? "")} · relación {(int)hero.GetRelationWithPlayer()}"),
                        profile = m.Profile ?? new CharacterProfile(),
                        summary = m.Summary, notes = m.Notes, custom = m.CustomInstructions,
                        player_name = m.KnownPlayerName, mood = m.Mood, mood_reason = m.MoodReason,
                        talks = m.ConversationCount, imported = m.ImportedFrom,
                        trust = m.Trust, romance = m.Romance, tension = m.Tension,
                        archetype = m.Archetype ?? "", archetype_text = m.ArchetypeText ?? "", morality = m.Morality ?? "", profanity = m.Profanity ?? "", piety = m.Piety ?? "",
                        archetypes = World.Lore.ArchetypeList().Select(a => new { name = a.Key, desc = a.Value }),
                        archived = m.Archive.Count, pages = m.Pages.Count, in_game = hero != null,
                        turns = m.Turns.Select(t => new { who = t.Role == "player" ? L.T("AIL_4c6b494e", "Tú") : m.HeroName, t.Text }),
                    };
                }

                case "/api/npc/save":
                {
                    var id = (string)d["id"];
                    var hero = Hero.Find(id);
                    var m = MemoryStore.Find(id) ?? (hero != null ? MemoryStore.Get(id, hero.Name.ToString()) : null);
                    if (m == null) throw new Exception("No existe ese personaje.");
                    var p = d["profile"] as JObject;
                    // Populate over the existing profile so fields the editor doesn't send (sample lines etc.) survive
                    if (p != null) { if (m.Profile == null) m.Profile = new CharacterProfile(); JsonConvert.PopulateObject(p.ToString(), m.Profile); }
                    m.Summary = (string)d["summary"] ?? m.Summary;
                    m.Notes = Lines((string)d["notes"]);
                    m.CustomInstructions = Lines((string)d["custom"]);
                    var pn = ((string)d["player_name"] ?? "").Trim();
                    m.KnownPlayerName = pn.Length == 0 ? null : pn;
                    m.Mood = ((string)d["mood"] ?? "").Trim();
                    if (m.Mood.Length > 0) m.MoodDay = CampaignTime.Now.ToDays;
                    if (int.TryParse(((string)d["trust"] ?? "").Trim(), out var trust)) { m.Trust = Math.Max(-100, Math.Min(100, trust)); m.TrustSet = true; }
                    if (int.TryParse(((string)d["romance"] ?? "").Trim(), out var romance)) m.Romance = Math.Max(0, Math.Min(100, romance));
                    if (int.TryParse(((string)d["tension"] ?? "").Trim(), out var tension)) { m.Tension = Math.Max(0, Math.Min(100, tension)); m.TensionDay = CampaignTime.Now.ToDays; }
                    if (d["archetype"] != null && ((string)d["archetype"] ?? "") != (m.Archetype ?? "")) World.Lore.SetArchetype(m, (string)d["archetype"]);
                    foreach (var kv in new[] { "morality", "profanity", "piety" })
                    {
                        var v = ((string)d[kv] ?? "").Trim();
                        if (v.Length == 0) continue;
                        if (kv == "morality") m.Morality = v; else if (kv == "profanity") m.Profanity = v; else m.Piety = v;
                    }
                    SaveFiles.Flush();
                    Log.Info("Editor: guardado " + m.HeroName);
                    return new { ok = true };
                }

                case "/api/npc/clearhistory":
                {
                    var m = MemoryStore.Find((string)d["id"]);
                    if (m != null) { m.Turns.Clear(); m.Summary = ""; SaveFiles.Flush(); }
                    return new { ok = true };
                }

                case "/api/npc/delete":
                {
                    // Full memory backup first (same as /api/export). If it fails, nothing gets deleted
                    var backup = SaveFiles.BackupMemory(Campaign.Current.UniqueGameId, "borrar_npc");
                    if (backup == null) throw new Exception(L.T("AIL_0490687a", "No se pudo hacer la copia previa; no se ha cambiado nada."));
                    MemoryStore.Remove((string)d["id"]);
                    SaveFiles.Flush();   // moves its file to _papelera now instead of on the next daily flush
                    return new { ok = true, backup };
                }

                case "/api/news/add":
                {
                    var n = new NewsItem
                    {
                        Type = string.IsNullOrWhiteSpace((string)d["type"]) ? "custom" : (string)d["type"],
                        Importance = (int?)d["importance"] ?? 2,
                        Text = ((string)d["text"] ?? "").Trim(),
                        Kingdoms = (d["kingdoms"] as JArray)?.Select(x => x.ToString()).ToList() ?? new List<string>(),
                        Heroes = (d["heroes"] as JArray)?.Select(x => x.ToString()).ToList() ?? new List<string>(),
                        SettlementId = ((string)d["settlement"] ?? "").Trim(),
                    };
                    if (n.Text.Length < 5) throw new Exception(L.T("AIL_bf60207b", "Escribe el texto del evento."));
                    n.TitleEs = ((string)d["title"] ?? "").Trim();
                    if (n.TitleEs.Length == 0) n.TitleEs = n.Text.Length > 60 ? n.Text.Substring(0, 60) + "…" : n.Text;
                    n.TextEs = n.Text;
                    if ((bool?)d["player"] == true && !n.Heroes.Contains(Hero.MainHero.StringId)) n.Heroes.Add(Hero.MainHero.StringId);
                    NewsStore.Add(n);
                    InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_8c51b5cf", "Nuevo evento: ") + n.Text, Colors.Yellow));
                    return new { ok = true, id = n.Id };
                }

                case "/api/news/delete":
                    NewsStore.Delete((string)d["id"]);
                    return new { ok = true };

                // ---------------------------------------------------------------- files: world data and templates
                case "/api/files":
                {
                    Directory.CreateDirectory(wd);
                    var world = Directory.GetFiles(wd, "*.json").Select(Path.GetFileName).OrderBy(x => x).Select(f => new { kind = "world", name = f, edited = false });
                    var prompts = Prompt.Templates.AllTemplates().Select(t =>
                    {
                        var p = Prompt.Templates.PathFor(t.Key);
                        bool edited = false;
                        try { edited = p != null && File.Exists(p) && File.ReadAllText(p, Encoding.UTF8).Replace("\r\n", "\n").Trim('\n', ' ', '﻿') != t.Default; } catch { }
                        return new { kind = "prompt", name = t.Key, edited };
                    });
                    return world.Concat(prompts);
                }

                case "/api/file":
                {
                    string kind = q["kind"] ?? "", name = q["name"] ?? "";
                    if (kind == "world") return new { text = ReadText(WorldFile(wd, name), "[]"), def = "" };
                    var t = Prompt.Templates.Get(name) ?? throw new Exception("No existe esa plantilla.");
                    return new { text = t.Text, def = t.Default };
                }

                case "/api/file/save":
                {
                    string kind = (string)d["kind"] ?? "", name = (string)d["name"] ?? "", text = (string)d["text"] ?? "";
                    if (kind == "world")
                    {
                        JToken.Parse(text);      // validate before overwriting anything
                        WriteText(WorldFile(wd, name), text);
                        return new { ok = true };
                    }
                    var t = Prompt.Templates.Get(name) ?? throw new Exception("No existe esa plantilla.");
                    var p = Prompt.Templates.PathFor(t.Key) ?? throw new Exception(L.T("AIL_8871d6a5", "No hay carpeta de plantillas para esta partida."));
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    WriteText(p, (bool?)d["reset"] == true ? t.Default : text);
                    Log.Info("Editor: plantilla " + ((bool?)d["reset"] == true ? "restaurada " : "guardada ") + t.Key);
                    return new { ok = true };
                }

                // ---------------------------------------------------------------- AI-generated personality, without talking in game
                case "/api/npc/generate":
                {
                    var id = (string)d["id"];
                    var hero = Hero.Find(id) ?? throw new Exception(L.T("AIL_10f114fb", "Ese personaje ya no existe en la partida."));
                    if (Generating.Contains(id)) return new { ok = true, started = false };
                    var sp = Dialogue.Speaker.FromHero(hero);
                    World.Lore.EnsureArchetype(sp);
                    var m = sp.Memory;
                    var facts = new StringBuilder();
                    facts.AppendLine($"{hero.Name}, {(hero.IsFemale ? "woman" : "man")}, {(int)hero.Age} years old, of {hero.Culture?.Name} culture.");
                    facts.AppendLine(hero.IsLord ? $"A noble of clan {hero.Clan?.Name}{(hero.Clan?.Leader == hero ? " (its head)" : "")}, of {hero.MapFaction?.Name}{(hero.MapFaction is Kingdom kk && kk.Leader == hero ? " — its ruler" : "")}."
                                   : hero.IsNotable ? $"A notable ({hero.Occupation}) of {hero.CurrentSettlement?.Name ?? hero.HomeSettlement?.Name}." : hero.IsWanderer ? "A wanderer who sells their sword or wits." : "A person of some name.");
                    if (hero.Spouse != null) facts.AppendLine($"Married to {hero.Spouse.Name}.");
                    if (hero.Father != null || hero.Mother != null) facts.AppendLine($"Child of {hero.Father?.Name}{(hero.Father != null && hero.Mother != null ? " and " : "")}{hero.Mother?.Name}.");
                    if (hero.Children.Count > 0) facts.AppendLine($"Children: {string.Join(", ", hero.Children.Take(5).Select(c => c.Name.ToString()))}.");
                    if (hero.Clan != null && hero.Clan.Fiefs.Count > 0) facts.AppendLine($"The clan holds: {string.Join(", ", hero.Clan.Fiefs.Take(5).Select(f => f.Name.ToString()))}.");
                    facts.AppendLine($"Traits (each -2 to 2): honor {hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Honor)}, mercy {hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Mercy)}, valor {hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Valor)}, generosity {hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Generosity)}, calculating {hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Calculating)}.");
                    if (!string.IsNullOrEmpty(m.Archetype)) facts.AppendLine($"Mould of character: {m.Archetype} — {m.ArchetypeText}");
                    try { var enc = hero.EncyclopediaText?.ToString(); if (!string.IsNullOrWhiteSpace(enc)) facts.AppendLine("What is commonly said of them: " + enc); } catch { }
                    string hint = ((string)d["hint"] ?? "").Trim();
                    string prompt = TGenerate.Format("facts", facts.ToString().TrimEnd(), "hint", hint.Length > 0 ? "The author of this world asks for this in particular: " + hint : "", "language", Settings.S.Get.Language);
                    Generating.Add(id);
                    Llm.LlmClient.CompleteAsync(new List<Llm.ChatMessage> { new Llm.ChatMessage("user", prompt) }, wantJson: true, task: "editor", maxTokensOverride: 1200, preferredProvider: Settings.S.Get.ProviderHeroes).ContinueWith(t =>
                    {
                        var res = t.IsFaulted ? null : t.Result;
                        MainThread.Post(() =>
                        {
                            Generating.Remove(id);
                            try
                            {
                                if (res == null || !res.Ok) { GenerateError[id] = res?.Error ?? L.T("AIL_ea76bfc7", "la IA no ha respondido"); return; }
                                int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                                if (a < 0 || b <= a) { GenerateError[id] = "respuesta ilegible"; return; }
                                var p = JObject.Parse(res.Text.Substring(a, b - a + 1)).ToObject<CharacterProfile>();
                                if (p == null || p.IsEmpty) { GenerateError[id] = L.T("AIL_e514530a", "respuesta vacía"); return; }
                                if (m.Profile != null && !string.IsNullOrWhiteSpace(m.Profile.GivenName)) p.GivenName = m.Profile.GivenName;
                                m.Profile = p; m.ProfileRevision++;
                                GenerateError.Remove(id);
                                SaveFiles.Flush();
                                Log.Info("Editor: personalidad generada para " + m.HeroName);
                            }
                            catch (Exception ex) { GenerateError[id] = ex.Message; Log.Error("Generar personalidad", ex); }
                        });
                    });
                    return new { ok = true, started = true };
                }

                case "/api/npc/generating":
                {
                    var id = q["id"] ?? "";
                    GenerateError.TryGetValue(id, out var err);
                    return new { busy = Generating.Contains(id), error = err ?? "" };
                }

                // ---------------------------------------------------------------- secrets drawn from the backstory
                case "/api/storysecrets":
                    return World.WorldSim.State.Secrets.OrderByDescending(x => x.Day).Select(x => new
                    {
                        id = x.Id, text = string.IsNullOrWhiteSpace(x.TextLocal) ? x.Text : x.TextLocal, access = x.Access, source = x.Source,
                        days = (int)(CampaignTime.Now.ToDays - x.Day),
                        about = x.About.Select(i => Hero.Find(i)?.Name?.ToString() ?? i), known_by = x.KnownBy.Select(i => Hero.Find(i)?.Name?.ToString() ?? i),
                    });

                case "/api/storysecrets/delete":
                    World.WorldSim.State.Secrets.RemoveAll(x => x.Id == (string)d["id"]);
                    SaveFiles.Flush();
                    return new { ok = true };

                // ---------------------------------------------------------------- custom companion
                case "/api/companion/create":
                {
                    var h = World.People.CreateCompanion((string)d["name"], (bool?)d["female"] == true, (int?)d["age"] ?? 25, (string)d["culture"], out string err);
                    if (h == null) throw new Exception(err ?? "No se pudo crear.");
                    var m = MemoryStore.Get(h.StringId, h.Name.ToString());
                    string story = ((string)d["backstory"] ?? "").Trim(), pers = ((string)d["personality"] ?? "").Trim();
                    if (story.Length > 0 || pers.Length > 0)
                    {
                        if (m.Profile == null) m.Profile = new CharacterProfile();
                        m.Profile.Backstory = story; m.Profile.Personality = pers;
                    }
                    SaveFiles.Flush();
                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_858c74a8", $"{h.Name} se une a tu partida como compañero."), Colors.Yellow));
                    return new { ok = true, id = h.StringId, name = h.Name.ToString() };
                }

                case "/api/family/create":
                {
                    var h = World.People.CreateFamily((string)d["relation"], (string)d["name"], (bool?)d["female"] == true, (int?)d["age"] ?? 25, (string)d["culture"], out string err);
                    if (h == null) throw new Exception(err ?? "No se pudo crear.");
                    var m = MemoryStore.Get(h.StringId, h.Name.ToString());
                    string story = ((string)d["backstory"] ?? "").Trim(), pers = ((string)d["personality"] ?? "").Trim();
                    if (story.Length > 0 || pers.Length > 0) { if (m.Profile == null) m.Profile = new CharacterProfile(); m.Profile.Backstory = story; m.Profile.Personality = pers; }
                    SaveFiles.Flush();
                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_254648d0", $"{h.Name} ya es de tu familia."), Colors.Yellow));
                    return new { ok = true, id = h.StringId, name = h.Name.ToString() };
                }

                case "/api/lang":
                    return new { code = L.Code, table = L.Current() };

                // ---------------------------------------------------------------- AI spending
                case "/api/usage":
                    return new
                    {
                        rows = Llm.UsageMeter.Snapshot(),
                        today = Llm.UsageMeter.SpentToday,
                        cap = Math.Max(0, Settings.S.Get.DailySpendCapCents) / 100.0,
                        queue = Llm.LlmClient.QueueLength,
                        providers = Llm.LlmClient.ProviderStatus(),
                    };

                case "/api/cultures":
                    return TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<CultureObject>().Where(c => c.IsMainCulture).Select(c => new { id = c.StringId, name = c.Name.ToString() });

                // ---------------------------------------------------------------- roleplay items
                case "/api/items":
                    return World.Belongings.Items.Select(i => new { name = i.Name, desc = i.Description, from = i.FromName, days = (int)(CampaignTime.Now.ToDays - i.Day) });

                case "/api/item/add":
                {
                    string name = ((string)d["name"] ?? "").Trim(), desc = ((string)d["desc"] ?? "").Trim();
                    if (name.Length < 2) throw new Exception(L.T("AIL_7f675e5a", "Ponle nombre al objeto."));
                    World.Belongings.Items.RemoveAll(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
                    World.Belongings.Items.Add(new World.RpItem { Name = name, Description = desc, FromName = ((string)d["from"] ?? "").Trim(), Day = CampaignTime.Now.ToDays });
                    SaveFiles.Flush();
                    InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_a33c4ead", "Ahora llevas: ") + name, Colors.Yellow));
                    return new { ok = true };
                }

                case "/api/item/delete":
                    World.Belongings.Items.RemoveAll(i => string.Equals(i.Name, (string)d["name"], StringComparison.OrdinalIgnoreCase));
                    SaveFiles.Flush();
                    return new { ok = true };

                // ---------------------------------------------------------------- service
                case "/api/open":
                {
                    string which = (string)d["what"] ?? "save";
                    string dir = which == "world" ? wd : which == "logs" ? Paths.LogDir : SaveFiles.DirFor(Campaign.Current.UniqueGameId);
                    Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                    return new { ok = true };
                }

                case "/api/export":
                    return JObject.Parse(MemoryStore.Serialize());

                case "/api/import":
                {
                    var json = d["data"]?.ToString(Formatting.None);
                    if (string.IsNullOrWhiteSpace(json) || !(d["data"] is JObject)) throw new Exception(L.T("AIL_61be9174", "Archivo no válido."));
                    // Full backup of current memory first (same as /api/export). If it fails, nothing gets replaced
                    var backup = SaveFiles.BackupMemory(Campaign.Current.UniqueGameId, "importar");
                    if (backup == null) throw new Exception(L.T("AIL_0490687a", "No se pudo hacer la copia previa; no se ha cambiado nada."));
                    MemoryStore.Deserialize(json);
                    SaveFiles.Flush();   // files missing from the import go to _papelera, they aren't deleted
                    return new { ok = true, npcs = MemoryStore.Count, backup };
                }

                default:
                    throw new Exception(L.T("AIL_eb19bc3f", "Ruta desconocida: ") + path);
            }
        }

        private static readonly HashSet<string> Generating = new HashSet<string>();
        private static readonly Dictionary<string, string> GenerateError = new Dictionary<string, string>();

        private static readonly Prompt.Tpl TGenerate = new Prompt.Tpl("character_creation/standalone_profile.txt",
@"Write the character sheet of a person of Calradia, the medieval world of Mount & Blade II: Bannerlord, from the facts below. Make them a distinct, believable individual with contradictions, not a type: the facts and the mould of character are the skeleton, you add the flesh. Nothing that contradicts the facts; no modern ideas; no magic.
FACTS
{facts}
{hint}
Write every field in {language}, each 1 to 3 sentences, concrete (habits, a fear, a grudge, a pleasure, how they treat a servant), never vague praise:
- personality: temperament and the contradictions in it.
- backstory: what made them who they are (events, not adjectives).
- speech_style: how they talk — length of sentences, favourite turns, what they never say, formality.
- goals: what they want this year and what they want of life.
- secret: something true they hide and would be hurt by.
- drives: what moves them and what they fear.
- values: what they respect and what they despise.
- social_behavior: how they treat superiors, equals, inferiors and strangers.
- cognitive_style: their humour, how readily they lie, how they decide, how easily they trust.
- religion: what they believe and how seriously.
- reputation: what is said of them behind their back.
- sample_lines: 3 short things they would typically say, in their voice.
Answer ONLY with JSON: { ""personality"": """", ""backstory"": """", ""speech_style"": """", ""goals"": """", ""secret"": """", ""drives"": """", ""values"": """", ""social_behavior"": """", ""cognitive_style"": """", ""religion"": """", ""reputation"": """", ""sample_lines"": ["""", """", """"] }");

        private static readonly string[] Moralities = { "principled", "pragmatic", "ruthless" };
        private static readonly string[] Profanities = { "none", "mild", "heavy" };
        private static readonly string[] Pieties = { "devout", "observant", "indifferent", "skeptic" };
        private static readonly string[] Accesses = { "low", "medium", "high" };

        /// <summary>Checks a world file before it is written and returns it tidied. Throws with the entry and field at fault.</summary>
        private static string CheckWorldFile(string name, string text, out int count)
        {
            count = 0;
            if (name == WorldFiles.World) return text;
            Exception Missing(int i, string field) => new Exception(L.F("AIL_e7b1c65d", $"Entrada {i + 1}: falta «{field}»."));
            Exception Repeated(int i, string field) => new Exception(L.F("AIL_e7b1c65e", $"Entrada {i + 1}: «{field}» está repetido."));
            Exception Bad(int i, string field) => new Exception(L.F("AIL_e7b1c65f", $"Entrada {i + 1}: valor no válido en «{field}»."));
            if (name == "info.json" || name == "secrets.json")
            {
                var list = JsonConvert.DeserializeObject<List<KnowledgeEntry>>(text) ?? new List<KnowledgeEntry>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < list.Count; i++)
                {
                    var e = list[i] ?? throw Missing(i, "text");
                    e.Id = (e.Id ?? "").Trim(); e.Text = (e.Text ?? "").Trim();
                    if (e.Id.Length == 0) throw Missing(i, "id");
                    if (e.Id.StartsWith("-")) throw Bad(i, "id");      // a leading dash marks a failed roll in a character's memory
                    if (!seen.Add(e.Id)) throw Repeated(i, "id");
                    if (e.Text.Length == 0) throw Missing(i, "text");
                    if (e.Chance < 0 || e.Chance > 100) throw Bad(i, "chance");
                    e.Access = string.IsNullOrWhiteSpace(e.Access) ? "medium" : e.Access.Trim().ToLowerInvariant();
                    if (Array.IndexOf(Accesses, e.Access) < 0) throw Bad(i, "access");
                    e.Who = (e.Who ?? new List<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().ToList();
                    e.About = (e.About ?? new List<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().ToList();
                }
                count = list.Count;
                return JsonConvert.SerializeObject(list, Formatting.Indented);
            }
            if (name == "archetypes.json")
            {
                var list = JsonConvert.DeserializeObject<List<Archetype>>(text) ?? new List<Archetype>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < list.Count; i++)
                {
                    var a = list[i] ?? throw Missing(i, "name");
                    a.Name = (a.Name ?? "").Trim(); a.Description = (a.Description ?? "").Trim(); a.Id = (a.Id ?? "").Trim();
                    if (a.Name.Length == 0) throw Missing(i, "name");
                    if (!seen.Add(a.Name)) throw Repeated(i, "name");
                    if (a.Description.Length == 0) throw Missing(i, "description");
                    if (Array.IndexOf(Moralities, a.Morality) < 0) throw Bad(i, "morality");
                    if (Array.IndexOf(Profanities, a.Profanity) < 0) throw Bad(i, "profanity");
                    if (Array.IndexOf(Pieties, a.Piety) < 0) throw Bad(i, "piety");
                    a.Roles = (a.Roles ?? new List<string>()).Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).Distinct().ToList();
                    if (a.Roles.Count == 0) throw Missing(i, "roles");
                }
                count = list.Count;
                return JsonConvert.SerializeObject(list, Formatting.Indented);
            }
            // religion.json and cultural_traditions.json: culture id to text
            var map = JsonConvert.DeserializeObject<Dictionary<string, string>>(text) ?? new Dictionary<string, string>();
            var clean = new Dictionary<string, string>();
            int at = 0;
            foreach (var kv in map)
            {
                string key = (kv.Key ?? "").Trim(), val = (kv.Value ?? "").Trim();
                if (key.Length == 0) throw Missing(at, "culture");
                if (val.Length == 0) throw Missing(at, "text");
                if (clean.ContainsKey(key)) throw Repeated(at, "culture");
                clean[key] = val;
                at++;
            }
            count = clean.Count;
            return JsonConvert.SerializeObject(clean, Formatting.Indented);
        }

        /// <summary>Path of a world_data file. Refuses anything outside that folder.</summary>
        private static string WorldFile(string wd, string name)
        {
            name = Path.GetFileName(name ?? "");
            if (name.Length == 0 || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new Exception(L.T("AIL_61be9174", "Archivo no válido."));
            return Path.Combine(wd, name);
        }

        private static List<string> Lines(string s) =>
            (s ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private static string ReadText(string p, string def = "") => File.Exists(p) ? File.ReadAllText(p, Encoding.UTF8) : def;

        private static void WriteText(string p, string text)
        {
            if (File.Exists(p)) File.Copy(p, p + ".bak", true);
            File.WriteAllText(p, text, new UTF8Encoding(false));
        }

        private static void Info(string text) =>
            InformationManager.ShowInquiry(new InquiryData(L.T("AIL_a9672a5d", "AI Legacy — Editor"), text, true, false, L.T("AIL_d36c447b", "Vale"), "", null, null), true);
    }
}
