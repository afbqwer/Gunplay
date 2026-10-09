using Verse;

namespace Gunplay
{
    public class ProjectileImpactEffectDef : Def
    {
        // 全部按材质可选的声效/特效：XML 未配的材质走 fallback，fallback 本身缺失则返回 null。
        public EffecterDef? fallback;
        public EffecterDef? fabric;
        public EffecterDef? flesh;
        public EffecterDef? metal;
        public EffecterDef? soil;
        public EffecterDef? stone;
        public EffecterDef? wood;

        public EffecterDef? Effect(MaterialKind kind)
        {
            EffecterDef? res = null;

            switch (kind)
            {
                case MaterialKind.None: res = fallback; break;
                case MaterialKind.Fabric: res = fabric; break;
                case MaterialKind.Flesh: res = flesh; break;
                case MaterialKind.Metal: res = metal; break;
                case MaterialKind.Soil: res = soil; break;
                case MaterialKind.Stone: res = stone; break;
                case MaterialKind.Wood: res = wood; break;
            }

            if (res == null) res = fallback;

            return res;
        }
    }
}