using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 每地图一份的轨迹集合。由 Map.FillComponents 反射创建，不需要补丁。
    //
    // 之所以用 MapComponent 而不是静态集合，是因为两样东西只有它有：
    //   1) MapComponentTick —— 每 tick、覆盖全部地图、且逐组件 try/catch 的钩子，是这里唯一的驱动源；
    //   2) 地图构造时重建组件 —— 读档后集合必为空；静态集合会跨存档残留上一局的 Thing 引用。
    internal class MapComponent_ProjectileTrails : MapComponent
    {
        readonly List<ProjectileTrailState> trails = new List<ProjectileTrailState>();
        readonly Dictionary<Thing, ProjectileTrailState> byProjectile = new Dictionary<Thing, ProjectileTrailState>();

        public MapComponent_ProjectileTrails(Map map) : base(map)
        {
        }

        public List<ProjectileTrailState> Trails => trails;

        // 已构造的地图必能取到组件（Map.Dispose 不清 components）；?. 只兜 map 本身为空。
        public static MapComponent_ProjectileTrails? For(Map? map)
        {
            return map?.GetComponent<MapComponent_ProjectileTrails>();
        }

        // 原版投射物登记。r 由调用方（Launch 后缀）一次解析好，这里与 Initialize 共用，
        // 不再按 defName 重复取覆写记录（见 TrailResolve）。
        public void Register(Projectile proj, Vector3 destination, in TrailResolve r)
        {
            ProjectileTrailState state = new ProjectileTrailState();
            state.LookupKey = proj;
            state.Initialize(proj, destination, in r);
            // 材质与颜色此时已定，分组解析一次，之后每帧直接用
            state.drawGroup = ProjectileTrailBatcher.ResolveGroup(state);
            trails.Add(state);
            byProjectile[proj] = state;
        }

        // CE 投射物登记（ProjectileCE 不是 Projectile 子类）
        public void RegisterCE(Thing proj, Vector3 destination, in TrailResolve r)
        {
            ProjectileTrailState state = new ProjectileTrailState();
            state.LookupKey = proj;
            state.InitializeCE(proj, destination, in r);
            state.drawGroup = ProjectileTrailBatcher.ResolveGroup(state);
            trails.Add(state);
            byProjectile[proj] = state;
        }

        public bool TryGet(Thing proj, out ProjectileTrailState state)
        {
            return byProjectile.TryGetValue(proj, out state);
        }

        // 命中：锁定最终位置，并立刻从查找表移除（之后轨迹只做尾部淡出）
        public void CaptureFinalPosition(Thing proj, Vector3 pos)
        {
            if (!byProjectile.TryGetValue(proj, out ProjectileTrailState state)) return;
            byProjectile.Remove(proj);
            state.CaptureFinalPosition(pos);
        }

        public void CaptureFinalPositionCE(Thing proj)
        {
            if (!byProjectile.TryGetValue(proj, out ProjectileTrailState state)) return;
            byProjectile.Remove(proj);
            state.CaptureFinalPositionCE(proj);
        }

        // 每 tick 推进全部轨迹，并回收已结束的。
        public override void MapComponentTick()
        {
            for (int i = trails.Count - 1; i >= 0; i--)
            {
                ProjectileTrailState state = trails[i];
                state.Tick();
                if (!state.Dead) continue;

                if (state.LookupKey != null) byProjectile.Remove(state.LookupKey);

                int last = trails.Count - 1;
                trails[i] = trails[last];
                trails.RemoveAt(last);
            }
        }

        // 地图移除后不再出现在 Find.Maps 里，tick 随之停止；在这里放掉轨迹持有的 Thing 引用。
        public override void MapRemoved()
        {
            trails.Clear();
            byProjectile.Clear();
        }

        // 不持久化：读档后由新发射的投射物重新填充。
        public override void ExposeData()
        {
        }
    }
}
