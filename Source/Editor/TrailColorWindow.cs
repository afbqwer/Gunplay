using System;
using System.Collections.Generic;
using System.Linq;
using ASQPicker;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹 def 颜色编辑窗口（设置 → 轨迹颜色...）。
    //
    // 每个 ProjectileTrailDef 一行：色块（当前有效颜色）+ defName/所属 Mod + 恢复按钮。
    //   * 点击色块 → ColorPickerWindow 取色，保存进 GunplaySettings.trailColors（"#RRGGBBAA"）；
    //   * 「×」删除该条目 = 回到 XML 的 trailColor；
    //   * 显示的颜色统一走 TrailColor.EffectiveForDef，与运行时同一口径。
    // 颜色改动立即生效，但只对之后新发射的投射物生效（在飞轨迹的颜色在登记时已确定）。
    public class TrailColorWindow : Window
    {
        const float RowHeight = 40f;
        const float SwatchWidth = 64f;
        const float SwatchHeight = 26f;
        const float ResetButtonWidth = 30f;
        const float Spacing = 8f;
        const float HeaderHeight = 34f;

        public override Vector2 InitialSize => new Vector2(560f, 460f);

        class Row
        {
            public ProjectileTrailDef def = null!;
            public string label = null!;
            public string modName = null!;
        }

        readonly List<Row> rows = new List<Row>();

        public TrailColorWindow()
        {
            forcePause = false;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            doCloseX = true;

            foreach (ProjectileTrailDef def in DefDatabase<ProjectileTrailDef>.AllDefs
                .OrderBy(d => d.defName, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(new Row
                {
                    def = def,
                    label = def.LabelCap.Resolve(),
                    modName = def.modContentPack?.Name ?? "??"
                });
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, HeaderHeight), "GunplayTrailColorsWindowTitle".Translate());
            Text.Font = GameFont.Small;

            float curY = inRect.y + HeaderHeight + Spacing;

            for (int i = 0; i < rows.Count; i++)
            {
                DrawRow(rows[i], new Rect(inRect.x, curY, inRect.width, RowHeight));
                curY += RowHeight + Spacing;
            }
        }

        void DrawRow(Row row, Rect rect)
        {
            Widgets.DrawHighlightIfMouseover(rect);

            bool customized = GunplaySettings.trailColors.ContainsKey(row.def.defName);
            Color color = TrailColor.EffectiveForDef(row.def);

            // 色块
            Rect swatchRect = new Rect(rect.x + 4f, rect.y + (RowHeight - SwatchHeight) * 0.5f, SwatchWidth, SwatchHeight);
            DrawSwatch(swatchRect, color);
            TooltipHandler.TipRegion(swatchRect, TrailColor.ToHtml(color));

            if (Widgets.ButtonInvisible(swatchRect))
            {
                Find.WindowStack.Add(new ColorPickerWindow(color, c =>
                {
                    GunplaySettings.trailColors[row.def.defName] = c;
                }));
            }

            float textX = swatchRect.xMax + Spacing;
            float textW = rect.xMax - textX - ResetButtonWidth - Spacing;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(textX, rect.y + 1f, textW, 20f), row.label);

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.55f, 0.55f, 0.55f);
            Widgets.Label(new Rect(textX, rect.y + 21f, textW, 18f),
                row.def.defName + " — " + row.modName);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            // 恢复按钮：仅存在覆写时显示
            if (!customized) return;

            Rect resetRect = new Rect(rect.xMax - ResetButtonWidth - 4f, rect.y + (RowHeight - 24f) * 0.5f, ResetButtonWidth, 24f);
            if (Widgets.ButtonText(resetRect, "×"))
            {
                GunplaySettings.trailColors.Remove(row.def.defName);
            }
            TooltipHandler.TipRegion(resetRect, "GunplayTrailColorResetTip".Translate());
        }

        // 色块本体 + 边框。GUI.DrawTexture 走 BaseContent.WhiteTex，把 GUI.color 当纯色填充用。
        static void DrawSwatch(Rect rect, Color color)
        {
            GUI.color = color;
            GUI.DrawTexture(rect, BaseContent.WhiteTex);
            GUI.color = Color.white;
            Widgets.DrawBox(rect, 1);
        }
    }
}
