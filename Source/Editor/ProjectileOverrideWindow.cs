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
    // 结构：上方过滤区（搜索框 + mod 下拉 + 仅显示已覆写 + 计数 + 清除全部），下方列表区（表头 + 虚拟滚动）。
    // 一行 = 一个投射物 ThingDef；武器列只是上下文（轨迹本身只由投射物 def 决定，见 PatchProjectileLaunch），
    // 多个武器共用同一投射物时显示第一个并在 defName 后加「+N」，悬浮列出全部。
    //
    // 每行若干开关 / 选择（列序：覆写 | 轨迹 | 弹速 | 材质 | 颜色 | 投射物 | 武器 | 宽度 | 长度 | 淡出；
    // CE 在场时「弹速」整列不参与布局，见 SpeedColumnShown）：
    //   「覆写」—— 该行是否有覆写记录。关掉即丢弃记录（轨迹开关、材质与宽度一起回到继承/计算值）。
    //   「轨迹」—— 是否绘制该投射物的轨迹。未覆写时显示该投射物的「默认」值
    //              （见 ProjectileRules.TrailEnabled：可能因本体无贴图、弧线开关关闭而不勾选）。
    //              直接点它会顺带把覆写打开，省掉「先开覆写再关轨迹」两步。
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
    // 开关 / 材质 / 颜色三类格子都支持拖拽批量修改（drag-paint）：在某一行按下左键后上下拖动，
    // 鼠标扫过的行会被涂成同一个值——开关取「按下那一行切换后的状态」（与本体复选框的涂抹一致，
    // 见 Widgets.ToggleInvisibleDraggable 的 paintable 分支），材质与颜色取「按下那一行当时的值」。
    // 材质 / 颜色的单击仍是打开选择窗口，两者不冲突：拖拽结束时鼠标不在原来那个按钮上，
    // 按钮不会触发点击。实现见 DragPaint。
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
        const float SpeedColumnWidth = 60f;
        const float ColorColumnWidth = 70f;
        const float SwatchWidth = 50f;
        const float SwatchHeight = 24f;
        const float ResetButtonWidth = 24f;
        const float MaterialColumnWidth = 150f;
        const float WidthColumnWidth = 80f;
        const float LengthColumnWidth = 80f;
        const float FadeColumnWidth = 80f;
        const float ModButtonWidth = 220f;
        const float TrailFilterWidth = 190f;
        const float OnlyOverriddenWidth = 180f;
        const float CountWidth = 200f;
        const float ClearButtonWidth = 150f;
        const float ScrollbarWidth = 16f;

        // 弹速列是否显示。CE 的 ProjectileCE 自成一套弹道物理、不继承原版 Projectile，
        // 全局弹速缩放对它不生效，因此 CE 在场时这一列没有意义。
        // 判据用 Detected（只看 CE 在不在）而不是 Enabled（还要看兼容补丁挂没挂上）——
        // 不兼容的原因是「CE 在场」本身，见 PatchCECompat.Detected。
        static bool SpeedColumnShown => !PatchCECompat.Detected;

        const float MinWidth = 0f;
        const float MaxWidth = 2f;

        const float ToggleSize = 24f;

        // 拖拽涂抹的格子种类（见 DragPaint.Sweep 的 tag）。材质列与颜色列相邻，
        // 横向拖出去时要靠它把「涂材质」挡在材质列内，否则颜色格会拿到一个 ProjectileTrailDef 转不成 Color。
        const int MaterialPaintTag = 1;
        const int ColorPaintTag = 2;

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
            row => TrailLength.Effective(row.projectile, row.trailDef),
            (row, v) => ProjectileOverrides.SetTrailLength(row.projectile, v));

        static readonly NumberColumn FadeColumn = new NumberColumn(
            FadeColumnWidth, TrailFade.Min, TrailFade.Max, FormatInt, "GunplayOverrideTipFade",
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
        Vector2 scrollPosition;

        bool visibleDirty = true;
        bool visibleKeepScroll;   // 重建列表时是否保留滚动位置（只有开关引发的重建才保留）
        int overriddenInFilter;

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
            RefreshTrailDef(row);

            row.editWidth = TrailWidth.EffectiveForDef(row.projectile);
            row.widthBuffer = Format(row.editWidth);
            row.editLength = TrailLength.Effective(row.projectile, row.trailDef);
            row.lengthBuffer = FormatInt(row.editLength);
            row.editFade = TrailFade.Effective(row.projectile, row.trailDef);
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

                    // 记录一次性全没了，每行缓存的材质都得重取（同 RefreshTrailDef 的理由：
                    // 材质的解析结果依赖覆写记录，而记录是缓存在行上的）
                    foreach (Row row in rows) RefreshTrailDef(row);

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
            if (SpeedColumnShown)
                Widgets.Label(new Rect(layout.speedX, rect.y, SpeedColumnWidth, rect.height), "GunplayOverrideColSpeed".Translate());
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

            internal ColumnLayout(float overrideX, float trailX, float speedX, float materialX, float colorX, float projX, float weaponX,
                float widthX, float lengthX, float fadeX, float projW, float weaponW)
            {
                this.overrideX = overrideX;
                this.trailX = trailX;
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
            // 列序：左边三个行为开关（覆写 / 轨迹 / 弹速）→ 轨迹外观（材质 → 颜色，颜色比材质具体，
            // 单项覆写的颜色会盖住材质色）→ 投射物 / 武器两列上下文 → 轨迹的三个数值。
            //
            // 弹速列隐藏时不是「占着位置留空」，而是整列宽度归零：省下的 60px 直接还给
            // 投射物 / 武器两个弹性列，而不是留一条空白。
            internal static ColumnLayout Compute(float width)
            {
                float speedWidth = SpeedColumnShown ? SpeedColumnWidth : 0f;

                float inner = Mathf.Max(0f, width - ScrollbarWidth);
                float rest = Mathf.Max(0f, inner - OverrideToggleWidth - TrailToggleWidth - speedWidth - ColorColumnWidth
                    - MaterialColumnWidth - WidthColumnWidth - LengthColumnWidth - FadeColumnWidth);

                float projW = rest * 0.5f;
                float weaponW = rest - projW;

                float overrideX = 0f;
                float trailX = overrideX + OverrideToggleWidth;
                float speedX = trailX + TrailToggleWidth;
                float materialX = speedX + speedWidth;
                float colorX = materialX + MaterialColumnWidth;
                float projX = colorX + ColorColumnWidth;
                float weaponX = projX + projW;
                float widthX = weaponX + weaponW;
                float lengthX = widthX + WidthColumnWidth;

                return new ColumnLayout(overrideX, trailX, speedX, materialX, colorX, projX, weaponX,
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

            ProjectileOverride? over = ProjectileOverrides.Get(row.projectile);
            bool overridden = over != null;
            bool trailOn = TrailShown(row);   // 未覆写时是默认情况，不再是恒为勾选

            DrawToggle(row, layout.overrideX, y, overridden);
            DrawTrailToggle(row, layout.trailX, y, trailOn);

            // CE 下这一列不参与布局（见 SpeedColumnShown），连取值都省掉
            if (SpeedColumnShown)
            {
                // 显示该投射物自己的决定（不含全局总开关，理由见 ProjectileRules.SpeedAffected）
                DrawSpeedToggle(row, layout.speedX, y, ProjectileRules.SpeedAffected(row.projectile),
                    over != null && over.hasSpeedOverride);
            }

            DrawMaterialCell(row, layout.materialX, y, overridden, trailOn);
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
            DrawToggleBox(x + (OverrideToggleWidth - ToggleSize) * 0.5f, y, state);
            Widgets.ToggleInvisibleDraggable(rect, ref state, MouseoverSoundAllowed, true);
            TooltipHandler.TipRegion(rect, "GunplayOverrideTipEnabled".Translate());

            if (state == before) return;

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

        // 鼠标悬停音。按住左键期间一律关掉：涂抹一次要扫过几十行，逐行"嘀"一声很难听。
        // 判断用「左键是否按着」而不是 DragPaint.Active —— 后者要等位移越过阈值才算数，
        // 而第一行的音早在按下那一瞬就发出去了，那时再静音已经晚了。
        static bool MouseoverSoundAllowed => !Input.GetMouseButton(0);

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
            Widgets.ToggleInvisibleDraggable(rect, ref state, MouseoverSoundAllowed, true);
            TooltipHandler.TipRegion(rect, "GunplayOverrideTipTrail".Translate());

            if (state == before) return;

            EnsureOverridden(row);
            ProjectileOverrides.SetTrailEnabled(row.projectile, state);
            AfterOverrideChanged();
        }

        // 弹速列：该投射物是否受全局弹速缩放（GunplaySettings.projectileSpeed）。
        // 与轨迹列同约定（未覆写时点一下会顺带开启覆写），但区别有两点：
        //   1) 它不随轨迹开关变灰——弹速与画不画轨迹无关；
        //   2) 它多一个「×」回到继承：这一项的三态（未设定 / 受 / 不受）里，
        //      「未设定」是有意义的（继承弧线规则与配置包的 preserveSpeed），
        //      而关掉整行覆写是唯一的其它退路，代价太大（同一行的宽度/长度/淡出/颜色一起丢）。
        void DrawSpeedToggle(Row row, float x, float y, bool speedOn, bool picked)
        {
            bool state = speedOn;

            // 勾选框的悬浮区与「×」的悬浮区不能重叠：两个 TipRegion 叠在一处时只有其中一个会显示。
            // 单独指定过时勾选框的悬浮区让出右侧那一段给「×」。
            Rect boxRect = new Rect(x, y, SpeedColumnWidth - (picked ? ResetButtonWidth : 0f), RowHeight);
            DrawToggleBox(x + 6f, y, state);
            Widgets.ToggleInvisibleDraggable(boxRect, ref state, MouseoverSoundAllowed, true);
            TooltipHandler.TipRegion(boxRect, "GunplayOverrideTipSpeed".Translate());

            if (state != speedOn)
            {
                EnsureOverridden(row);
                ProjectileOverrides.SetSpeedAffected(row.projectile, state);
                AfterOverrideChanged();
                return;
            }

            // 没单独设过就没什么可清除的，右侧留空（勾选框固定靠左，避免出现/消失时整列跳动）
            if (!picked) return;

            Rect resetRect = new Rect(x + SpeedColumnWidth - ResetButtonWidth, y + (RowHeight - 24f) * 0.5f,
                ResetButtonWidth, 24f);
            if (Widgets.ButtonText(resetRect, "×"))
            {
                ProjectileOverrides.ClearSpeedOverride(row.projectile);
                AfterOverrideChanged();
            }
            TooltipHandler.TipRegion(resetRect, "GunplayOverrideSpeedResetTip".Translate());
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

            if (Widgets.ButtonInvisible(swatchRect, MouseoverSoundAllowed))
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

            // 拖拽涂抹：把起始格的「当前生效颜色」涂到扫过的格子上。
            // 起始格若本来就是继承色（hasColor=false），涂出去的是那个继承色的具体值 ——
            // 也就是被涂的行会多出一条自定义色记录。这是有意的：用户拖的是「这个颜色」，
            // 不是「跟着那个材质走」，否则目标行的材质与起始行不同时涂出来的色就不一样了。
            if (DragPaint.Sweep(cellRect, ColorPaintTag, color))
            {
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

            // 判「是否单独指定了材质」直接看记录里的 defName，而不是回查 DefDatabase：
            // 每帧对每行都要用一次，这里只是显示与提示，一个字典查找就够；
            // 材质 def 被移除时 defName 仍在，提示仍旧说"已单独指定"，与 SetTrailMaterial 的意图一致。
            bool picked = ProjectileOverrides.Get(row.projectile)?.trailMaterialDefName != null;

            string label = row.trailDef != null ? row.trailDef.defName : "—";
            Rect buttonRect = new Rect(x + 4f, y + 5f, Mathf.Max(0f, MaterialColumnWidth - 8f), RowHeight - 10f);

            if (Widgets.ButtonText(buttonRect, label, true, MouseoverSoundAllowed))
            {
                TrailMaterialPicker.Open(row.trailDef, def =>
                {
                    ProjectileOverrides.SetTrailMaterial(row.projectile, def);

                    // 材质换了，本行后面几列的继承口径（颜色 / 宽度 / 长度 / 淡出）跟着换，
                    // 缓存与输入框缓冲区都要重取一次，否则显示的还是旧材质的值
                    SyncEditValue(row);
                    AfterOverrideChanged();
                });
            }

            TooltipHandler.TipRegion(buttonRect, picked
                ? "GunplayOverrideTipMaterialPicked".Translate(label)
                : "GunplayOverrideTipMaterialInherited".Translate(label));

            // 拖拽涂抹：把起始格的「当前生效材质」涂到扫过的格子上（row.trailDef 可能为 null，
            // 那表示起始格用的是继承材质，涂出去就是让目标行也回到继承 —— 与单击「清除」同一个结果）。
            // 单击仍由上面的 ButtonText 负责：拖拽结束时鼠标不在原来那个按钮上，Unity 的
            // GUI.Button 不会触发；反过来，只是点一下时 DragPaint 也判不出拖动，两边互不干扰。
            if (DragPaint.Sweep(cellRect, MaterialPaintTag, row.trailDef))
            {
                ProjectileOverrides.SetTrailMaterial(row.projectile, DragPaint.Value as ProjectileTrailDef);

                // 与单击换材质同一条收尾：本行后面几列的继承口径跟着换，缓存与输入框缓冲区都要重取
                SyncEditValue(row);
                AfterOverrideChanged();
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
