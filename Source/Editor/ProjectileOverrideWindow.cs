using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ASQPicker;
using RimWorld;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 投射物单项覆写窗口。
    //
    // 结构：上方过滤区（搜索框 + mod 下拉 + 仅显示已覆写 + 计数 + 清除全部），下方列表区（表头 + 虚拟滚动）。
    // 一行 = 一个投射物 ThingDef；武器列只是上下文（轨迹本身只由投射物 def 决定，见 PatchProjectileLaunch），
    // 多个武器共用同一投射物时显示第一个并在 defName 后加「+N」，悬浮列出全部。
    //
    // 每行两个开关：
    //   「覆写」—— 该行是否有覆写记录。关掉即丢弃记录（轨迹开关与宽度一起回到继承/计算值）。
    //   「轨迹」—— 是否绘制该投射物的轨迹，未覆写时显示继承态（勾选）。直接点它会顺带把覆写打开，
    //              省掉「先开覆写再关轨迹」两步。
    //
    // 列表可能有数百行（装 CE 或大型武器包之后更多），因此走虚拟滚动：只为可见区间建 Rect，
    // 但输入框的字符串缓冲必须挂在行对象上跨帧保留，否则滚出视野再滚回来会丢焦点/丢输入。
    public class ProjectileOverrideWindow : Window
    {
        const float Spacing = 6f;
        const float FilterRowHeight = 30f;
        const float HeaderHeight = 24f;
        const float RowHeight = 40f;
        const float IconSize = 32f;
        const float OverrideToggleWidth = 60f;
        const float TrailToggleWidth = 60f;
        const float ColorColumnWidth = 90f;
        const float SwatchWidth = 50f;
        const float SwatchHeight = 24f;
        const float ResetButtonWidth = 24f;
        const float WidthColumnWidth = 110f;
        const float LengthColumnWidth = 100f;
        const float FadeColumnWidth = 90f;
        const float ModButtonWidth = 220f;
        const float TrailFilterWidth = 190f;
        const float OnlyOverriddenWidth = 180f;
        const float CountWidth = 200f;
        const float ClearButtonWidth = 150f;
        const float ScrollbarWidth = 16f;

        const float MinWidth = 0f;
        const float MaxWidth = 2f;

        public override Vector2 InitialSize => new Vector2(1280f, 800f);

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
            public ProjectileTrailDef? trailDef;    // 该投射物解析出的轨迹 def（颜色继承的来源）
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
            internal readonly Func<float, string> format;
            internal readonly string tipKey;
            internal readonly Func<Row, float> effective;   // 该行当前生效值（未覆写时也用它做只读显示）
            internal readonly Action<Row, float> apply;     // 写入覆写

            internal NumberColumn(float width, float min, float max, Func<float, string> format,
                string tipKey, Func<Row, float> effective, Action<Row, float> apply)
            {
                this.width = width;
                this.min = min;
                this.max = max;
                this.format = format;
                this.tipKey = tipKey;
                this.effective = effective;
                this.apply = apply;
            }
        }

        // 宽度是绝对值（0~2 世界单位，3 位小数）；长度系数与淡出是整数刻度（tick）
        static readonly NumberColumn WidthColumn = new NumberColumn(
            WidthColumnWidth, MinWidth, MaxWidth, Format, "GunplayOverrideTipWidth",
            row => TrailWidth.EffectiveForDef(row.projectile),
            (row, v) => ProjectileOverrides.SetTrailWidth(row.projectile, v));

        static readonly NumberColumn LengthColumn = new NumberColumn(
            LengthColumnWidth, TrailLength.Min, TrailLength.Max, FormatInt, "GunplayOverrideTipLength",
            row => TrailLength.Effective(row.projectile),
            (row, v) => ProjectileOverrides.SetTrailLength(row.projectile, v));

        static readonly NumberColumn FadeColumn = new NumberColumn(
            FadeColumnWidth, TrailFade.Min, TrailFade.Max, FormatInt, "GunplayOverrideTipFade",
            row => TrailFade.Effective(row.projectile),
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
        Vector2 scrollPosition;

        bool visibleDirty = true;
        bool visibleKeepScroll;   // 重建列表时是否保留滚动位置（只有开关引发的重建才保留）
        int overriddenInFilter;

        public ProjectileOverrideWindow()
        {
            forcePause = false;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = true;
            doCloseX = true;

            BuildRows();
        }

        public static void Open()
        {
            Find.WindowStack.Add(new ProjectileOverrideWindow());
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

                    if (!map.TryGetValue(projectile, out List<ThingDef> list))
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
                    weaponLabel = weapons.Count > 0 ? DisplayLabel(weapons[0]) : null,
                    modName = def.modContentPack?.Name ?? "??",
                    searchText = BuildSearchText(def, weapons),
                    weaponTip = BuildWeaponTip(weapons),
                    trailDef = (GunplaySetup.ProjectileProp(def) ?? GunplaySetup.DefaultProjectileProp)?.trail,
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
        static string DisplayLabel(ThingDef def)
        {
            string label = def.LabelCap.Resolve();
            return label.NullOrEmpty() ? def.defName : label;
        }

        // 无武器时返回 null（该列显示占位符）
        static string? WeaponDefName(List<ThingDef> weapons)
        {
            if (weapons.Count == 0) return null;

            return weapons.Count > 1 ? weapons[0].defName + " +" + (weapons.Count - 1) : weapons[0].defName;
        }

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
                sb.AppendLine("  " + weapon.defName + (weapon.label.NullOrEmpty() ? "" : " — " + weapon.label));
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
            row.editWidth = TrailWidth.EffectiveForDef(row.projectile);
            row.widthBuffer = Format(row.editWidth);
            row.editLength = TrailLength.Effective(row.projectile);
            row.lengthBuffer = FormatInt(row.editLength);
            row.editFade = TrailFade.Effective(row.projectile);
            row.fadeBuffer = FormatInt(row.editFade);
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

        // 一行当前是否显示轨迹。有覆写就以覆写为准，否则取默认情况
        // （全局开关 + XML 是否配了 trail，见 ProjectileRules.TrailEnabled）。
        // 轨迹列显示什么、轨迹过滤按什么筛，都只认这个方法，保证两者永远一致。
        static bool TrailShown(Row row)
        {
            ProjectileOverride? over = ProjectileOverrides.Get(row.projectile);
            return over != null ? over.trailEnabled : ProjectileRules.TrailEnabled(row.projectile);
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
            string modLabel = "GunplayOverrideModFilter".Translate() + ": " + currentMod;
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
            Widgets.CheckboxLabeled(onlyRect, "GunplayOverrideOnlyOverridden".Translate(), ref onlyOverridden);
            if (onlyOverridden != before) MarkVisibleDirty();
            curX += OnlyOverriddenWidth + Spacing;

            // 计数
            Rect countRect = new Rect(curX, rect.y, CountWidth, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(countRect, "GunplayOverrideCount".Translate(overriddenInFilter, visible.Count));
            Text.Anchor = TextAnchor.UpperLeft;

            // 清除全部
            Rect clearRect = new Rect(rect.xMax - ClearButtonWidth, rect.y, ClearButtonWidth, rect.height);
            if (Widgets.ButtonText(clearRect, "GunplayOverrideClearAll".Translate())) ConfirmClearAll();
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
            if (ProjectileOverrides.Count == 0) return;

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "GunplayOverrideClearAllConfirm".Translate(),
                () =>
                {
                    ProjectileOverrides.Clear();
                    MarkVisibleDirty();
                    RebuildVisible();
                },
                true));
        }

        void DrawHeader(Rect rect)
        {
            ColumnLayout layout = ColumnLayout.Compute(rect.width);

            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(0.75f, 0.75f, 0.75f);

            Widgets.Label(new Rect(layout.overrideX, rect.y, OverrideToggleWidth, rect.height), "GunplayOverrideColEnabled".Translate());
            Widgets.Label(new Rect(layout.trailX, rect.y, TrailToggleWidth, rect.height), "GunplayOverrideColTrail".Translate());
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
            internal readonly float colorX;
            internal readonly float projX;
            internal readonly float weaponX;
            internal readonly float widthX;
            internal readonly float lengthX;
            internal readonly float fadeX;
            internal readonly float projW;
            internal readonly float weaponW;

            internal ColumnLayout(float overrideX, float trailX, float colorX, float projX, float weaponX,
                float widthX, float lengthX, float fadeX, float projW, float weaponW)
            {
                this.overrideX = overrideX;
                this.trailX = trailX;
                this.colorX = colorX;
                this.projX = projX;
                this.weaponX = weaponX;
                this.widthX = widthX;
                this.lengthX = lengthX;
                this.fadeX = fadeX;
                this.projW = projW;
                this.weaponW = weaponW;
            }

            // 固定宽度的列各占其位，剩余宽度由投射物 / 武器两列平分
            internal static ColumnLayout Compute(float width)
            {
                float inner = Mathf.Max(0f, width - ScrollbarWidth);
                float rest = Mathf.Max(0f, inner - OverrideToggleWidth - TrailToggleWidth - ColorColumnWidth
                    - WidthColumnWidth - LengthColumnWidth - FadeColumnWidth);

                float projW = rest * 0.5f;
                float weaponW = rest - projW;

                float overrideX = 0f;
                float trailX = overrideX + OverrideToggleWidth;
                float colorX = trailX + TrailToggleWidth;
                float projX = colorX + ColorColumnWidth;
                float weaponX = projX + projW;
                float widthX = weaponX + weaponW;
                float lengthX = widthX + WidthColumnWidth;

                return new ColumnLayout(overrideX, trailX, colorX, projX, weaponX,
                    widthX, lengthX, lengthX + LengthColumnWidth, projW, weaponW);
            }
        }

        void DrawRows(Rect outRect)
        {
            if (visibleDirty) RebuildVisible();

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

            ProjectileOverride? over = ProjectileOverrides.Get(row.projectile);
            bool overridden = over != null;
            bool trailOn = TrailShown(row);   // 未覆写时是默认情况，不再是恒为勾选

            DrawToggle(row, layout.overrideX, y, overridden);
            DrawTrailToggle(row, layout.trailX, y, trailOn);
            DrawColorCell(row, layout.colorX, y, overridden, trailOn);
            DrawNameCell(row.projectile, row.projectileLabel, row.projectile.defName, layout.projX, y, layout.projW, null);
            DrawWeaponCell(row, layout.weaponX, y, layout.weaponW);
            DrawNumberCell(row, WidthColumn, layout.widthX, y, overridden, trailOn, ref row.editWidth, ref row.widthBuffer);
            DrawNumberCell(row, LengthColumn, layout.lengthX, y, overridden, trailOn, ref row.editLength, ref row.lengthBuffer);
            DrawNumberCell(row, FadeColumn, layout.fadeX, y, overridden, trailOn, ref row.editFade, ref row.fadeBuffer);
        }

        void DrawToggle(Row row, float x, float y, bool overridden)
        {
            bool state = overridden;
            bool before = state;

            Rect rect = new Rect(x, y, OverrideToggleWidth, RowHeight);
            Widgets.Checkbox(new Vector2(x + (OverrideToggleWidth - 24f) * 0.5f, y + (RowHeight - 24f) * 0.5f), ref state, 24f);
            TooltipHandler.TipRegion(rect, "GunplayOverrideTipEnabled".Translate());

            if (state == before) return;

            if (state)
            {
                ProjectileOverrides.Enable(row.projectile);
                SyncEditValue(row);
            }
            else
            {
                ProjectileOverrides.Disable(row.projectile);
            }

            AfterOverrideChanged();
        }

        void DrawTrailToggle(Row row, float x, float y, bool trailOn)
        {
            bool state = trailOn;
            bool before = state;

            Rect rect = new Rect(x, y, TrailToggleWidth, RowHeight);
            Widgets.Checkbox(new Vector2(x + (TrailToggleWidth - 24f) * 0.5f, y + (RowHeight - 24f) * 0.5f), ref state, 24f);
            TooltipHandler.TipRegion(rect, "GunplayOverrideTipTrail".Translate());

            if (state == before) return;

            EnsureOverridden(row);
            ProjectileOverrides.SetTrailEnabled(row.projectile, state);
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
        // 色块点击即取色；该行尚未开启覆写时与轨迹开关同约定——顺带开启覆写。
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

            GUI.color = color;
            GUI.DrawTexture(swatchRect, BaseContent.WhiteTex);
            GUI.color = Color.white;
            Widgets.DrawBox(swatchRect, 1);
            TooltipHandler.TipRegion(swatchRect, "GunplayOverrideTipColor".Translate());

            if (Widgets.ButtonInvisible(swatchRect))
            {
                if (EnsureOverridden(row)) AfterOverrideChanged();

                Find.WindowStack.Add(new ColorPickerWindow(color, c =>
                    ProjectileOverrides.SetTrailColor(row.projectile, c)));
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
        }

        // 数值列（宽度 / 长度 / 淡出）的统一渲染。三列流程完全一致：
        //   轨迹关掉 → 占位符；未覆写 → 只读显示当前生效值；覆写 → 可编辑输入框。
        // 差异全部来自 col，见 NumberColumn 与三个静态实例。
        // editValue / buffer 走 ref 是因为它们必须长期挂在 Row 上：缓冲跨帧保留，
        // 否则滚出视野再滚回来会丢焦点、丢正在输入的内容。
        void DrawNumberCell(Row row, in NumberColumn col, float x, float y, bool overridden, bool trailOn,
            ref float editValue, ref string? buffer)
        {
            Rect rect = new Rect(x + 4f, y + 5f, Mathf.Max(0f, col.width - 8f), RowHeight - 10f);

            if (overridden && !trailOn)
            {
                DrawDash(rect);
                return;
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

            float before = editValue;
            Widgets.TextFieldNumeric(rect, ref editValue, ref buffer, col.min, col.max);
            TooltipHandler.TipRegion(rect, col.tipKey.Translate());

            if (editValue == before) return;

            editValue = Mathf.Clamp(editValue, col.min, col.max);
            col.apply(row, editValue);
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
