using System.Collections.Generic;
using Verse;

namespace Gunplay
{
    // 投射物侧配置：由“投射物 ThingDef”决定，与发射武器无关（纯投射物决定）。
    // 解析优先级：defTargets > weaponTargets > 名称关键字匹配 > default。
    // 没有「与本包同名的投射物」这条隐式回退——显式指定统一走 defTargets。
    public class ProjectilePropDef : Def
    {
        // 显式指向若干投射物 ThingDef 的 defName。三者中优先级最高；命中即接管，不再看后面的规则。
        // 单个目标也写成列表：<defTargets><li>Bullet_Foo</li></defTargets>。
        public List<string>? defTargets;

        // 显式指向若干「武器」ThingDef 的 defName：每把武器经其射击 verb 的 defaultProjectile
        // 反查出投射物，再登记到那个投射物上。优先级低于 defTargets、高于名称关键字。
        //
        // 注意粒度仍是**投射物**、不是武器：轨迹与撞击特效/音效只由投射物 def 决定，
        // 所以多把武器共用同一投射物时它们共享同一条记录；两条显式配置命中同一投射物时后者覆盖前者并告警。
        // 一把武器挂多个 shoot verb 时只取第一个。
        // 用途是「知道武器名、不知道投射物名」时省掉一次翻 def；能靠 matchKeywords 命中的不必用它。
        public List<string>? weaponTargets;

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
