using System;
using System.Collections.Generic;
using System.Linq;
using ASQPicker;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 运行时生成贴图的形状参数窗口（设置 → 轨迹贴图...）。
    //
    // 左半：各项参数滑块（取值区间与 TrailTextureParams 的 Min/Max 常量同源，改动立即生效）；
    // 右半：预览——棋盘格底 + 按游戏里的拉伸方式画一条竖向轨迹（头在上，即弹体所在端），
    //       下方另有一张原尺寸贴图，便于判断虚化细节。
    // 参数改动会就地重写贴图像素（TrailTexture.Invalidate），已在飞的轨迹也立刻跟着变。
    public class TrailTextureWindow : Window
    {
        const float LeftWidth = 390f;
        const float Spacing = 10f;
        const float RowHeight = 30f;
        const float TitleHeight = 32f;
        const float ButtonHeight = 32f;

        const float LabelWidth = 150f;
        const float ValueWidth = 52f;

        const float CheckerSize = 14f;
        const float ColorSwatchWidth = 42f;
        const float ColorSwatchHeight = 24f;
        const float CustomButtonWidth = 84f;

        public override Vector2 InitialSize => new Vector2(760f, 600f);

        // 走属性而非缓存实例：读档会换掉 GunplaySettings 上的参数对象，缓存下来就会改到旧对象上。
        TrailTextureParams p => GunplaySettings.trailTextureParams;

        // 预览着色（窗口内临时状态，不持久化）：默认取 default 轨迹的颜色。
        Color previewColor;
        bool previewColorSet;

        public TrailTextureWindow()
        {
            forcePause = false;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            doCloseX = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, TitleHeight), "GunplayTrailTextureWindowTitle".Translate());
            Text.Font = GameFont.Small;

            float top = inRect.y + TitleHeight + Spacing;
            float height = Mathf.Max(0f, inRect.yMax - top);

            DrawParams(new Rect(inRect.x, top, LeftWidth, height));
            DrawPreview(new Rect(inRect.x + LeftWidth + Spacing, top, Mathf.Max(0f, inRect.width - LeftWidth - Spacing), height));
        }

        // ---------------- 左：参数 ----------------

        void DrawParams(Rect rect)
        {
            Rect rowRect = new Rect(rect.x, rect.y, rect.width, RowHeight);
            float bottom = rect.yMax - ButtonHeight - Spacing;

            // 纵向
            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(rowRect, "GunplayTrailTextureGroupLength".Translate());
            GUI.color = Color.white;
            rowRect.y += RowHeight;

            Slider(rowRect, "GunplayTrailTexturePeakAlpha".Translate(), ref p.peakAlpha,
                TrailTextureParams.PeakAlphaMin, TrailTextureParams.PeakAlphaMax);
            rowRect.y += RowHeight;
            Slider(rowRect, "GunplayTrailTextureTopAlpha".Translate(), ref p.topAlpha,
                TrailTextureParams.TopAlphaMin, TrailTextureParams.TopAlphaMax);
            rowRect.y += RowHeight;
            Slider(rowRect, "GunplayTrailTextureTopFade".Translate(), ref p.topFade,
                TrailTextureParams.TopFadeMin, TrailTextureParams.TopFadeMax);
            rowRect.y += RowHeight;
            Slider(rowRect, "GunplayTrailTextureTopFadeCurve".Translate(), ref p.topFadeCurve,
                TrailTextureParams.TopFadeCurveMin, TrailTextureParams.TopFadeCurveMax);
            rowRect.y += RowHeight;
            Slider(rowRect, "GunplayTrailTextureTailFalloff".Translate(), ref p.tailFalloff,
                TrailTextureParams.TailFalloffMin, TrailTextureParams.TailFalloffMax);
            rowRect.y += RowHeight + Spacing;

            // 横向
            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Widgets.Label(rowRect, "GunplayTrailTextureGroupWidth".Translate());
            GUI.color = Color.white;
            rowRect.y += RowHeight;

            Slider(rowRect, "GunplayTrailTextureFlatCore".Translate(), ref p.flatCore,
                TrailTextureParams.FlatCoreMin, TrailTextureParams.FlatCoreMax);
            rowRect.y += RowHeight;
            Slider(rowRect, "GunplayTrailTextureEdgeAlpha".Translate(), ref p.edgeAlpha,
                TrailTextureParams.EdgeAlphaMin, TrailTextureParams.EdgeAlphaMax);
            rowRect.y += RowHeight;
            Slider(rowRect, "GunplayTrailTextureEdgeFalloff".Translate(), ref p.edgeFalloff,
                TrailTextureParams.EdgeFalloffMin, TrailTextureParams.EdgeFalloffMax);

            // 预览颜色：给右侧预览着色用，不写进设置（滑块下方空间充裕，放这里不挤预览）
            rowRect.y += RowHeight + Spacing * 2f;
            DrawColorRow(new Rect(rect.x, rowRect.y, rect.width, ColorSwatchHeight + 22f));

            // 恢复默认
            Rect buttonRect = new Rect(rect.x, bottom, Mathf.Min(180f, rect.width), ButtonHeight);
            if (Widgets.ButtonText(buttonRect, "GunplayTrailTextureReset".Translate()))
            {
                p.ResetToDefaults();
                TrailTexture.Invalidate();
            }
        }

        // 一行：标签 + 滑块 + 数值。值变化时重写贴图，并顺手约束一次取值范围。
        void Slider(Rect rect, string label, ref float value, float min, float max)
        {
            float before = value;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(rect.x, rect.y, LabelWidth, rect.height), label);

            Rect sliderRect = new Rect(rect.x + LabelWidth, rect.y, rect.width - LabelWidth - ValueWidth - Spacing, rect.height);
            value = Widgets.HorizontalSlider(sliderRect, value, min, max);

            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(sliderRect.xMax + Spacing, rect.y, ValueWidth, rect.height), value.ToString("0.00"));
            Text.Anchor = TextAnchor.UpperLeft;

            if (value == before) return;

            value = Mathf.Clamp(value, min, max);
            TrailTexture.Invalidate();
        }

        // ---------------- 右：预览 ----------------

        void DrawPreview(Rect rect)
        {
            // 棋盘格底：纯色底上看不出 alpha 的强弱
            DrawCheckerboard(rect);
            Widgets.DrawBox(rect, 1);

            Texture2D tex = TrailTexture.Get();
            if (!previewColorSet)
            {
                previewColor = DefaultPreviewColor();
                previewColorSet = true;
            }

            // 竖向预算：上方两列标题 + 贴图本体 + 头尾图例（颜色选择在左侧面板）
            const float TitleRow = 24f;
            const float LegendRow = 22f;

            float top = rect.y + TitleRow;
            float availableHeight = Mathf.Max(0f, rect.yMax - LegendRow - top);
            float columnLeftX = rect.x + rect.width * 0.27f;
            float columnRightX = rect.x + rect.width * 0.73f;

            // 左：细节视图（1:1，放不下等比缩小），用于判断虚化的过渡细节
            float detailScale = Mathf.Min(1f, availableHeight / TrailTexture.Height);
            float detailW = TrailTexture.Width * detailScale;
            float detailH = TrailTexture.Height * detailScale;
            Rect detailRect = new Rect(columnLeftX - detailW * 0.5f, top, detailW, detailH);

            DrawTrailColumn(detailRect, tex, "GunplayTrailTexturePreviewActual".Translate()
                + "  " + TrailTexture.Width + "×" + TrailTexture.Height);

            // 右：实际形态——游戏里轨迹又细又长（宽 0.1~0.3 格、长可达十几格），
            // 这里按细长比例拉满可用高度，效果更接近肉眼所见
            float realW = Mathf.Clamp(rect.width * 0.08f, 16f, 26f);
            Rect realRect = new Rect(columnRightX - realW * 0.5f, top, realW, availableHeight);

            DrawTrailColumn(realRect, tex, "GunplayTrailTexturePreviewShape".Translate());

            // 头尾图例（两列共用）
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(rect.x, realRect.yMax + 1f, rect.width, LegendRow),
                "↑ " + "GunplayTrailTexturePreviewHead".Translate() + "      " + "GunplayTrailTexturePreviewTail".Translate() + " ↓");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // 一列轨迹：标题 + 贴图本体（头在上）
        void DrawTrailColumn(Rect trailRect, Texture2D tex, string title)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.85f, 0.85f, 0.85f);
            Widgets.Label(new Rect(trailRect.x - 60f, trailRect.y - 22f, trailRect.width + 120f, 20f), title);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            GUI.color = previewColor;
            GUI.DrawTexture(trailRect, tex);
            GUI.color = Color.white;
            Widgets.DrawBox(trailRect, 1);

            // 头（弹体端）标记
            //Rect headRect = new Rect(trailRect.center.x - HeadMarkerSize * 0.5f,
            //    trailRect.y - HeadMarkerSize - 3f, HeadMarkerSize, HeadMarkerSize);
            //GUI.color = previewColor;
            //GUI.DrawTexture(headRect, BaseContent.WhiteTex);
            //GUI.color = Color.white;
        }

        // 颜色预览行：每个轨迹 def 一枚当前生效颜色的色块（点击即用来着色预览）+ 自定义取色。
        void DrawColorRow(Rect rect)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(rect.x, rect.y, rect.width, 18f), "GunplayTrailTexturePreviewColor".Translate());
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            // 按颜色去重：default/medium/wide 这类同色 def 各占一格没意义，合并成一格
            // （悬浮提示里列出所有同色的 defName）。
            List<Color> colors = new List<Color>();
            List<string> tips = new List<string>();

            foreach (ProjectileTrailDef def in DefDatabase<ProjectileTrailDef>.AllDefs
                .OrderBy(d => d.defName, StringComparer.OrdinalIgnoreCase))
            {
                Color color = TrailColor.EffectiveForDef(def);

                int existing = colors.FindIndex(c => SameColor(c, color));
                if (existing >= 0) tips[existing] += " / " + def.defName;
                else
                {
                    colors.Add(color);
                    tips.Add(def.defName);
                }
            }

            float curX = rect.x;
            float y = rect.y + 20f;

            for (int i = 0; i < colors.Count; i++)
            {
                if (curX + ColorSwatchWidth > rect.xMax - CustomButtonWidth - Spacing) break;

                bool selected = SameColor(colors[i], previewColor);
                Rect swatchRect = new Rect(curX, y, ColorSwatchWidth, ColorSwatchHeight);

                GUI.color = colors[i];
                GUI.DrawTexture(swatchRect, BaseContent.WhiteTex);
                GUI.color = Color.white;
                Widgets.DrawBox(swatchRect, selected ? 2 : 1);
                TooltipHandler.TipRegion(swatchRect, tips[i]);

                Color picked = colors[i];
                if (Widgets.ButtonInvisible(swatchRect))
                {
                    previewColor = picked;
                    previewColorSet = true;
                }

                curX += ColorSwatchWidth + 4f;
            }

            Rect customRect = new Rect(rect.xMax - CustomButtonWidth, y, CustomButtonWidth, ColorSwatchHeight);
            if (Widgets.ButtonText(customRect, "GunplayTrailTexturePreviewCustom".Translate()))
            {
                Find.WindowStack.Add(new ColorPickerWindow(previewColor, c =>
                {
                    previewColor = c;
                    previewColorSet = true;
                }));
            }
            TooltipHandler.TipRegion(customRect, TrailColor.ToHtml(previewColor));
        }

        // 默认预览色：default 轨迹当前生效的颜色（通常是白）
        static Color DefaultPreviewColor()
        {
            return TrailColor.EffectiveForDef(DefDatabase<ProjectileTrailDef>.GetNamed("default", false));
        }

        // 色块选中态判定。取色器不编辑 alpha（保存时保留原值），这里也只比 RGB。
        static bool SameColor(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.001f && Mathf.Abs(a.g - b.g) < 0.001f && Mathf.Abs(a.b - b.b) < 0.001f;
        }

        static void DrawCheckerboard(Rect rect)
        {
            Color a = new Color(0.13f, 0.13f, 0.14f);
            Color b = new Color(0.18f, 0.18f, 0.19f);

            int cols = Mathf.CeilToInt(rect.width / CheckerSize);
            int rows = Mathf.CeilToInt(rect.height / CheckerSize);

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    float cx = rect.x + x * CheckerSize;
                    float cy = rect.y + y * CheckerSize;
                    float w = Mathf.Min(CheckerSize, rect.xMax - cx);
                    float h = Mathf.Min(CheckerSize, rect.yMax - cy);
                    if (w <= 0f || h <= 0f) continue;

                    GUI.color = (x + y) % 2 == 0 ? a : b;
                    GUI.DrawTexture(new Rect(cx, cy, w, h), BaseContent.WhiteTex);
                }
            }

            GUI.color = Color.white;
        }
    }
}
