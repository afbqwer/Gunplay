using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ASQPicker;
using Gunplay.Patch;
using RimWorld;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 投射物单项覆写窗口。
    //
    // 结构：上方过滤区（搜索框 + mod 下拉 + 轨迹过滤 + 仅显示已覆写 + 仅显示有武器 + 计数 + 清除全部），
    // 下方列表区（表头 + 虚拟滚动）。
    // 一行 = 一个投射物 ThingDef；武器列只是上下文（轨迹本身只由投射物 def 决定，见 PatchProjectileLaunch），
    // 多个武器共用同一投射物时显示第一个并在 defName 后加「+N」，悬浮列出全部。
    //
    // 每行若干开关 / 选择（列序：覆写 | 轨迹 | 曲线 | 弹速 | 材质 | 颜色 | 投射物 | 武器 | 宽度 | 长度 | 淡出；
    // CE 在场时「弹速」整列不参与布局，见 SpeedColumnShown）：
    //   「覆写」—— 该行是否有覆写记录。关掉即丢弃记录（轨迹开关、材质与宽度一起回到继承/计算值）。
    //   「轨迹」—— 是否绘制该投射物的轨迹。未覆写时显示该投射物的「默认」值
    //              （见 ProjectileRules.TrailEnabled：可能因本体无贴图、弧线开关关闭而不勾选）。
    //              直接点它会顺带把覆写打开，省掉「先开覆写再关轨迹」两步。
    //   「曲线」—— 该投射物是否使用曲线轨迹绘制（弧线样式，而非直线四边形）。未覆写时显示
    //              该投射物的「默认」判定（见 ProjectileRules.CurveEnabled：原版弧线投射物为曲线，
    //              CE 投射物跟随 ceStraightTrails；自动检测开启时，运行时检测到沿曲线飞行的
    //              投射物也会显示为曲线）。与轨迹开关同约定：点一下顺带开启覆写。
    //              轨迹关掉时本项暂时不生效，但覆写会保留（重新打开轨迹时继续有效）。
    //   「弹速」—— 该投射物是否受全局弹速缩放影响。唯一与轨迹无关的覆写项：轨迹关掉它照样生效，
    //              因此不像材质/颜色/数值那样随轨迹开关变灰。未覆写时显示该投射物的「默认」判定
    //              （见 ProjectileRules.SpeedAffected：弧线投射物不缩放，配置包可用 preserveSpeed 豁免），
    //              同样点一下就顺带开启覆写。已单独指定时右侧有「×」可回到继承——否则要取消它
    //              只能关掉整行覆写，那会一并丢掉同一行的宽度/长度/淡出/颜色。
    //              CE 在场时整列隐藏：CE 的 ProjectileCE 不继承原版 Projectile，弹速缩放对它不生效，
    //              留着这一列同样是死控件（判据见 SpeedColumnShown）。
    //   「材质」—— 该投射物用哪种轨迹材质（ProjectileTrailDef）。未覆写时是本体配置解析出的那一种；
    //              点击打开 Def 选择窗口，「清除」= 回到继承。与轨迹开关同约定：在未覆写的行上点它
    //              会顺带开启覆写，但**只**记材质一项，不会把当时的宽度/长度/淡出一起固化（见 SetTrailMaterial）。
    //   「颜色」—— 该投射物的轨迹颜色。它比材质自带的颜色更具体，因此排在材质列之后。
    //
    // 开关 / 材质 / 颜色 / 数值四类格子都支持拖拽修改（实现见 DragPaint）：
    //   开关 · 材质 · 颜色 —— 直接拖动（方向不限），扫过的行被涂成同一个值
    //     （开关取「按下那一行切换后的状态」，与本体复选框的涂抹一致，见
    //      Widgets.ToggleInvisibleDraggable 的 paintable 分支；材质与颜色取「按下那一行当时的值」）。
    //   数值三列 —— Shift + 拖动（方向不限），把本行的值涂到扫过的行。
    //     唯独它多要一个 Shift：在数值输入框上直接拖是"选中文字"（拖过数字就能全选替换），
    //     抢掉是净损失；按住 Shift 才是用户明确表态"我要涂"，此时方向也不必再限制。
    // 单击都仍是原来的行为（开选择窗口 / 聚焦输入框），与拖拽不冲突：没越过 4.5 像素阈值就不算拖拽，
    // 越过了则拖拽结束、鼠标松开时也已经不在原来那个控件上。
    //
    // 列表可能有数百行（装 CE 或大型武器包之后更多），因此走虚拟滚动：只为可见区间建 Rect，
    // 但输入框的字符串缓冲必须挂在行对象上跨帧保留，否则滚出视野再滚回来会丢焦点/丢输入。
    public class ProjectileOverrideWindow : Window
    {
        const float Spacing = 6f;
        const float FilterRowHeight = 30f;
        const float HeaderHeight = 24f;
        const float RowHeight = 36f;
        const float IconSize = 32f;
        const float OverrideToggleWidth = 60f;
        const float TrailToggleWidth = 40f;
        const float CurveToggleWidth = 40f;
        const float SpeedColumnWidth = 60f;
        const float ColorColumnWidth = 70f;
        const float SwatchWidth = 50f;
        const float SwatchHeight = 24f;
        const float ResetButtonWidth = 24f;
        const float MaterialColumnWidth = 130f;
        const float WidthColumnWidth = 60f;
        const float LengthColumnWidth = 60f;
        const float FadeColumnWidth = 60f;
        const float ModButtonWidth = 200f;
        const float TrailFilterWidth = 190f;
        const float OnlyOverriddenWidth = 150f;
        const float OnlyWithWeaponWidth = 150f;
        const float CountWidth = 200f;
        const float ClearButtonWidth = 150f;
        const float ScrollbarWidth = 16f;

        // 弹速列是否显示。CE 的 ProjectileCE 自成一套弹道物理、不继承原版 Projectile，
        // 全局弹速缩放对它不生效，因此 CE 在场时这一列没有意义。
        // 判据用 Detected（只看 CE 在不在）而不是 Enabled（还要看兼容补丁挂没挂上）——
        // 不兼容的原因是「CE 在场」本身，见 PatchCECompat.Detected。
        static bool SpeedColumnShown => !PatchCECompat.Detected;

        // 宽度下限刻意不取 0：0 是「未设定」的哨兵（见 ProjectileOverride.TryGetPositive）。
        // 若允许输入 0，「填了 0」与「没填」会被判成同一件事——用户设 0 后实际回落到计算值，
        // 而输入框这一帧仍显示 0（打字路径不重算），当场看不出来。要「不画轨迹」有轨迹开关，
        // 0 宽度本身没有意义，因此把下限抬到刚好越过哨兵。
        const float MinWidth = 0.01f;
        const float MaxWidth = 2f;

        const float ToggleSize = 24f;

        // 拖拽涂抹的格子种类（见 DragPaint.Cell 的 tag）。材质列与颜色列相邻，
        // 横向拖出去时要靠它把「涂材质」挡在材质列内，否则颜色格会拿到一个 ProjectileTrailDef 转不成 Color。
        // 数值三列各一个，免得 Shift 涂抹横着划过去时把宽度涂到长度列上。
        // 全部 internal：顶层的光标图标补丁按它决定画什么，见 PatchDrawDragPaintIcon。
        // 后四个是开关列——它们的涂抹由本体复选框接管（见 DrawToggleBox），
        // 这里只借 tag 给"涂过哪个格子"的位掩码编号，不参与 DragPaint.Cell。
        internal const int MaterialPaintTag = 1;
        internal const int ColorPaintTag = 2;
        internal const int WidthPaintTag = 3;
        internal const int LengthPaintTag = 4;
        internal const int FadePaintTag = 5;
        const int OverridePaintTag = 6;
        const int TrailPaintTag = 7;
        const int SpeedPaintTag = 8;
        const int CurvePaintTag = 9;

        // 「本轮拖拽涂过」的高光。淡蓝一层，盖在格子内容之上（理由见 DrawPaintedHighlights）。
        static readonly Color PaintedHighlightColor = new Color(0.35f, 0.65f, 1f, 0.25f);

        public override Vector2 InitialSize => new Vector2(1280f, 720f);

        // 轨迹列 / 轨迹过滤的取值口径：三选一
        enum TrailFilter
        {
            Any,      // 不过滤
            Shown,    // 只看会画轨迹的
            Hidden    // 只看不画轨迹的
        }

        // 全字段由 BuildRows 的对象初始化器一次性填好；这里不用 required（需要运行时 polyfill），
        // 恒赋值的字段标 null!，可为 null 的（无武器时的两列）如实标可空。
        class Row
        {
            public ThingDef projectile = null!;
            public string projectileLabel = null!;
            public List<ThingDef> weapons = null!;
            public string? weaponDefName;   // 首个武器的 defName（含 "+N"）；无武器时为 null
            public string? weaponLabel;     // 首个武器的显示名；无武器时为 null
            public string modName = null!;
            public string searchText = null!;   // 参与搜索的文本：投射物 + 全部武器 的 defName/label
            public string weaponTip = null!;    // 武器列的悬浮内容
            public int paintedMask;         // 位 = tag：本轮拖拽涂过哪几列。松开鼠标整体清零（见 MarkPainted）
            public ProjectileTrailDef? trailDef;    // 该投射物当前生效的轨迹材质（颜色/宽度继承的来源）
            public float editWidth;         // 输入框绑定的当前值
            public string? widthBuffer;     // 输入框字符串缓冲；null 表示尚未输入过
            public float editLength;        // 拖尾长度系数的输入框绑定值
            public string? lengthBuffer;
            public float editFade;          // 淡出时长（tick）的输入框绑定值
            public string? fadeBuffer;
        }

        // 数值列的定义。宽度 / 长度 / 淡出三列的绘制流程完全一致，差别只有这几项，
        // 因此把差异抽出来，加第四个数值覆写项时只需再声明一个实例 + 一列宽度。
        // 每行的输入值与其字符串缓冲仍留在 Row 上，由调用方以 ref 传入（见 DrawNumberCell）。
        readonly struct NumberColumn
        {
            internal readonly float width;
            internal readonly float min;
            internal readonly float max;
            internal readonly bool integer;   // 长度 / 淡出是整数刻度，涂过去的值要取整
            internal readonly int paintTag;   // Shift + 拖拽涂抹的格子种类（见 DragPaint.Cell）
            internal readonly Func<float, string> format;
            internal readonly string tipKey;
            internal readonly Func<Row, float> effective;   // 该行当前生效值（未覆写时也用它做只读显示）
            internal readonly Action<Row, float> apply;     // 写入覆写

            internal NumberColumn(float width, float min, float max, bool integer, int paintTag,
                Func<float, string> format, string tipKey, Func<Row, float> effective, Action<Row, float> apply)
            {
                this.width = width;
                this.min = min;
                this.max = max;
                this.integer = integer;
                this.paintTag = paintTag;
                this.format = format;
                this.tipKey = tipKey;
                this.effective = effective;
                this.apply = apply;
            }
        }

        // 宽度是绝对值（0~2 世界单位，3 位小数）；长度系数与淡出是整数刻度（tick）
        static readonly NumberColumn WidthColumn = new NumberColumn(
            WidthColumnWidth, MinWidth, MaxWidth, false, WidthPaintTag, Format, "GunplayOverrideTipWidth",
            row => TrailWidth.EffectiveForDef(row.projectile),
            (row, v) => ProjectileOverrides.SetTrailWidth(row.projectile, v));

        static readonly NumberColumn LengthColumn = new NumberColumn(
            LengthColumnWidth, TrailLength.Min, TrailLength.Max, true, LengthPaintTag, FormatInt, "GunplayOverrideTipLength",
            row => TrailLength.Effective(row.projectile, row.trailDef),
            (row, v) => ProjectileOverrides.SetTrailLength(row.projectile, v));

        static readonly NumberColumn FadeColumn = new NumberColumn(
            FadeColumnWidth, TrailFade.Min, TrailFade.Max, true, FadePaintTag, FormatInt, "GunplayOverrideTipFade",
            row => TrailFade.Effective(row.projectile, row.trailDef),
            (row, v) => ProjectileOverrides.SetTrailFade(row.projectile, v));

        readonly List<Row> rows = new List<Row>();
        readonly List<Row> visible = new List<Row>();
        readonly List<string> modOptions = new List<string>();

        readonly QuickSearchWidget quickSearch = new QuickSearchWidget();

        // GenText.Truncate 的实现是「逐字符裁剪 + 每砍一个字符测量一次文本宽度」，
        // 每帧对每行调 2 次会很贵，所以它自带了 cache 参数，这里按字体各存一份：
        // 结果只随列宽变化，列宽变了整体清空即可。
        readonly Dictionary<string, string> truncateSmall = new Dictionary<string, string>();
        readonly Dictionary<string, string> truncateTiny = new Dictionary<string, string>();
        float truncateWidth = -1f;

        string? modFilter;              // null = 全部
        TrailFilter trailFilter = TrailFilter.Any;
        bool onlyOverridden;
        bool onlyWithWeapon;            // 只看「有武器发射」的投射物（weapons.Count > 0）
        Vector2 scrollPosition;

        bool visibleDirty = true;
        bool visibleKeepScroll;   // 重建列表时是否保留滚动位置（只有开关引发的重建才保留）
        int overriddenInFilter;

        // 上一帧左键是否按着。只用来识别"松开"这个瞬间——高光要整体清零（见 ClearPaintedHighlightsOnRelease）。
        bool dragHighlightActive;

        public ProjectileOverrideWindow()
        {
            forcePause = false;
            draggable = false;
            resizeable = false;
            closeOnClickedOutside = true;
            doCloseX = true;

            BuildRows();
        }

        public static void Open()
        {
            Find.WindowStack.Add(new ProjectileOverrideWindow());
        }

        // 拖到一半关窗（按 Esc、点关闭按钮、点窗外）会让"正在涂抹"的状态卡住：
        // 之后没有行再调 Sweep 去推进它，而顶层画光标图标的地方只看 Active，
        // 于是那个图标会一直粘在鼠标上。这里兜一次底。
        public override void PostClose()
        {
            base.PostClose();
            DragPaint.Reset();
        }

        // ---------------- 数据构建 ----------------

        // 反查「投射物 → 发射它的武器」：扫描全部 ThingDef 的 verbs.defaultProjectile。
        // 只在开窗时构建一次；炮塔的 gun def、动物远程攻击等都在这里被自然收录。
        static Dictionary<ThingDef, List<ThingDef>> BuildWeaponMap()
        {
            Dictionary<ThingDef, List<ThingDef>> map = new Dictionary<ThingDef, List<ThingDef>>();

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                List<VerbProperties> verbs = def.Verbs;
                if (verbs == null) continue;

                foreach (VerbProperties verb in verbs)
                {
                    ThingDef projectile = verb.defaultProjectile;
                    if (projectile == null) continue;

                    if (!map.TryGetValue(projectile, out List<ThingDef>? list))
                    {
                        list = new List<ThingDef>();
                        map[projectile] = list;
                    }
                    if (!list.Contains(def)) list.Add(def);
                }
            }

            foreach (List<ThingDef> list in map.Values)
            {
                list.Sort((a, b) => string.Compare(a.defName, b.defName, StringComparison.OrdinalIgnoreCase));
            }

            return map;
        }

        void BuildRows()
        {
            rows.Clear();
            modOptions.Clear();

            Dictionary<ThingDef, List<ThingDef>> weaponMap = BuildWeaponMap();

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.projectile == null) continue;

                List<ThingDef> weapons = weaponMap.TryGetValue(def, out List<ThingDef>? list)
                    ? list
                    : new List<ThingDef>();

                rows.Add(new Row
                {
                    projectile = def,
                    projectileLabel = DisplayLabel(def),
                    weapons = weapons,
                    weaponDefName = WeaponDefName(weapons),
                    weaponLabel = weapons.Count > 0 ? DisplayLabel(weapons[0],weapons.Count - 1) : null,
                    modName = def.modContentPack?.Name ?? "??",
                    searchText = BuildSearchText(def, weapons),
                    weaponTip = BuildWeaponTip(weapons),
                    trailDef = ProjectileRules.TrailDefFor(def),
                    editWidth = 0f,
                    widthBuffer = null,
                    editLength = 0f,
                    lengthBuffer = null,
                    editFade = 0f,
                    fadeBuffer = null
                });
            }

            // 固定按投射物 defName 字典序（表头不可点排序）
            rows.Sort((a, b) => string.Compare(a.projectile.defName, b.projectile.defName, StringComparison.OrdinalIgnoreCase));

            modOptions.AddRange(rows.Select(r => r.modName).Distinct()
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase));

            SyncAllEditValues();
        }

        // 上排显示名。部分投射物没有 label，退回 defName，避免出现空行。
        static string DisplayLabel(ThingDef def, int extra = 0)
        {
            string label = def.LabelCap.Resolve();
            return WithCount(label.NullOrEmpty() ? def.defName : label, extra);
        }

        // 无武器时返回 null（该列显示占位符）
        static string? WeaponDefName(List<ThingDef> weapons) =>
            weapons.Count == 0 ? null : WithCount(weapons[0].defName, weapons.Count - 1);

        // 有额外项时追加 " +N" 后缀
        static string WithCount(string name, int extra) => extra > 0 ? $"{name} +{extra}" : name;

        static string BuildSearchText(ThingDef projectile, List<ThingDef> weapons)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(projectile.defName).Append(' ').Append(projectile.label).Append(' ');

            foreach (ThingDef weapon in weapons)
            {
                sb.Append(weapon.defName).Append(' ').Append(weapon.label).Append(' ');
            }

            return sb.ToString();
        }

        static string BuildWeaponTip(List<ThingDef> weapons)
        {
            if (weapons.Count == 0) return "GunplayOverrideNoWeapon".Translate();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("GunplayOverrideWeaponTipHeader".Translate());

            foreach (ThingDef weapon in weapons)
            {
                sb.AppendLine("  " + weapon.defName + (weapon.label.NullOrEmpty() ? "" : "\n    " + weapon.label));
            }

            return sb.ToString().TrimEnd();
        }

        // 把已有覆写（读档/上次设置）同步到行上的输入框状态
        void SyncAllEditValues()
        {
            foreach (Row row in rows)
            {
                if (!ProjectileOverrides.Has(row.projectile)) continue;

                SyncEditValue(row);
            }
        }

        static void SyncEditValue(Row row)
        {
            // 一次解析：材质列 / 颜色列的「继承」预览要用 row.trailDef，三个数值列也要用同一份结果。
            // 顺手把 trailDef 同步到行上，省掉 RefreshTrailDef 那次重复解析（见 TrailResolve）。
            TrailResolve r = ProjectileRules.Resolve(row.projectile);
            row.trailDef = r.trailDef;

            row.editWidth = TrailWidth.EffectiveForDef(r, row.projectile);
            row.widthBuffer = Format(row.editWidth);
            row.editLength = TrailLength.Effective(r);
            row.lengthBuffer = FormatInt(row.editLength);
            row.editFade = TrailFade.Effective(r);
            row.fadeBuffer = FormatInt(row.editFade);
        }

        // 重取该行「当前生效的材质」。row.trailDef 是开窗时缓存的，只在覆写记录变化时才需要重算：
        // 不重算的话，关掉覆写（记录被删）后材质列、颜色列的继承色、以及三个数值列的只读值
        // 都会继续显示旧材质的内容——数据其实已经还原了，是显示没跟上。
        static void RefreshTrailDef(Row row)
        {
            row.trailDef = ProjectileRules.TrailDefFor(row.projectile);
        }

        static string Format(float value)
        {
            return value.ToString("0.###");
        }

        // 长度系数与淡出时长都以整数刻度显示（分数在这里没有视觉意义，见 TrailLength / TrailFade）
        static string FormatInt(float value)
        {
            return Mathf.RoundToInt(value).ToString();
        }

        // ---------------- 是否显示轨迹 ----------------

        // 一行当前是否显示轨迹。有覆写就以覆写为准，否则取默认情况。判定统一收在
        // ProjectileRules.TrailEnabled（全局开关 + XML 是否配了 trail + 无贴图 + 弧线开关）。
        // 轨迹列显示什么、轨迹过滤按什么筛、新建覆写时的默认值，都只认这一处口径。
        static bool TrailShown(Row row)
        {
            return ProjectileRules.TrailEnabled(row.projectile);
        }

        // ---------------- 过滤 ----------------

        void RebuildVisible()
        {
            visible.Clear();
            quickSearch.noResultsMatched = false;

            foreach (Row row in rows)
            {
                if (modFilter != null && row.modName != modFilter) continue;
                if (trailFilter != TrailFilter.Any && TrailShown(row) != (trailFilter == TrailFilter.Shown)) continue;
                if (onlyOverridden && !ProjectileOverrides.Has(row.projectile)) continue;
                if (onlyWithWeapon && row.weapons.Count == 0) continue;
                if (!quickSearch.filter.Matches(row.searchText)) continue;

                visible.Add(row);
            }

            quickSearch.noResultsMatched = visible.Count == 0;
            visibleDirty = false;
            if (!visibleKeepScroll) scrollPosition.y = 0f;
            RecountOverridden();
        }

        void RecountOverridden()
        {
            int count = 0;
            foreach (Row row in visible)
            {
                if (ProjectileOverrides.Has(row.projectile)) count++;
            }

            overriddenInFilter = count;
        }

        // keepScroll：由行内开关引发的重建传 true，避免每次点开关都跳回列表顶部；
        // 过滤条件本身变化时传 false（默认），回到顶部更符合预期。
        void MarkVisibleDirty(bool keepScroll = false)
        {
            visibleDirty = true;
            visibleKeepScroll = keepScroll;
        }

        void OnSearchChanged()
        {
            MarkVisibleDirty();
        }

        // ---------------- 绘制 ----------------

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            ClearPaintedHighlightsOnRelease();

            float curY = inRect.y;

            Rect searchRect = new Rect(inRect.x, curY, inRect.width, QuickSearchWidget.WidgetHeight);
            quickSearch.OnGUI(searchRect, OnSearchChanged);
            curY += QuickSearchWidget.WidgetHeight + Spacing;

            DrawFilterRow(new Rect(inRect.x, curY, inRect.width, FilterRowHeight));
            curY += FilterRowHeight + Spacing;

            DrawHeader(new Rect(inRect.x, curY, inRect.width, HeaderHeight));
            curY += HeaderHeight;

            DrawRows(new Rect(inRect.x, curY, inRect.width, Mathf.Max(0f, inRect.yMax - curY)));
        }

        void DrawFilterRow(Rect rect)
        {
            float curX = rect.x;

            // mod 过滤（按投射物所属 modContentPack）
            Rect modRect = new Rect(curX, rect.y, ModButtonWidth, rect.height);
            string? currentMod = modFilter;
            if (currentMod == null) currentMod = "GunplayOverrideAllMods".Translate();
            string modLabel = currentMod;
            if (Widgets.ButtonText(modRect, modLabel)) OpenModMenu();
            curX += ModButtonWidth + Spacing;

            // 轨迹过滤：按「当前是否显示轨迹」筛（口径见 TrailShown）
            Rect trailRect = new Rect(curX, rect.y, TrailFilterWidth, rect.height);
            if (Widgets.ButtonText(trailRect, "GunplayOverrideTrailFilter".Translate() + ": " + TrailFilterLabel()))
                OpenTrailFilterMenu();
            curX += TrailFilterWidth + Spacing;

            // 仅显示已覆写
            Rect onlyRect = new Rect(curX, rect.y, OnlyOverriddenWidth, rect.height);
            bool before = onlyOverridden;
            FilterCheckboxLabeled(onlyRect, "GunplayOverrideOnlyOverridden".Translate(), ref onlyOverridden);
            if (onlyOverridden != before) MarkVisibleDirty();
            curX += OnlyOverriddenWidth + Spacing;

            // 仅显示有武器发射的投射物（weapons.Count > 0）。炮塔 / 动物远程攻击 / 脚本投射物
            // 这些「无武器」的投射物在列表里通常占大多数却没有可配的武器上下文，据此把它们滤掉。
            Rect weaponRect = new Rect(curX, rect.y, OnlyWithWeaponWidth, rect.height);
            bool weaponBefore = onlyWithWeapon;
            FilterCheckboxLabeled(weaponRect, "GunplayOverrideOnlyWithWeapon".Translate(), ref onlyWithWeapon);
            if (onlyWithWeapon != weaponBefore) MarkVisibleDirty();
            curX += OnlyWithWeaponWidth + Spacing;

            // 计数
            Rect countRect = new Rect(curX, rect.y, CountWidth, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(countRect, "GunplayOverrideCount".Translate(overriddenInFilter, visible.Count));
            Text.Anchor = TextAnchor.UpperLeft;

            // 清除全部
            Rect clearRect = new Rect(rect.xMax - ClearButtonWidth, rect.y, ClearButtonWidth, rect.height);
            if (Widgets.ButtonText(clearRect, "GunplayOverrideClearAll".Translate())) ConfirmClearAll();
        }

        // 过滤区那两个开关的绘制。与 Widgets.CheckboxLabeled 唯一的差别：标签文字右对齐、紧贴方框。
        // CheckboxLabeled 把方框画在**整格右端**，文字却按 MiddleLeft 留在**整格最左端**
        // （源码：rect2.xMax -= 24f; Label(rect2, label); CheckboxDraw(rect.x + rect.width - 24f, ...)），
        // 于是字与自己的方框之间空出一大段，反而挨着前一个开关的方框，容易被读成属于上一个开关。
        static void FilterCheckboxLabeled(Rect rect, string label, ref bool state)
        {
            Rect labelRect = rect;
            labelRect.xMax -= ToggleSize;

            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(labelRect, label);
            Text.Anchor = anchor;

            // 与 CheckboxLabeled 内部同款分工：整格负责点击，方框只画不处理，避免同一次点击被算两遍
            Widgets.ToggleInvisibleDraggable(rect, ref state, true, false);
            Widgets.CheckboxDraw(rect.xMax - ToggleSize, rect.y + (rect.height - ToggleSize) * 0.5f, state, false, ToggleSize);
        }

        void OpenModMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>
            {
                new FloatMenuOption("GunplayOverrideAllMods".Translate(), () =>
                {
                    modFilter = null;
                    MarkVisibleDirty();
                })
            };

            foreach (string mod in modOptions)
            {
                string captured = mod;
                options.Add(new FloatMenuOption(captured, () =>
                {
                    modFilter = captured;
                    MarkVisibleDirty();
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        string TrailFilterLabel()
        {
            switch (trailFilter)
            {
                case TrailFilter.Shown: return "GunplayOverrideTrailFilterShown".Translate();
                case TrailFilter.Hidden: return "GunplayOverrideTrailFilterHidden".Translate();
                default: return "GunplayOverrideTrailFilterAll".Translate();
            }
        }

        void OpenTrailFilterMenu()
        {
            List<FloatMenuOption> options = new List<FloatMenuOption>();

            foreach (TrailFilter option in new[] { TrailFilter.Any, TrailFilter.Shown, TrailFilter.Hidden })
            {
                TrailFilter captured = option;
                options.Add(new FloatMenuOption(TrailFilterLabelFor(captured), () =>
                {
                    trailFilter = captured;
                    MarkVisibleDirty();
                }));
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }

        static string TrailFilterLabelFor(TrailFilter filter)
        {
            switch (filter)
            {
                case TrailFilter.Shown: return "GunplayOverrideTrailFilterShown".Translate();
                case TrailFilter.Hidden: return "GunplayOverrideTrailFilterHidden".Translate();
                default: return "GunplayOverrideTrailFilterAll".Translate();
            }
        }

        void ConfirmClearAll()
        {
            int count = ProjectileOverrides.Count;
            if (count == 0) return;

            // 报两个数：总条目数，以及其中「键对应的投射物 def 已不存在」的条数。
            // 后者不在列表里（BuildRows 只收 projectile != null 的 def），却会被 Clear 一并清掉，
            // 不说明的话用户会觉得"确认框里的数字比列表里多"。
            int orphaned = ProjectileOverrides.CountOrphaned();

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "GunplayOverrideClearAllConfirm".Translate(count, orphaned),
                () =>
                {
                    ProjectileOverrides.Clear();

                    // 记录一次性全没了，每行缓存的材质都得重取（同 RefreshTrailDef 的理由：
                    // 材质的解析结果依赖覆写记录，而记录是缓存在行上的）
                    foreach (Row row in rows) RefreshTrailDef(row);

                    MarkVisibleDirty();
                    RebuildVisible();
                },
                true,
                "GunplayOverrideClearAll".Translate()));
        }

        void DrawHeader(Rect rect)
        {
            ColumnLayout layout = ColumnLayout.Compute(rect.width);

            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(0.75f, 0.75f, 0.75f);

            // 四个开关列（覆写 / 轨迹 / 曲线 / 弹速）的图标在格子里居中（见 DrawToggleBox），表头也居中才对得上；
            // 其余列的内容都是左对齐（按钮 / 色块 / 输入框统一内缩 4px），表头保持左对齐。
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(layout.overrideX, rect.y, OverrideToggleWidth, rect.height), "GunplayOverrideColEnabled".Translate());
            Widgets.Label(new Rect(layout.trailX, rect.y, TrailToggleWidth, rect.height), "GunplayOverrideColTrail".Translate());
            Widgets.Label(new Rect(layout.curveX, rect.y, CurveToggleWidth, rect.height), "GunplayOverrideColCurve".Translate());
            if (SpeedColumnShown)
                Widgets.Label(new Rect(layout.speedX, rect.y, SpeedColumnWidth, rect.height), "GunplayOverrideColSpeed".Translate());

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(layout.materialX, rect.y, MaterialColumnWidth, rect.height), "GunplayOverrideColMaterial".Translate());
            Widgets.Label(new Rect(layout.colorX, rect.y, ColorColumnWidth, rect.height), "GunplayOverrideColColor".Translate());
            Widgets.Label(new Rect(layout.projX, rect.y, layout.projW, rect.height), "GunplayOverrideColProjectile".Translate());
            Widgets.Label(new Rect(layout.weaponX, rect.y, layout.weaponW, rect.height), "GunplayOverrideColWeapon".Translate());
            Widgets.Label(new Rect(layout.widthX, rect.y, WidthColumnWidth, rect.height), "GunplayOverrideColWidth".Translate());
            Widgets.Label(new Rect(layout.lengthX, rect.y, LengthColumnWidth, rect.height), "GunplayOverrideColLength".Translate());
            Widgets.Label(new Rect(layout.fadeX, rect.y, FadeColumnWidth, rect.height), "GunplayOverrideColFade".Translate());

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // 各列的 x 坐标与两个弹性列（投射物 / 武器）的宽度。用结构体而不是一排 out 参数：
        // 表头、行容器、行绘制三处都要用同一组值，逐个 out 传递既冗长又容易接错顺序。
        readonly struct ColumnLayout
        {
            internal readonly float overrideX;
            internal readonly float trailX;
            internal readonly float curveX;
            internal readonly float speedX;
            internal readonly float colorX;
            internal readonly float materialX;
            internal readonly float projX;
            internal readonly float weaponX;
            internal readonly float widthX;
            internal readonly float lengthX;
            internal readonly float fadeX;
            internal readonly float projW;
            internal readonly float weaponW;

            internal ColumnLayout(float overrideX, float trailX, float curveX, float speedX, float materialX, float colorX, float projX, float weaponX,
                float widthX, float lengthX, float fadeX, float projW, float weaponW)
            {
                this.overrideX = overrideX;
                this.trailX = trailX;
                this.curveX = curveX;
                this.speedX = speedX;
                this.materialX = materialX;
                this.colorX = colorX;
                this.projX = projX;
                this.weaponX = weaponX;
                this.widthX = widthX;
                this.lengthX = lengthX;
                this.fadeX = fadeX;
                this.projW = projW;
                this.weaponW = weaponW;
            }

            // 固定宽度的列各占其位，剩余宽度由投射物 / 武器两列平分。
            // 列序：左边四个行为开关（覆写 / 轨迹 / 曲线 / 弹速）→ 轨迹外观（材质 → 颜色，颜色比材质具体，
            // 单项覆写的颜色会盖住材质色）→ 投射物 / 武器两列上下文 → 轨迹的三个数值。
            //
            // 弹速列隐藏时不是「占着位置留空」，而是整列宽度归零：省下的 60px 直接还给
            // 投射物 / 武器两个弹性列，而不是留一条空白。
            internal static ColumnLayout Compute(float width)
            {
                float speedWidth = SpeedColumnShown ? SpeedColumnWidth : 0f;

                float inner = Mathf.Max(0f, width - ScrollbarWidth);
                float rest = Mathf.Max(0f, inner - OverrideToggleWidth - TrailToggleWidth - CurveToggleWidth - speedWidth - ColorColumnWidth
                    - MaterialColumnWidth - WidthColumnWidth - LengthColumnWidth - FadeColumnWidth);

                float projW = rest * 0.5f;
                float weaponW = rest - projW;

                float overrideX = 0f;
                float trailX = overrideX + OverrideToggleWidth;
                float curveX = trailX + TrailToggleWidth;
                float speedX = curveX + CurveToggleWidth;
                float materialX = speedX + speedWidth;
                float colorX = materialX + MaterialColumnWidth;
                float projX = colorX + ColorColumnWidth;
                float weaponX = projX + projW;
                float widthX = weaponX + weaponW;
                float lengthX = widthX + WidthColumnWidth;

                return new ColumnLayout(overrideX, trailX, curveX, speedX, materialX, colorX, projX, weaponX,
                    widthX, lengthX, lengthX + LengthColumnWidth, projW, weaponW);
            }
        }

        void DrawRows(Rect outRect)
        {
            // 拖动期间冻结列表的重建（仍照常绘制），理由见 DragFrozen
            if (visibleDirty && !DragFrozen) RebuildVisible();

            if (visible.Count == 0)
            {
                Widgets.Label(outRect, "GunplayOverrideNoResults".Translate());
                return;
            }

            ColumnLayout layout = ColumnLayout.Compute(outRect.width);

            float viewWidth = Mathf.Max(0f, outRect.width - ScrollbarWidth);
            Rect viewRect = new Rect(0f, 0f, viewWidth, visible.Count * RowHeight);

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            int first = Mathf.Clamp(Mathf.FloorToInt(scrollPosition.y / RowHeight), 0, visible.Count - 1);
            int last = Mathf.Clamp(Mathf.CeilToInt((scrollPosition.y + outRect.height) / RowHeight), 0, visible.Count - 1);

            for (int i = first; i <= last; i++)
            {
                DrawRow(visible[i], i, viewWidth, layout);
            }

            Widgets.EndScrollView();
        }

        void DrawRow(Row row, int index, float viewWidth, in ColumnLayout layout)
        {
            float y = index * RowHeight;
            Widgets.DrawHighlightIfMouseover(new Rect(0f, y, viewWidth, RowHeight));

            // 涂抹高光画在这一层（内容之前，行高亮之后），见 DrawPaintedHighlights
            DrawPaintedHighlights(row, y, layout);

            ProjectileOverride? over = ProjectileOverrides.Get(row.projectile);
            bool overridden = over != null;
            bool trailOn = TrailShown(row);   // 未覆写时是默认情况，不再是恒为勾选

            DrawToggle(row, layout.overrideX, y, overridden);
            DrawTrailToggle(row, layout.trailX, y, trailOn);
            DrawCurveToggle(row, layout.curveX, y, ProjectileRules.CurveEnabled(row.projectile));

            // CE 下这一列不参与布局（见 SpeedColumnShown），连取值都省掉
            if (SpeedColumnShown)
            {
                // 显示该投射物自己的决定（不含全局总开关，理由见 ProjectileRules.SpeedAffected）
                DrawSpeedToggle(row, layout.speedX, y, ProjectileRules.SpeedAffected(row.projectile));
            }

            DrawMaterialCell(row, layout.materialX, y, overridden, trailOn);
            DrawColorCell(row, layout.colorX, y, overridden, trailOn);
            DrawNameCell(row.projectile, row.projectileLabel, row.projectile.defName, layout.projX, y, layout.projW, null);
            DrawWeaponCell(row, layout.weaponX, y, layout.weaponW);
            DrawNumberCell(row, WidthColumn, layout.widthX, y, overridden, trailOn, ref row.editWidth, ref row.widthBuffer);
            DrawNumberCell(row, LengthColumn, layout.lengthX, y, overridden, trailOn, ref row.editLength, ref row.lengthBuffer);
            DrawNumberCell(row, FadeColumn, layout.fadeX, y, overridden, trailOn, ref row.editFade, ref row.fadeBuffer);
        }

        // ---------------- 涂抹高光 ----------------

        // 格子被涂过就记一位。用位掩码而不是 HashSet，是为了不给出几百个 Row 各挂一个集合对象；
        // 清零是一次线性遍历，也只发生在松开鼠标那一帧（见 ClearPaintedHighlightsOnRelease）。
        static void MarkPainted(Row row, int tag) => row.paintedMask |= 1 << tag;

        static bool IsPainted(Row row, int tag) => (row.paintedMask & (1 << tag)) != 0;

        // 开关列的涂抹由本体的复选框接管，我们拿不到"这一下是涂的还是点的"，
        // 只能这样判：本体的涂抹只在按住左键时发力（见 Widgets.ToggleInvisibleDraggable 的
        // paintable 分支与那个跟随光标的图标），而单击是松开那一帧才触发——那时左键已经没按着。
        static bool IsDragPaint => Input.GetMouseButton(0);

        // 松开鼠标就整体还原。只在"按着 → 没按着"这个跳变时遍历，不必每帧扫几百行。
        void ClearPaintedHighlightsOnRelease()
        {
            bool held = Input.GetMouseButton(0);

            if (dragHighlightActive && !held)
            {
                foreach (Row row in rows) row.paintedMask = 0;
            }

            dragHighlightActive = held;
        }

        // 高光画在**背景**上（在格子内容之前）。
        //
        // 格子里的内容基本都是内缩的——数值格的输入框、材质格的按钮都内缩 4~5 像素，
        // 色块也留了边距——于是四周会露出一圈蓝，看上去就是一个淡蓝边框。
        // 这比画在内容之上要好：那种做法文字会整体被蒙色，发蓝发闷。
        void DrawPaintedHighlights(Row row, float y, in ColumnLayout layout)
        {
            if (row.paintedMask == 0) return;

            if (IsPainted(row, OverridePaintTag)) HighlightCell(layout.overrideX, y, OverrideToggleWidth);
            if (IsPainted(row, TrailPaintTag)) HighlightCell(layout.trailX, y, TrailToggleWidth);
            if (IsPainted(row, CurvePaintTag)) HighlightCell(layout.curveX, y, CurveToggleWidth);
            if (SpeedColumnShown && IsPainted(row, SpeedPaintTag)) HighlightCell(layout.speedX, y, SpeedColumnWidth);
            if (IsPainted(row, MaterialPaintTag)) HighlightCell(layout.materialX, y, MaterialColumnWidth);
            if (IsPainted(row, ColorPaintTag)) HighlightCell(layout.colorX, y, ColorColumnWidth);
            if (IsPainted(row, WidthPaintTag)) HighlightCell(layout.widthX, y, WidthColumnWidth);
            if (IsPainted(row, LengthPaintTag)) HighlightCell(layout.lengthX, y, LengthColumnWidth);
            if (IsPainted(row, FadePaintTag)) HighlightCell(layout.fadeX, y, FadeColumnWidth);
        }

        static void HighlightCell(float x, float y, float width)
        {
            GUI.color = PaintedHighlightColor;
            GUI.DrawTexture(new Rect(x, y, width, RowHeight), BaseContent.WhiteTex);
            GUI.color = Color.white;
        }

        void DrawToggle(Row row, float x, float y, bool overridden)
        {
            bool state = overridden;
            bool before = state;

            Rect rect = new Rect(x, y, OverrideToggleWidth, RowHeight);
            DrawToggleBox(x + (OverrideToggleWidth - ToggleSize) * 0.5f, y, state);
            Widgets.ToggleInvisibleDraggable(rect, ref state, true, true);
            TooltipHandler.TipRegion(rect, "GunplayOverrideTipEnabled".Translate());

            if (state == before) return;
            if (IsDragPaint) MarkPainted(row, OverridePaintTag);

            if (state)
            {
                ProjectileOverrides.Enable(row.projectile);
                SyncEditValue(row);
            }
            else
            {
                // 记录被删 = 轨迹开关、材质、颜色、宽度/长度/淡出一起回到继承/计算值。
                // 数值列的只读显示是实时算的，但材质是缓存的，必须重取一次（见 RefreshTrailDef）。
                ProjectileOverrides.Disable(row.projectile);
                RefreshTrailDef(row);
            }

            AfterOverrideChanged();
        }

        // 一个开关格：图标只负责显示，输入交给 Widgets.ToggleInvisibleDraggable，命中区是整个格子。
        //
        // 不用 Widgets.Checkbox 是因为它把绘制与输入的命中区绑死成图标那 24×24：
        // 拖拽涂抹时要鼠标精准压住每一行的小图标才行，实际根本拖不直。
        // paintable: true 让本体接管涂抹（它先把起始格切换成目标态，再把这个态涂到扫过的其余格子上），
        // 连跟随光标的图标都一并画了，见 Widgets.WidgetsOnGUI。
        static void DrawToggleBox(float iconX, float y, bool state)
        {
            Widgets.CheckboxDraw(iconX, y + (RowHeight - ToggleSize) * 0.5f, state, false, ToggleSize);
        }

        // 拖动期间（左键按着）冻结 visible 列表的重建。
        //
        // 起因：过滤生效时（仅显示已覆写 / 轨迹过滤），把一行涂成不符合条件就会让它从 visible 里
        // 消失、后面的行整体上移，鼠标底下于是换成了另一行 —— 下一次判定继续涂它，又一次上移……
        // 一次向下的拖动会像雪崩一样把下面全涂掉，远超用户划过的范围。
        // 冻结到松开鼠标为止（松开那一帧 GetMouseButton 已是 false，当帧就重建），
        // 于是「过滤结果在涂抹期间不变」，涂的永远是最初那批行。
        static bool DragFrozen => Input.GetMouseButton(0);

        void DrawTrailToggle(Row row, float x, float y, bool trailOn)
        {
            bool state = trailOn;
            bool before = state;

            Rect rect = new Rect(x, y, TrailToggleWidth, RowHeight);
            DrawToggleBox(x + (TrailToggleWidth - ToggleSize) * 0.5f, y, state);
            Widgets.ToggleInvisibleDraggable(rect, ref state, true, true);
            TooltipHandler.TipRegion(rect, "GunplayOverrideTipTrail".Translate());

            if (state == before) return;
            if (IsDragPaint) MarkPainted(row, TrailPaintTag);

            EnsureOverridden(row);
            ProjectileOverrides.SetTrailEnabled(row.projectile, state);
            AfterOverrideChanged();
        }

        // 曲线列：该投射物是否使用曲线轨迹绘制（弧线样式，而非直线四边形）。
        // 与轨迹列同约定（未覆写时显示生效值、点一下顺带开启覆写），口径统一走
        // ProjectileRules.CurveEnabled（单项覆写 > 自动检测 > 默认规则）。
        // 与弹速列一样不随轨迹开关变灰：轨迹关掉时它只是暂不生效，覆写仍保留。
        void DrawCurveToggle(Row row, float x, float y, bool curveOn)
        {
            bool state = curveOn;
            bool before = state;

            Rect rect = new Rect(x, y, CurveToggleWidth, RowHeight);
            DrawToggleBox(x + (CurveToggleWidth - ToggleSize) * 0.5f, y, state);
            Widgets.ToggleInvisibleDraggable(rect, ref state, true, true);
            TooltipHandler.TipRegion(rect, "GunplayOverrideTipCurve".Translate());

            if (state == before) return;
            if (IsDragPaint) MarkPainted(row, CurvePaintTag);

            EnsureOverridden(row);
            ProjectileOverrides.SetCurveEnabled(row.projectile, state);
            AfterOverrideChanged();
        }

        // 弹速列：该投射物是否受全局弹速缩放（GunplaySettings.projectileSpeed）。
        // 与轨迹列同约定（未覆写时点一下会顺带开启覆写），区别是它不随轨迹开关变灰——弹速与画不画轨迹无关。
        void DrawSpeedToggle(Row row, float x, float y, bool speedOn)
        {
            bool state = speedOn;

            Rect boxRect = new Rect(x, y, SpeedColumnWidth, RowHeight);
            DrawToggleBox(x + (SpeedColumnWidth - ToggleSize) * 0.5f, y, state);
            Widgets.ToggleInvisibleDraggable(boxRect, ref state, true, true);
            TooltipHandler.TipRegion(boxRect, "GunplayOverrideTipSpeed".Translate());

            if (state == speedOn) return;

            if (IsDragPaint) MarkPainted(row, SpeedPaintTag);

            EnsureOverridden(row);
            ProjectileOverrides.SetSpeedAffected(row.projectile, state);
            AfterOverrideChanged();
        }

        // 顺带开启覆写：该行还没有记录时，按当前生效值建一条（见 ProjectileOverrides.GetOrCreate）。
        // 轨迹开关 / 色块 / 数值输入三处点击都先走这里，省掉「先开覆写再设置」两步。
        // 返回是否真的新建了记录，调用方据此决定要不要走 AfterOverrideChanged。
        bool EnsureOverridden(Row row)
        {
            if (ProjectileOverrides.Has(row.projectile)) return false;

            ProjectileOverrides.Enable(row.projectile);
            SyncEditValue(row);
            return true;
        }

        // 覆写状态变化后的统一收尾。抽成一处不只是去重——三处调用原本的收尾顺序并不一致。
        void AfterOverrideChanged()
        {
            RecountOverridden();

            // 过滤生效时该行可能已不符合条件，需要重建列表；保留滚动位置，避免跳回列表顶部
            if (onlyOverridden || trailFilter != TrailFilter.Any) MarkVisibleDirty(true);
        }

        // 名称单元格：图标 + 上排 label（Small）+ 下排 defName（Tiny 灰色）。
        // 两行都按可用宽度截断，避免长名字折行后压到下一行。
        void DrawNameCell(ThingDef def, string label, string defNameText, float x, float y, float width, string? tooltip)
        {
            float textX = x + IconSize + 10f;
            float textW = Mathf.Max(0f, width - IconSize - 14f);

            GameFont font = Text.Font;
            TextAnchor anchor = Text.Anchor;

            if (Mathf.Abs(textW - truncateWidth) > 0.5f)
            {
                truncateSmall.Clear();
                truncateTiny.Clear();
                truncateWidth = textW;
            }

            Widgets.ThingIcon(new Rect(x + 2f, y + (RowHeight - IconSize) * 0.5f, IconSize, IconSize), def);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(textX, y + 1f, textW, 20f), GenText.Truncate(label, textW, truncateSmall));

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.55f, 0.55f, 0.55f);
            Widgets.Label(new Rect(textX, y + 21f, textW, 18f), GenText.Truncate(defNameText, textW, truncateTiny));

            GUI.color = Color.white;
            Text.Anchor = anchor;
            Text.Font = font;

            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(new Rect(x, y, width, RowHeight), tooltip);
        }

        void DrawWeaponCell(Row row, float x, float y, float width)
        {
            if (row.weapons.Count == 0)
            {
                GUI.color = new Color(0.55f, 0.55f, 0.55f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x + 2f, y, Mathf.Max(0f, width - 4f), RowHeight), "—");
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;

                TooltipHandler.TipRegion(new Rect(x, y, width, RowHeight), "GunplayOverrideNoWeapon".Translate());
                return;
            }

            // 走到这里必有武器（Count == 0 已在上面返回），两个 ?. 兜底只是为了让编译器不必猜
            ThingDef weapon = row.weapons[0];
            DrawNameCell(weapon, row.weaponLabel ?? weapon.defName, row.weaponDefName ?? weapon.defName, x, y, width, row.weaponTip);
        }

        // 颜色单元格：色块（当前生效颜色）+ 有自定义色时的「×」恢复按钮。
        // 色块点击打开取色窗；与轨迹/材质列同约定——在该行尚未覆写时顺带开启覆写，
        // 但仅限「确定」且颜色确有变化：点开又原样确定、或直接取消，都不该留下覆写记录。
        void DrawColorCell(Row row, float x, float y, bool overridden, bool trailOn)
        {
            // 该投射物的轨迹被关掉时，颜色没有意义
            if (overridden && !trailOn)
            {
                GUI.color = new Color(0.55f, 0.55f, 0.55f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x + 4f, y, Mathf.Max(0f, ColorColumnWidth - 8f), RowHeight), "—");
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                return;
            }

            Rect cellRect = new Rect(x, y, ColorColumnWidth, RowHeight);

            ProjectileOverride? over = ProjectileOverrides.Get(row.projectile);
            bool hasColor = over != null && over.hasColor;

            // 显示当前生效颜色：自定义色优先，否则继承轨迹 def 的颜色
            Color color = hasColor ? over!.trailColor : TrailColor.EffectiveForDef(row.trailDef);

            float swatchX = x + 4f;
            Rect swatchRect = new Rect(swatchX, y + (RowHeight - SwatchHeight) * 0.5f,
                Mathf.Min(SwatchWidth, Mathf.Max(0f, ColorColumnWidth - (hasColor ? ResetButtonWidth + 12f : 8f))), SwatchHeight);

            ColorPickerWindow.DrawCheckerSwatch(swatchRect, color);
            TooltipHandler.TipRegion(swatchRect, "GunplayOverrideTipColor".Translate());

            if (Widgets.ButtonInvisible(swatchRect))
            {
                // 覆写必须在取色窗的「确定」回调里按需开启，不能在点击时就开：
                // 该窗口只在「确定」时回调、「取消」不回调（见 ColorPickerWindow.DoWindowContents），
                // 点击时先 EnsureOverridden 会让「点开又取消」也把整行覆写打开。
                // 并且颜色没变（c == color，UnityEngine.Color 的相等判定自带容差）时一并跳过：
                // 用户没打算改，就不该留下覆写记录。
                Color initial = color;
                Find.WindowStack.Add(new ColorPickerWindow(initial, c =>
                {
                    if (c == initial) return;

                    if (EnsureOverridden(row)) AfterOverrideChanged();
                    ProjectileOverrides.SetTrailColor(row.projectile, c);
                }));
            }

            // 「×」清除自定义颜色，回到继承
            if (hasColor)
            {
                Rect resetRect = new Rect(swatchRect.xMax + 4f, y + (RowHeight - 24f) * 0.5f, ResetButtonWidth, 24f);
                if (Widgets.ButtonText(resetRect, "×"))
                {
                    ProjectileOverrides.ClearTrailColor(row.projectile);
                }
                TooltipHandler.TipRegion(resetRect, "GunplayOverrideColorResetTip".Translate());
            }

            // 拖拽涂抹：把起始格的「当前生效颜色」涂到扫过的格子上。
            // 起始格若本来就是继承色（hasColor=false），涂出去的是那个继承色的具体值 ——
            // 也就是被涂的行会多出一条自定义色记录。这是有意的：用户拖的是「这个颜色」，
            // 不是「跟着那个材质走」，否则目标行的材质与起始行不同时涂出来的色就不一样了。
            if (DragPaint.Cell(cellRect, ColorPaintTag, color))
            {
                MarkPainted(row, ColorPaintTag);
                ProjectileOverrides.SetTrailColor(row.projectile, (Color)DragPaint.Value!);
                AfterOverrideChanged();
            }
        }

        // 材质单元格：显示该投射物当前生效的轨迹材质（=「材质类型」，本体 XML 配置或单项覆写选定），
        // 点击打开 Def 选择窗口；窗口里的「清除」= 回到继承本体配置。
        // 与其它单元格同约定：在未覆写的行上点它会顺带开启覆写，但只记材质一项（见 SetTrailMaterial）。
        void DrawMaterialCell(Row row, float x, float y, bool overridden, bool trailOn)
        {
            Rect cellRect = new Rect(x, y, MaterialColumnWidth, RowHeight);

            // 轨迹关掉时材质没有意义（与颜色列同一处理）
            if (overridden && !trailOn)
            {
                DrawDash(cellRect);
                return;
            }

            // 三态：未指定（继承）/ 已指定且 def 有效 / 已指定但 def 已被移除（失效）。
            // 「已指定」必须真的回查 DefDatabase，不能只看 defName 非空：材质 def 被移除后
            // defName 仍在，只判非空的话按钮会显示回落后的继承材质名，提示却说"已单独指定"，
            // 等于把继承材质冒充成用户选的那个。TryGetTrailMaterial 内部就是一次 GetNamed(silent)，
            // 一次字典查找，每行每帧可以接受。
            string? pickedName = ProjectileOverrides.Get(row.projectile)?.trailMaterialDefName;
            bool pickedValid = pickedName != null && ProjectileOverrides.TryGetTrailMaterial(row.projectile, out _);
            bool pickedMissing = pickedName != null && !pickedValid;

            // 当前生效材质（失效态下即回落到继承的那个）。失效态刻意不用它做按钮文字——
            // 那正是要和「继承」区分开的东西。
            string effectiveLabel = row.trailDef != null ? row.trailDef.defName : "—";

            string label;
            if (pickedMissing) label = "GunplayOverrideMaterialMissingLabel".Translate(pickedName!);
            else label = effectiveLabel;

            Rect buttonRect = new Rect(x + 4f, y + 5f, Mathf.Max(0f, MaterialColumnWidth - 8f), RowHeight - 10f);

            if (Widgets.ButtonText(buttonRect, label))
            {
                // 「当前选择」仍传生效材质而不是失效的 defName：那个 def 已不在 DefDatabase 里，
                // 传它对应的对象只会高亮一个不该高亮的项；显示生效材质才与"确认后保持不变"一致。
                TrailMaterialPicker.Open(row.trailDef, def =>
                {
                    ProjectileOverrides.SetTrailMaterial(row.projectile, def);

                    // 材质换了，本行后面几列的继承口径（颜色 / 宽度 / 长度 / 淡出）跟着换，
                    // 缓存与输入框缓冲区都要重取一次，否则显示的还是旧材质的值
                    SyncEditValue(row);
                    AfterOverrideChanged();
                });
            }

            // 失效态单独一条提示：说明指定的 def 没了、当前回落到谁，并指出怎么收拾它。
            // 刻意不自动删掉这条失效记录——设置是全局跨存档的，临时停用 mod 不该永久丢用户数据。
            TooltipHandler.TipRegion(buttonRect, pickedMissing
                ? "GunplayOverrideTipMaterialMissing".Translate(pickedName!, effectiveLabel)
                : pickedValid
                    ? "GunplayOverrideTipMaterialPicked".Translate(effectiveLabel)
                    : "GunplayOverrideTipMaterialInherited".Translate(effectiveLabel));

            // 拖拽涂抹：把起始格的「当前生效材质」涂到扫过的格子上（row.trailDef 可能为 null，
            // 那表示起始格用的是继承材质，涂出去就是让目标行也回到继承 —— 与单击「清除」同一个结果）。
            // 单击仍由上面的 ButtonText 负责：拖拽结束时鼠标不在原来那个按钮上，Unity 的
            // GUI.Button 不会触发；反过来，只是点一下时 DragPaint 也判不出拖动，两边互不干扰。
            if (DragPaint.Cell(cellRect, MaterialPaintTag, row.trailDef))
            {
                MarkPainted(row, MaterialPaintTag);
                ProjectileOverrides.SetTrailMaterial(row.projectile, DragPaint.Value as ProjectileTrailDef);

                // 与单击换材质同一条收尾：本行后面几列的继承口径跟着换，缓存与输入框缓冲区都要重取
                SyncEditValue(row);
                AfterOverrideChanged();
            }
        }

        // 数值列（宽度 / 长度 / 淡出）的统一渲染。三列流程完全一致：
        //   轨迹关掉 → 占位符；未覆写 → 只读显示当前生效值；覆写 → 可编辑输入框。
        // 差异全部来自 col，见 NumberColumn 与三个静态实例。
        //
        // 数值格也接涂抹手势：Shift + 拖动（方向不限）→ 把本行的值涂到扫过的行。
        // 未覆写的行也能起拖：它的"值"就是当前生效值，涂一下即把它固化成一项覆写。
        //
        // editValue / buffer 走 ref 是因为它们必须长期挂在 Row 上：缓冲跨帧保留，
        // 否则滚出视野再滚回来会丢焦点、丢正在输入的内容。
        void DrawNumberCell(Row row, in NumberColumn col, float x, float y, bool overridden, bool trailOn,
            ref float editValue, ref string? buffer)
        {
            // 命中区是整个格子（与材质 / 颜色列同规矩，见指南 8.1）。
            // 不能图省事直接拿下面那个内缩 5px 的绘制矩形：行高 40、绘制高 30，
            // 于是行与行之间留了 10 像素的空隙，起拖十有八九落在那段空隙里，判定直接落空。
            Rect hitRect = new Rect(x, y, col.width, RowHeight);
            Rect rect = new Rect(x + 4f, y + 5f, Mathf.Max(0f, col.width - 8f), RowHeight - 10f);

            if (overridden && !trailOn)
            {
                DrawDash(rect);
                return;
            }

            HandleNumberDrag(row, col, hitRect);

            // 这一帧要不要把输入框让开。★ 必须在 MouseDown 那一帧就成立，不能等判定出拖 ★
            //
            // 输入框一旦拿到这个 MouseDown，就会把它吃成"聚焦 + 开始选字"，涂抹再也起不来。
            // 症状很有迷惑性——未聚焦的输入框上 Shift + 拖只聚焦不涂，已聚焦的反而涂得动，
            // 因为后者不再抢这一次 MouseDown。
            // 所以条件是「本轮已在涂抹」**或**「按住左键 + Shift 且鼠标正压在本格上」，
            // 后者覆盖了尚未越过阈值的那几帧，也就是真正要命的那一帧。
            bool yieldToPaint = (DragPaint.Active && DragPaint.Tag == col.paintTag)
                || (DragPaint.HasPaintIntent && Mouse.IsOver(hitRect));

            if (yieldToPaint)
            {
                GUIUtility.keyboardControl = 0;
                GUIUtility.hotControl = 0;
            }

            if (!overridden)
            {
                // 显示随全局设置实时变化的生效值，只读文本
                GUI.color = new Color(0.55f, 0.55f, 0.55f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(rect, col.format(col.effective(row)));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(rect, col.tipKey.Translate());
                return;
            }

            // 让开的办法很土但有效：这时候干脆不画输入框，只把值显示出来；松手就恢复。
            if (yieldToPaint)
            {
                DrawPaintValue(rect, col.format(editValue));
                TooltipHandler.TipRegion(rect, col.tipKey.Translate());
                return;
            }

            float before = editValue;
            Widgets.TextFieldNumeric(rect, ref editValue, ref buffer, col.min, col.max);
            TooltipHandler.TipRegion(rect, col.tipKey.Translate());

            if (editValue == before) return;

            editValue = Mathf.Clamp(editValue, col.min, col.max);
            col.apply(row, editValue);
        }

        // 涂抹期间代替输入框的只读显示。比"未覆写"那档灰值亮一点：
        // 这一格其实是有覆写值的，只是此刻不让编辑。
        static void DrawPaintValue(Rect rect, string text)
        {
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect, text);
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }

        // 数值格上的涂抹手势（见 DragPaint.Cell）。
        void HandleNumberDrag(Row row, in NumberColumn col, Rect rect)
        {
            // requireShift：在数值输入框上直接拖是"选中文字"（拖过数字就能全选替换），抢掉是净损失；
            // 按住 Shift 才是用户明确表态"我要涂"，此时方向也不再限制。
            if (DragPaint.Cell(rect, col.paintTag, col.effective(row), requireShift: true))
            {
                WriteNumber(row, col, (float)DragPaint.Value!);
            }
        }

        // 把一个值写进某行。缓冲那一半交给 SyncEditValue —— 它本来就是
        // 「按当前生效值重算本行所有输入框绑定值」的收尾函数，正好复用。
        // （本行的 editValue / buffer 与 Row 上的字段是同一份，所以本帧的输入框会立刻显示新值。）
        //
        // 与输入框打字的收尾不同，这里必须走 AfterOverrideChanged：打字只发生在已覆写的行上，
        // 而涂抹可以从"未覆写"的行起手，会新建记录、从而改变已覆写计数与过滤结果。
        void WriteNumber(Row row, in NumberColumn col, float value)
        {
            MarkPainted(row, col.paintTag);
            col.apply(row, Snap(col, value));
            SyncEditValue(row);
            AfterOverrideChanged();
        }

        // 涂过去的值也要夹进列区间；整数列（长度 / 淡出）取整，
        // 否则源行是 15.37 时会涂出一堆显示成 15、实际按 15.37 生效的行。
        static float Snap(in NumberColumn col, float value)
        {
            value = Mathf.Clamp(value, col.min, col.max);
            return col.integer ? Mathf.Round(value) : value;
        }

        // 「无意义」占位符：该行的轨迹被关掉时，宽度 / 长度 / 淡出都不生效
        static void DrawDash(Rect rect)
        {
            GUI.color = new Color(0.55f, 0.55f, 0.55f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect, "—");
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
        }
    }
}
