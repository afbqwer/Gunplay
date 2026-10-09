using System.Reflection;
using HarmonyLib;
using Verse;

namespace Gunplay.Patch
{
    // 在所有动态 Thing 之后、标记/覆盖层/微粒之前，插入轨迹的批量绘制。
    //
    // 位置依据（已核实 1.6 源码 Map.MapUpdate 的绘制块顺序）：
    //   DrawMapMesh → DrawDynamicThings → 游戏条件/地图边缘裁剪 → 标记 → 覆盖层 → 临时Thing → 微粒Fleck
    // 轨迹落在 DrawDynamicThings 之后，因此枪口烟、火花、标记、区域/电力覆盖层都会正确地盖在轨迹之上；
    // 而跨层关系（被 Pawn 遮挡、盖住建筑/物品）由 Y 深度与深度测试自动保证。
    //
    // 取图方式：DrawDynamicThings 只有一个调用方（Map.MapUpdate），且仅在 Find.CurrentMap == this 时调用，
    // 所以这里直接用 Find.CurrentMap 是准确的，无需反射私有字段。
    //
    // 挂载方式：本类不使用 [HarmonyPatch] 属性，由 GunplaySetup 显式调用 TryPatch。
    // 目标方法缺失时只报错并禁用轨迹，不会像属性式 PatchAll 那样抛异常导致整个 mod 加载失败。
    internal static class PatchDynamicDrawTrails
    {
        public static bool TryPatch(Harmony harmony)
        {
            MethodInfo target = AccessTools.Method(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings));
            if (target == null)
            {
                Log.Error("[Gunplay] Could not find DynamicDrawManager.DrawDynamicThings; projectile trails are disabled.");
                return false;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(PatchDynamicDrawTrails), nameof(Postfix))));
            return true;
        }

        // 缓存「当前地图 → 组件」。Map.GetComponent<T> 会线性扫描 components 列表，
        // 放在每帧的绘制路径上不值得；地图切换或组件尚未就绪时才重新解析。
        static Map cachedMap;
        static MapComponent_ProjectileTrails cachedComp;

        static void Postfix()
        {
            // 与目标方法保持同一开关口径
            if (!DebugViewSettings.drawThingsDynamic) return;

            Map map = Find.CurrentMap;
            if (map == null || map.Disposed)
            {
                cachedMap = null;
                cachedComp = null;
                return;
            }

            if (map != cachedMap || cachedComp == null)
            {
                cachedMap = map;
                cachedComp = MapComponent_ProjectileTrails.For(map);
            }
            if (cachedComp == null) return;

            ProjectileTrailBatcher.Draw(cachedComp.Trails);
        }
    }
}
