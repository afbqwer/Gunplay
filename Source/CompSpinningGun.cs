using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Gunplay
{
    class CompSpinningGun : CompPrimer
    {
        // 同 CompPrimeAnimation：props 的类型由 compClass 保证，强转而非 as。
        new CompPropertiesSpinningGun props => (CompPropertiesSpinningGun)base.props;
        public override int TicksToIdle => props.ticksToIdle;

        float rotation = 0;

        public override Graphic GetGraphic(int ticksPassed)
        {
            rotation += position * props.rotationSpeed * ticksPassed;

            int frame = ((int)rotation) % props.frames.Count;
            return props.frames[frame].Graphic;
        }
    }
}
