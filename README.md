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
| Escape reflex | +15 melee dodge. When attacked in melee, 35% chance to hop 3–6 tiles away from the attacker before the blow lands (once per in-game hour; not while the Fly is deliberately brawling). |
| Sugar drive | Mood boost from berries, insect jelly, chocolate, agave, ambrosia and fine/lavish meals. The brain leans toward cooking and growing when sweets run low. |
| Swarm sense | +3% work speed per nearby Fly of the same faction (max +12%). Mood: lonely with no Flies on the map, good in company, better in a swarm. |
| Flight muscles | **Fly hop** ability — a long jump that does *not* need hemogen. Metabolism −1. |
| Darting gait | Replaces fast runner: +0.1 move speed (fast runner is +0.2) for metabolism −1 instead of −3. Older saves swap it automatically. |
| Hive learning | Replaces quick study (metabolism 0). Learning speed +10% for each other hive-learning Fly of the same faction on the map, up to +50%; −10% when alone. Older saves swap quick study for this automatically. |
| Fly head / antennae / mouth pincers / chitin skin | Cosmetic: three fly head shapes, antennae, pincers, and one of six dark skin shades per pawn. |

Plus vanilla genes: beardless, strong immunity, strong stomach, delicate, ugly and fertile.

Flies also appear at random among outlanders, pirates and factionless pawns (visitors, raiders, refugees and so on).

### Player rules

- The **Work** and **Schedule** tabs are locked for Flies. The brain rewrites priorities every 250 ticks and can switch a work type off completely. The top two work types always stay on, and patient/bed rest is never turned off.
- **Drafted Flies obey orders** normally, and undrafted "prioritize" orders still work.
- In an emergency (fire in the home area, badly injured colonists), Flies drop what they're doing and respond, unless they're exhausted.

### Scenario: The Swarm

Five Flies escape the gene lab that made them and walk onto the map. All eight pawn choices are Flies, including rerolls.

- **Research:** the standard starting research, plus tree sowing and cocoa.
- **Supplies:**
  - 600 silver, 50 survival meals
  - 20 medicine, 15 herbal medicine, 25 components
- **Sugar stock:** 120 berries, 40 insect jelly, 20 chocolate.
- **Weapons:** 2 steel knives, a short bow and a revolver.
- **Materials:** 450 steel and 400 wood.

## The FlyWire sleep circuit

A Fly's sleep timing comes from real fruit-fly wiring. `Tools/build_connectome.py` extracts the circadian → sleep-homeostat pathway from the [FlyWire](https://flywire.ai) whole-brain connectome (v783). It covers 36 cell types, 260 neurons and 192 connections, summing to about 29,000 synapses:

- **clock neurons:** s-LNv, LNd, DN1p, DN1a
- **anterior-bulb TuBu neurons**
- **the central complex's sleep hub:**
  - ER5 ring neurons, the sleep homeostat
  - ExR1 helicon cells
  - ExR3, a dopamine neuron
- **the dorsal fan-shaped body (dFB)**, which promotes sleep

The script writes these into `Source/Brain/FlyConnectomeSleep.g.cs`. Each cell type becomes one node. Connection strength comes from synapse count, and the sign comes from the predicted neurotransmitter. Dopamine onto the dFB is set to inhibitory, following Pimentel et al. 2016.

The wiring inside the circuit is measured, not designed. What *is* a modelling choice is how the game connects to it:

- **Sleep need:** tiredness excites the R5 homeostat and the dFB.
- **Body clock:** local time of day drives the clock neurons.
- **Daylight:** light drives the TuBu neurons.
- **Output:** the dFB drives the Sleep decision, and the helicon cells drive the Work decision.

What emerges is that daylight arousal flows TuBu → helicon → Work, while the dFB flips on like a switch once the Fly is tired enough. Together these put sleep at dusk and through the night, with no hard-coded bedtime.

The circuit is **connectome-derived, not a simulation of a real fly**. Synapse counts only approximate connection strengths. Neuropeptides such as PDF, and learning, are not modelled.

`Tools/SleepSim` is a small .NET console app for checking schedules offline. It runs the real `Source/Brain` code against a simplified model of RimWorld needs:

```
cd Tools/SleepSim
dotnet run -- circuit 5      # FlyWire wiring
dotnet run -- legacy 5       # the old hand-built wiring
dotnet run -- probe          # circuit response by hour and rest level
```

To regenerate the circuit, download `Supplemental_file1_neuron_annotations.tsv` from [flywire_annotations](https://github.com/flyconnectome/flywire_annotations) and `Connectivity_783.parquet` from [Drosophila_brain_model](https://github.com/philshiu/Drosophila_brain_model), then run:

```
python Tools/build_connectome.py annotations.tsv Connectivity_783.parquet Source/Brain/FlyConnectomeSleep.g.cs
```

**Credits:**

- FlyWire Consortium: Dorkenwald et al. 2024, *Nature*; Schlegel et al. 2024, *Nature*. Data CC-BY 4.0, [doi:10.5281/zenodo.10676866](https://doi.org/10.5281/zenodo.10676866).
- Shiu et al. 2024, *Nature*, for the connectivity and sign table (MIT).

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
Tools/make_art.py          procedural art generator for the textures
Tools/build_connectome.py  FlyWire sleep-circuit extractor
Tools/SleepSim/            offline schedule simulator
```

### Source

| File | What's in it |
|---|---|
| `Brain/FlyBrain.cs` | The fixed-point neural network (hand-built core plus the FlyWire sleep block, 91 neurons and 434 connections in all; 32 sub-steps per update) and the clock/daylight drive tables. |
| `Brain/FlyConnectomeSleep.g.cs` | Generated FlyWire sleep-circuit wiring. |
| `FlySim.cs` | Brain gene, sensors, priority writer, schedule modes, colony-sense map component. |
| `FlyGenes.cs` | Sugar drive, swarm sense, work-speed stat parts. |
| `FlyLooks.cs` | Per-pawn eye colour and chitin shade. |
| `FlyScenario.cs` | Starting-pawn page that keeps every choice a Fly. |
| `Patches/FlyPatches.cs` | Harmony patches: schedule override, priority guards, UI locks. |
| `AI/ThinkNode_FlyBrain.cs` | Crisis job giver. |
| `FlyMod.cs` | Mod settings, Harmony setup, Multiplayer hookup. |

## Building

The project targets .NET Framework 4.7.2 and builds against the game's own DLLs. The paths in `FlyXenotype.csproj` default to this PC's Steam install, the Harmony workshop copy and the local RimworldMP fork, so normally:

```
cd Source
dotnet build -c Release
```

Override `/p:RimWorldManaged=`, `/p:HarmonyDll=` or `/p:MultiplayerApiDll=` if those live elsewhere. The output goes to `Assemblies/FlyXenotype.dll`.

**Keep both copies in sync:** this folder and `RimWorld\Mods\FlyXenotype` in the Steam install.

## Known to-dos

- Placeholder icons for the fly brain, escape reflex, sugar drive and swarm sense genes, and for the UI lock.
- Verify in play: sleep should come in one block from dusk, set by the FlyWire circuit, and Flies stay asleep until rested. Recreation only starts below 40% and runs until 80%, for at most 2 hours.
- Metabolism now totals −1 (hunger ×1.25, same as Pigskin/Yttakin; was −7, ×2.25) after play-testing: flight muscles −2 → −1, quick study (−3) → hive learning (0), fast runner (−3) → darting gait (−1).
- Remove the debug logging (`FlyDiag`) before release.
- Full Multiplayer test with two clients.
