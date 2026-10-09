using UnityEngine;
using Verse;

namespace Gunplay
{
    // 兼容空壳，不再参与任何绘制或逻辑。
    //
    // 轨迹系统已重构为「全局收集 + 批量绘制」（见 ProjectileTrailState / MapComponent_ProjectileTrails /
    // ProjectileTrailBatcher）：新发射的轨迹不再 spawn 任何 Thing。
    // 保留本类型只为让旧存档中已生成的 Gunplay.ProjectileTrail Thing 能被正常解析
    // （ProjectileTrailDef.thingClass 指向不变），避免读档时因类型缺失报错。
    // 残留实例会在下一次 tick 自我销毁，且不绘制任何内容。
    internal class ProjectileTrail : ThingWithComps
    {
        protected override void Tick()
        {
            base.Tick();
            Destroy(DestroyMode.Vanish);
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
        }
    }
}
