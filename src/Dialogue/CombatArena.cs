using System;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using SandBox;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// A duel agreed in a town, as AIInfluence offers it: «fight right here, or go to the town arena?». At the arena the
    /// player leaves this place and its people, and the two fight in the game's own arena duel; afterwards the opponent
    /// comes back to talk, knowing how it ended.
    /// </summary>
    internal static partial class Combat
    {
        // Where an agreed duel may move to the arena from (AIInfluence's list)
        private static readonly string[] ArenaFrom = { "center", "tavern", "lordshall" };

        private static bool _venueOpen;
        private static Speaker _arenaWho;
        private static string _arenaKind;
        private static bool _arenaStarted;
        private static Settlement _arenaTown;
        private static Action _arenaAfter;      // the opponent's word once the arena is left

        private static void ResetArena() { _venueOpen = false; _arenaWho = null; _arenaKind = null; _arenaStarted = false; _arenaTown = null; }

        private static bool ArenaPossible(Speaker s)
        {
            try
            {
                if (s == null || !s.IsHero || s.Hero == null || !s.Hero.IsAlive || s.Hero.IsPrisoner) return false;
                var st = Settlement.CurrentSettlement;
                if (st == null || !st.IsTown || LocationComplex.Current?.GetLocationWithId("arena") == null) return false;
                string here = CampaignMission.Current?.Location?.StringId;
                return here != null && ArenaFrom.Contains(here);
            }
            catch { return false; }
        }

        /// <summary>The talk is over and the duel agreed: where to fight. The duel waits for the answer.</summary>
        private static void AskVenue(PendingFight f)
        {
            _venueOpen = true;
            var mission = Mission.Current;
            var who = f.Who;
            var text = new TextObject("{=!}" + (f.Kind == "death"
                ? L.T("AIL_e7b1de61", "Tú y {NPC} habéis acordado un duelo a muerte. ¿Lucháis aquí mismo o vais a la arena de la ciudad? En la arena dejas este sitio y a la gente que te rodea.")
                : L.T("AIL_e7b1de62", "Tú y {NPC} habéis acordado un duelo amistoso. ¿Lucháis aquí mismo o vais a la arena de la ciudad? En la arena dejas este sitio y a la gente que te rodea.")));
            text.SetTextVariable("NPC", who.Name);
            InformationManager.ShowInquiry(new InquiryData(L.T("AIL_e7b1de63", "¿Dónde lucháis?"), text.ToString(), true, true,
                L.T("AIL_e7b1de64", "En la arena"), L.T("AIL_e7b1de65", "Aquí mismo"),
                () =>
                {
                    _venueOpen = false;
                    if (Mission.Current != mission || _fight != f) { Log.Info("[duel] duel venue chosen after the scene changed: ignored"); return; }
                    Log.Info($"[duel] duel venue chosen by the player: arena ({who.Name})");
                    _fight = null;
                    _arenaWho = who; _arenaKind = f.Kind; _arenaStarted = false; _arenaTown = Settlement.CurrentSettlement;
                    try { Mission.Current?.EndMission(); } catch (Exception ex) { Log.Error("Leaving for the arena", ex); _arenaWho = null; }
                },
                () =>
                {
                    _venueOpen = false;
                    if (Mission.Current != mission || _fight != f) return;
                    Log.Info($"[duel] duel venue chosen by the player: here ({who.Name})");
                    f.Wait = 3f;
                }), true);
        }

        /// <summary>Back in the town's menu: the arena duel opens; once it is over, the opponent comes to talk.</summary>
        private static void TickArena()
        {
            if (Mission.Current != null || Campaign.Current?.ConversationManager?.IsConversationInProgress == true) return;
            // the opponent's word waits until the player is out on the map again (a talk there can start from nowhere else)
            if (_arenaAfter != null) { if (World.WorldSim.OnMap()) { var after = _arenaAfter; _arenaAfter = null; after(); } return; }
            if (_arenaWho == null) return;
            var s = _arenaWho;
            if (_arenaStarted) return;      // the duel's end handler takes it from here
            try
            {
                var st = Settlement.CurrentSettlement;
                var arena = LocationComplex.Current?.GetLocationWithId("arena");
                if (st == null || st != _arenaTown || arena == null || !s.Hero.IsAlive)
                {
                    Log.Warn($"[duel] arena duel unavailable for {s.Name}");
                    Say(L.T("AIL_e7b1de66", "El duelo no ha podido empezar. Podéis concertar otro encuentro."), Colors.Gray);
                    ResetArena(); return;
                }
                int level = 1;
                try { level = st.Town.GetWallLevel(); } catch { }
                string scene = arena.GetSceneName(level);
                if (string.IsNullOrEmpty(scene)) { Log.Warn("[duel] no arena scene for this town"); ResetArena(); return; }
                _arenaStarted = true;
                string kind = _arenaKind;
                Log.Info($"[duel] arena duel: {s.Name} ({kind}) in {scene}");
                var m = SandBoxMissions.OpenArenaDuelMission(scene, arena, s.Hero.CharacterObject, false, false, winner => ArenaEnded(s, kind, winner == CharacterObject.PlayerCharacter), 100f);
                if (m == null)
                {
                    Log.Warn($"[duel] arena duel did not begin for {s.Name}");
                    Say(L.T("AIL_e7b1de66", "El duelo no ha podido empezar. Podéis concertar otro encuentro."), Colors.Gray);
                    ResetArena();
                }
            }
            catch (Exception ex) { Log.Error("Arena duel", ex); ResetArena(); }
        }

        /// <summary>The arena duel ended: its outcome is remembered, and the opponent comes back to talk once the arena is left.</summary>
        private static void ArenaEnded(Speaker s, string kind, bool playerWon)
        {
            try
            {
                var mem = s.Memory;
                string you = Prompt.ContextBuilder.PlayerNameFor(s) ?? "the stranger";
                string what = kind == "death" ? "a duel to the death" : "a duel";
                Log.Info($"[duel] arena duel with {s.Name} ({kind}): {(playerWon ? "player wins" : "player loses")}");
                ChatSession.AddNote(mem, playerWon ? $"{you} beat me in {what} at the arena." : $"I beat {you} in {what} at the arena.", S.Get.MaxNotesPerHero, s.Name);
                mem.Tension = Math.Max(0, mem.Tension - 40);
                if (playerWon) ChangeRelationAction.ApplyPlayerRelation(s.Hero, 2, affectRelatives: false, showQuickNotification: true);
                bool dies = kind == "death" && playerWon && S.Get.AllowDuelDeath;
                if (kind == "death")
                {
                    if (dies) { ToKill.Add(s.Hero); Say(L.F("AIL_e10a40d5", $"{s.Name} cae en el duelo."), Colors.Red); }
                    else if (playerWon) Say(L.F("AIL_a92c1422", $"{s.Name} queda tendido y malherido: has ganado el duelo."), Colors.Yellow);
                    else { try { P.HitPoints = 1; } catch { } Say(L.F("AIL_080a0e95", $"{s.Name} te deja por muerto. Sobrevives de milagro."), Colors.Red); }
                }
                else Say(playerWon ? L.F("AIL_d0109f54", $"Has vencido a {s.Name}.") : L.F("AIL_805db527", $"{s.Name} te ha vencido."), Colors.Yellow);
                var hero = s.Hero;
                ResetArena();
                if (dies) return;
                // back in the town: they come to talk about it
                _arenaAfter = () =>
                {
                    try
                    {
                        if (hero == null || !hero.IsAlive || hero.IsPrisoner) return;
                        string sit = playerWon ? "The duel at the arena is over: the player has won and you have lost." : "The duel at the arena is over: you have won and the player has lost.";
                        if (Initiative.StartMap(hero, sit)) Log.Info($"Returned to conversation after duel: {s.Name}");
                    }
                    catch (Exception ex) { Log.Error("Back to the talk after the arena", ex); }
                };
            }
            catch (Exception ex) { Log.Error("End of the arena duel", ex); ResetArena(); }
        }
    }
}
