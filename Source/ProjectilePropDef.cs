using System.Collections.Generic;
using Verse;

namespace Gunplay
{
    // 投射物侧配置：由“投射物 ThingDef”决定，与发射武器无关（纯投射物决定）。
    // 解析优先级：显式 defTarget / 同名 defName > 名称关键字匹配 > default。
    public class ProjectilePropDef : Def
    {
        // 显式指向某投射物 ThingDef 的 defName。命中时优先于名称关键字匹配。
        public string? defTarget;

        // 名称匹配关键字：投射物 defName 含任一关键字（忽略大小写）即套用本配置包。
        // 关键字更长者优先匹配（更具体者胜出）。同样可由第三方 mod 在 XML 中扩展。
        public List<string>? matchKeywords;

        public ProjectileTrailDef? trail;

        public ProjectileImpactSoundDef? projectileImpactSound;
        public ProjectileImpactEffectDef? projectileImpactEffect;

        // 本配置包是否豁免全局弹速缩放（true = 该包内投射物保持自身 def 的原始弹速）。
        // 这只是「默认」：单项覆写窗口的「弹速」列可按投射物覆盖它，见 ProjectileRules.SpeedAffected。
        public bool preserveSpeed = false;
    }
}
