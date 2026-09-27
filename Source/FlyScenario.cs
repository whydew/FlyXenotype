using System.Collections.Generic;
using RimWorld;
using Verse;

namespace FlyXenotype
{
    /// <summary>
    /// Starting-pawn page for "The Swarm". Vanilla's xenotype page forces the xenotype only on the
    /// starting slots; the extra "left behind" choices fall back to baseliners. This makes every
    /// choice on the page a Fly (including rerolls), so the whole bench is swappable.
    /// Runs once on the hosting machine at game creation - no Multiplayer sync concerns.
    /// </summary>
    public class ScenPart_FlyStartingPawns : ScenPart_ConfigPage_ConfigureStartingPawns_Xenotypes
    {
        public override void PostIdeoChosen()
        {
            base.PostIdeoChosen();

            List<Pawn> pawns = Find.GameInitData.startingAndOptionalPawns;
            int starting = Find.GameInitData.startingPawnCount;
            if (starting <= 0 || pawns.Count <= starting)
                return;

            // Slot 0 carries the forced xenotype (and pawn-kind getter) set up by the base page.
            PawnGenerationRequest template = StartingPawnUtility.GetGenerationRequest(0);
            for (int i = starting; i < pawns.Count; i++)
            {
                if (pawns[i]?.genes?.Xenotype == template.ForcedXenotype)
                    continue;
                StartingPawnUtility.SetGenerationRequest(i, template);
                StartingPawnUtility.RandomizeInPlace(pawns[i]);
            }
        }

        protected override ScenPart CopyForEditingInner()
        {
            // Keep our subclass when the scenario is copied in the scenario editor.
            var src = (ScenPart_ConfigPage_ConfigureStartingPawns_Xenotypes)base.CopyForEditingInner();
            var copy = new ScenPart_FlyStartingPawns
            {
                def = src.def,
                visible = src.visible,
                summarized = src.summarized,
                pawnChoiceCount = src.pawnChoiceCount,
                customSummary = src.customSummary,
            };
            copy.xenotypeCounts.AddRange(src.xenotypeCounts);
            copy.overrideKinds.AddRange(src.overrideKinds);
            return copy;
        }
    }
}
