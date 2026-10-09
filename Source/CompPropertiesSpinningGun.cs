using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gunplay
{
    public class CompPropertiesSpinningGun : CompProperties
    {
        public CompPropertiesSpinningGun() { compClass = typeof(CompSpinningGun); }

        // XML 必填（Count 为 0 时 comp 无从取帧）；这里不用可空类型，避免取帧处到处判空。
        public List<GraphicData> frames = null!;
        public float rotationSpeed = 1.0f;
        public int ticksToIdle = 60;
    }
}
