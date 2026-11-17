using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Memory;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>
    /// Console commands for children out of wedlock and concubines, the ones Illegitimate Children has: force or end a
    /// pregnancy, check them, the odds of an affair, legitimize, come of age, a child's status, make or break a concubine,
    /// tell a secret to the family or clear them, clean the game's pregnancy queue, repair the party and the occupations.
    /// Names: as the game writes them, spaces as _ ; part of a name is enough when only one fits.
    /// </summary>
    public static class IllegitimateCommands
    {
        private static Hero Find(string n)
        {
            if (string.IsNullOrWhiteSpace(n)) return null;
            n = n.Replace('_', ' ').Trim().ToLowerInvariant();
            if (n == "me" || n == "player") return Hero.MainHero;
            return Hero.AllAliveHeroes.FirstOrDefault(h => h.Name.ToString().ToLowerInvariant() == n)
                   ?? Hero.AllAliveHeroes.FirstOrDefault(h => h.Name.ToString().ToLowerInvariant().Contains(n));
        }

        private static string Joined(List<string> args) => args == null ? "" : string.Join(" ", args).Trim();

        private static Bastard Pregnancy(List<string> args, out string error)
        {
            error = null;
            var open = Bastards.Records.Where(b => !b.Born && !b.Lost).ToList();
            if (open.Count == 0) { error = "No pregnancy out of wedlock is being kept."; return null; }
            string who = Joined(args);
            if (who.Length == 0)
            {
                if (open.Count == 1) return open[0];
                error = "Say whose: " + string.Join(", ", open.Select(b => Hero.Find(b.MotherId)?.Name?.ToString() ?? b.MotherId));
                return null;
            }
            var mother = Find(who);
            var rec = mother == null ? null : open.FirstOrDefault(b => b.MotherId == mother.StringId);
            if (rec == null) error = $"No pregnancy out of wedlock for '{who}'.";
            return rec;
        }

        private static Bastard Child(List<string> args, out Hero child, out string error)
        {
            error = null;
            child = Find(Joined(args));
            if (child == null) { error = "Usage: give the child's name."; return null; }
            var b = Bastards.RecordOf(child);
            if (b == null) error = $"{child.Name} was not born out of wedlock (no record).";
            return b;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("force_birth", "ailegacy")]
        public static string ForceBirth(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var b = Pregnancy(args, out string err);
            if (b == null) return err;
            return Bastards.DueNow(b) ? $"{Hero.Find(b.MotherId)?.Name} gives birth within the day." : "The game's pregnancy for her was not found.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("abort_pregnancy", "ailegacy")]
        public static string AbortPregnancy(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var b = Pregnancy(args, out string err);
            if (b == null) return err;
            Bastards.EndPregnancy(b);
            return $"Pregnancy ended: {Hero.Find(b.MotherId)?.Name} (by {Hero.Find(b.FatherId)?.Name}). It is kept as lost.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("check_pregnancies", "ailegacy")]
        public static string CheckPregnancies(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            bool list = args != null && args.Count > 0 && (args[0].Equals("list", StringComparison.OrdinalIgnoreCase) || args[0].Equals("check", StringComparison.OrdinalIgnoreCase));
            string text = Bastards.CheckText(!list);
            return list ? text + Environment.NewLine + "Run ailegacy.check_pregnancies without arguments to close the lost ones." : text;
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("affair_odds", "ailegacy")]
        public static string AffairOdds(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var w = Find(Joined(args));
            if (w == null) return "Usage: ailegacy.affair_odds <woman>";
            return Bastards.AffairOdds(w);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("bastards", "ailegacy")]
        public static string List(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var sb = new StringBuilder();
            foreach (var b in Bastards.Records.Where(x => x.Born))
                foreach (var id in b.Children)
                {
                    var c = Hero.Find(id);
                    sb.AppendLine($"  {c?.Name?.ToString() ?? id}: mother {Hero.Find(b.MotherId)?.Name}, father {Hero.Find(b.FatherId)?.Name}{(b.Legitimized.Contains(id) ? ", legitimate" : "")}{(b.Secret ? ", passed off" : "")}{(c != null && !c.IsAlive ? ", dead" : "")}");
                }
            return sb.Length == 0 ? "No child born out of wedlock is kept." : sb.ToString().TrimEnd();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("legitimize", "ailegacy")]
        public static string Legitimize(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var b = Child(args, out var child, out string err);
            if (b == null) return err;
            if (b.Legitimized.Contains(child.StringId)) return $"{child.Name} is already legitimate.";
            Bastards.Legitimize(b, child);
            return $"{child.Name} has been legitimized.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("come_of_age", "ailegacy")]
        public static string ComeOfAge(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var b = Child(args, out var child, out string err);
            if (b == null) return err;
            if (child.Age >= 18) return $"{child.Name} is already {child.Age:0}.";
            child.SetBirthDay(child.BirthDay - CampaignTime.Years(18f - child.Age));
            return $"{child.Name} is now 18 years old.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("child_status", "ailegacy")]
        public static string ChildStatus(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var b = Child(args, out var child, out string err);
            if (b == null) return err;
            var mother = Hero.Find(b.MotherId); var father = Hero.Find(b.FatherId); var husband = b.HusbandId != null ? Hero.Find(b.HusbandId) : null;
            var sb = new StringBuilder();
            sb.AppendLine($"{child.Name}, {child.Age:0} years, {(child.IsFemale ? "female" : "male")}, {(child.IsAlive ? "alive" : "dead")}");
            sb.AppendLine($"  mother {mother?.Name}, father {father?.Name}{(husband != null ? $", mother's husband then {husband.Name}" : "")}");
            sb.AppendLine($"  clan {child.Clan?.Name?.ToString() ?? "none"}, occupation {child.Occupation}, culture {child.Culture?.Name}");
            sb.AppendLine($"  legitimate {b.Legitimized.Contains(child.StringId)}, acknowledged {b.Acknowledged}, passed off as the husband's {b.Secret}, father known to the mother {Bastards.FatherKnown(b)}");
            sb.AppendLine($"  game father {child.Father?.Name?.ToString() ?? "none"}, game mother {child.Mother?.Name?.ToString() ?? "none"}, secret {(b.SecretId ?? "none")}");
            return sb.ToString().TrimEnd();
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("set_concubine", "ailegacy")]
        public static string SetConcubine(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var parts = Joined(args).Split('|');
            if (parts.Length != 2) return "Usage: ailegacy.set_concubine <concubine> | <partner>  (partner «me» for you)";
            var x = Find(parts[0]); var partner = Find(parts[1]);
            if (x == null) return $"Hero '{parts[0].Trim()}' not found.";
            if (partner == null) return $"Hero '{parts[1].Trim()}' not found.";
            if (partner == Hero.MainHero)
            {
                string why = Concubines.WhyNot(x, false);
                if (why != null) return $"{x.Name} cannot live with you as a concubine: {why}.";
                return Concubines.Take(x, false) ?? $"{x.Name} could not be made your concubine (see ailegacy.log).";
            }
            return Concubines.Force(partner, x);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("break_concubine", "ailegacy")]
        public static string BreakConcubine(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var x = Find(Joined(args).Split('|')[0]);
            if (x == null) return "Usage: ailegacy.break_concubine <concubine>";
            var partner = Concubines.PartnerOf(x);
            if (partner == null) return $"{x.Name} is nobody's concubine.";
            Concubines.End(x, quiet: true);
            return $"{x.Name} is no longer {partner.Name}'s concubine and went back to their house.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("reveal_to_family", "ailegacy")]
        public static string RevealToFamily(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            var b = Child(args, out var child, out string err);
            if (b == null) return err;
            if (b.SecretId == null) return $"There is no secret about {child.Name}'s father.";
            var mother = Hero.Find(b.MotherId);
            var family = new[] { mother?.Spouse, mother?.Clan?.Leader }.Where(h => h != null && h.IsAlive && h != mother).Distinct().ToList();
            if (family.Count == 0) return "The mother has no spouse or head of house to tell.";
            int n = StorySecrets.Tell(b.SecretId, family);
            return n < 0 ? "The secret is no longer kept." : $"The secret about {child.Name} is now known to {string.Join(", ", family.Select(h => h.Name))}.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("clear_bastard_secrets", "ailegacy")]
        public static string ClearBastardSecrets(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            int n = 0;
            foreach (var b in Bastards.Records.Where(x => x.SecretId != null))
            {
                if (StorySecrets.Remove(b.SecretId)) n++;
                b.SecretId = null;
            }
            SaveFiles.Dirty();
            return $"Removed {n} secret(s) about children born out of wedlock. The children still pass for their mother's husband's.";
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("fix_vanilla_pregnancies", "ailegacy")]
        public static string FixVanillaPregnancies(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            bool list = args != null && args.Count > 0 && args[0].Equals("list", StringComparison.OrdinalIgnoreCase);
            return Bastards.CleanGameQueue(!list);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("repair_party", "ailegacy")]
        public static string RepairParty(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            bool check = args != null && args.Count > 0 && args[0].Equals("check", StringComparison.OrdinalIgnoreCase);
            return PartyRepair.Run(check);
        }

        [CommandLineFunctionality.CommandLineArgumentFunction("sync_occupations", "ailegacy")]
        public static string SyncOccupations(List<string> args)
        {
            if (Campaign.Current == null) return "Load a campaign first.";
            return $"Matched the hidden occupation of {People.SyncAll()} lord(s).";
        }
    }
}
