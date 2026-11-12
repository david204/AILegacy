using System;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AILegacy.World
{
    /// <summary>
    /// De figurante a personaje: un vecino de una escena, o uno de tus soldados, pasa a ser alguien con nombre que sigue
    /// existiendo (un errante del lugar) o entra a tu servicio como compañero. Conserva la cara, la edad y lo vivido contigo.
    /// </summary>
    internal static class People
    {
        /// <summary>Crea un personaje con nombre a partir de ese figurante. companion = entra en tu clan y tu partida.</summary>
        public static Hero Promote(Speaker s, Agent agent, bool companion, out string error)
        {
            error = null;
            try
            {
                if (s == null || s.IsHero || s.Character == null) { error = L.T("ya es un personaje con nombre"); return null; }
                if (companion && Clan.PlayerClan.Companions.Count >= Clan.PlayerClan.CompanionLimit) { error = L.T("no te queda sitio para más compañeros"); return null; }
                var st = Settlement.CurrentSettlement ?? s.ResidentSettlement
                         ?? Settlement.All.Where(x => x.IsTown).OrderBy(x => x.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D)).FirstOrDefault();
                if (st == null) { error = "no hay asentamiento de referencia"; return null; }

                bool female = s.IsFemale;
                var culture = s.Culture ?? st.Culture;
                // Molde: un errante de su cultura y su sexo (le da habilidades y equipo de partida razonables)
                var templates = CharacterObject.All.Where(c => c.IsTemplate && c.Occupation == Occupation.Wanderer && c.IsFemale == female).ToList();
                var template = templates.Where(c => c.Culture == culture).OrderBy(_ => Guid.NewGuid()).FirstOrDefault() ?? templates.OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                if (template == null) { error = L.T("el juego no tiene un molde de errante para crear al personaje"); return null; }

                int age = (int)Math.Max(18f, Math.Min(70f, s.Age));
                var hero = HeroCreator.CreateSpecialHero(template, st, null, null, age);
                string name = s.Name;
                var full = new TextObject("{=!}" + name);
                hero.SetName(full, full);
                try { if (agent != null) hero.StaticBodyProperties = agent.BodyPropertiesValue.StaticProperties; } catch (Exception ex) { Log.Warn("No se pudo copiar la cara: " + ex.Message); }
                hero.SetNewOccupation(Occupation.Wanderer);
                hero.ChangeState(Hero.CharacterStates.Active);

                // Lo vivido contigo pasa a su ficha de personaje
                try
                {
                    var old = s.Memory;
                    var nm = MemoryStore.Get(hero.StringId, name);
                    JsonConvert.PopulateObject(JsonConvert.SerializeObject(old), nm);
                    nm.HeroId = hero.StringId; nm.HeroName = name;
                    if (nm.Profile != null) nm.Profile.GivenName = "";
                    nm.ConversationCount = Math.Max(1, nm.ConversationCount);
                    nm.Turns.Add(new ChatTurn { Role = "player", Day = CampaignTime.Now.ToDays,
                        Text = companion ? "[What happened: you left your old life and entered their service as a sworn companion. You ride with them now.]"
                                         : "[What happened: they made it plain they will seek you out again. You are now someone they know by name.]" });
                }
                catch (Exception ex) { Log.Error("Pasar la memoria al nuevo personaje", ex); }

                if (companion)
                {
                    AddCompanionAction.Apply(Clan.PlayerClan, hero);
                    AddHeroToPartyAction.Apply(hero, MobileParty.MainParty, true);
                    // Si era uno de tus soldados, sale de la tropa (es el mismo hombre)
                    try { if (MobileParty.MainParty.MemberRoster.GetTroopCount(s.Character) > 0) MobileParty.MainParty.MemberRoster.AddToCounts(s.Character, -1); } catch { }
                }
                else EnterSettlementAction.ApplyForCharacterOnly(hero, st);

                SaveFiles.Flush();
                Log.Info($"[gente] {name} pasa a ser un personaje con nombre ({hero.StringId}){(companion ? ", compañero tuyo" : $", errante de {st.Name}")}");
                return hero;
            }
            catch (Exception ex) { Log.Error("Convertir a un figurante en personaje", ex); error = ex.Message; return null; }
        }

        /// <summary>Crea un familiar tuyo a medida (desde el editor): cónyuge, hijo, hermano o progenitor, con el parentesco de verdad en el juego.</summary>
        public static Hero CreateFamily(string relation, string name, bool female, int age, string cultureId, out string error)
        {
            error = null;
            try
            {
                var p = Hero.MainHero;
                name = (name ?? "").Trim();
                relation = (relation ?? "").Trim().ToLowerInvariant();
                if (name.Length < 2) { error = L.T("Ponle nombre."); return null; }
                if (relation == "spouse" && p.Spouse != null) { error = L.T("Ya estás casado."); return null; }
                if (relation == "child") { age = Math.Max(0, Math.Min((int)p.Age - 16, age)); if (p.Age < 17) { error = L.T("Eres demasiado joven para tener hijos."); return null; } }
                else if (relation == "parent") { age = Math.Max((int)p.Age + 16, age); if ((female ? p.Mother : p.Father) != null) { error = female ? L.T("Ya tienes madre.") : L.T("Ya tienes padre."); return null; } }
                else age = Math.Max(18, Math.Min(80, age));
                if (relation == "sibling" && p.Father == null && p.Mother == null) { error = L.T("Para tener hermanos primero hace falta un padre o una madre: crea antes un progenitor."); return null; }

                var culture = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<CultureObject>(cultureId ?? "") ?? p.Culture;
                var templates = CharacterObject.All.Where(c => c.IsTemplate && c.Occupation == Occupation.Wanderer && c.IsFemale == female).ToList();
                var template = templates.Where(c => c.Culture == culture).OrderBy(_ => Guid.NewGuid()).FirstOrDefault() ?? templates.OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                if (template == null) { error = L.T("El juego no tiene un molde para ese sexo."); return null; }
                var st = Settlement.CurrentSettlement ?? p.HomeSettlement ?? Settlement.All.Where(x => x.IsTown).OrderBy(x => x.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D)).First();
                var hero = HeroCreator.CreateSpecialHero(template, st, Clan.PlayerClan, null, Math.Max(1, age));
                var full = new TextObject("{=!}" + name);
                hero.SetName(full, full);
                hero.SetNewOccupation(Occupation.Lord);
                if (hero.Clan != Clan.PlayerClan) hero.Clan = Clan.PlayerClan;

                switch (relation)
                {
                    case "child":
                        if (p.IsFemale) { hero.Mother = p; if (p.Spouse != null) hero.Father = p.Spouse; } else { hero.Father = p; if (p.Spouse != null) hero.Mother = p.Spouse; }
                        break;
                    case "sibling": hero.Father = p.Father; hero.Mother = p.Mother; break;
                    case "parent": if (female) p.Mother = hero; else p.Father = hero; break;
                    case "spouse": MarriageAction.Apply(p, hero, false); break;
                    default: error = L.T("Parentesco no válido."); return null;
                }
                if (hero.IsChild) { hero.ChangeState(Hero.CharacterStates.NotSpawned); try { hero.ChangeState(Hero.CharacterStates.Active); } catch { } }
                else
                {
                    hero.ChangeState(Hero.CharacterStates.Active);
                    AddHeroToPartyAction.Apply(hero, MobileParty.MainParty, true);
                }
                MemoryStore.Get(hero.StringId, name);
                SaveFiles.Flush();
                Log.Info($"[gente] familiar creado desde el editor: {name} ({relation}, {hero.StringId})");
                return hero;
            }
            catch (Exception ex) { Log.Error("Crear familiar", ex); error = ex.Message; return null; }
        }

        /// <summary>Crea un compañero a medida (desde el editor).</summary>
        public static Hero CreateCompanion(string name, bool female, int age, string cultureId, out string error)
        {
            error = null;
            try
            {
                name = (name ?? "").Trim();
                if (name.Length < 2) { error = L.T("Ponle nombre."); return null; }
                if (Clan.PlayerClan.Companions.Count >= Clan.PlayerClan.CompanionLimit) { error = L.T("No te queda sitio para más compañeros."); return null; }
                var culture = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<CultureObject>(cultureId ?? "") ?? Hero.MainHero.Culture;
                var templates = CharacterObject.All.Where(c => c.IsTemplate && c.Occupation == Occupation.Wanderer && c.IsFemale == female).ToList();
                var template = templates.Where(c => c.Culture == culture).OrderBy(_ => Guid.NewGuid()).FirstOrDefault() ?? templates.OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                if (template == null) { error = L.T("El juego no tiene un molde de errante para ese sexo."); return null; }
                var st = Settlement.CurrentSettlement ?? Settlement.All.Where(x => x.IsTown).OrderBy(x => x.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D)).First();
                var hero = HeroCreator.CreateSpecialHero(template, st, null, null, Math.Max(18, Math.Min(70, age)));
                var full = new TextObject("{=!}" + name);
                hero.SetName(full, full);
                hero.SetNewOccupation(Occupation.Wanderer);
                hero.ChangeState(Hero.CharacterStates.Active);
                AddCompanionAction.Apply(Clan.PlayerClan, hero);
                AddHeroToPartyAction.Apply(hero, MobileParty.MainParty, true);
                MemoryStore.Get(hero.StringId, name);
                SaveFiles.Flush();
                Log.Info($"[gente] compañero creado desde el editor: {name} ({hero.StringId})");
                return hero;
            }
            catch (Exception ex) { Log.Error("Crear compañero", ex); error = ex.Message; return null; }
        }
    }
}
