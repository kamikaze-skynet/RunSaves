using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace RunSaves;

/// <summary>Turns a recorded upgrade back into the game's own upgrade RPC for one player.</summary>
internal static class Upgrades
{
    internal static bool Send(ImprovementsManager im, ulong client, Improvement imp)
    {
        switch (imp.Kind)
        {
            case "Stats":
            {
                var list = new List<StatModifierMessage>();
                foreach (var e in imp.Stats ?? new List<StatEntry>())
                    if (Enum.TryParse(e.Stat, out StatType st) && Enum.TryParse(e.Type, out StatModifiersType ty))
                        list.Add(new StatModifierMessage { Stat = st, Value = e.Value, Type = ty });
                if (list.Count == 0) return false;
                var arr = new Il2CppReferenceArray<StatModifierMessage>(list.Count);
                for (int i = 0; i < list.Count; i++) arr[i] = list[i];
                im.SendCharacterStatModifiersRpc(client, arr);
                return true;
            }
            case "SpellStat":
                if (!Enum.TryParse(imp.Spell, out SpellType sp1) || !Enum.TryParse(imp.Stat, out StatType st1)) return false;
                im.SendSpellStatModifierRpc(client, new SpellStatModifierMessage { Spell = sp1, Stat = st1, Value = imp.Value });
                return true;
            case "Infusion":
                if (!Enum.TryParse(imp.Spell, out SpellType sp2) || !Enum.TryParse(imp.Element, out ElementType el)) return false;
                im.SendSpellInfusionRpc(client, new SpellInfusionMessage { Spell = sp2, Element = el });
                return true;
            case "Variant":
                if (!Enum.TryParse(imp.Spell, out SpellType sp3) || !Enum.TryParse(imp.Variant, out SpellVariant va)) return false;
                im.SendSpellVariantRpc(client, new SpellVariantMessage { Spell = sp3, SpellVariant = va });
                return true;
            case "Artifact":
                if (!Enum.TryParse(imp.Artifact, out ArtifactType ar)) return false;
                im.SendArtifactRpc(client, new ArtifactMessage { Artifact = ar });
                return true;
            case "Final":
                if (!Enum.TryParse(imp.Final, out FinalUpgradeType fu)) return false;
                im.SendFinalUpgradeRpc(client, fu);
                return true;
            default:
                return false;
        }
    }
}
