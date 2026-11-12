using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// La guardia. Tras una pelea en un asentamiento (o si alguien grita pidiéndola), el guardia más cercano se acerca,
    /// te pide cuentas y, según lo que le digas y quién seas, te deja ir, te multa o te encierra.
    /// </summary>
    internal static class Justice
    {
        private static Speaker _guard;        // el guardia que lleva el caso ahora
        private static bool _resolved;
        private static bool _talked;
        private static Settlement _jailIn;    // pendiente de encerrar al jugador al salir de la escena
        private static string _case, _jailStanding, _jailCrime;      // lo ocurrido, para el juicio del señor
        private static Speaker _jailGuard;
        private static Hero P => Hero.MainHero;

        private static readonly Tpl TSituation = new Tpl("justice/guard_after_trouble.txt",
@"You are a guard on duty in {settlement}, which belongs to {owner} of {faction}. {what_happened}
You walked up to them. Your duty is to find out what happened and settle it on the spot. You alone decide, with one of these actions when you have heard enough (not in your very first words):
- let_go: you accept their account or it is not worth the trouble; they walk free, perhaps with a warning.
- fine:<denars>: they pay on the spot for the disturbance (usually {fine_low} to {fine_high} denars, more for blood or for the insolent).
- arrest: you take them to the cells. For serious blood, for those who lie to your face, threaten you, cannot or will not pay, or are already wanted here.
What weighs on you: how bad it was (fists are common, steel is serious, a death is grave); who they are to this place ({standing}); their standing with the law here ({crime}); and how they answer you — respect and a straight story go far, lies, mockery and threats do not. You are a common soldier: wary of the powerful, hard on troublemakers, not easily bought but not a saint either. Do not let them stall for ever: after a few exchanges, decide.");

        public static void Touch() { var _ = TSituation.Key; }

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

        public static bool Active(Speaker s) => s != null && _guard != null && !_resolved && s.SituationKind == "justice" && s.Key == _guard.Key;

        // ============================================================ buscar un guardia y mandarlo

        private static Agent NearestGuard(Agent except, float maxDistance)
        {
            try
            {
                var m = Mission.Current;
                if (m == null || Agent.Main == null) return null;
                Agent best = null; float bestD = float.MaxValue;
                foreach (var a in m.Agents)
                {
                    if (a == null || !a.IsHuman || !a.IsActive() || a == Agent.Main || a == except || a.IsHero) continue;
                    var c = a.Character as CharacterObject;
                    if (c == null || (c.Occupation != Occupation.Guard && c.Occupation != Occupation.Soldier && c.Occupation != Occupation.PrisonGuard)) continue;
                    if (a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main)) continue;
                    float d = a.Position.Distance(Agent.Main.Position);
                    if (d < bestD && d <= maxDistance) { best = a; bestD = d; }
                }
                return best;
            }
            catch (Exception ex) { Log.Error("Buscando guardia", ex); return null; }
        }

        private static string Standing(Settlement st)
        {
            if (st.OwnerClan != null && st.OwnerClan == Clan.PlayerClan) return "they are the lord of this very place";
            if (st.MapFaction != null && st.MapFaction == P.MapFaction)
                return P.MapFaction is Kingdom k && k.Leader == P ? "they are your sovereign" : "they serve the same realm as your lord" + (Clan.PlayerClan.Tier >= 3 ? ", and are a noble of some name" : "");
            if (st.MapFaction != null && P.MapFaction != null && st.MapFaction.IsAtWarWith(P.MapFaction)) return "they belong to a realm AT WAR with yours: an enemy inside your walls";
            return Clan.PlayerClan != null && Clan.PlayerClan.Tier >= 4 ? "an outsider, but a famous one" : "an outsider with no standing here";
        }

        private static string Crime(Settlement st)
        {
            try
            {
                float c = st.MapFaction?.MainHeroCrimeRating ?? 0f;
                return c < 5 ? "clean" : c < 30 ? "known for small trouble" : c < 60 ? "a known offender" : "a wanted criminal";
            }
            catch { return "unknown"; }
        }

        /// <summary>Hay jaleo: si hay un guardia lo bastante cerca (o lo han llamado a gritos), va a por ti.</summary>
        public static bool Summon(string whatHappenedEn, Agent except, bool called)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.AllowJustice || _guard != null || Initiative.Busy || Mission.Current == null) return false;
                var st = Settlement.CurrentSettlement;
                if (st == null || st.OwnerClan == Clan.PlayerClan) return false;       // en tu casa nadie te pide cuentas
                var agent = NearestGuard(except, called ? cfg.GuardCallDistance : cfg.GuardHearingDistance);
                if (agent == null) return false;

                var s = Speaker.FromResident((CharacterObject)agent.Character, agent);
                s.SituationKind = "justice";
                s.Situation = TSituation.Format("settlement", st.Name, "owner", st.OwnerClan?.Leader?.Name?.ToString() ?? "its lord", "faction", st.MapFaction?.Name?.ToString() ?? "no realm",
                    "what_happened", whatHappenedEn, "fine_low", cfg.FineMin, "fine_high", cfg.FineMax, "standing", Standing(st), "crime", Crime(st));
                _guard = s; _resolved = false; _talked = false;
                _case = whatHappenedEn;
                if (!Initiative.Start(agent, s, "Walk up to them and speak first: demand to know what just happened here.", Unanswered))
                { _guard = null; return false; }
                return true;
            }
            catch (Exception ex) { Log.Error("Llamando a la guardia", ex); _guard = null; return false; }
        }

        public static void OnFightEnded(Speaker opponent, Agent opponentAgent, string kind, bool playerWon, bool unprovoked = false)
        {
            string who = opponent.IsHero ? opponent.Name : (opponent.IsFemale ? "a local woman" : "a local man");
            string what = kind == "brawl" ? $"Moments ago the armed person in front of you had a fist fight with {who} in public"
                        : kind == "death" ? $"Moments ago the armed person in front of you fought {who} with drawn steel, to the death, in public"
                        : $"Moments ago the armed person in front of you fought a duel with drawn steel against {who} in public";
            what += playerWon ? $", and left them on the ground. You saw the end of it." : $", and got the worst of it. You saw the end of it.";
            if (unprovoked) what += " Witnesses say the armed person struck first, without a word and without provocation.";
            Summon(what, opponentAgent, false);
        }

        // ============================================================ lo que decide el guardia

        private static void Close(string rumorEn, string titleEs, string textEs)
        {
            _resolved = true;
            try
            {
                var st = Settlement.CurrentSettlement;
                NewsStore.Add(new NewsItem
                {
                    Type = "rumor", Importance = 1, Text = rumorEn, TitleEs = titleEs, TextEs = textEs, SettlementId = st?.StringId,
                    Kingdoms = st?.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(), Heroes = new List<string> { P.StringId },
                });
            }
            catch { }
        }

        public static string LetGo(Speaker s)
        {
            var st = Settlement.CurrentSettlement;
            Close($"The guard of {st?.Name} questioned {P.Name} after a disturbance and let them go.", L.T("AIL_ca1dd233", "La guardia lo deja pasar"), L.F("AIL_05112fe7", $"La guardia de {st?.Name} interrogó a {P.Name} tras un altercado y lo dejó marchar."));
            return L.T("AIL_3f4f693a", "El guardia te deja marchar.");
        }

        public static string Fine(Speaker s, int amount)
        {
            var cfg = S.Get;
            var st = Settlement.CurrentSettlement;
            amount = Math.Max(1, Math.Min(cfg.FineMax * 5, amount));
            if (P.Gold < amount) return L.F("AIL_6d808299", $"No llevas {amount} denarios para pagar la multa.");
            if (st != null) GiveGoldAction.ApplyForCharacterToSettlement(P, st, amount);
            else P.ChangeHeroGold(-amount);
            Close($"The guard of {st?.Name} fined {P.Name} {amount} denars for a disturbance.", L.T("AIL_74ddb46b", "Multa de la guardia"), L.F("AIL_23d64abe", $"La guardia de {st?.Name} multó a {P.Name} con {amount} denarios por un altercado."));
            return L.F("AIL_dc5f5110", $"Pagas una multa de {amount} denarios.");
        }

        public static string Arrest(Speaker s)
        {
            var st = Settlement.CurrentSettlement;
            if (st == null) return null;
            _jailIn = st;
            _jailGuard = s; _jailStanding = Standing(st); _jailCrime = Crime(st);
            try { if (st.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, 10f, true); } catch { }
            Close($"The guard of {st.Name} arrested {P.Name} after a disturbance.", L.T("AIL_efa2c194", "Detenido por la guardia"), L.F("AIL_b8f58f0d", $"La guardia de {st.Name} detuvo a {P.Name} tras un altercado."));
            return L.F("AIL_f2440a00", $"El guardia te detiene: vas a los calabozos de {st.Name}.");
        }

        /// <summary>El guardia no llegó a hablar contigo (te fuiste de la escena, la IA no contestó…).</summary>
        private static void Unanswered() { _guard = null; }

        // ============================================================ tick

        public static void Tick(float dt)
        {
            try
            {
                if (Campaign.Current == null) { _guard = null; _jailIn = null; return; }
                bool talking = Campaign.Current.ConversationManager?.IsConversationInProgress == true;

                if (_guard != null)
                {
                    if (talking && ChatSession.Active != null && ChatSession.Active.Who?.Key == _guard.Key) _talked = true;
                    else if (!talking && !ChatSession.IsBusy && !Initiative.Busy)
                    {
                        // La charla con el guardia ha terminado
                        if (_talked && !_resolved)
                        {
                            var st = Settlement.CurrentSettlement;
                            try { if (st?.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, S.Get.CrimeForWalkingAway, true); } catch { }
                            Say(L.T("AIL_fe057bc7", "Dejas al guardia con la palabra en la boca: tu mala fama aquí crece."), Colors.Red);
                        }
                        _guard = null;
                    }
                }

                // A los calabozos: primero hay que salir de la escena
                if (_jailIn != null && !talking)
                {
                    if (Mission.Current != null)
                    {
                        if (!Mission.Current.MissionEnded) Mission.Current.EndMission();
                    }
                    else
                    {
                        var st = _jailIn; _jailIn = null;
                        if (Settlement.CurrentSettlement == st)
                        {
                            // igual que hace el juego cuando falla una fuga de la cárcel
                            PlayerEncounter.LeaveSettlement();
                            PlayerEncounter.Finish();
                            TakePrisonerAction.Apply(st.Party, P);
                            Log.Info($"El jugador queda preso en {st.Name}");
                            Verdict.Judge(st, _case, _jailGuard, _jailStanding, _jailCrime);
                            _jailGuard = null;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("Justicia: tick", ex); _guard = null; _jailIn = null; }
        }
    }
}
