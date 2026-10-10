using System.Collections.Generic;
using ASQPicker;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 单个投射物的覆写记录。目前五项：是否绘制轨迹 + 轨迹宽度 + 轨迹颜色 + 拖尾长度 + 淡出时长。
    // 后续要加覆写项（轨迹材质、速度缩放等）时，只在这里加字段 +
    // 在 ProjectileOverrideWindow 加一列即可，存档格式向后兼容（缺失字段走默认值）。
    public class ProjectileOverride : IExposable
    {
        // 该投射物是否绘制轨迹。默认 true，与「未覆写时继承全局」的表现保持一致。
        public bool trailEnabled = true;

        // 轨迹宽度，绝对值且完全独立：既不看伤害/贴图，也不乘全局系数。
        public float trailWidth;

        // 自定义轨迹颜色。hasColor = false 表示继承该投射物所用轨迹 def 的颜色（见 TrailColor）。
        // 颜色本身经 ColorPickerWindow.LookColor 以 "#RRGGBBAA" 字符串序列化。
        public bool hasColor;
        public Color trailColor = Color.white;

        // 拖尾长度系数（绝对值）：length = 弹速 × 该值，见 TrailLength。
        // 0 = 未设定，继承全局 GunplaySettings.trailLengthFactor。
        public float trailLengthFactor;

        // 淡出时长（tick，绝对值），见 TrailFade。0 = 未设定，继承全局 GunplaySettings.trailFadeTicks。
        public float trailFadeTicks;

        // 两项都用 0 而不是各自的 Default 作哨兵，是因为「字段缺失」和「值恰好等于默认」必须区分开：
        // 它们是后加的，存档里已有的覆写记录都没有。若哨兵取 Default，那些老记录会被当成
        // 「用户手动设成了绝对值」，之后改全局设置它们不再跟随——而用户从没碰过这两项。
        // 详见 ProjectileOverrides.TryGetTrailLength / TryGetTrailFade 里的 > 0 判定。

        public void ExposeData()
        {
            Scribe_Values.Look(ref trailEnabled, "trailEnabled", true);
            Scribe_Values.Look(ref trailWidth, "trailWidth", 0f);
            Scribe_Values.Look(ref trailLengthFactor, "trailLengthFactor", 0f);
            Scribe_Values.Look(ref trailFadeTicks, "trailFadeTicks", 0f);

            Scribe_Values.Look(ref hasColor, "hasColor", false);
            ColorPickerWindow.LookColor(ref trailColor, "trailColor", Color.white);
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

        public static bool TryGetTrailLength(ThingDef? projectileDef, out float factor)
        {
            factor = 0f;

            ProjectileOverride? over = Get(projectileDef);
            if (over == null || over.trailLengthFactor <= 0f) return false;

            factor = over.trailLengthFactor;
            return true;
        }

        public static bool TryGetTrailFade(ThingDef? projectileDef, out float ticks)
        {
            ticks = 0f;

            ProjectileOverride? over = Get(projectileDef);
            if (over == null || over.trailFadeTicks <= 0f) return false;

            ticks = over.trailFadeTicks;
            return true;
        }

        // 自定义颜色是否命中。未设（记录不存在或 hasColor=false）时返回 false。
        public static bool TryGetTrailColor(ThingDef? projectileDef, out Color color)
        {
            color = Color.white;
            ProjectileOverride? over = Get(projectileDef);
            if (over == null || !over.hasColor) return false;

            color = over.trailColor;
            return true;
        }

        // 开启覆写：以「当前生效值」为起点（宽度取计算值、时长取全局值），
        // 用户从现状开始调，不会看到轨迹突然变粗/变细或拖尾长度突变。
        public static void Enable(ThingDef? projectileDef)
        {
            if (projectileDef == null) return;

            Map[projectileDef.defName] = Create(projectileDef);
        }

        public static void SetTrailWidth(ThingDef? projectileDef, float width)
        {
            if (projectileDef == null) return;

            ProjectileOverride? over = Get(projectileDef);
            if (over != null)
            {
                over.trailWidth = width;
                return;
            }

            ProjectileOverride created = Create(projectileDef);
            created.trailWidth = width;
            Map[projectileDef.defName] = created;
        }

        // 拖尾长度系数。与宽度同约定：记录不存在时顺带开启覆写。
        public static void SetTrailLength(ThingDef? projectileDef, float factor)
        {
            if (projectileDef == null) return;

            ProjectileOverride? over = Get(projectileDef);
            if (over != null)
            {
                over.trailLengthFactor = factor;
                return;
            }

            ProjectileOverride created = Create(projectileDef);
            created.trailLengthFactor = factor;
            Map[projectileDef.defName] = created;
        }

        // 淡出时长（tick）。同上。
        public static void SetTrailFade(ThingDef? projectileDef, float ticks)
        {
            if (projectileDef == null) return;

            ProjectileOverride? over = Get(projectileDef);
            if (over != null)
            {
                over.trailFadeTicks = ticks;
                return;
            }

            ProjectileOverride created = Create(projectileDef);
            created.trailFadeTicks = ticks;
            Map[projectileDef.defName] = created;
        }

        // 设置轨迹开关。记录不存在时按「顺带开启覆写」处理，调用方无需自己分两步。
        public static void SetTrailEnabled(ThingDef? projectileDef, bool enabled)
        {
            if (projectileDef == null) return;

            ProjectileOverride? over = Get(projectileDef);
            if (over != null)
            {
                over.trailEnabled = enabled;
                return;
            }

            ProjectileOverride created = Create(projectileDef);
            created.trailEnabled = enabled;
            Map[projectileDef.defName] = created;
        }

        // 设置自定义颜色。记录不存在时按「顺带开启覆写」处理（与轨迹开关同约定）。
        public static void SetTrailColor(ThingDef? projectileDef, Color color)
        {
            if (projectileDef == null) return;

            ProjectileOverride? over = Get(projectileDef);
            if (over != null)
            {
                over.hasColor = true;
                over.trailColor = color;
                return;
            }

            ProjectileOverride created = Create(projectileDef);
            created.hasColor = true;
            created.trailColor = color;
            Map[projectileDef.defName] = created;
        }

        // 清除自定义颜色 = 回到「继承该投射物所用轨迹 def 的颜色」。覆写记录本身保留。
        public static void ClearTrailColor(ThingDef? projectileDef)
        {
            ProjectileOverride? over = Get(projectileDef);
            if (over != null) over.hasColor = false;
        }

        // 新建一条覆写记录：所有字段都填成「当前生效值」，使开启覆写这一动作本身不改变画面，
        // 之后改哪个字段只影响哪个字段。新增覆写项时在这里补默认值即可。
        static ProjectileOverride Create(ThingDef projectileDef)
        {
            return new ProjectileOverride
            {
                trailEnabled = true,
                trailWidth = TrailWidth.EffectiveForDef(projectileDef),
                trailLengthFactor = GunplaySettings.trailLengthFactor,
                trailFadeTicks = GunplaySettings.trailFadeTicks
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
