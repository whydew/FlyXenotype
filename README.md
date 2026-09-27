# Fly Xenotype

A RimWorld 1.6 mod (Biotech) that adds the **Fly** xenotype: colonists whose minds run on a small neural controller modelled on a fly's brain. Flies pick their own work priorities and sleep/work/recreation schedule — the player can't set them. The mod is built to be fully deterministic for the **RimWorld Multiplayer** mod.

- **Package id:** `matt.flyxenotype`
- **Requires:** Harmony, Biotech
- **Load after:** Harmony, Biotech, Multiplayer (optional)

## What it adds

### The Fly xenotype

| Gene | Effect |
|---|---|
| Fly brain | Picks its own work priorities and schedule. +10% work speed on the Fly's own top-priority jobs. |
| Compound eyes | Aiming delay ×0.8, half the chance to spring traps. Bug eyes in a per-pawn colour (red, amber, green, teal, blue, violet or gold). |
| Escape reflex | +15 melee dodge chance. |
| Sugar drive | Mood boost from berries, insect jelly, chocolate, agave, ambrosia and fine/lavish meals. The brain leans toward cooking and growing when sweets run low. |
| Swarm sense | +3% work speed per nearby Fly of the same faction (max +12%). Mood: lonely with no Flies on the map, good in company, better in a swarm. |
| Flight muscles | **Fly hop** ability — a long jump that does *not* need hemogen. |
| Fly head / antennae / mouth pincers / chitin skin | Cosmetic: three fly head shapes, antennae, pincers, and one of six dark skin shades per pawn. |

Plus vanilla genes: beardless, quick movement, fast learning, strong immunity, strong stomach, delicate, ugly and fertile.

Flies also appear at random among outlanders, pirates and factionless pawns (visitors, raiders, refugees and so on).

### Player rules

- The **Work** and **Schedule** tabs are locked for Flies. The brain rewrites priorities every 250 ticks and can switch a work type off completely. The top two work types always stay on, and patient/bed rest is never turned off.
- **Drafted Flies obey orders** normally, and undrafted "prioritize" orders still work.
- In an emergency (fire, dying colonist, raid), Flies drop what they're doing and respond, unless they're exhausted.

### Scenario: The Swarm

Five Flies escape the gene lab that made them and walk onto the map. All eight pawn choices are Flies, including rerolls.

- **Research:** the standard starting research, plus tree sowing and cocoa.
- **Supplies:**
  - 600 silver, 50 survival meals
  - 20 medicine, 15 herbal medicine, 25 components
- **Sugar stock:** 120 berries, 40 insect jelly, 20 chocolate.
- **Weapons:** 2 steel knives, a short bow and a revolver.
- **Materials:** 450 steel and 400 wood.

## Multiplayer

Everything the brain does is deterministic:

- **Integer maths:** fixed-point numbers only; no floats and no `Rand` calls.
- **Randomness:** noise comes from a stateless hash of the pawn ID and tick.
- **Update timing:** brains update on a fixed 250-tick pass.
- **Looks:** eye colour and skin shade come from a hash of the pawn ID.

Brain writes bypass `SetPriority` on purpose. Multiplayer's same-value check would otherwise drop them.

## Mod settings

- **Hide locked cells:** blank out Fly rows in the Work and Schedule tabs instead of showing the lock marker.
- **Log hashes:** debug option; logs brain-state hashes when Dev Mode is on, for tracking down desyncs.

## Folder layout

```
About/            About.xml
Assemblies/       FlyXenotype.dll
Defs/
  AbilityDefs/      Fly hop
  FlySensorDefs/    brain sensor inputs (colony needs, threats)
  GeneDefs/         FB_Genes.xml (gameplay), FB_LookGenes.xml (cosmetic)
  HeadTypeDefs/     fly head shapes
  Scenarios/        The Swarm
  ThinkTreeDefs/    crisis-response hook
  ThoughtDefs/      sugar rush, swarm moods
  XenotypeDefs/     FB_Fly
Languages/        English keyed strings
Patches/          adds Flies to faction xenotype chances
Source/           C# source (see below)
Textures/FB/      heads, head attachments, xenotype + gene icons
Tools/make_art.py procedural art generator for the textures
```

### Source

| File | What's in it |
|---|---|
| `Brain/FlyBrain.cs` | The fixed-point neural network: 59 neurons, 248 connections, 32 sub-steps per update. |
| `FlySim.cs` | Brain gene, sensors, priority writer, schedule modes, colony-sense map component. |
| `FlyGenes.cs` | Sugar drive, swarm sense, work-speed stat parts. |
| `FlyLooks.cs` | Per-pawn eye colour and chitin shade. |
| `FlyScenario.cs` | Starting-pawn page that keeps every choice a Fly. |
| `Patches/FlyPatches.cs` | Harmony patches: schedule override, priority guards, UI locks. |
| `AI/ThinkNode_FlyBrain.cs` | Crisis job giver. |
| `FlyMod.cs` | Mod settings, Harmony setup, Multiplayer hookup. |

## Building

The project targets .NET Framework 4.7.2 and builds against the game's own DLLs. Point the properties at your install:

```
cd Source
dotnet build -c Release ^
  /p:RimWorldManaged="C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed" ^
  /p:HarmonyDll=<path to 0Harmony.dll> ^
  /p:MultiplayerApiDll=<path to 0MultiplayerAPI.dll>
```

The output goes to `Assemblies/FlyXenotype.dll`.

**Keep both copies in sync:** this folder and `RimWorld\Mods\FlyXenotype` in the Steam install.

## Known to-dos

- Placeholder icons for the fly brain, escape reflex, sugar drive and swarm sense genes, and for the UI lock.
- Recreation tends to win too often in the brain; needs tuning.
- Metabolism totals about −7 (clamped to −5). Balance after play-testing.
- Remove the debug logging (`FlyDiag`) before release.
- Full Multiplayer test with two clients.
