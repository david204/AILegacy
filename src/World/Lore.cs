using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace AILegacy.World
{
    public class Archetype
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("description")] public string Description;
        /// <summary>A quién le puede tocar: ruler, lord, town_notable, village_notable, gang, wanderer, commoner, guard, merchant.</summary>
        [JsonProperty("roles")] public List<string> Roles = new List<string>();
        /// <summary>principled | pragmatic | ruthless</summary>
        [JsonProperty("morality")] public string Morality = "pragmatic";
        /// <summary>none | mild | heavy</summary>
        [JsonProperty("profanity")] public string Profanity = "mild";
        /// <summary>devout | observant | indifferent | skeptic</summary>
        [JsonProperty("piety")] public string Piety = "observant";
    }

    /// <summary>
    /// Trasfondo editable de los personajes: Modules\AILegacy\world_data\archetypes.json (moldes de carácter),
    /// religion.json (la fe de cada cultura) y cultural_traditions.json (costumbres de cada cultura).
    /// Si no existen se crean con el contenido de fábrica. Se releen solos al editarlos.
    /// </summary>
    internal static class Lore
    {
        private static string Dir => Path.Combine(Paths.ModuleDir, "world_data");

        private class Cache<T> where T : class
        {
            private readonly string _file; private readonly Func<T> _defaults;
            private T _value; private DateTime _stamp; private DateTime _nextCheck;
            public Cache(string file, Func<T> defaults) { _file = file; _defaults = defaults; }
            public T Value
            {
                get
                {
                    try
                    {
                        var now = DateTime.UtcNow;
                        if (_value != null && now < _nextCheck) return _value;
                        _nextCheck = now.AddSeconds(5);
                        var path = Path.Combine(Dir, _file);
                        if (!File.Exists(path))
                        {
                            Directory.CreateDirectory(Dir);
                            File.WriteAllText(path, JsonConvert.SerializeObject(_defaults(), Formatting.Indented), new UTF8Encoding(false));
                        }
                        var stamp = File.GetLastWriteTimeUtc(path);
                        if (_value == null || stamp != _stamp)
                        {
                            _value = JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8)) ?? _defaults();
                            _stamp = stamp;
                        }
                    }
                    catch (Exception ex) { Log.Warn($"world_data\\{_file} ilegible, uso el de fábrica: {ex.Message}"); _value = _value ?? _defaults(); }
                    return _value;
                }
            }
        }

        private static readonly Cache<List<Archetype>> ArchetypesFile = new Cache<List<Archetype>>("archetypes.json", DefaultArchetypes);
        private static readonly Cache<Dictionary<string, string>> ReligionFile = new Cache<Dictionary<string, string>>("religion.json", DefaultReligions);
        private static readonly Cache<Dictionary<string, string>> TraditionsFile = new Cache<Dictionary<string, string>>("cultural_traditions.json", DefaultTraditions);

        // ============================================================ contenido de fábrica (propio de AI Legacy)

        private static Archetype A(string id, string name, string desc, string roles, string morality, string profanity, string piety) =>
            new Archetype { Id = id, Name = name, Description = desc, Roles = roles.Split(',').Select(x => x.Trim()).ToList(), Morality = morality, Profanity = profanity, Piety = piety };

        private static List<Archetype> DefaultArchetypes() => new List<Archetype>
        {
            A("iron_sovereign", "The Iron Sovereign", "Rules by will and fear of disorder; sees mercy as a debt the realm cannot afford and counts every slight.", "ruler,lord", "ruthless", "mild", "observant"),
            A("weary_crown", "The Weary Crown", "Carries the throne like a wound; longs for quiet, distrusts glory, and is slow to anger but slower to forgive.", "ruler", "principled", "none", "devout"),
            A("gilded_schemer", "The Gilded Schemer", "Smiles, flatters and remembers; every favour is an investment and every friend a piece on the board.", "ruler,lord,merchant,town_notable", "pragmatic", "none", "indifferent"),
            A("old_wolf", "The Old Wolf", "A veteran who has buried too many friends; blunt, loyal to those who bled beside him, contemptuous of courtiers.", "lord,wanderer,guard", "principled", "heavy", "observant"),
            A("glory_hound", "The Glory Hound", "Lives for songs about himself; brave to the point of folly, generous when admired, vicious when mocked.", "lord,wanderer", "pragmatic", "mild", "indifferent"),
            A("dutiful_heir", "The Dutiful Heir", "Raised to serve the family name; correct, anxious to prove worth, torn between duty and private wishes.", "lord", "principled", "none", "devout"),
            A("border_reaver", "The Border Reaver", "Takes what the weak cannot hold and calls it custom; cheerful, practical and without remorse.", "lord,gang,wanderer", "ruthless", "heavy", "skeptic"),
            A("pious_blade", "The Pious Blade", "Believes the gods watch every stroke; honest to a fault, stern with sinners, sternest with himself.", "lord,wanderer,guard", "principled", "none", "devout"),
            A("court_cynic", "The Court Cynic", "Has seen every oath broken; witty, tired, expects the worst of people and is rarely surprised.", "lord,town_notable,merchant", "pragmatic", "mild", "skeptic"),
            A("hearth_keeper", "The Hearth Keeper", "Cares for land, kin and tenants above crowns; warm at table, immovable when home is threatened.", "lord,village_notable,commoner", "principled", "mild", "observant"),
            A("coin_counter", "The Coin Counter", "Weighs everything in silver, including friendship; polite, patient, never forgets a debt in either direction.", "merchant,town_notable", "pragmatic", "none", "indifferent"),
            A("guild_patriarch", "The Guild Elder", "Proud of craft and standing; talks of the old days, guards privileges jealously, despises shoddy work.", "town_notable,merchant", "principled", "mild", "observant"),
            A("back_alley_king", "The Back-Alley King", "Runs the streets by favours and knives; charming, watchful, generous to the loyal and final with traitors.", "gang", "ruthless", "heavy", "skeptic"),
            A("reluctant_thug", "The Reluctant Tough", "Fell into the life and cannot leave it; gruff, superstitious, protective of the neighbourhood's weak.", "gang,guard,commoner", "pragmatic", "heavy", "observant"),
            A("village_patriarch", "The Village Elder", "Speaks for people who cannot speak to lords; cautious, proverb-loving, bargains hard for every sack of grain.", "village_notable", "principled", "mild", "devout"),
            A("grasping_landlord", "The Grasping Landowner", "Sees neighbours as future tenants; pious on feast days, hard at harvest, fawning before the powerful.", "village_notable,town_notable", "ruthless", "none", "observant"),
            A("sellsword_philosopher", "The Thinking Sellsword", "Fights for pay and thinks too much about it; dry humour, few illusions, a private code never explained.", "wanderer", "pragmatic", "mild", "skeptic"),
            A("exiled_noble", "The Exile", "Lost a name or a home and has not stopped counting the cost; courteous, bitter, hungry for restoration.", "wanderer,lord", "principled", "none", "observant"),
            A("merry_rogue", "The Merry Rogue", "Laughs first and runs second; loyal while the wine lasts, surprisingly brave when a friend is cornered.", "wanderer,commoner,gang", "pragmatic", "heavy", "indifferent"),
            A("zealous_convert", "The Zealot", "Found certainty and wants everyone to share it; fervent, tireless, quick to see omens and enemies.", "wanderer,commoner,village_notable", "principled", "none", "devout"),
            A("plain_soldier", "The Plain Soldier", "Does the watch, wants the pay and a dry bed; wary of nobles, hard on troublemakers, not a saint.", "guard,commoner", "pragmatic", "heavy", "observant"),
            A("worn_peasant", "The Worn Peasant", "Has outlived bad lords and bad harvests; says little to armed strangers, misses nothing, hopes for rain.", "commoner", "principled", "mild", "devout"),
            A("town_gossip", "The Town Gossip", "Knows whose wife and whose debt; friendly, curious, trades news like bread and embroiders a little.", "commoner,merchant", "pragmatic", "mild", "indifferent"),
            A("ambitious_upstart", "The Upstart", "Born low and climbing; studies the mighty, flatters upward, kicks downward, terrified of going back.", "commoner,merchant,town_notable,wanderer", "ruthless", "mild", "indifferent"),
        };

        private static Dictionary<string, string> DefaultReligions() => new Dictionary<string, string>
        {
            ["empire"] = "The old imperial rites: household gods, the deified founders and the Senate's auguries. Faith is civic duty; omens are read before war, and oaths sworn on the ancestors bind.",
            ["vlandia"] = "A stern faith of oaths and judgement: a man answers for his word before heaven. Vigils before battle, trial by combat seen as divine verdict, shrines kept by wandering brothers.",
            ["sturgia"] = "The forest and river spirits, the thunder and the ancestors in the barrow. Offerings before a voyage, blood-debts honoured, the dead fed at midwinter. The gods respect strength and kept promises.",
            ["battania"] = "The old gods of the hills and the high places, served by the wise in the sacred groves. Every glen has its guardian; breaking hospitality or felling a holy tree brings a curse.",
            ["khuzait"] = "The Eternal Sky above and the spirits of the steppe below, spoken to by shamans. A man's luck is his soul; storms, stars and the flight of birds are messages, and the hearth-fire must never be insulted.",
            ["aserai"] = "The faith of the wells and the desert prophets: one law for prince and beggar, alms for the poor, hospitality as sacred duty, and judgement for those who break a sworn contract.",
            ["nord"] = "The sea-gods and the shield-fathers: a good death and a remembered name are the only immortality. Oaths on rings, sacrifices for wind and luck, and no shame greater than cowardice.",
            ["default"] = "The gods of your fathers, honoured at the hearth and the roadside shrine; oaths are sworn before them and bad luck follows those who mock them.",
        };

        private static Dictionary<string, string> DefaultTraditions() => new Dictionary<string, string>
        {
            ["empire"] = "Marriage is an alliance of houses negotiated by the family; courtship is public, measured and conducted with letters and gifts. Rank and precedence matter in every greeting; guests are received with wine before business.",
            ["vlandia"] = "Courtship follows the tourney and the hall: deeds of arms, tokens worn openly, a formal request to the lady's father or liege. A knight's word given in public cannot be taken back without shame. Guests are safe under the roof.",
            ["sturgia"] = "A suitor proves himself with gifts and a bride-price agreed between kin; the woman's own word is asked last but counts. Boasting at table is expected, insults are answered at once, and a feast seals every bargain.",
            ["battania"] = "Marriages are made at the gatherings, with the clan's blessing; a year-and-a-day handfasting is known. Poets are feared more than swords: satire can ruin a name. Hospitality is owed even to an enemy for three nights.",
            ["khuzait"] = "Matches are arranged between clans with herds as bride-wealth, though a daring suitor may win a bride by proving himself to her kin. Elders speak first; a guest is offered milk or kumis, and refusing it is an insult.",
            ["aserai"] = "Marriage is a contract witnessed by elders, with a dowry and long negotiation; modesty in public, poetry in private. Bargaining is courtesy, haste is rudeness, and bread and salt shared make a bond.",
            ["nord"] = "A match is a bargain between free people witnessed at the thing; a woman may refuse. Gift demands gift. Feuds are settled by payment or blood, and a guest who has drunk your ale is under your protection.",
            ["default"] = "Marriage is arranged with the families' consent; guests are honoured, elders respected, and a public promise is binding.",
        };

        // ============================================================ arquetipo de un personaje

        private static List<string> RolesOf(Speaker s)
        {
            var r = new List<string>();
            if (s.IsHero)
            {
                var h = s.Hero;
                if (h.IsKingdomLeader) r.Add("ruler");
                if (h.IsLord) r.Add("lord");
                if (h.IsWanderer || h.IsPlayerCompanion) r.Add("wanderer");
                if (h.IsMerchant || h.IsArtisan) { r.Add("merchant"); r.Add("town_notable"); }
                if (h.IsGangLeader) r.Add("gang");
                if (h.IsHeadman || h.IsRuralNotable) r.Add("village_notable");
                if (h.IsPreacher) r.Add("village_notable");
                if (r.Count == 0) r.Add("commoner");
            }
            else
            {
                var occ = s.Character.Occupation.ToString();
                if (occ == "Soldier" || occ == "Guard" || occ == "PrisonGuard" || occ == "Mercenary") r.Add("guard");
                else if (occ.Contains("Trader") || occ == "Merchant" || occ == "ShopWorker" || occ == "Tavernkeeper" || occ == "Blacksmith" || occ == "Armorer" || occ == "Weaponsmith") r.Add("merchant");
                r.Add("commoner");
            }
            return r;
        }

        /// <summary>Le da su molde (una vez y para siempre), encajado con su papel y, si es héroe, con sus rasgos del juego.</summary>
        /// <summary>Los moldes de carácter disponibles (para el editor): nombre y descripción.</summary>
        public static List<KeyValuePair<string, string>> ArchetypeList() =>
            ArchetypesFile.Value.Select(a => new KeyValuePair<string, string>(a.Name, a.Description)).ToList();

        /// <summary>Pone a mano un molde de carácter (desde el editor). Nombre vacío = quitarlo para que se vuelva a asignar solo.</summary>
        public static bool SetArchetype(Memory.HeroMemory mem, string name)
        {
            if (mem == null) return false;
            if (string.IsNullOrWhiteSpace(name)) { mem.Archetype = null; mem.ArchetypeText = null; return true; }
            var a = ArchetypesFile.Value.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (a == null) return false;
            mem.Archetype = a.Name; mem.ArchetypeText = a.Description; mem.Morality = a.Morality; mem.Profanity = a.Profanity; mem.Piety = a.Piety;
            return true;
        }

        public static void EnsureArchetype(Speaker s)
        {
            var mem = s.Memory;
            if (!S.Get.EnableArchetypes || !string.IsNullOrEmpty(mem.Archetype)) return;
            try
            {
                var roles = RolesOf(s);
                var pool = ArchetypesFile.Value.Where(a => a.Roles.Any(roles.Contains)).ToList();
                if (pool.Count == 0) return;
                // Los rasgos del juego empujan: honor y piedad → íntegros; crueldad y engaño → despiadados
                int lean = 0;
                if (s.IsHero)
                    lean = s.Hero.GetTraitLevel(DefaultTraits.Honor) + s.Hero.GetTraitLevel(DefaultTraits.Mercy) - Math.Max(0, s.Hero.GetTraitLevel(DefaultTraits.Calculating) - 1);
                var fit = pool.Where(a => lean >= 2 ? a.Morality == "principled" : lean <= -2 ? a.Morality == "ruthless" : true).ToList();
                if (fit.Count == 0) fit = pool;
                // Elección estable por personaje (no depende de la sesión)
                unchecked
                {
                    uint h = 2166136261;
                    foreach (char c in s.Key) { h ^= c; h *= 16777619; }
                    var a = fit[(int)(h % (uint)fit.Count)];
                    mem.Archetype = a.Name; mem.ArchetypeText = a.Description; mem.Morality = a.Morality; mem.Profanity = a.Profanity; mem.Piety = a.Piety;
                }
            }
            catch (Exception ex) { Log.Error("Arquetipo", ex); }
        }

        private static string ForCulture(Dictionary<string, string> d, Speaker s)
        {
            string id = s.Culture?.StringId ?? "";
            if (d.TryGetValue(id, out var t)) return t;
            foreach (var kv in d) if (kv.Key != "default" && id.StartsWith(kv.Key)) return kv.Value;
            return d.TryGetValue("default", out var def) ? def : "";
        }

        /// <summary>Para el prompt: molde, brújula moral, lenguaje, fe y costumbres.</summary>
        public static void AppendTo(StringBuilder sys, Speaker s)
        {
            var cfg = S.Get;
            var mem = s.Memory;
            try
            {
                if (cfg.EnableArchetypes)
                {
                    EnsureArchetype(s);
                    if (!string.IsNullOrEmpty(mem.Archetype))
                    {
                        sys.AppendLine($"The mould you were cast in: {mem.Archetype} — {mem.ArchetypeText}");
                        sys.AppendLine("Moral compass: " + (mem.Morality == "principled" ? "principled — you have lines you will not cross, even at a cost."
                                       : mem.Morality == "ruthless" ? "ruthless — ends justify means; scruples are for people who can afford them." : "pragmatic — you do what works and keep your conscience quiet.") +
                                       " Speech: " + (mem.Profanity == "heavy" ? "you curse freely and coarsely." : mem.Profanity == "none" ? "you never swear; coarse talk offends you." : "you swear now and then, when provoked."));
                    }
                }
                if (cfg.EnableReligion)
                {
                    string faith = ForCulture(ReligionFile.Value, s);
                    if (faith.Length > 0)
                        sys.AppendLine("Faith of your people: " + faith + " You yourself are " +
                                       (mem.Piety == "devout" ? "devout: the gods are in your mouth and your decisions." : mem.Piety == "skeptic" ? "a skeptic: you keep the forms and believe little."
                                        : mem.Piety == "indifferent" ? "indifferent: you think of the gods when in trouble." : "observant: you keep the rites as everyone does."));
                    string customs = ForCulture(TraditionsFile.Value, s);
                    if (customs.Length > 0) sys.AppendLine("Customs of your people: " + customs);
                }
            }
            catch (Exception ex) { Log.Error("Trasfondo en el prompt", ex); }
        }

        // ============================================================ evolución del carácter

        private static readonly Tpl TDevelopment = new Tpl("character_development/development.txt",
@"You maintain the inner character of {npc_name}, a person in Calradia. Something has just happened to them that could change who they are:
{event}

Their character now (JSON): {profile}

Decide how this event changes them, as a novelist would: people change slowly and in keeping with who they already are. Most events deepen one trait, harden or soften one attitude, add a goal or a fear.
- Change at most {max_fields} of these fields, and only if the event truly touches them: personality, goals, drives, values, social_behavior, cognitive_style, religion, reputation, speech_style. Rewrite a changed field in full (English, one or two sentences), keeping what still holds.
- backstory_addition: ONE sentence recording what happened, to be appended to their past. Always give it.
- Never change their name, family, rank or the facts of the world.
Answer ONLY with JSON containing backstory_addition and just the fields you changed.");

        public static void Touch() { var _ = TDevelopment.Key; }

        /// <summary>Un suceso que marca: en segundo plano, la IA retoca su carácter.</summary>
        public static void Develop(Speaker s, string whatHappened)
        {
            var cfg = S.Get;
            var mem = s.Memory;
            if (!cfg.EnableCharacterDevelopment || !s.IsHero || mem.Profile == null || mem.Profile.IsEmpty || mem.DevelopmentInFlight) return;
            double now = CampaignTime.Now.ToDays;
            if (mem.LastDevelopmentDay >= 0 && now - mem.LastDevelopmentDay < cfg.DevelopmentMinDays) return;
            mem.DevelopmentInFlight = true;
            mem.LastDevelopmentDay = now;
            string prompt = TDevelopment.Format("npc_name", s.Name, "event", whatHappened, "profile", JsonConvert.SerializeObject(mem.Profile), "max_fields", cfg.DevelopmentMaxFields);
            Log.Info($"Evolución del carácter de {s.Name}: {whatHappened}");
            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, maxTokensOverride: 700, preferredProvider: cfg.ProviderSummaries).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    mem.DevelopmentInFlight = false;
                    if (res == null || !res.Ok) { Log.Warn($"Evolución de {s.Name}: sin respuesta ({res?.Error})"); return; }
                    try
                    {
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a < 0 || b <= a) return;
                        var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                        var p = mem.Profile;
                        int changed = 0;
                        string Take(string key, string old)
                        {
                            var v = ((string)j[key] ?? "").Trim();
                            if (v.Length < 8 || v == old || changed >= cfg.DevelopmentMaxFields) return old;
                            changed++;
                            return v.Length > 500 ? v.Substring(0, 500) : v;
                        }
                        p.Personality = Take("personality", p.Personality); p.Goals = Take("goals", p.Goals); p.Drives = Take("drives", p.Drives);
                        p.Values = Take("values", p.Values); p.SocialBehavior = Take("social_behavior", p.SocialBehavior); p.CognitiveStyle = Take("cognitive_style", p.CognitiveStyle);
                        p.Religion = Take("religion", p.Religion); p.Reputation = Take("reputation", p.Reputation); p.SpeechStyle = Take("speech_style", p.SpeechStyle);
                        var add = ((string)j["backstory_addition"] ?? "").Trim();
                        if (add.Length > 8)
                        {
                            p.Backstory = ((p.Backstory ?? "") + " " + (add.Length > 300 ? add.Substring(0, 300) : add)).Trim();
                            if (p.Backstory.Length > 1800) p.Backstory = p.Backstory.Substring(p.Backstory.Length - 1800);
                        }
                        mem.ProfileRevision++;
                        Log.Info($"Carácter de {s.Name} actualizado (revisión {mem.ProfileRevision}, {changed} campo/s): {add}");
                        SaveFiles.Flush();
                    }
                    catch (Exception ex) { Log.Error("Evolución del carácter", ex); }
                });
            });
        }
    }
}
