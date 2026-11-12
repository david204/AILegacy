# AI Legacy modding API

Other mods can read what AI Legacy knows and add to it. Everything is in one static class, `AILegacy.Api.AILegacyApi`, inside `AILegacy.dll`.

## Setup

Reference `Modules\AILegacy\bin\Win64_Shipping_Client\AILegacy.dll` from your project (Copy Local = false) and make your module load after AI Legacy in your `SubModule.xml`:

```xml
<DependedModules>
  <DependedModule Id="AILegacy" DependentVersion="v0.38.0" />
</DependedModules>
```

Call the API from the game's main thread: campaign events, ticks, your own behaviors. No call throws. When there is no campaign loaded or the input is not valid you get `false`, `null` or an empty list.

Text you pass (notes, instructions, world rules, action descriptions) ends up in prompts, so write it in English.

`AILegacyApi.ApiVersion` is 1. It goes up when something is added.

## Reading

```csharp
if (AILegacyApi.IsReady)
{
    NpcInfo info = AILegacyApi.GetNpc(hero);          // null if they have never dealt with the player
    int trust = info?.Trust ?? 0;                     // -100..100
    var known = AILegacyApi.GetKnownHeroes();         // heroes the player has talked to
    var events = AILegacyApi.GetWorldEvents();        // world events in play, oldest first
}
```

`NpcInfo` has the conversation count, last conversation day, trust, romance, tension, suspected lies, mood, the running summary, notes and instructions. `WorldEventInfo` has id, day, type, title, text, English summary, importance, kingdom and settlement ids, and source. Both are copies.

## Changing one character

```csharp
AILegacyApi.AddNote(hero, "They saved your brother at Epicrotea.");
AILegacyApi.RemoveNote(hero, "They saved your brother at Epicrotea.");
AILegacyApi.AddInstruction(hero, "You owe them a debt and it shames you.");
AILegacyApi.RemoveInstruction(hero, "You owe them a debt and it shames you.");
AILegacyApi.ChangeTrust(hero, +10);       // returns the new value
AILegacyApi.ChangeRomance(hero, -5);
AILegacyApi.AddMoodCause(hero, "Your caravan was robbed on the road to Sargot.", sign: -1, strength: 2, days: 6);
```

A note is a fact the character remembers about the player. An instruction is a standing rule for how they behave with the player. A mood cause is written in the second person; `sign` is -1 (weighs on them), +1 (lifts them) or 0 (unsettles them), and `strength` goes from 1 to 3.

## Changing every prompt

```csharp
AILegacyApi.SetWorldRule("mymod.magic", "Magic is real here and feared.");
AILegacyApi.SetWorldRule("mymod.magic", null);     // removes it

AILegacyApi.SetContextProvider("mymod.order", hero =>
    MyMod.IsProtected(hero.HomeSettlement) ? "Your village is under the protection of the Order." : null);
```

A world rule is something every character knows. It sits in the part of the prompt shared by all characters, so change it rarely: each change makes the provider's prompt cache start over. World rules and context providers are not saved. Set them again on every load.

A context provider is called on every turn of a conversation, so keep it fast.

## Your own actions

Characters decide actions while they talk. You can give them more:

```csharp
string name = AILegacyApi.RegisterAction(
    modId: "mymod",
    name: "bless",
    description: "You give the other person the blessing of your order. Only if they asked for it and you think them worthy.",
    argumentHint: null,                                   // or e.g. "amount of gold"
    isAvailable: hero => hero.IsPreacher,
    execute: (hero, argument) =>
    {
        MyMod.Bless(Hero.MainHero);
        return hero.Name + " gives you the blessing of the order.";   // shown to the player; "" shows nothing
    });
```

The action is offered to the AI as `mod_mymod_bless` (or `mod_mymod_bless:<amount of gold>` when you give an argument hint). Return `null` from `execute` when it could not be done. The character is then told it did not happen, so they do not talk as if it had.

`AILegacyApi.UnregisterActions("mymod")` removes all of yours.

## Events

```csharp
AILegacyApi.PlayerSaid += (hero, text) => { };
AILegacyApi.NpcReplied += (hero, text) => { };
AILegacyApi.ActionExecuted += (hero, action, argument, message) => { };
```

`ActionExecuted` fires only for actions that really took effect. If your handler throws, AI Legacy logs it and carries on.
