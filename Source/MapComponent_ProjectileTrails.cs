using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 每地图一份的轨迹集合。
    //
    // 关键点：RimWorld 的 Map.FillComponents() 会反射遍历 typeof(MapComponent).AllSubclassesNonAbstract()，
    // 对每个「非抽象、非 CustomMapComponent 子类、当前地图尚无该组件」的类型执行
    // Activator.CreateInstance(type, map)。因此本类不需要任何补丁即可在每张地图上自动创建，
    // 并自动获得 MapComponentTick / ExposeData / MapRemoved 回调。
    internal class MapComponent_ProjectileTrails : MapComponent
    {
        readonly List<ProjectileTrailState> trails = new List<ProjectileTrailState>();
        readonly Dictionary<Thing, ProjectileTrailState> byProjectile = new Dictionary<Thing, ProjectileTrailState>();

        public MapComponent_ProjectileTrails(Map map) : base(map)
        {
        }

        public List<ProjectileTrailState> Trails => trails;

        // 该地图尚未创建本组件时返回 null（例如地图正在卸载）；调用方统一用 ?. 处理。
        public static MapComponent_ProjectileTrails? For(Map? map)
        {
            return map?.GetComponent<MapComponent_ProjectileTrails>();
        }

        // 原版投射物登记
        public void Register(Projectile proj, Vector3 destination, ProjectileTrailDef trailDef)
        {
            ProjectileTrailState state = new ProjectileTrailState();
            state.LookupKey = proj;
            state.Initialize(proj, destination, trailDef);
            trails.Add(state);
            byProjectile[proj] = state;
        }

        // CE 投射物登记（ProjectileCE 不是 Projectile 子类）
        public void RegisterCE(Thing proj, Vector3 destination, ProjectileTrailDef trailDef)
        {
            ProjectileTrailState state = new ProjectileTrailState();
            state.LookupKey = proj;
            state.InitializeCE(proj, destination, trailDef);
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

        // 全部轨迹的每 tick 推进与过期回收。由 MapComponentUtility 每 tick 调用一次。
        public override void MapComponentTick()
        {
            for (int i = trails.Count - 1; i >= 0; i--)
            {
                ProjectileTrailState state = trails[i];
                state.Tick();
                if (!state.Dead) continue;

                if (state.LookupKey != null) byProjectile.Remove(state.LookupKey);
                trails.RemoveAt(i);
            }
        }

        public override void MapRemoved()
        {
            trails.Clear();
            byProjectile.Clear();
        }

        // 轨迹是瞬时视觉，完全不持久化；读档后由之后新发射的投射物重新填充。
        public override void ExposeData()
        {
        }
    }
}
