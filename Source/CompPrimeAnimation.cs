using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Gunplay
{
    class CompPrimeAnimation : CompPrimer
    {
        // base.props 一定是本 comp 的 CompProperties（ThingComp 由 props.compClass 创建），
        // 所以这里用强转而不是 as：类型不对时抛 InvalidCastException 指向真正的原因，
        // 而不是让空引用散落到后面每一处 props.xxx。
        new CompPropertiesPrimeAnimation props => (CompPropertiesPrimeAnimation)base.props;

        public override int TicksToIdle => props.ticksToIdle;

        public override Graphic GetGraphic(int ticksPassed)
        {
            int frame = Mathf.FloorToInt(props.frames.Count * position * 0.99999f);
            return props.frames[frame].Graphic;
        }
    }
}
