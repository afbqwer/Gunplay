using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gunplay
{
    // 武器侧配置（由武器 ThingDef 决定）。
    // 投射物侧配置（trail / projectileImpactSound / projectileImpactEffect / preserveSpeed）
    // 已迁移到 ProjectilePropDef，由投射物 def 决定。
    public class GunPropDef : Def
    {
        public string? defTarget;

        public CompPropertiesSpinningGun? spinner;
        public CompPropertiesPrimeAnimation? primer;
        public CompPropertiesPrimerBow? bow;

        public SoundDef? soundAiming;
        public SoundDef? soundCast;

        public float barrelLength = -1;

        // 该值是否来自 default（XML 未显式配置 barrelLength）。
        // 不参与 XML 读写，仅由 GunplaySetup 在启动解析时标记：
        // 「按贴图自动推导枪口」只接管这一类武器，显式配置（含 0）仍然优先。
        internal bool barrelLengthInherited;

        public float drawScale = 1;
    }
}
