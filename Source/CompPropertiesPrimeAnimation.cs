using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gunplay
{
    public class CompPropertiesPrimeAnimation : CompProperties
    {
        public CompPropertiesPrimeAnimation() { compClass = typeof(CompPrimeAnimation); }

        // XML 必填（Count 为 0 时 comp 无从取帧）；这里不用可空类型，避免取帧处到处判空。
        public List<GraphicData> frames = null!;
        public int ticksToIdle = 0;
    }
}
