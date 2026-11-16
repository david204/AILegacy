using System;
using System.Collections.Generic;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Body gesture and facial expression for whoever speaks in a scene. Uses the game's own conversation animations:
    /// a stance (the idle pose of conversation_animations.xml), a face (convo_*), a short reaction, and for some gestures
    /// a clip of its own (act_conversation_threat_point, act_greeting_front_1...). Every id here exists in
    /// Native/ModuleData/action_types.xml and action_sets.xml (human sets) or in SandBox's conversation_animations.xml.
    /// Anyone sitting, mounted, busy or walking only gets the face and the game's own seated or mounted reaction.
    /// </summary>
    internal static class Gestures
    {
        /// <summary>Moods the AI can pick for a line in scene talk (groups and background talk).</summary>
        public const string Moods = "neutral, pleased, delighted, amused, mocking, unsure, worried, annoyed, stern, angry, shocked, dismissive";

        /// <summary>How the speaker looks while saying a line, for one-to-one replies.</summary>
        public const string Emotions = "neutral, friendly, happy, delighted, amused, grateful, proud, confident, thoughtful, doubtful, surprised, worried, nervous, afraid, " +
                                       "embarrassed, sad, tired, stern, annoyed, angry, furious, menacing, contemptuous, mocking, flirty";

        /// <summary>What the speaker does with their body along with the line.</summary>
        public const string GestureNames = "none, agree, agree_strongly, disagree, refuse, shrug, explain, promise, cross_arms, point, beckon, greet, bow, laugh, cheer, plead, " +
                                           "threaten, death_threat, rage, scared, surrender, disappointed";

        private sealed class Look
        {
            public string Stance;       // idle pose id in conversation_animations.xml
            public string[] Faces;      // facial animations, one is picked
            public string Reaction;     // body reaction when nothing more precise is asked (the six of conversation_animations.xml), "" = none
            public Look(string stance, string reaction, params string[] faces) { Stance = stance; Reaction = reaction; Faces = faces; }
        }

        private static readonly Dictionary<string, Look> Looks = new Dictionary<string, Look>(StringComparer.OrdinalIgnoreCase)
        {
            { "neutral",      new Look("normal",      "",              "convo_normal", "convo_calm_friendly") },
            { "friendly",     new Look("normal",      "positive",      "convo_calm_friendly", "convo_relaxed_happy") },
            { "happy",        new Look("normal",      "positive",      "convo_happy", "convo_merry", "convo_relaxed_happy") },
            { "delighted",    new Look("confident",   "very_positive", "convo_delighted", "convo_huge_smile", "convo_excited") },
            { "amused",       new Look("hip",         "positive",      "convo_bemused", "convo_merry", "convo_mocking_teasing") },
            { "grateful",     new Look("demure",      "positive",      "convo_grateful", "convo_approving") },
            { "proud",        new Look("confident",   "trivial",       "convo_nonchalant", "convo_approving") },
            { "confident",    new Look("confident2",  "trivial",       "convo_focused_happy", "convo_nonchalant") },
            { "thoughtful",   new Look("normal2",     "unsure",        "convo_thinking", "convo_pondering") },
            { "doubtful",     new Look("closed",      "unsure",        "convo_undecided_open", "convo_confused_normal", "convo_disbelief", "convo_undecided_closed") },
            { "surprised",    new Look("normal",      "very_negative", "convo_astonished", "convo_shocked") },
            { "worried",      new Look("nervous",     "unsure",        "convo_worried", "convo_uncomfortable") },
            { "nervous",      new Look("nervous",     "unsure",        "convo_nervous", "convo_uncomfortable") },
            { "afraid",       new Look("nervous2",    "very_negative", "convo_nervous2", "convo_shocked") },
            { "embarrassed",  new Look("demure",      "unsure",        "convo_uncomfortable", "convo_innocent_smile") },
            { "sad",          new Look("weary",       "negative",      "convo_dismayed", "convo_grave", "convo_beaten") },
            { "tired",        new Look("weary2",      "trivial",       "convo_beaten", "convo_bored", "convo_bored2") },
            { "stern",        new Look("closed",      "negative",      "convo_stern", "convo_very_stern", "convo_grave") },
            { "annoyed",      new Look("closed2",     "negative",      "convo_annoyed", "convo_irritable", "convo_confused_annoyed") },
            { "angry",        new Look("aggressive",  "very_negative", "convo_angry", "convo_aggressive") },
            { "furious",      new Look("aggressive2", "very_negative", "convo_furious", "convo_bared_teeth", "convo_insulted") },
            { "menacing",     new Look("warrior2",    "negative",      "convo_predatory", "convo_evil_smile", "convo_very_stern") },
            { "contemptuous", new Look("confident2",  "trivial",       "convo_contemptuous", "convo_mocking_aristocratic", "convo_disgusted") },
            { "mocking",      new Look("hip",         "trivial",       "convo_mocking_teasing", "convo_mocking_revenge", "convo_evil_smile") },
            { "flirty",       new Look("hip",         "positive",      "convo_innocent_smile", "convo_mocking_teasing") },
        };

        // Other words for the same looks: the scene-talk moods, and what models write when they ignore the list
        private static readonly Dictionary<string, string> LookAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "pleased", "happy" }, { "unsure", "doubtful" }, { "shocked", "surprised" }, { "dismissive", "contemptuous" },
            { "calm", "neutral" }, { "warm", "friendly" }, { "affectionate", "friendly" }, { "joyful", "delighted" }, { "excited", "delighted" },
            { "scared", "afraid" }, { "fearful", "afraid" }, { "anxious", "nervous" }, { "suspicious", "doubtful" }, { "wary", "doubtful" },
            { "confused", "doubtful" }, { "curious", "thoughtful" }, { "pensive", "thoughtful" }, { "sorrowful", "sad" }, { "grieving", "sad" },
            { "weary", "tired" }, { "bored", "tired" }, { "cold", "stern" }, { "serious", "stern" }, { "irritated", "annoyed" },
            { "enraged", "furious" }, { "threatening", "menacing" }, { "arrogant", "contemptuous" }, { "disgusted", "contemptuous" },
            { "teasing", "mocking" }, { "playful", "amused" }, { "shy", "embarrassed" }, { "ashamed", "embarrassed" }, { "thankful", "grateful" },
        };

        private sealed class Move
        {
            public string Reaction;     // the game's reaction, played through the line itself (works seated and mounted too)
            public string[] Clips;      // a clip of its own, only standing; none = the reaction is the gesture
            public Move(string reaction, params string[] clips) { Reaction = reaction; Clips = clips; }
        }

        private static readonly Dictionary<string, Move> Moves = new Dictionary<string, Move>(StringComparer.OrdinalIgnoreCase)
        {
            { "agree",          new Move("positive") },
            { "agree_strongly", new Move("very_positive") },
            { "disagree",       new Move("negative",      "act_conversation_talk_negative", "act_conversation_talk_negative_and_explain") },
            { "refuse",         new Move("very_negative", "act_conversation_talk_negative_promise_and_crossed_arms", "act_conversation_talk_dunno_negative_promise_and_crossed_arms") },
            { "shrug",          new Move("unsure",        "act_conversation_talk_dunno") },
            { "explain",        new Move("trivial",       "act_conversation_talk_explain", "act_conversation_talk_commenting", "act_conversation_talk_explain_promise_crossedarms") },
            { "promise",        new Move("positive",      "act_conversation_talk_promise", "act_conversation_talk_promise_and_crossed_arms") },
            { "cross_arms",     new Move("trivial",       "act_conversation_talk_crossedarms") },
            { "point",          new Move("trivial",       "act_conversation_point_somewhere") },
            { "beckon",         new Move("trivial",       "act_taunt_10", "act_taunt_11") },
            { "greet",          new Move("positive",      "act_greeting_front_1", "act_greeting_front_2", "act_greeting_front_3", "act_greeting_front_4", "act_greeting_front_5", "act_greeting_front_6") },
            { "bow",            new Move("positive",      "act_taunt_02", "act_taunt_20") },
            { "laugh",          new Move("very_positive", "act_taunt_15") },
            { "cheer",          new Move("very_positive", "act_cheer_2", "act_cheer_3", "act_cheer_4") },
            { "plead",          new Move("unsure",        "act_stand_beggar_1", "act_stand_beggar_2") },
            { "threaten",       new Move("very_negative", "act_conversation_threat_point", "act_conversation_threat_arm", "act_conversation_talk_explain_and_threat_arm", "act_conversation_talk_negative_and_explain_and_threat_arm") },
            { "death_threat",   new Move("very_negative", "act_conversation_threat_cuttrhoat", "act_conversation_threat_body") },
            { "rage",           new Move("very_negative", "act_conversation_rage", "act_conversation_rage_commenting") },
            { "scared",         new Move("unsure",        "act_scared_reaction_1", "act_scared_reaction_2", "act_taunt_21", "act_taunt_01") },
            { "surrender",      new Move("unsure",        "act_taunt_26", "act_taunt_28") },
            { "disappointed",   new Move("negative",      "act_taunt_04", "act_taunt_05", "act_taunt_06", "act_taunt_07") },
        };

        private static readonly Dictionary<string, string> MoveAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "nod", "agree" }, { "yes", "agree" }, { "shake_head", "disagree" }, { "no", "disagree" }, { "deny", "refuse" },
            { "dunno", "shrug" }, { "gesture", "explain" }, { "crossed_arms", "cross_arms" }, { "arms_crossed", "cross_arms" },
            { "wave", "greet" }, { "salute", "greet" }, { "curtsy", "bow" }, { "laughter", "laugh" }, { "celebrate", "cheer" },
            { "beg", "plead" }, { "threat", "threaten" }, { "angry", "rage" }, { "fear", "scared" }, { "flinch", "scared" },
            { "yield", "surrender" }, { "sigh", "disappointed" }, { "call_over", "beckon" },
        };

        private static readonly HashSet<Agent> Moved = new HashSet<Agent>();     // agents we gave a body gesture
        private static readonly Random Rng = new Random();
        private static bool _broken;

        private static Look FindLook(string emotion)
        {
            string e = (emotion ?? "").Trim().ToLowerInvariant().Replace(' ', '_');
            if (e.Length == 0) return null;
            if (Looks.TryGetValue(e, out var l)) return l;
            return LookAliases.TryGetValue(e, out var real) && Looks.TryGetValue(real, out l) ? l : null;
        }

        private static Move FindMove(string gesture)
        {
            string g = (gesture ?? "").Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
            if (g.Length == 0 || g == "none") return null;
            if (Moves.TryGetValue(g, out var m)) return m;
            return MoveAliases.TryGetValue(g, out var real) && Moves.TryGetValue(real, out m) ? m : null;
        }

        private static string Pick(string[] list) => list == null || list.Length == 0 ? "" : list[list.Length == 1 ? 0 : Rng.Next(list.Length)];

        /// <summary>
        /// The game's own animation tags for a line of the dialogue box: stance, face and reaction. The game plays them
        /// itself when the line shows, and swaps the stance for its seated or mounted pose when the speaker sits or rides.
        /// Empty when gestures are off or nothing is known.
        /// </summary>
        public static string Tags(string emotion, string gesture)
        {
            try
            {
                if (_broken || !S.Get.EnableGestures) return "";
                var look = FindLook(emotion);
                var move = FindMove(gesture);
                if (look == null && move == null) return "";
                look = look ?? Looks["neutral"];
                string tags = "[ib:" + look.Stance + "][if:" + Pick(look.Faces) + "]";
                if (move != null) tags += "[rb:" + move.Reaction + "]";
                return tags;
            }
            catch { return ""; }
        }

        /// <summary>Tags for a line said while they are still thinking it over.</summary>
        public static string ThinkingTags => S.Get.EnableGestures ? "[if:convo_thinking]" : "";

        /// <summary>Scene talk: a mood word from the list in <see cref="Moods"/> (or any emotion).</summary>
        public static void Play(Agent a, string mood) => Play(a, mood, null);

        /// <summary>Face and body played straight on the agent (scene talk, or a reply shown in our own window).</summary>
        public static void Play(Agent a, string emotion, string gesture)
        {
            if (_broken || a == null || !S.Get.EnableGestures) return;
            try
            {
                if (!a.IsActive() || !a.IsHuman || a == Agent.Main) return;
                var look = FindLook(emotion) ?? Looks["neutral"];
                var move = FindMove(gesture);
                a.SetAgentFacialAnimation(Agent.FacialAnimChannel.Mid, Pick(look.Faces), false);
                if (!Free(a)) return;
                if (move != null && TryClip(a, move)) return;
                string reaction = move?.Reaction ?? look.Reaction;
                if (string.IsNullOrEmpty(reaction)) return;
                var act = ActionIndexCache.Create("act_conversation_normal_" + reaction);
                if (a.SetActionChannel(0, in act, forceFaceMorphRestart: true)) Moved.Add(a);
            }
            catch (Exception ex) { _broken = true; Log.Error("Gestures (disabled for this session)", ex); }
        }

        /// <summary>
        /// The gesture's own clip, once the game has played the line's tags. Only standing and free: seated or mounted
        /// characters keep the game's reaction. Nothing happens for a gesture with no clip or a clip the agent's set lacks.
        /// </summary>
        public static void PlayClip(Agent a, string gesture)
        {
            if (_broken || a == null || !S.Get.EnableGestures) return;
            try
            {
                var move = FindMove(gesture);
                if (move == null || !a.IsActive() || !a.IsHuman || a == Agent.Main || !Free(a)) return;
                TryClip(a, move);
            }
            catch (Exception ex) { _broken = true; Log.Error("Gestures (disabled for this session)", ex); }
        }

        /// <summary>The face of someone thinking it over, while their reply is on its way.</summary>
        public static void Think(Agent a)
        {
            if (_broken || a == null || !S.Get.EnableGestures) return;
            try
            {
                if (a.IsActive() && a.IsHuman && a != Agent.Main) a.SetAgentFacialAnimation(Agent.FacialAnimChannel.Mid, "convo_thinking", true);
            }
            catch { }
        }

        private static bool TryClip(Agent a, Move move)
        {
            if (move.Clips == null || move.Clips.Length == 0) return false;
            // start with a random one; a clip missing from this agent's action set is skipped
            int start = Rng.Next(move.Clips.Length);
            for (int i = 0; i < move.Clips.Length; i++)
            {
                var act = ActionIndexCache.Create(move.Clips[(start + i) % move.Clips.Length]);
                if (a.SetActionChannel(0, in act, forceFaceMorphRestart: true)) { Moved.Add(a); return true; }
            }
            return false;
        }

        private static bool Free(Agent a) =>
            a.MountAgent == null && !a.IsUsingGameObject && !a.IsSitting() && a.MovementVelocity.LengthSquared < 0.04f
            && (Mission.Current == null || Mission.Current.Mode != MissionMode.Battle);

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
