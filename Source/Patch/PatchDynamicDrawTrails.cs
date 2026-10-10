using System;
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
            MethodInfo? target = AccessTools.Method(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings));
            if (target == null)
            {
                Log.Error("[Gunplay] Could not find DynamicDrawManager.DrawDynamicThings; projectile trails are disabled.");
                return false;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(PatchDynamicDrawTrails), nameof(Postfix))));
            return true;
        }

        // 缓存「当前地图 → 组件」。Map.GetComponent<T> 会线性扫描 components 列表，
        // 放在每帧的绘制路径上不值得；只在切换地图时重新解析。
        static Map? cachedMap;
        static MapComponent_ProjectileTrails? cachedComp;

        // 绘制一旦出错就整体禁用。几何/上传的错误是确定性的（同一份数据每帧走同一条路径），
        // 继续跑只会每帧抛一次；而这里是 Map.MapUpdate 内部，异常会连标记、覆盖层、微粒
        // 一起中断。与 TryPatch 失败即禁用轨迹是同一口径。
        static bool disabled;

        static void Postfix()
        {
            if (disabled) return;

            // 与目标方法保持同一开关口径
            if (!DebugViewSettings.drawThingsDynamic) return;

            Map? map = Find.CurrentMap;
            if (map == null || map.Disposed)
            {
                cachedMap = null;
                cachedComp = null;
                return;
            }

            // 组件在 Map 构造时由 FillComponents 创建，已构造的地图必有它——解析一次就够。
            // 取不到只可能是这张地图确实没有，不必每帧重扫一遍 components 列表。
            if (map != cachedMap)
            {
                cachedMap = map;
                cachedComp = MapComponent_ProjectileTrails.For(map);
            }
            if (cachedComp == null) return;

            try
            {
                ProjectileTrailBatcher.Draw(cachedComp.Trails);
            }
            catch (Exception e)
            {
                disabled = true;
                Log.Error("[Gunplay] Projectile trail draw failed; trails are disabled for this session. " + e);
            }
        }
    }
}
