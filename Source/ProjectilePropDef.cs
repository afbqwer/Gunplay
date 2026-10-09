using Verse;

namespace Gunplay
{
    // 投射物侧配置：由“投射物 ThingDef”决定，与发射武器无关（纯投射物决定）。
    // 解析优先级：显式 defTarget / 同名 defName > 名称关键字规则 > default。
    public class ProjectilePropDef : Def
    {
        // 显式指向某投射物 ThingDef 的 defName。命中时优先于名称关键字规则。
        public string defTarget;

        public ProjectileTrailDef trail;

        public ProjectileImpactSoundDef projectileImpactSound;
        public ProjectileImpactEffectDef projectileImpactEffect;

        public bool preserveSpeed = false;
    }
}
