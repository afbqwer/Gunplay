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

        public float drawScale = 1;
    }
}
