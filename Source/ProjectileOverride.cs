using System.Collections.Generic;
using Verse;

namespace Gunplay
{
    // 单个投射物的覆写记录。目前两项：是否绘制轨迹 + 轨迹宽度。
    // 后续要加覆写项（轨迹材质、拖尾长度、速度缩放等）时，只在这里加字段 +
    // 在 ProjectileOverrideWindow 加一列即可，存档格式向后兼容（缺失字段走默认值）。
    public class ProjectileOverride : IExposable
    {
        // 该投射物是否绘制轨迹。默认 true，与「未覆写时继承全局」的表现保持一致。
        public bool trailEnabled = true;

        // 轨迹宽度，绝对值且完全独立：既不看伤害/贴图，也不乘全局系数。
        public float trailWidth;

        public void ExposeData()
        {
            Scribe_Values.Look(ref trailEnabled, "trailEnabled", true);
            Scribe_Values.Look(ref trailWidth, "trailWidth", 0f);
        }
    }

    // 覆写的读写入口。存储挂在 GunplaySettings 上（全局，跨存档），键为投射物 defName。
    //
    // 约定：字典里存在记录 == 该投射物已开启覆写；关闭「覆写」开关就是删除记录。
    // 因此不存在「关着但记着旧值」的中间态，重新打开时会重新取当时的计算宽度。
    // 用 defName 而非 ThingDef 做键，是为了让 mod 增删后残留的条目不炸（读不到就当没有）。
    public static class ProjectileOverrides
    {
        // 存储挂在 GunplaySettings 上（全局，跨存档），键为投射物 defName。
        // 静态字典必定存在，不像过去那样要先拿实例再判 null。
        static Dictionary<string, ProjectileOverride> Map => GunplaySettings.projectileOverrides;

        public static bool Has(ThingDef projectileDef)
        {
            return projectileDef != null && Map.ContainsKey(projectileDef.defName);
        }

        // 未覆写时返回 null，调用方据此区分「没设过」和「设过但是关着」。
        public static ProjectileOverride? Get(ThingDef? projectileDef)
        {
            if (projectileDef == null) return null;

            // 用 BCL 的 TryGetValue：Verse 的同名扩展要求字典的 value 为可空类型，会触发 CS8620。
            return Map.TryGetValue(projectileDef.defName, out ProjectileOverride? over) ? over : null;
        }

        public static bool TryGetTrailWidth(ThingDef? projectileDef, out float width)
        {
            ProjectileOverride? over = Get(projectileDef);
            width = over?.trailWidth ?? 0f;
            return over != null;
        }

        // 开启覆写：以当前计算出的宽度为起点，用户从「现状」开始调，不会看到轨迹突然变粗/变细。
        public static void Enable(ThingDef? projectileDef)
        {
            if (projectileDef == null) return;

            Set(projectileDef, true, TrailWidth.EffectiveForDef(projectileDef));
        }

        public static void SetTrailWidth(ThingDef? projectileDef, float width)
        {
            if (projectileDef == null) return;

            ProjectileOverride? over = Get(projectileDef);
            if (over != null) over.trailWidth = width;
            else Set(projectileDef, true, width);
        }

        // 设置轨迹开关。记录不存在时按「顺带开启覆写」处理，调用方无需自己分两步。
        public static void SetTrailEnabled(ThingDef? projectileDef, bool enabled)
        {
            if (projectileDef == null) return;

            ProjectileOverride? over = Get(projectileDef);
            if (over != null) over.trailEnabled = enabled;
            else Set(projectileDef, enabled, TrailWidth.EffectiveForDef(projectileDef));
        }

        static void Set(ThingDef projectileDef, bool trailEnabled, float trailWidth)
        {
            Map[projectileDef.defName] = new ProjectileOverride
            {
                trailEnabled = trailEnabled,
                trailWidth = trailWidth
            };
        }

        // 关闭覆写 = 丢弃记录（见类注释）。下次打开会重新计算。
        public static void Disable(ThingDef? projectileDef)
        {
            if (projectileDef == null) return;

            Map.Remove(projectileDef.defName);
        }

        public static void Clear()
        {
            Map.Clear();
        }

        // 单项覆写层是否允许绘制轨迹。未覆写 = 继承全局（允许），是否真的画由全局 enableTrails 决定。
        // 调用处形如：GunplaySettings.enableTrails && ProjectileOverrides.AllowsTrail(def)
        public static bool AllowsTrail(ThingDef? projectileDef)
        {
            ProjectileOverride? over = Get(projectileDef);
            return over == null || over.trailEnabled;
        }

        public static int Count => Map.Count;
    }
}
