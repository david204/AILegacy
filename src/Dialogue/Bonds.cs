using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Lo que une al NPC con el jugador más allá de la relación del juego:
    /// confianza (¿me fío de su palabra?), romance, y qué autoridad tiene el jugador sobre él.
    /// </summary>
    internal static class Bonds
    {
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;

        // ============================================================ confianza

        /// <summary>La primera vez, la confianza sale de lo que ya os une en el juego.</summary>
        public static void EnsureTrust(Speaker s)
        {
            var mem = s.Memory;
            if (mem.TrustSet) return;
            int t = 0;
            if (s.IsHero)
            {
                var h = s.Hero;
                t = (int)h.GetRelationWithPlayer() / 2;
                if (IsCloseFamily(h)) t += 30;
                else if (h.Clan != null && h.Clan == Clan.PlayerClan) t += 20;
                t -= mem.LiesSuspected * S.Get.TrustLostPerLie;
            }
            mem.Trust = Clamp(t, -100, 100);
            mem.TrustSet = true;
        }

        public static string DescribeTrust(int t)
        {
            if (t <= -50) return "you believe nothing they say and expect betrayal";
            if (t <= -15) return "you distrust them and weigh every word";
            if (t < 15) return "you neither trust nor distrust them yet";
            if (t < 40) return "you trust them in small matters";
            if (t < 70) return "you trust them with things that matter";
            return "you would trust them with your life";
        }

        public static int ChangeTrust(Speaker s, int delta)
        {
            EnsureTrust(s);
            int before = s.Memory.Trust;
            s.Memory.Trust = Clamp(before + delta, -100, 100);
            return s.Memory.Trust - before;
        }

        // ============================================================ romance

        public static bool IsCloseFamily(Hero h)
        {
            if (h == null) return false;
            try
            {
                return h.Father == P || h.Mother == P || P.Father == h || P.Mother == h || h.Siblings.Contains(P)
                       || (h.Father != null && (h.Father.Father == P || h.Father.Mother == P))
                       || (h.Mother != null && (h.Mother.Father == P || h.Mother.Mother == P))
                       || (P.Father != null && (P.Father.Father == h || P.Father.Mother == h))
                       || (P.Mother != null && (P.Mother.Father == h || P.Mother.Mother == h));
            }
            catch { return true; }
        }

        /// <summary>¿Puede haber romance entre este NPC y el jugador?</summary>
        public static bool RomancePossible(Speaker s)
        {
            var cfg = S.Get;
            if (!cfg.EnableRomance || !s.IsHero) return false;
            var h = s.Hero;
            if (h.Age < 18 || P.Age < 18 || IsCloseFamily(h)) return false;
            if (h.IsFemale == P.IsFemale && !cfg.RomanceSameSex) return false;
            return true;
        }

        /// <summary>Tu cónyuge ya te quiere aunque AI Legacy no lo hubiera apuntado todavía.</summary>
        public static void EnsureRomance(Speaker s)
        {
            if (!s.IsHero) return;
            var mem = s.Memory;
            if (mem.Romance == 0 && mem.LastRomanceDay < 0 && s.Hero.Spouse == P)
            {
                mem.Romance = Clamp(55 + (int)s.Hero.GetRelationWithPlayer() / 3, 40, 90);
                mem.LastRomanceDay = CampaignTime.Now.ToDays;
            }
        }

        public static string DescribeRomance(int r)
        {
            if (r <= 0) return "none — you feel nothing romantic for them";
            if (r < 20) return "a spark — you notice them, nothing more";
            if (r < 50) return "courtship — you enjoy their attention and flirt back, but you are not won yet";
            if (r < 80) return "in love — you want to be with them";
            return "devoted — they are the love of your life";
        }

        public static int ChangeRomance(Speaker s, int delta)
        {
            var mem = s.Memory;
            int before = mem.Romance;
            mem.Romance = Clamp(before + delta, 0, 100);
            return mem.Romance - before;
        }

        /// <summary>Hablar o escribirse cuenta como contacto: frena el enfriamiento.</summary>
        public static void Touch(HeroMemory mem)
        {
            if (mem != null) mem.LastRomanceDay = CampaignTime.Now.ToDays;
        }

        /// <summary>Cada día: el romance sin contacto se enfría.</summary>
        public static void OnDailyTick()
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableRomance) return;
                double now = CampaignTime.Now.ToDays;
                foreach (var mem in MemoryStore.AllHeroes)
                {
                    if (mem.Romance <= 0) continue;
                    double last = Math.Max(mem.LastRomanceDay, mem.LastConversationDay);
                    if (last < 0) { mem.LastRomanceDay = now; continue; }
                    if (now - last < cfg.RomanceDecayAfterDays) continue;
                    var h = Hero.Find(mem.HeroId);
                    if (h == null || h.IsDead) { mem.Romance = 0; continue; }
                    if (h.PartyBelongedTo != null && h.PartyBelongedTo.IsMainParty) continue;   // viaja contigo: no se enfría
                    int floor = h.Spouse == P ? 40 : 0;
                    if (mem.Romance > floor) mem.Romance--;
                }
            }
            catch (Exception ex) { Log.Error("Romance: tick diario", ex); }
        }

        /// <summary>Una noche juntos. Entre cónyuges puede haber embarazo. Devuelve el texto a enseñar.</summary>
        public static string Intimacy(Speaker s)
        {
            var mem = s.Memory;
            var h = s.Hero;
            mem.LastIntimacyDay = CampaignTime.Now.ToDays;
            Touch(mem);
            try
            {
                if (h.Spouse == P && h.IsFemale != P.IsFemale)
                {
                    var mother = h.IsFemale ? h : P;
                    if (!mother.IsPregnant && mother.Age < 46 && Rng.Next(100) < S.Get.PregnancyChancePercent)
                    {
                        MakePregnantAction.Apply(mother);
                        Log.Info($"Embarazo: {mother.Name}");
                    }
                }
            }
            catch (Exception ex) { Log.Error("Intimidad: embarazo", ex); }
            return L.F($"Pasas un rato a solas con {s.Name}.");
        }

        // ============================================================ tensión y amenaza

        /// <summary>Tensión actual, ya descontado lo que se ha calmado con los días.</summary>
        public static int Tension(HeroMemory mem)
        {
            if (mem.Tension <= 0) return 0;
            double now = CampaignTime.Now.ToDays;
            if (mem.TensionDay < 0) mem.TensionDay = now;
            int cooled = (int)((now - mem.TensionDay) * S.Get.TensionDecayPerDay);
            if (cooled > 0)
            {
                mem.Tension = Math.Max(0, mem.Tension - cooled);
                mem.TensionDay = now;
            }
            return mem.Tension;
        }

        public static int ChangeTension(Speaker s, int delta)
        {
            var mem = s.Memory;
            int before = Tension(mem);
            mem.Tension = Clamp(before + delta, 0, 100);
            mem.TensionDay = CampaignTime.Now.ToDays;
            return mem.Tension - before;
        }

        public static string DescribeTension(int t)
        {
            if (t < 15) return "calm";
            if (t < 40) return "uneasy — words are getting sharp";
            if (t < 60) return "heated — one more provocation and this turns ugly";
            if (t < 85) return "at breaking point — you are ready to act, not just talk";
            return "explosive — violence is a breath away";
        }

        /// <summary>¿Quién intimida a quién? Fuerzas de cada uno, y si el NPC está en manos del jugador o al revés.</summary>
        public static string DescribeThreat(Speaker s)
        {
            try
            {
                if (!s.IsHero)
                {
                    int men = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty?.MemberRoster.TotalManCount ?? 1;
                    return men > 20 ? "They are an armed leader with soldiers at their back and you are a commoner: if they threaten you, you are afraid, though guards and neighbours are near."
                                    : "They are armed and you are a commoner: a threat from them frightens you, though guards and neighbours are near.";
                }
                var h = s.Hero;
                if (h.IsPrisoner) return "You are a prisoner: you are at the mercy of your captors and cannot back any threat with force.";
                var mine = OrderSystem.PartyLedBy(h);
                var theirs = TaleWorlds.CampaignSystem.Party.MobileParty.MainParty;
                if (mine != null && theirs != null && mine.CurrentSettlement == null)
                {
                    int a = Math.Max(1, mine.MemberRoster.TotalHealthyCount), b = Math.Max(1, theirs.MemberRoster.TotalHealthyCount);
                    if (theirs.Army != null && theirs.Army.LeaderParty == theirs) b = Math.Max(b, theirs.Army.TotalHealthyMembers);
                    if (mine.Army != null) a = Math.Max(a, mine.Army.TotalHealthyMembers);
                    double ratio = (double)b / a;
                    string who = ratio >= 3 ? "they could crush you easily — their threats are real and you know it"
                               : ratio >= 1.5 ? "they are clearly stronger — a fight would probably go badly for you"
                               : ratio > 0.67 ? "you are roughly evenly matched — a fight could go either way"
                               : ratio > 0.34 ? "you are clearly stronger — their threats sound hollow"
                               : "you could crush them easily — threats from them are almost laughable";
                    return $"Here in the field you have {a} fighting men and they have {b}: {who}.";
                }
                var st = s.Settlement;
                if (st != null && st.OwnerClan == h.Clan && h.Clan != Clan.PlayerClan)
                    return "You are in your own hall, surrounded by your guards: a threat from them here is bold or foolish.";
                if (st != null && st.OwnerClan == Clan.PlayerClan && h.Clan != Clan.PlayerClan)
                    return "You are inside their walls, among their guards: you cannot back a threat with force here.";
                return "";
            }
            catch { return ""; }
        }

        // ============================================================ autoridad

        /// <summary>Qué poder tiene el jugador para dar órdenes a este NPC (para el prompt).</summary>
        public static string DescribeAuthority(Hero npc)
        {
            try
            {
                if (npc.Clan != null && npc.Clan == Clan.PlayerClan)
                    return "They are the head of your clan: when they give your party an order, you carry it out unless it is madness.";
                if (npc.MapFaction is Kingdom k && k == P.MapFaction)
                {
                    if (k.Leader == P)
                        return npc.Clan != null && npc.Clan.IsUnderMercenaryService
                            ? "They are the ruler who pays your mercenary contract: you follow reasonable military orders while the contract lasts."
                            : "They are your sovereign: you owe them military service and normally obey their orders in war, though you may argue if an order is foolish or against your honour.";
                    if (k.Leader == npc) return "They are your vassal: you command them, they do not command you. You do what they ask only if it suits you.";
                    return "They are a fellow vassal of your realm with no authority over you: helping them is a favour that depends on how much you like and trust them, and on what you gain.";
                }
                if (npc.Spouse == P) return "They are your spouse: no formal authority, but you usually stand by each other.";
                return "They have no authority over you. You do what they ask only for a good reason: friendship, trust, payment, a shared enemy or your own gain.";
            }
            catch { return ""; }
        }

        // ============================================================ texto para el prompt

        public static void AppendTo(StringBuilder sys, Speaker s)
        {
            var cfg = S.Get;
            var mem = s.Memory;
            if (cfg.EnableTrust)
            {
                EnsureTrust(s);
                sys.AppendLine($"How far you trust their word (trust {mem.Trust} on a -100..100 scale, separate from liking them): {DescribeTrust(mem.Trust)}. " +
                               (mem.Trust >= cfg.TrustForSecrets
                                   ? "You trust them enough to confide sensitive things if it comes up naturally."
                                   : $"That is NOT enough to confide in them: keep secrets, plans, troop movements, intrigues and anything that could hurt you or yours to yourself (you would need trust {cfg.TrustForSecrets}+). Deflect, stay vague or lie."));
            }
            if (RomancePossible(s))
            {
                EnsureRomance(s);
                var h = s.Hero;
                var line = $"Romantic feelings for them (romance {mem.Romance} on a 0..100 scale): {DescribeRomance(mem.Romance)}.";
                if (h.Spouse != null && h.Spouse != P && h.Spouse.IsAlive)
                    line += $" You are married to {h.Spouse.Name}: giving in would be adultery — a grave scandal that your honour, fear or loyalty may forbid.";
                else if (h.Spouse != P && mem.Romance < cfg.RomanceForMarriage)
                    line += $" You would not accept a marriage proposal yet (romance {cfg.RomanceForMarriage}+ needed): love has to be earned over time, not in one conversation.";
                sys.AppendLine(line);
            }
            if (s.IsHero && OrderSystem.PartyLedBy(s.Hero) != null)
            {
                var auth = DescribeAuthority(s.Hero);
                if (auth.Length > 0) sys.AppendLine("Their authority over you: " + auth);
            }
            if (cfg.EnableTension)
            {
                int t = Tension(mem);
                sys.AppendLine($"Tension between you two right now ({t} on a 0..100 scale): {DescribeTension(t)}." +
                               (t >= cfg.TensionForViolence ? " You are past the point of patience: act on it if they push again (end the talk, throw them out, or one of the violent actions below if listed)." : ""));
                var threat = DescribeThreat(s);
                if (threat.Length > 0) sys.AppendLine("Balance of force: " + threat + " Let it shape how you react to threats and how boldly you threaten: brave characters defy stronger foes, cautious ones give way.");
            }
        }

        private static int Clamp(int v, int min, int max) => Math.Max(min, Math.Min(max, v));

        // ============================================================ entre personajes (no contigo)

        public static string OtherWord(int t) =>
            t <= -60 ? "cannot stand them" : t <= -25 ? "distrusts and resents them" : t < -8 ? "is cool towards them" : t <= 8 ? "has no strong feeling about them"
            : t < 25 ? "thinks well of them" : t < 60 ? "trusts and likes them" : "would do almost anything for them";

        /// <summary>Algo ha pasado entre dos personajes con nombre: cambia lo que uno piensa del otro y lo recuerda.</summary>
        public static void TouchOther(Speaker from, Speaker to, int delta, string note)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableNpcBonds || from == null || to == null || !from.IsHero || !to.IsHero || from.Hero == to.Hero) return;
                var mem = from.Memory;
                if (!mem.Others.TryGetValue(to.Hero.StringId, out var b)) mem.Others[to.Hero.StringId] = b = new OtherBond { Trust = Clamp(from.Hero.GetRelation(to.Hero), -60, 60) };
                b.Name = to.Name;
                b.Trust = Clamp(b.Trust + delta, -100, 100);
                b.Day = CampaignTime.Now.ToDays;
                if (!string.IsNullOrWhiteSpace(note))
                {
                    b.Notes.Add(note.Length > 220 ? note.Substring(0, 220) : note);
                    while (b.Notes.Count > cfg.NpcBondNotes) b.Notes.RemoveAt(0);
                }
                // Lo que pasa entre ellos se nota también en la relación del juego (poco a poco)
                if (cfg.NpcBondsAffectRelation && Math.Abs(delta) >= 4)
                    ChangeRelationAction.ApplyRelationChangeBetweenHeroes(from.Hero, to.Hero, delta > 0 ? 1 : -1, false);
                while (mem.Others.Count > cfg.NpcBondMaxPeople)
                    mem.Others.Remove(mem.Others.OrderBy(kv => kv.Value.Day).First().Key);
            }
            catch (Exception ex) { Log.Error("Vínculo entre personajes", ex); }
        }

        /// <summary>Lo que hay entre estos dos, visto por el primero (para sus charlas entre ellos).</summary>
        public static string Between(Speaker a, Speaker b)
        {
            try
            {
                if (!S.Get.EnableNpcBonds || !a.IsHero || !b.IsHero) return "";
                var parts = new List<string>();
                if (a.Memory.Others.TryGetValue(b.Hero.StringId, out var ab)) parts.Add($"{a.Name} {OtherWord(ab.Trust)}" + (ab.Notes.Count > 0 ? $" (last between them: {ab.Notes[ab.Notes.Count - 1]})" : ""));
                if (b.Memory.Others.TryGetValue(a.Hero.StringId, out var ba)) parts.Add($"{b.Name} {OtherWord(ba.Trust)}");
                return parts.Count == 0 ? "" : " From their own dealings: " + string.Join("; ", parts) + ".";
            }
            catch { return ""; }
        }

        /// <summary>Para hablar contigo: con quién ha tratado últimamente y cómo quedó la cosa.</summary>
        public static void AppendOthers(System.Text.StringBuilder sys, Speaker s)
        {
            try
            {
                if (!S.Get.EnableNpcBonds || !s.IsHero || s.Memory.Others.Count == 0) return;
                double now = CampaignTime.Now.ToDays;
                var list = s.Memory.Others.Values.Where(b => b.Day >= 0 && !string.IsNullOrEmpty(b.Name)).OrderByDescending(b => b.Day).Take(4).ToList();
                if (list.Count == 0) return;
                sys.AppendLine("Your own dealings with other people lately (they happened; you may bring them up or act on them):");
                foreach (var b in list)
                {
                    int d = (int)(now - b.Day);
                    sys.AppendLine($"- {b.Name}: you {OtherWord(b.Trust).Replace("cannot", "cannot").Replace("distrusts and resents", "distrust and resent").Replace("is cool", "are cool").Replace("has no", "have no").Replace("thinks", "think").Replace("trusts and likes", "trust and like")}"
                                   + (b.Notes.Count > 0 ? $". {(d <= 0 ? "Today" : d == 1 ? "Yesterday" : d + " days ago")}: {b.Notes[b.Notes.Count - 1]}" : "."));
                }
            }
            catch { }
        }
    }
}
