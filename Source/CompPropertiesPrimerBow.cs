using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Verse;

namespace Gunplay
{
    public class CompPropertiesPrimerBow : CompProperties
    {
        public CompPropertiesPrimerBow() { compClass = typeof(CompPrimerBow); }

        // 可选：未配时 CompPrimerBow 回退到 parent.DefaultGraphic。
        public GraphicData? graphicData;
        public float stretchHorizontal = 0.45f;
        public float stretchVertical = -0.25f;
    }
}
