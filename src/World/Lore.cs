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
        /// <summary>Who can get it: ruler, lord, town_notable, village_notable, gang, wanderer, commoner, guard, merchant.</summary>
        [JsonProperty("roles")] public List<string> Roles = new List<string>();
        /// <summary>principled | pragmatic | ruthless</summary>
        [JsonProperty("morality")] public string Morality = "pragmatic";
        /// <summary>none | mild | heavy</summary>
        [JsonProperty("profanity")] public string Profanity = "mild";
        /// <summary>devout | observant | indifferent | skeptic</summary>
        [JsonProperty("piety")] public string Piety = "observant";
    }

    /// <summary>A way of speaking someone is known for (world_data\voices.json). The AI never sees the name, only the prompt.</summary>
    public class SpeechHabit
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        /// <summary>What they do, written after "this person": "speaks in proverbs and folk sayings".</summary>
        [JsonProperty("prompt")] public string Prompt;
        /// <summary>any | adult | child</summary>
        [JsonProperty("age")] public string Age = "any";
        [JsonProperty("weight")] public float Weight = 1f;
    }

    /// <summary>
    /// Editable character background: Modules\AILegacy\world_data\archetypes.json (character archetypes),
    /// religion.json (faith per culture) and cultural_traditions.json (customs per culture).
    /// Missing files are created with the defaults, and edits are picked up without a restart.
    /// A campaign's own copy (see WorldFiles) wins over the module's.
    /// </summary>
    internal static class Lore
    {
        private static string Dir => Path.Combine(Paths.ModuleDir, "world_data");

        private class Cache<T> where T : class
        {
            private readonly string _file; private readonly Func<T> _defaults;
            private T _value; private DateTime _stamp; private DateTime _nextCheck; private string _path;
            public Cache(string file, Func<T> defaults) { _file = file; _defaults = defaults; }
            public void Forget() { _value = null; }
            public T Value
            {
                get
                {
                    try
                    {
                        var now = DateTime.UtcNow;
                        var path = WorldFiles.Resolve(_file);
                        if (_value != null && path == _path && now < _nextCheck) return _value;
                        _nextCheck = now.AddSeconds(5);
                        var def = Path.Combine(Dir, _file);
                        if (!File.Exists(def))
                        {
                            Directory.CreateDirectory(Dir);
                            File.WriteAllText(def, JsonConvert.SerializeObject(_defaults(), Formatting.Indented), new UTF8Encoding(false));
                        }
                        var stamp = File.GetLastWriteTimeUtc(path);
                        if (_value == null || stamp != _stamp || path != _path)
                        {
                            _value = JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8)) ?? _defaults();
                            _stamp = stamp; _path = path;
                        }
                    }
                    catch (Exception ex) { Log.Warn($"world_data\\{_file} unreadable, using the factory default: {ex.Message}"); _value = _value ?? _defaults(); }
                    return _value;
                }
            }
        }

        private static readonly Cache<List<Archetype>> ArchetypesFile = new Cache<List<Archetype>>("archetypes.json", DefaultArchetypes);
        private static readonly Cache<Dictionary<string, string>> ReligionFile = new Cache<Dictionary<string, string>>("religion.json", DefaultReligions);
        private static readonly Cache<Dictionary<string, string>> TraditionsFile = new Cache<Dictionary<string, string>>("cultural_traditions.json", DefaultTraditions);

        // ============================================================ built-in defaults

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

        // ============================================================ character archetype

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

        /// <summary>Writes the module's default files if they are not there yet, so the editor has something to show.</summary>
        public static void EnsureFiles()
        {
            var a = ArchetypesFile.Value; var r = ReligionFile.Value; var t = TraditionsFile.Value; var v = HabitsFile.Value;
        }

        /// <summary>Drops what is cached so an edit made from the editor counts from the next sentence on.</summary>
        public static void Reload() { ArchetypesFile.Forget(); ReligionFile.Forget(); TraditionsFile.Forget(); HabitsFile.Forget(); }

        public static List<KeyValuePair<string, string>> ArchetypeList() =>
            ArchetypesFile.Value.Select(a => new KeyValuePair<string, string>(a.Name, a.Description)).ToList();

        /// <summary>Sets an archetype by hand from the editor. An empty name clears it so it gets picked again automatically.</summary>
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
                // Game traits tip the scale: honor and mercy lean principled, cruelty and scheming lean ruthless
                int lean = 0;
                if (s.IsHero)
                    lean = s.Hero.GetTraitLevel(DefaultTraits.Honor) + s.Hero.GetTraitLevel(DefaultTraits.Mercy) - Math.Max(0, s.Hero.GetTraitLevel(DefaultTraits.Calculating) - 1);
                var fit = pool.Where(a => lean >= 2 ? a.Morality == "principled" : lean <= -2 ? a.Morality == "ruthless" : true).ToList();
                if (fit.Count == 0) fit = pool;
                // Stable pick per character (FNV hash of the key, so it doesn't change between sessions)
                unchecked
                {
                    uint h = 2166136261;
                    foreach (char c in s.Key) { h ^= c; h *= 16777619; }
                    var a = fit[(int)(h % (uint)fit.Count)];
                    mem.Archetype = a.Name; mem.ArchetypeText = a.Description; mem.Morality = a.Morality; mem.Profanity = a.Profanity; mem.Piety = a.Piety;
                }
            }
            catch (Exception ex) { Log.Error("Archetype", ex); }
        }

        private static string ForCulture(Dictionary<string, string> d, Speaker s)
        {
            string id = s.Culture?.StringId ?? "";
            if (d.TryGetValue(id, out var t)) return t;
            foreach (var kv in d) if (kv.Key != "default" && id.StartsWith(kv.Key)) return kv.Value;
            return d.TryGetValue("default", out var def) ? def : "";
        }

        /// <summary>Adds archetype, morals, speech, faith and customs to the system prompt.</summary>
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
                        string habit = HabitForTalk(s);
                        if (habit.Length > 0) sys.AppendLine(habit);
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
            catch (Exception ex) { Log.Error("Backstory in prompt", ex); }
        }

        // ============================================================ speech habits (world_data\voices.json)

        private static readonly Cache<List<SpeechHabit>> HabitsFile = new Cache<List<SpeechHabit>>("voices.json", DefaultHabits);

        private static SpeechHabit H(string id, string name, string prompt, string age = "any") => new SpeechHabit { Id = id, Name = name, Prompt = prompt, Age = age, Weight = 1f };

        private static List<SpeechHabit> DefaultHabits() => new List<SpeechHabit>
        {
            H("proverbs", "Speaks in proverbs", "answers with old sayings and proverbs, some of them garbled or made up on the spot"),
            H("answers_question", "Answers with a question", "tends to answer a question with another question"),
            H("trails_off", "Leaves sentences unfinished", "often lets a sentence trail off and leaves the end hanging"),
            H("third_person", "Speaks of self in the third person", "now and then talks about themself in the third person, by name"),
            H("swears_by_dead", "Swears by the dead", "swears by dead parents, grandparents or fallen comrades to make a point"),
            H("filler_phrase", "A pet filler phrase", "has a pet filler phrase of their own that keeps slipping into their talk"),
            H("laughs_own_jokes", "Laughs at own jokes", "chuckles at their own jokes before anyone else can react"),
            H("trade_jargon", "Trade words for everything", "describes everything in the words of their own trade or craft"),
            H("diminutives", "Little words", "puts small, fond diminutives on things and people"),
            H("stiff_formal", "Stiff and formal", "speaks stiffly and formally, even about trivial things"),
            H("counts_things", "Counts everything", "numbers their points and gives exact counts, sums and dates"),
            H("whispers", "Lowers the voice", "drops to a whisper for anything that matters, even when nobody else is around"),
            H("too_loud", "Too loud", "talks loudly, as if everyone around were a little deaf"),
            H("echoes", "Echoes your words", "repeats the other person's last words before answering"),
            H("old_epic", "Old heroic style", "slips into a grand, old-fashioned heroic way of speaking"),
            H("self_corrects", "Corrects themself", "corrects themself mid-sentence, hunting for the exact word"),
            H("animal_likeness", "Animal comparisons", "compares people and situations to animals"),
            H("food_talk", "Food comparisons", "explains things through food, cooking and meals"),
            H("weather_first", "Starts with the weather", "opens nearly every talk with the weather or the season"),
            H("wrong_names", "Gets names wrong", "keeps getting people's names slightly wrong"),
            H("nicknames", "Nicknames for everyone", "gives everyone a nickname instead of using their name"),
            H("sighs", "Sighs before speaking", "sighs heavily before saying anything of weight"),
            H("elder_says", "Quotes an elder", "keeps quoting what an old relative or teacher used to say"),
            H("exaggerates", "Exaggerates", "exaggerates everything wildly: numbers, dangers, distances"),
            H("understatement", "Dry understatement", "describes disasters and wonders with flat, dry understatement"),
            H("rhymes", "Accidental rhymes", "falls into little rhymes without meaning to"),
            H("tangents", "Wanders off", "wanders into long side stories before getting to the point"),
            H("calls_gods", "Calls on the gods", "calls on the gods or the holy ones of their people every few sentences"),
            H("superstitious", "Superstitious", "fills their talk with small charms and warnings against bad luck"),
            H("nervous_laugh", "Nervous laugh", "gives a short nervous laugh when uneasy"),
            H("stammers", "Stammers when stirred", "stammers and trips over words when excited or upset"),
            H("talks_to_tool", "Talks to their animal or tool", "speaks aside to their horse, dog, tool or weapon as if it could answer"),
            H("anecdotes", "An anecdote for everything", "answers almost everything with a story of something that once happened to them"),
            H("yes_but", "Agrees, then argues", "agrees first and then argues the opposite"),
            H("flatters", "Flatters", "slips compliments into everything they say"),
            H("archaic", "Old words", "uses old-fashioned words that most people stopped using long ago"),
            H("street_talk", "Street talk", "talks in rough street slang"),
            H("prices", "Puts a price on things", "puts a price on everything, people and favours included"),
            H("asides", "Asides to nobody", "mutters little comments to nobody in particular"),
            H("literal", "Takes things literally", "takes figures of speech literally and answers them that way"),
            H("singsong", "Sing-song voice", "speaks in a lilting, sing-song way, stretching some words"),
            H("boasts_kin", "Boasts of relatives", "keeps bringing up the deeds of a relative, famous or not"),
            H("colourful_curses", "Colourful curses", "makes up elaborate, colourful curses and blessings instead of plain words"),
            H("gloomy", "Gloomy remarks", "drops short gloomy remarks about how it will all end badly"),
            H("big_words", "Misused big words", "uses learned words, often in the wrong place"),
            H("finishes_sentences", "Finishes your sentences", "interrupts and finishes other people's sentences, not always correctly"),
            H("odd_order", "Odd word order", "puts words in an odd order, like someone who thinks in another tongue"),
            H("hurried", "Always in a hurry", "talks fast and keeps saying they have little time"),
            H("orders", "Speaks in orders", "phrases everything as an order, even a request", "adult"),
            H("rough_affection", "Insults as affection", "uses rough insults as terms of affection", "adult"),
            H("quiet_menace", "Quiet menace", "says threatening things softly and politely", "adult"),
            H("contract_talk", "Talks like a contract", "speaks like a written agreement, with terms, conditions and exceptions", "adult"),
            H("soldier_words", "Soldier's words", "uses soldiers' words for everyday life: flanks, sieges, retreats", "adult"),
            H("sailor_words", "Sailor's words", "talks like a sailor, with words of the sea for things on land", "adult"),
            H("aches", "Complains of aches", "keeps mentioning their aches, old wounds and bad knees", "adult"),
            H("calls_child", "Calls everyone 'child'", "calls anyone younger 'child', 'lad' or 'lass'", "adult"),
            H("why", "Asks 'but why?'", "keeps asking why, and why again", "child"),
            H("parent_says", "Quotes their parents", "repeats what their mother or father says, word for word", "child"),
            H("made_up_words", "Makes up words", "makes up words for things they don't know the name of", "child"),
            H("dares", "Dares everyone", "turns everything into a dare or a bet", "child"),
            H("imaginary_friend", "Imaginary friend", "keeps talking to and quoting an imaginary friend", "child"),
            H("grown_up_words", "Grown-up phrases", "uses grown-up phrases they have overheard, not quite right", "child"),
            H("solemn_polite", "Solemnly polite", "is very solemn and polite, like a little courtier", "child"),
            H("counts_fingers", "Counts on fingers", "counts things on their fingers out loud", "child"),
        };

        private static readonly System.Text.RegularExpressions.Regex HabitId = new System.Text.RegularExpressions.Regex("^[a-z0-9_]{2,48}$");

        /// <summary>The habits as they can be used: ids trimmed and lowercased, no empty or repeated ids, no empty prompts.</summary>
        public static List<SpeechHabit> Habits()
        {
            var list = new List<SpeechHabit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in HabitsFile.Value ?? new List<SpeechHabit>())
            {
                if (h == null) continue;
                string id = (h.Id ?? "").Trim().ToLowerInvariant();
                string prompt = NormalizeHabit(h.Prompt);
                if (id.Length == 0 || prompt.Length == 0 || !seen.Add(id)) continue;
                float w = float.IsNaN(h.Weight) || float.IsInfinity(h.Weight) ? 1f : h.Weight;
                if (w <= 0f) continue;
                string age = (h.Age ?? "").Trim().ToLowerInvariant();
                list.Add(new SpeechHabit { Id = id, Name = string.IsNullOrWhiteSpace(h.Name) ? id : h.Name.Trim(), Prompt = prompt, Age = age == "adult" || age == "child" ? age : "any", Weight = w });
            }
            return list;
        }

        /// <summary>One line, without a leading "this person", without the full stop, first letter small (unless it starts a name).</summary>
        public static string NormalizeHabit(string prompt)
        {
            string p = System.Text.RegularExpressions.Regex.Replace((prompt ?? "").Trim(), @"\s+", " ");
            if (p.StartsWith("this person ", StringComparison.OrdinalIgnoreCase)) p = p.Substring(12).TrimStart();
            p = p.TrimEnd('.', '!', ';', ',', ' ');
            if (p.Length > 1 && char.IsUpper(p[0]) && !char.IsUpper(p[1])) p = char.ToLowerInvariant(p[0]) + p.Substring(1);
            return p;
        }

        private static readonly Random HabitRng = new Random();

        /// <summary>
        /// The way of speaking this character is known for. Given once, the first time a prompt is built for them with archetypes on,
        /// at random by weight among the habits for their age (children have their own); one a neighbour already has (same clan, same
        /// place) weighs 0.08 as much. Given again only if theirs is no longer in voices.json.
        /// </summary>
        public static void EnsureHabit(Speaker s)
        {
            var mem = s?.Memory;
            if (mem == null || !S.Get.EnableArchetypes) return;
            try
            {
                var all = Habits();
                if (all.Count == 0) return;
                if (!string.IsNullOrEmpty(mem.SpeechHabit) && all.Any(h => h.Id == mem.SpeechHabit)) return;
                bool child = s.Age < 18f || s.KnownChild;
                var pool = all.Where(h => h.Age == "any" || h.Age == (child ? "child" : "adult")).ToList();
                if (pool.Count == 0) return;
                var near = NeighbourHabits(s);
                double total = pool.Sum(h => h.Weight * (near.Contains(h.Id) ? 0.08 : 1.0));
                double roll = HabitRng.NextDouble() * total;
                var pick = pool[pool.Count - 1];
                foreach (var h in pool) { roll -= h.Weight * (near.Contains(h.Id) ? 0.08 : 1.0); if (roll <= 0) { pick = h; break; } }
                mem.SpeechHabit = pick.Id;
                Log.Info($"[ARCHETYPES] {s.Name}: nature {mem.Archetype ?? "-"}, swearing {mem.Profanity ?? "-"}, speech habit {pick.Id}");
            }
            catch (Exception ex) { Log.Error("Speech habit", ex); }
        }

        /// <summary>The habits already taken by people around them: their clan, and the people of the place they are in (or come from).</summary>
        private static HashSet<string> NeighbourHabits(Speaker s)
        {
            var set = new HashSet<string>();
            try
            {
                var people = new List<Hero>();
                if (s.IsHero && s.Hero.Clan != null) people.AddRange(s.Hero.Clan.Heroes);
                var st = s.Settlement ?? s.HomeSettlement;
                if (st != null)
                {
                    people.AddRange(st.Notables);
                    people.AddRange(st.HeroesWithoutParty);
                    foreach (var p in st.Parties) if (p?.LeaderHero != null) people.Add(p.LeaderHero);
                }
                foreach (var h in people)
                {
                    if (h == null || (s.IsHero && h == s.Hero)) continue;
                    var m = Memory.MemoryStore.Find(h.StringId);
                    if (!string.IsNullOrEmpty(m?.SpeechHabit)) set.Add(m.SpeechHabit);
                }
            }
            catch { }
            return set;
        }

        private static string HabitPrompt(Speaker s)
        {
            if (s == null || !S.Get.EnableArchetypes) return null;
            EnsureHabit(s);
            string id = s.Memory?.SpeechHabit;
            return string.IsNullOrEmpty(id) ? null : Habits().FirstOrDefault(h => h.Id == id)?.Prompt;
        }

        private static readonly Tpl THabitCreate = new Tpl("character_creation/speech_habit.txt",
            "VOICE HABIT — this person {habit}. Build speech_style and sample_lines around this habit, in this person's own way; it is the first thing people notice about how they talk.");
        private static readonly Tpl THabitTalk = new Tpl("archetypes/speech_habit_talk.txt", "Voice habit: {habit} (often, but not in every line).");
        private static readonly Tpl THabitOfficial = new Tpl("archetypes/speech_habit_official.txt",
            "Voice habit in life: {habit}. In an official text it may show once at most, and only if it suits a proclamation; otherwise leave it out.");

        /// <summary>For the prompt that writes their character: build the way they speak around their habit. Empty with no habit.</summary>
        public static string HabitForCreation(Speaker s) { var h = HabitPrompt(s); return h == null ? "" : THabitCreate.Format("habit", h); }

        /// <summary>For talk (alone, in groups, in a fight). Empty with no habit.</summary>
        public static string HabitForTalk(Speaker s) { var h = HabitPrompt(s); return h == null ? "" : THabitTalk.Format("habit", h); }

        /// <summary>For decrees and a ruler's statements: a trace at most. Empty with no habit.</summary>
        public static string HabitForOfficial(Speaker s) { var h = HabitPrompt(s); return h == null ? "" : THabitOfficial.Format("habit", h); }

        public static List<KeyValuePair<string, string>> HabitList() =>
            Habits().Select(h => new KeyValuePair<string, string>(h.Id, h.Name + (h.Age == "child" ? " (children)" : ""))).ToList();

        /// <summary>From the editor: a habit by id, or empty for a new one at random the next time.</summary>
        public static void SetHabit(Memory.HeroMemory mem, string id)
        {
            if (mem == null) return;
            id = (id ?? "").Trim().ToLowerInvariant();
            mem.SpeechHabit = id.Length == 0 ? null : Habits().Any(h => h.Id == id) ? id : mem.SpeechHabit;
        }

        public static bool ValidHabitId(string id) => HabitId.IsMatch(id ?? "");

        // ============================================================ character development

        private static readonly Tpl TDevelopment = new Tpl("character_development/development.txt",
@"You maintain the inner character of {npc_name}, a person in Calradia. Something has just happened to them that could change who they are:
{event}

Their character now (JSON): {profile}

Decide how this event changes them, as a novelist would: people change slowly and in keeping with who they already are. Most events deepen one trait, harden or soften one attitude, add a goal or a fear.
- Change at most {max_fields} of these fields, and only if the event truly touches them: personality, goals, drives, values, social_behavior, cognitive_style, religion, reputation, speech_style. Rewrite a changed field in full (English, one or two sentences), keeping what still holds.
- backstory_addition: ONE sentence recording what happened, to be appended to their past. Always give it.
- Never change their name, family, rank or the facts of the world.
Answer ONLY with JSON containing backstory_addition and just the fields you changed.");

        public static void Touch() { var _ = TDevelopment.Key + THabitCreate.Key + THabitTalk.Key + THabitOfficial.Key; }

        /// <summary>After a defining event, the AI reworks the character's profile in the background.</summary>
        public static void Develop(Speaker s, string whatHappened)
        {
            var cfg = S.Get;
            var mem = s.Memory;
            if (!cfg.EnableCharacterDevelopment || !s.IsHero || mem.Profile == null || mem.Profile.IsEmpty || mem.DevelopmentInFlight) return;
            double now = CampaignTime.Now.ToDays;
            if (mem.LastDevelopmentDay >= 0 && now - mem.LastDevelopmentDay < cfg.DevelopmentMinDays) return;
            mem.DevelopmentInFlight = true;
            mem.LastDevelopmentDay = now;
            int session = WorldSim.Session;
            string prompt = TDevelopment.Format("npc_name", s.Name, "event", whatHappened, "profile", JsonConvert.SerializeObject(mem.Profile), "max_fields", cfg.DevelopmentMaxFields);
            Log.Info($"Character evolution of {s.Name}: {whatHappened}");
            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "lore", maxTokensOverride: 700, preferredProvider: cfg.ProviderSummaries).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    mem.DevelopmentInFlight = false;
                    // Another save was loaded meanwhile: this memory belongs to the campaign that asked, and saving now would write it into the one loaded
                    if (session != WorldSim.Session) { Log.Info($"Evolution of {s.Name}: response from another game, discarded"); return; }
                    if (res == null || !res.Ok) { Log.Warn($"Evolution of {s.Name}: no response ({res?.Error})"); return; }
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
                        Log.Info($"Character of {s.Name} updated (revision {mem.ProfileRevision}, {changed} field(s)): {add}");
                        SaveFiles.Flush();
                    }
                    catch (Exception ex) { Log.Error("Character evolution", ex); }
                });
            });
        }
    }
}
