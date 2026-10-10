using UnityEngine;
using Verse;

namespace Gunplay
{
    // 遗留空壳，不再参与任何绘制或逻辑，也没有任何 def 再指向它。
    //
    // 轨迹系统早已重构为「全局收集 + 批量绘制」（见 ProjectileTrailState / MapComponent_ProjectileTrails /
    // ProjectileTrailBatcher）：新发射的轨迹不 spawn 任何 Thing。
    // 本类型原先作为旧版轨迹 Thing 的 thingClass 保留，用来让旧存档中的 Gunplay.ProjectileTrail Thing
    // 仍能解析；自 ProjectileTrailDef 改为普通 Def（不再进 ThingDef 数据库）后，这条路径已不存在，
    // 保留仅为二进制兼容，避免旧程序集/补丁因类型缺失而报错。
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
