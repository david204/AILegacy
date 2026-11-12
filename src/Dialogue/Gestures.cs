using System;
using System.Collections.Generic;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Gesto y cara de quien habla en una escena (charlas entre personajes, de grupo, lo que te dicen al pasar).
    /// Se usan las animaciones de conversación del propio juego; quien está sentado, montado, ocupado o andando solo cambia la cara.
    /// </summary>
    internal static class Gestures
    {
        /// <summary>Los estados de ánimo que puede elegir la IA para una frase.</summary>
        public const string Moods = "neutral, pleased, delighted, amused, mocking, unsure, worried, annoyed, stern, angry, shocked, dismissive";

        // ánimo → (gesto del cuerpo, cara)
        private static readonly Dictionary<string, (string act, string face)> Map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            { "neutral",    ("",              "convo_normal") },
            { "pleased",    ("positive",      "convo_happy") },
            { "delighted",  ("very_positive", "convo_delighted") },
            { "amused",     ("positive",      "convo_merry") },
            { "mocking",    ("trivial",       "convo_mocking_teasing") },
            { "unsure",     ("unsure",        "convo_undecided_open") },
            { "worried",    ("unsure",        "convo_worried") },
            { "annoyed",    ("negative",      "convo_annoyed") },
            { "stern",      ("negative",      "convo_stern") },
            { "angry",      ("very_negative", "convo_angry") },
            { "shocked",    ("very_negative", "convo_shocked") },
            { "dismissive", ("trivial",       "convo_nonchalant") },
        };

        private static readonly HashSet<Agent> Moved = new HashSet<Agent>();     // a quién le hemos puesto un gesto de cuerpo
        private static bool _broken;

        public static void Play(Agent a, string mood)
        {
            if (_broken || a == null || !S.Get.EnableGestures) return;
            try
            {
                if (!a.IsActive() || !a.IsHuman || a == Agent.Main) return;
                if (string.IsNullOrWhiteSpace(mood) || !Map.TryGetValue(mood.Trim(), out var g)) g = Map["neutral"];
                a.SetAgentFacialAnimation(Agent.FacialAnimChannel.Mid, g.face, false);
                if (g.act.Length == 0) return;
                bool free = a.MountAgent == null && !a.IsUsingGameObject && !a.IsSitting() && a.MovementVelocity.LengthSquared < 0.04f
                            && (Mission.Current == null || Mission.Current.Mode != MissionMode.Battle);
                if (!free) return;
                var act = ActionIndexCache.Create("act_conversation_normal_" + g.act);
                a.SetActionChannel(0, in act, forceFaceMorphRestart: true);
                Moved.Add(a);
            }
            catch (Exception ex) { _broken = true; Log.Error("Gestos (se desactivan en esta sesión)", ex); }
        }

        /// <summary>Al acabar la frase: vuelve la cara normal y se suelta el gesto.</summary>
        public static void End(Agent a)
        {
            if (a == null) return;
            try
            {
                bool moved = Moved.Remove(a);
                if (!a.IsActive()) return;
                a.SetAgentFacialAnimation(Agent.FacialAnimChannel.Mid, "", false);
                if (moved && !a.IsUsingGameObject)
                {
                    var none = ActionIndexCache.act_none;
                    a.SetActionChannel(0, in none);
                }
            }
            catch { }
        }

        public static void Forget() => Moved.Clear();
    }
}
