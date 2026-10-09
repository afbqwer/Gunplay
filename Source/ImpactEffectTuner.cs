using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 直接修改 GunplayImpact* 系列 EffecterDef 的数据（这些 Def 仅本 mod 使用），
    // 以便在不改动运行时补丁的情况下应用尘云开关与粒子数量倍率。
    public static class ImpactEffectTuner
    {
        static readonly HashSet<string> PuffFlecks = new HashSet<string> { "DustPuff", "DustPuffThick", "AirPuff" };

        static Dictionary<SubEffecterDef, IntRange> originalBurstCounts;

        public static void Apply()
        {
            if (originalBurstCounts == null)
                originalBurstCounts = new Dictionary<SubEffecterDef, IntRange>();

            float multiplier = Gunplay.settings.particleMultiplier;
            bool showPuffs = Gunplay.settings.showPuffs;

            foreach (EffecterDef def in DefDatabase<EffecterDef>.AllDefs)
            {
                if (def.defName == null || !def.defName.StartsWith("GunplayImpact") || def.children == null)
                    continue;

                foreach (SubEffecterDef child in def.children)
                {
                    // 始终从原始值重算，避免多次 Apply 叠加倍率
                    if (!originalBurstCounts.TryGetValue(child, out IntRange original))
                    {
                        original = child.burstCount;
                        originalBurstCounts[child] = original;
                    }

                    bool isPuff = child.fleckDef != null && PuffFlecks.Contains(child.fleckDef.defName);

                    child.burstCount = (isPuff && !showPuffs) ? new IntRange(0, 0) : Scale(original, multiplier);
                }
            }
        }

        static IntRange Scale(IntRange range, float multiplier)
        {
            int min = Mathf.RoundToInt(range.min * multiplier);
            int max = Mathf.RoundToInt(range.max * multiplier);
            if (range.max > 0 && max == 0) max = 1;
            if (max < min) max = min;
            return new IntRange(min, max);
        }
    }
}
