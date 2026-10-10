using System;
using System.Collections.Generic;
using ASQPicker;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 单个投射物的覆写记录。目前七项：轨迹开关 + 轨迹材质 + 轨迹宽度 + 轨迹颜色 + 拖尾长度 + 淡出时长 + 弹速。
    //
    // 加一项覆写的实际成本（别信「加个字段加一列」那种说法）：记录字段 + ExposeData +
    // ProjectileOverrides 的 TryGet/Set + 一个 TrailXxx 口径类 + 窗口的一列 + 三份语言键，
    // 大约五处。现在只有六项，为将来的第 N 项提前搭数据驱动的框架不划算，
    // 新项先照抄现有形状写；等真的到八项以上再谈抽象。
    // 存档格式始终向后兼容：缺失字段一律走默认值。
    //
    // 「是否命中」的统一契约 —— 用哨兵值表达「未设定」，而不是靠「记录存在」：
    //   宽度 / 拖尾长度 / 淡出时长 —— 0 表示未设定，回落到计算值或全局设置；
    //   轨迹材质 —— null 表示未设定，回落 ProjectilePropDef.trail（本体的引用天然可空，不需要哨兵）；
    //   轨迹颜色 —— 单独用 hasColor 标记，因为白色是合法颜色，哨兵表达不了三态；
    //   弹速 —— 同理用 hasSpeedOverride 标记，因为 false（不受全局弹速影响）也是合法值。
    // 记录本身存在 == 覆写已开启；关掉覆写即删除记录（Disable）。
    public class ProjectileOverride : IExposable
    {
        // 该投射物是否绘制轨迹。开启覆写时以「该投射物的默认值」为起点
        // （ProjectileRules.TrailEnabled，可能因无贴图或弧线开关而为 false），见 Create。
        public bool trailEnabled = true;

        // 该投射物使用的轨迹材质（ProjectileTrailDef.defName），即「材质类型」选择。
        // null = 未设定，继承 ProjectilePropDef.trail（见 ProjectileRules.TrailDefFor）。
        // 与其它项一样用 defName 而不是引用做键/存储，mod 增删后残留的条目读不到就当没有。
        public string? trailMaterialDefName;

        // 轨迹宽度，绝对值且完全独立：既不看伤害/贴图，也不乘全局系数。0 = 未设定。
        public float trailWidth;

        // 自定义轨迹颜色。hasColor = false 表示继承该投射物所用材质的颜色（见 TrailColor）。
        // 颜色本身经 ColorPickerWindow.LookColor 以 "#RRGGBBAA" 字符串序列化。
        public bool hasColor;
        public Color trailColor = Color.white;

        // 拖尾长度系数（绝对值）：length = 弹速 × 该值，见 TrailLength。
        // 0 = 未设定，继承材质设置与全局 GunplaySettings.trailLengthFactor。
        public float trailLengthFactor;

        // 淡出时长（tick，绝对值），见 TrailFade。0 = 未设定，继承材质设置与全局 GunplaySettings.trailFadeTicks。
        public float trailFadeTicks;

        // 该投射物是否受全局弹速缩放（GunplaySettings.projectileSpeed）。
        // hasSpeedOverride = false 表示未设定，继承「默认」判定：弧线投射物不缩放，
        // 配置包可用 ProjectilePropDef.preserveSpeed 豁免（见 ProjectileRules.SpeedAffected）。
        // false 是合法值（燃烧弹等），所以与 hasColor 一样靠显式标记表达三态，哨兵表达不了。
        // 与其余各项不同，这一项与轨迹无关：轨迹关掉后它依然生效。
        public bool hasSpeedOverride;
        public bool speedAffected = true;

        // 三个数值字段都用 0 而不是各自的 Default 作哨兵，是因为「字段缺失」和「值恰好等于默认」
        // 必须区分开：后加的字段在老存档的已有记录里必然缺失。若哨兵取 Default，
        // 那些老记录会被当成「用户手动设成了绝对值」，之后改全局设置它们不再跟随。
        // 判定统一走 ProjectileOverrides.TryGetPositive。
        public void ExposeData()
        {
            Scribe_Values.Look(ref trailEnabled, "trailEnabled", true);
            Scribe_Values.Look(ref trailMaterialDefName, "trailMaterial", null);
            Scribe_Values.Look(ref trailWidth, "trailWidth", 0f);
            Scribe_Values.Look(ref trailLengthFactor, "trailLengthFactor", 0f);
            Scribe_Values.Look(ref trailFadeTicks, "trailFadeTicks", 0f);
            Scribe_Values.Look(ref hasSpeedOverride, "hasSpeedOverride", false);
            Scribe_Values.Look(ref speedAffected, "speedAffected", true);

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

        // 单项覆写是否指定了轨迹材质（= 逐个投射物选择「材质类型」）。
        // 未设定时返回 false，调用方回落到 ProjectilePropDef.trail。
        // 查不到也返回 false：材质 def 被所在 mod 移除后，残留的 defName 不该让该投射物连轨迹一起丢掉。
        public static bool TryGetTrailMaterial(ThingDef? projectileDef, out ProjectileTrailDef def)
        {
            def = null!;

            ProjectileOverride? over = Get(projectileDef);
            if (over?.trailMaterialDefName == null) return false;

            ProjectileTrailDef? found = DefDatabase<ProjectileTrailDef>.GetNamed(over.trailMaterialDefName, false);
            if (found == null) return false;

            def = found;
            return true;
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

        // 单项弹速覆写是否命中。未设定（记录不存在或 hasSpeedOverride=false）时返回 false，
        // 调用方回落到「默认」判定（弧线规则 + 配置包 preserveSpeed）。
        // 与其它项不同，这一项可以取 false 且有意义，因此不能用 0/false 作哨兵。
        public static bool TryGetSpeedAffected(ThingDef? projectileDef, out bool affected)
        {
            affected = true;

            ProjectileOverride? over = Get(projectileDef);
            if (over == null || !over.hasSpeedOverride) return false;

            affected = over.speedAffected;
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

        // 下面几个 Set* 共用同一条约定：记录不存在时按「顺带开启覆写」处理，
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

        // 设定该投射物使用的材质（= 逐个投射物指定「材质类型」）。
        // 传 null = 清除选择、回到「继承本体配置的材质」。
        //
        // 与其它 Set* 的区别：这里不走 GetOrCreate。新建记录时**不**快照当前的宽度/长度/淡出——
        // 选材质这件事的意思是"以后按这个材质来"，若顺手把旧材质的绝对值固化成覆写，
        // 新材质自己设定的宽度/长度/淡出就会被静默屏蔽（覆写 > 材质）。所以新建时数值项一律留"未设定"。
        // 轨迹开关仍以该投射物的当前生效值为起点，否则新建一条记录会把正在显示的轨迹关掉。
        public static void SetTrailMaterial(ThingDef? projectileDef, ProjectileTrailDef? def)
        {
            if (projectileDef == null) return;

            if (!Map.TryGetValue(projectileDef.defName, out ProjectileOverride? over))
            {
                // 注意：TrailEnabled 会回头读这条记录，必须在写进字典之前算完（此时读到的还是 null）
                over = new ProjectileOverride
                {
                    trailEnabled = ProjectileRules.TrailEnabled(projectileDef)
                };
                Map[projectileDef.defName] = over;
            }

            over.trailMaterialDefName = def?.defName;
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

        // 设定该投射物是否受全局弹速影响。这里必须置 hasSpeedOverride：
        // 记录里存的是「用户明确设过」（三态），而不是「默认判定算出来的值」。
        public static void SetSpeedAffected(ThingDef? projectileDef, bool affected)
        {
            if (projectileDef == null) return;

            ProjectileOverride over = GetOrCreate(projectileDef);
            over.hasSpeedOverride = true;
            over.speedAffected = affected;
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
        // trailEnabled 取该投射物的「默认」值（而非恒 true）：无贴图或弧线开关关闭的投射物
        // 本就默认不显示，开启覆写不应把它们悄悄变成显示（见 ProjectileRules.TrailEnabled）。
        // 长度 / 淡出要按该投射物当前生效的材质解析（材质可能自己定了绝对值），否则「开启覆写」
        // 这个动作本身就会把画面改掉——那正是本方法存在的意义（开局即现状）。
        static ProjectileOverride Create(ThingDef projectileDef)
        {
            ProjectileTrailDef? trailDef = ProjectileRules.TrailDefFor(projectileDef);

            return new ProjectileOverride
            {
                trailEnabled = ProjectileRules.TrailEnabled(projectileDef),
                trailMaterialDefName = null,   // 新记录不选材质 = 继承，与开启前的画面一致
                trailWidth = TrailWidth.EffectiveForDef(projectileDef),
                trailLengthFactor = TrailLength.Effective(projectileDef, trailDef),
                trailFadeTicks = TrailFade.Effective(projectileDef, trailDef),
                hasSpeedOverride = false    // 新记录不接管弹速 = 继承默认判定，与开启覆写前的行为一致
            };
        }

        public static void Clear()
        {
            Map.Clear();
        }

        public static int Count => Map.Count;
    }
}
