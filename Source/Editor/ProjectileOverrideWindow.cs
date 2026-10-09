using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        const float WidthColumnWidth = 110f;
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
            public float editWidth;         // 输入框绑定的当前值
            public string? widthBuffer;     // 输入框字符串缓冲；null 表示尚未输入过
        }

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
                    editWidth = 0f,
                    widthBuffer = null
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
        }

        static string Format(float value)
        {
            return value.ToString("0.###");
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
            Columns(rect.width, out float overrideX, out float trailX, out float projX, out float weaponX, out float widthX, out float projW, out float weaponW);

            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(0.75f, 0.75f, 0.75f);

            Widgets.Label(new Rect(overrideX, rect.y, OverrideToggleWidth, rect.height), "GunplayOverrideColEnabled".Translate());
            Widgets.Label(new Rect(trailX, rect.y, TrailToggleWidth, rect.height), "GunplayOverrideColTrail".Translate());
            Widgets.Label(new Rect(projX, rect.y, projW, rect.height), "GunplayOverrideColProjectile".Translate());
            Widgets.Label(new Rect(weaponX, rect.y, weaponW, rect.height), "GunplayOverrideColWeapon".Translate());
            Widgets.Label(new Rect(widthX, rect.y, WidthColumnWidth, rect.height), "GunplayOverrideColWidth".Translate());

            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        static void Columns(float width, out float overrideX, out float trailX, out float projX, out float weaponX, out float widthX, out float projW, out float weaponW)
        {
            float inner = Mathf.Max(0f, width - ScrollbarWidth);
            float rest = Mathf.Max(0f, inner - OverrideToggleWidth - TrailToggleWidth - WidthColumnWidth);

            projW = rest * 0.5f;
            weaponW = rest - projW;

            overrideX = 0f;
            trailX = overrideX + OverrideToggleWidth;
            projX = trailX + TrailToggleWidth;
            weaponX = projX + projW;
            widthX = weaponX + weaponW;
        }

        void DrawRows(Rect outRect)
        {
            if (visibleDirty) RebuildVisible();

            if (visible.Count == 0)
            {
                Widgets.Label(outRect, "GunplayOverrideNoResults".Translate());
                return;
            }

            Columns(outRect.width, out float overrideX, out float trailX, out float projX, out float weaponX, out float widthX, out float projW, out float weaponW);

            float viewWidth = Mathf.Max(0f, outRect.width - ScrollbarWidth);
            Rect viewRect = new Rect(0f, 0f, viewWidth, visible.Count * RowHeight);

            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            int first = Mathf.Clamp(Mathf.FloorToInt(scrollPosition.y / RowHeight), 0, visible.Count - 1);
            int last = Mathf.Clamp(Mathf.CeilToInt((scrollPosition.y + outRect.height) / RowHeight), 0, visible.Count - 1);

            for (int i = first; i <= last; i++)
            {
                DrawRow(visible[i], i, viewWidth, overrideX, trailX, projX, weaponX, widthX, projW, weaponW);
            }

            Widgets.EndScrollView();
        }

        void DrawRow(Row row, int index, float viewWidth,
            float overrideX, float trailX, float projX, float weaponX, float widthX, float projW, float weaponW)
        {
            float y = index * RowHeight;
            Widgets.DrawHighlightIfMouseover(new Rect(0f, y, viewWidth, RowHeight));

            ProjectileOverride? over = ProjectileOverrides.Get(row.projectile);
            bool overridden = over != null;
            bool trailOn = TrailShown(row);   // 未覆写时是默认情况，不再是恒为勾选

            DrawToggle(row, overrideX, y, overridden);
            DrawTrailToggle(row, trailX, y, trailOn);
            DrawNameCell(row.projectile, row.projectileLabel, row.projectile.defName, projX, y, projW, null);
            DrawWeaponCell(row, weaponX, y, weaponW);
            DrawWidthCell(row, widthX, y, overridden, trailOn);
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

            RecountOverridden();

            // 过滤生效时该行可能已不符合条件，需要重建列表；此时保留滚动位置，避免跳回顶部
            if (onlyOverridden || trailFilter != TrailFilter.Any) MarkVisibleDirty(true);
        }

        void DrawTrailToggle(Row row, float x, float y, bool trailOn)
        {
            bool state = trailOn;
            bool before = state;

            Rect rect = new Rect(x, y, TrailToggleWidth, RowHeight);
            Widgets.Checkbox(new Vector2(x + (TrailToggleWidth - 24f) * 0.5f, y + (RowHeight - 24f) * 0.5f), ref state, 24f);
            TooltipHandler.TipRegion(rect, "GunplayOverrideTipTrail".Translate());

            if (state == before) return;

            // 直接点轨迹开关时，若该行还没开启覆写，顺带开启（省掉先开覆写这一步）
            if (!ProjectileOverrides.Has(row.projectile))
            {
                ProjectileOverrides.Enable(row.projectile);
                SyncEditValue(row);
            }

            ProjectileOverrides.SetTrailEnabled(row.projectile, state);
            RecountOverridden();

            // 同上：轨迹过滤生效时该行可能已不符合条件
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

        void DrawWidthCell(Row row, float x, float y, bool overridden, bool trailOn)
        {
            Rect rect = new Rect(x + 4f, y + 5f, Mathf.Max(0f, WidthColumnWidth - 8f), RowHeight - 10f);

            // 该投射物的轨迹被关掉时，宽度没有意义：不显示输入框
            if (overridden && !trailOn)
            {
                GUI.color = new Color(0.55f, 0.55f, 0.55f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(rect, "—");
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                return;
            }

            if (!overridden)
            {
                // 未覆写：显示当前实际生效的宽度（随全局模式/系数实时变化），只读文本
                GUI.color = new Color(0.55f, 0.55f, 0.55f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(rect, Format(TrailWidth.EffectiveForDef(row.projectile)));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                return;
            }

            float before = row.editWidth;
            Widgets.TextFieldNumeric(rect, ref row.editWidth, ref row.widthBuffer, MinWidth, MaxWidth);

            if (row.editWidth == before) return;

            row.editWidth = Mathf.Clamp(row.editWidth, MinWidth, MaxWidth);
            ProjectileOverrides.SetTrailWidth(row.projectile, row.editWidth);
        }

        // 本窗口是独立窗口，关闭时不会触发主设置窗口的写盘，这里自己落一次。
        public override void PostClose()
        {
            base.PostClose();
            GunplaySettings.Save();
        }
    }
}
