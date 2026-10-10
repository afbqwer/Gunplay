using System;
using System.Collections.Generic;
using ASQPicker;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 单个投射物的覆写记录。目前五项：轨迹开关 + 轨迹宽度 + 轨迹颜色 + 拖尾长度 + 淡出时长。
    //
    // 加一项覆写的实际成本（别信「加个字段加一列」那种说法）：记录字段 + ExposeData +
    // ProjectileOverrides 的 TryGet/Set + 一个 TrailXxx 口径类 + 窗口的一列 + 三份语言键，
    // 大约五处。现在只有五项，为将来的第 N 项提前搭数据驱动的框架不划算，
    // 新项先照抄现有形状写；等真的到八项以上再谈抽象。
    // 存档格式始终向后兼容：缺失字段一律走默认值。
    //
    // 「是否命中」的统一契约 —— 用哨兵值表达「未设定」，而不是靠「记录存在」：
    //   宽度 / 拖尾长度 / 淡出时长 —— 0 表示未设定，回落到计算值或全局设置；
    //   轨迹颜色 —— 单独用 hasColor 标记，因为白色是合法颜色，哨兵表达不了三态。
    // 记录本身存在 == 覆写已开启；关掉覆写即删除记录（Disable）。
    public class ProjectileOverride : IExposable
    {
        // 该投射物是否绘制轨迹。默认 true，与「未覆写时继承全局」的表现保持一致。
        public bool trailEnabled = true;

        // 轨迹宽度，绝对值且完全独立：既不看伤害/贴图，也不乘全局系数。0 = 未设定。
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

        // 三个数值字段都用 0 而不是各自的 Default 作哨兵，是因为「字段缺失」和「值恰好等于默认」
        // 必须区分开：后加的字段在老存档的已有记录里必然缺失。若哨兵取 Default，
        // 那些老记录会被当成「用户手动设成了绝对值」，之后改全局设置它们不再跟随。
        // 判定统一走 ProjectileOverrides.TryGetPositive。
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
    // 因此不存在「关着但记着旧值」的中间态，重新打开时会重新取当时的生效值。
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

        // 命中判定与 0 哨兵的处理都收在下面这两个方法里，调用方不必自己判。
        public static bool TryGetTrailWidth(ThingDef? projectileDef, out float width)
        {
            return TryGetPositive(projectileDef, o => o.trailWidth, out width);
        }

        public static bool TryGetTrailLength(ThingDef? projectileDef, out float factor)
        {
            return TryGetPositive(projectileDef, o => o.trailLengthFactor, out factor);
        }

        public static bool TryGetTrailFade(ThingDef? projectileDef, out float ticks)
        {
            return TryGetPositive(projectileDef, o => o.trailFadeTicks, out ticks);
        }

        // 三个数值覆写项共用同一套「命中」语义：记录存在且值 > 0。0 是未设定哨兵，见 ProjectileOverride。
        static bool TryGetPositive(ThingDef? projectileDef, Func<ProjectileOverride, float> selector, out float value)
        {
            value = 0f;

            ProjectileOverride? over = Get(projectileDef);
            if (over == null) return false;

            float candidate = selector(over);
            if (candidate <= 0f) return false;

            value = candidate;
            return true;
        }

        // 自定义颜色是否命中。未设（记录不存在或 hasColor=false）时返回 false。
        // 颜色不走哨兵判定：白色是合法颜色，只能靠 hasColor 这个显式标记表达三态。
        public static bool TryGetTrailColor(ThingDef? projectileDef, out Color color)
        {
            color = Color.white;
            ProjectileOverride? over = Get(projectileDef);
            if (over == null || !over.hasColor) return false;

            color = over.trailColor;
            return true;
        }

        // 开启覆写：以「当前生效值」为起点（宽度取计算值、长度/淡出取全局值），
        // 用户从现状开始调，不会看到轨迹突然变粗/变细或拖尾长度突变。
        // 刻意不走 GetOrCreate：这里是「重置为当前生效值」，不是「保留已有设置」。
        public static void Enable(ThingDef? projectileDef)
        {
            if (projectileDef == null) return;

            Map[projectileDef.defName] = Create(projectileDef);
        }

        // 关闭覆写 = 丢弃记录（见类注释）。下次打开会重新计算。
        public static void Disable(ThingDef? projectileDef)
        {
            if (projectileDef == null) return;

            Map.Remove(projectileDef.defName);
        }

        // 下面五个 Set* 共用同一条约定：记录不存在时按「顺带开启覆写」处理，
        // 调用方（窗口里的开关/色块/输入框）无需自己先判存在再分两步。
        // 这条约定只在 GetOrCreate 里实现一次。
        public static void SetTrailWidth(ThingDef? projectileDef, float width)
        {
            if (projectileDef == null) return;

            GetOrCreate(projectileDef).trailWidth = width;
        }

        public static void SetTrailLength(ThingDef? projectileDef, float factor)
        {
            if (projectileDef == null) return;

            GetOrCreate(projectileDef).trailLengthFactor = factor;
        }

        public static void SetTrailFade(ThingDef? projectileDef, float ticks)
        {
            if (projectileDef == null) return;

            GetOrCreate(projectileDef).trailFadeTicks = ticks;
        }

        public static void SetTrailEnabled(ThingDef? projectileDef, bool enabled)
        {
            if (projectileDef == null) return;

            GetOrCreate(projectileDef).trailEnabled = enabled;
        }

        public static void SetTrailColor(ThingDef? projectileDef, Color color)
        {
            if (projectileDef == null) return;

            ProjectileOverride over = GetOrCreate(projectileDef);
            over.hasColor = true;
            over.trailColor = color;
        }

        // 清除自定义颜色 = 回到「继承该投射物所用轨迹 def 的颜色」。覆写记录本身保留。
        public static void ClearTrailColor(ThingDef? projectileDef)
        {
            ProjectileOverride? over = Get(projectileDef);
            if (over != null) over.hasColor = false;
        }

        // 取已有记录，没有就新建一条并登记。新记录的所有字段都是「当前生效值」，
        // 因此「开启覆写」这个动作本身不改变画面，之后改哪个字段只影响哪个字段。
        static ProjectileOverride GetOrCreate(ThingDef projectileDef)
        {
            if (Map.TryGetValue(projectileDef.defName, out ProjectileOverride? existing)) return existing;

            // 注意顺序：Create 内部要读「未覆写时的生效值」，必须在写入字典之前算完
            ProjectileOverride created = Create(projectileDef);
            Map[projectileDef.defName] = created;
            return created;
        }

        // 按「当前生效值」造一条全新记录。新增覆写项时在这里补默认值。
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
