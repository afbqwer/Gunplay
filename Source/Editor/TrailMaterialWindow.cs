using System;
using System.Linq;
using ASQPicker;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹材质编辑窗口（设置 → 轨迹材质...）。
    //
    // 与旧「轨迹贴图」窗口的区别：旧版编辑的是**一份全局参数**；现在编辑的是**某个材质的参数**
    // （材质类型 = ProjectileTrailDef，见 TrailMaterialParams 的类注释），因此：
    //   * 顶栏选择要编辑哪个材质；选中的材质在设置里没有快照时，编辑的第一笔会自动从 def 模板克隆一份
    //     （TrailMaterial.EnsureEdit）——光看不改不会让该 def 脱离 XML 默认值；
    //   * 「重置为 Def 模板」丢弃快照，回到 XML 里那份模板；
    //   * 左侧按组列出全部材质参数（形状 / 宽度轮廓 / 颜色 / 几何），右侧预览用的是**运行时同一张贴图**
    //     （TrailMaterial.TextureFor），所以预览与游戏内所见一致，改一个滑块两边同时变。
    public class TrailMaterialWindow : Window
    {
        const float LeftWidth = 440f;
        const float Spacing = 10f;
        const float RowHeight = 28f;
        const float TitleHeight = 32f;
        const float TopBarHeight = 30f;
        const float GroupHeaderHeight = 22f;
        const float GroupGap = 12f;
        const float ScrollbarWidth = 16f;

        const float LabelWidth = 178f;
        const float ValueWidth = 62f;

        const float CheckerSize = 14f;

        // 宽度未设定时，勾选「覆盖」用的起始值。宽度是逐投射物算出来的（伤害/贴图口径），
        // 材质窗口里拿不到唯一答案，所以取一个与内置 def 同量级的常见值作起点，之后随便调。
        const float WidthOverrideStart = 0.15f;

        public override Vector2 InitialSize => new Vector2(920f, 680f);

        // 左侧内容总高（滚动用）。行高固定，按行数算出来而不是靠 CurHeight 之类的运行时量取——
        // 这里没有分栏问题，直接给准数最省事，也避免"内容超了却不出现滚动条"。
        static readonly float ParamsContentHeight =
            GroupHeaderHeight + RowHeight * 2 + GroupGap       // 贴图来源
            + GroupHeaderHeight + RowHeight * 5 + GroupGap     // 纵向
            + GroupHeaderHeight + RowHeight * 3 + GroupGap     // 横向
            + GroupHeaderHeight + RowHeight * 3 + GroupGap     // 宽度轮廓
            + GroupHeaderHeight + RowHeight * 4 + GroupGap;    // 颜色与几何

        ProjectileTrailDef? current;
        Vector2 scroll;

        // 走属性而非缓存实例：读档会换掉设置里的快照对象，缓存下来就会改到旧对象上。
        TrailMaterialParams? Params => current != null ? TrailMaterial.Effective(current) : null;

        public TrailMaterialWindow()
        {
            forcePause = false;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            doCloseX = true;

            // 默认编辑 default 材质（内置必然存在）；万一被删就从列表里取第一个
            current = DefDatabase<ProjectileTrailDef>.GetNamed("default", false)
                ?? DefDatabase<ProjectileTrailDef>.AllDefs
                    .OrderBy(d => d.defName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, TitleHeight), "GunplayTrailMaterialWindowTitle".Translate());
            Text.Font = GameFont.Small;

            float top = inRect.y + TitleHeight + Spacing;

            Rect topBar = new Rect(inRect.x, top, inRect.width, TopBarHeight);
            DrawTopBar(topBar);

            top = topBar.yMax + Spacing;
            float height = Mathf.Max(0f, inRect.yMax - top);

            DrawParams(new Rect(inRect.x, top, Mathf.Min(LeftWidth, inRect.width), height));
            DrawPreview(new Rect(inRect.x + LeftWidth + Spacing, top,
                Mathf.Max(0f, inRect.width - LeftWidth - Spacing), height));
        }

        // ---------------- 顶栏：选材质 / 状态 / 重置 ----------------

        void DrawTopBar(Rect rect)
        {
            const float PickWidth = 320f;
            const float StatusWidth = 200f;
            const float ResetWidth = 240f;

            Rect pickRect = new Rect(rect.x, rect.y, Mathf.Min(PickWidth, rect.width), rect.height);
            string label = current != null
                ? "GunplayTrailMaterialCurrent".Translate(current.defName)
                : "GunplayTrailMaterialNone".Translate();

            if (Widgets.ButtonText(pickRect, label))
            {
                TrailMaterialPicker.Open(current, def =>
                {
                    // 「清除」按钮（def = null）在这里解释成"不换"，材质总归要有一个
                    if (def != null) current = def;
                });
            }
            TooltipHandler.TipRegion(pickRect, "GunplayTrailMaterialPickTip".Translate());

            if (current == null) return;

            bool edited = TrailMaterial.HasEdit(current);

            Rect statusRect = new Rect(pickRect.xMax + Spacing, rect.y, StatusWidth, rect.height);
            GUI.color = edited ? new Color(1f, 0.85f, 0.5f) : new Color(0.6f, 0.6f, 0.6f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(statusRect, edited
                ? "GunplayTrailMaterialEdited".Translate()
                : "GunplayTrailMaterialFromDef".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            if (!edited) return;

            Rect resetRect = new Rect(rect.xMax - ResetWidth, rect.y, ResetWidth, rect.height);
            if (Widgets.ButtonText(resetRect, "GunplayTrailMaterialReset".Translate()))
            {
                TrailMaterial.ResetEdit(current);
            }
            TooltipHandler.TipRegion(resetRect, "GunplayTrailMaterialResetTip".Translate());
        }

        // ---------------- 左：参数 ----------------

        void DrawParams(Rect rect)
        {
            TrailMaterialParams? material = Params;
            if (current == null || material == null)
            {
                Widgets.Label(rect, "GunplayTrailMaterialNone".Translate());
                return;
            }

            float contentWidth = Mathf.Max(0f, rect.width - ScrollbarWidth);
            Rect viewRect = new Rect(0f, 0f, contentWidth, ParamsContentHeight);

            Widgets.BeginScrollView(rect, ref scroll, viewRect);

            float y = 0f;

            // ---- 贴图来源 ----
            y = GroupHeader(y, contentWidth, "GunplayTrailMaterialGroupSource");
            bool generated = material.useGeneratedTexture;
            if (Widgets.RadioButtonLabeled(new Rect(0f, y, contentWidth, RowHeight),
                "GunplayTrailMaterialSourceGenerated".Translate(), generated)) SetSource(true);
            y += RowHeight;
            if (Widgets.RadioButtonLabeled(new Rect(0f, y, contentWidth, RowHeight),
                "GunplayTrailMaterialSourceFile".Translate(), !generated)) SetSource(false);
            TooltipHandler.TipRegion(new Rect(0f, y - RowHeight * 2f, contentWidth, RowHeight * 2f),
                "GunplayTrailMaterialSourceTip".Translate());
            y += RowHeight + GroupGap;

            // 文件贴图档下形状与宽度轮廓都不生效（贴图是别人画好的，没法按参数重写像素），
            // 整组灰掉而不是让滑块默默无效
            bool savedEnabled = GUI.enabled;
            GUI.enabled = generated;

            // ---- 纵向 ----
            y = GroupHeader(y, contentWidth, "GunplayTrailTextureGroupLength");
            y = SliderRow(y, contentWidth, "GunplayTrailTexturePeakAlpha", m => m.peakAlpha, (m, v) => m.peakAlpha = v,
                TrailMaterialParams.PeakAlphaMin, TrailMaterialParams.PeakAlphaMax, null);
            y = SliderRow(y, contentWidth, "GunplayTrailTextureTopAlpha", m => m.topAlpha, (m, v) => m.topAlpha = v,
                TrailMaterialParams.TopAlphaMin, TrailMaterialParams.TopAlphaMax, null);
            y = SliderRow(y, contentWidth, "GunplayTrailTextureTopFade", m => m.topFade, (m, v) => m.topFade = v,
                TrailMaterialParams.TopFadeMin, TrailMaterialParams.TopFadeMax, null);
            y = SliderRow(y, contentWidth, "GunplayTrailTextureTopFadeCurve", m => m.topFadeCurve, (m, v) => m.topFadeCurve = v,
                TrailMaterialParams.TopFadeCurveMin, TrailMaterialParams.TopFadeCurveMax, null);
            y = SliderRow(y, contentWidth, "GunplayTrailTextureTailFalloff", m => m.tailFalloff, (m, v) => m.tailFalloff = v,
                TrailMaterialParams.TailFalloffMin, TrailMaterialParams.TailFalloffMax, null);
            y += GroupGap;

            // ---- 横向 ----
            y = GroupHeader(y, contentWidth, "GunplayTrailTextureGroupWidth");
            y = SliderRow(y, contentWidth, "GunplayTrailTextureFlatCore", m => m.flatCore, (m, v) => m.flatCore = v,
                TrailMaterialParams.FlatCoreMin, TrailMaterialParams.FlatCoreMax, null);
            y = SliderRow(y, contentWidth, "GunplayTrailTextureEdgeAlpha", m => m.edgeAlpha, (m, v) => m.edgeAlpha = v,
                TrailMaterialParams.EdgeAlphaMin, TrailMaterialParams.EdgeAlphaMax, null);
            y = SliderRow(y, contentWidth, "GunplayTrailTextureEdgeFalloff", m => m.edgeFalloff, (m, v) => m.edgeFalloff = v,
                TrailMaterialParams.EdgeFalloffMin, TrailMaterialParams.EdgeFalloffMax, null);
            y += GroupGap;

            // ---- 宽度轮廓 ----
            y = GroupHeader(y, contentWidth, "GunplayTrailMaterialGroupProfile");
            y = SliderRow(y, contentWidth, "GunplayTrailMaterialHeadWidth", m => m.headWidth, (m, v) => m.headWidth = v,
                TrailMaterialParams.WidthProfileMin, TrailMaterialParams.WidthProfileMax, "GunplayTrailMaterialProfileTip");
            y = SliderRow(y, contentWidth, "GunplayTrailMaterialTailWidth", m => m.tailWidth, (m, v) => m.tailWidth = v,
                TrailMaterialParams.WidthProfileMin, TrailMaterialParams.WidthProfileMax, "GunplayTrailMaterialProfileTip");
            y = SliderRow(y, contentWidth, "GunplayTrailMaterialProfileCurve", m => m.widthProfileCurve, (m, v) => m.widthProfileCurve = v,
                TrailMaterialParams.WidthProfileCurveMin, TrailMaterialParams.WidthProfileCurveMax, "GunplayTrailMaterialProfileCurveTip");
            y += GroupGap;

            GUI.enabled = savedEnabled;

            // ---- 颜色与几何 ----
            y = GroupHeader(y, contentWidth, "GunplayTrailMaterialGroupGeometry");
            y = ColorRow(y, contentWidth);

            // 宽度：材质值一旦设定就是绝对值（0 世界单位是合法值，用勾选框而不是 -1 哨兵来表达"未设定"）
            y = OverrideRow(y, contentWidth, "GunplayTrailMaterialWidth",
                m => m.trailWidth, (m, v) => m.trailWidth = v,
                WidthOverrideStart, 0f, 2f, false, "GunplayTrailMaterialWidthTip");

            // 长度与淡出的哨兵在数据类里是 -1（与宽度同口径），这里同样用勾选框表达"未设定"
            y = OverrideRow(y, contentWidth, "GunplayTrailMaterialLength",
                m => m.trailLengthFactor, (m, v) => m.trailLengthFactor = v,
                Mathf.Clamp(GunplaySettings.trailLengthFactor, TrailLength.Min, TrailLength.Max),
                TrailLength.Min, TrailLength.Max, true, "GunplayTrailMaterialLengthTip");

            OverrideRow(y, contentWidth, "GunplayTrailMaterialFade",
                m => m.trailFadeTicks, (m, v) => m.trailFadeTicks = v,
                Mathf.Clamp(GunplaySettings.trailFadeTicks, TrailFade.Min, TrailFade.Max),
                TrailFade.Min, TrailFade.Max, true, "GunplayTrailMaterialFadeTip");

            Widgets.EndScrollView();
        }

        // 一行：标签 + 滑块 + 数值。getter/setter 直接作用在数据类字段上——
        // 十个形状参数的 rect 计算与写回完全同形，逐行照抄只会让后面加参数时漏改某一处。
        float SliderRow(float y, float width, string labelKey,
            Func<TrailMaterialParams, float> getter, Action<TrailMaterialParams, float> setter,
            float min, float max, string? tipKey)
        {
            TrailMaterialParams? m = Params;
            if (m == null) return y + RowHeight;

            float before = getter(m);
            float value = before;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, y, LabelWidth, RowHeight), labelKey.Translate());

            Rect sliderRect = new Rect(LabelWidth, y, Mathf.Max(0f, width - LabelWidth - ValueWidth - Spacing), RowHeight);
            value = Widgets.HorizontalSlider(sliderRect, value, min, max);

            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(sliderRect.xMax + Spacing, y, ValueWidth, RowHeight), value.ToString("0.00"));
            Text.Anchor = TextAnchor.UpperLeft;

            if (tipKey != null) TooltipHandler.TipRegion(new Rect(0f, y, width, RowHeight), tipKey.Translate());

            if (value != before)
            {
                float clamped = Mathf.Clamp(value, min, max);
                Edit(target => setter(target, clamped));
            }

            return y + RowHeight;
        }

        // 一行：勾选框 + 标签 + 滑块 + 数值。勾选框 = 「本材质是否自行设定该项」，
        // 未勾选时不写字段（继续回落到基础宽度 / 全局设置），与单项覆写窗口里那一列的手感一致。
        float OverrideRow(float y, float width, string labelKey,
            Func<TrailMaterialParams, float> getter, Action<TrailMaterialParams, float> setter,
            float startValue, float min, float max, bool integer, string tipKey)
        {
            TrailMaterialParams? m = Params;
            if (m == null) return y + RowHeight;

            const float CheckSize = 24f;
            const float CheckWidth = 28f;

            float stored = getter(m);
            bool isSet = stored > 0f;

            bool state = isSet;
            Widgets.Checkbox(new Vector2(0f, y + (RowHeight - CheckSize) * 0.5f), ref state, CheckSize);

            if (state != isSet)
            {
                float applied = state ? startValue : -1f;
                Edit(target => setter(target, applied));

                isSet = state;
                stored = state ? startValue : 0f;
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = isSet ? Color.white : new Color(0.62f, 0.62f, 0.62f);
            Widgets.Label(new Rect(CheckWidth + 4f, y, Mathf.Max(0f, LabelWidth - CheckWidth - 4f), RowHeight), labelKey.Translate());
            GUI.color = Color.white;

            // 未设定时显示"勾选后将会取到的起始值"，滑块禁用——既不误导成"当前生效值"，
            // 也让人一眼看到勾上之后会从哪开始。
            float shown = isSet ? stored : startValue;

            bool savedEnabled = GUI.enabled;
            GUI.enabled = isSet;

            Rect sliderRect = new Rect(LabelWidth, y, Mathf.Max(0f, width - LabelWidth - ValueWidth - Spacing), RowHeight);
            float value = Widgets.HorizontalSlider(sliderRect, shown, min, max);

            Text.Anchor = TextAnchor.MiddleRight;
            GUI.color = isSet ? Color.white : new Color(0.62f, 0.62f, 0.62f);
            Widgets.Label(new Rect(sliderRect.xMax + Spacing, y, ValueWidth, RowHeight),
                integer ? Mathf.RoundToInt(value).ToString() : value.ToString("0.###"));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            TooltipHandler.TipRegion(new Rect(0f, y, width, RowHeight), tipKey.Translate());

            GUI.enabled = savedEnabled;

            if (isSet && value != shown)
            {
                float clamped = Mathf.Clamp(value, min, max);
                // 长度 / 淡出以整数刻度显示（分数在这里没有视觉意义，见 TrailLength / TrailFade）
                if (integer) clamped = Mathf.Round(clamped);
                Edit(target => setter(target, clamped));
            }

            return y + RowHeight;
        }

        // 一行：颜色色块 + 当前值。颜色经 MaterialPropertyBlock 逐组传入，不进贴图，
        // 所以改色不会影响已在飞的轨迹（颜色在登记时已确定），这点与形状参数不同。
        float ColorRow(float y, float width)
        {
            TrailMaterialParams? m = Params;
            if (m == null) return y + RowHeight;

            const float SwatchWidth = 120f;

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, y, LabelWidth, RowHeight), "GunplayTrailMaterialColor".Translate());

            Rect swatchRect = new Rect(LabelWidth, y + 3f, SwatchWidth, RowHeight - 6f);
            GUI.color = m.trailColor;
            GUI.DrawTexture(swatchRect, BaseContent.WhiteTex);
            GUI.color = Color.white;
            Widgets.DrawBox(swatchRect, 1);
            TooltipHandler.TipRegion(swatchRect, "GunplayTrailMaterialColorTip".Translate());

            if (Widgets.ButtonInvisible(swatchRect))
            {
                Find.WindowStack.Add(new ColorPickerWindow(m.trailColor, c => Edit(target => target.trailColor = c)));
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            GUI.color = new Color(0.6f, 0.6f, 0.6f);
            Widgets.Label(new Rect(swatchRect.xMax + Spacing, y,
                Mathf.Max(0f, width - swatchRect.xMax - Spacing), RowHeight), TrailColor.ToHtml(m.trailColor));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            return y + RowHeight;
        }

        float GroupHeader(float y, float width, string labelKey)
        {
            GUI.color = new Color(0.72f, 0.72f, 0.72f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(0f, y, width, GroupHeaderHeight), labelKey.Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;

            return y + GroupHeaderHeight;
        }

        // 一切写入都走这里：先取（必要时创建）编辑快照，改字段，再重建该 def 的材质。
        // 生成档是"就地重填同一张贴图"，于是拖动滑块时已在飞的轨迹也立刻跟着变。
        void Edit(Action<TrailMaterialParams> apply)
        {
            if (current == null) return;

            TrailMaterialParams target = TrailMaterial.EnsureEdit(current);
            apply(target);
            TrailMaterial.Invalidate(current);
        }

        void SetSource(bool generated)
        {
            if (Params?.useGeneratedTexture == generated) return;

            Edit(material => material.useGeneratedTexture = generated);
        }

        // ---------------- 右：预览 ----------------

        void DrawPreview(Rect rect)
        {
            // 棋盘格底：纯色底上看不出 alpha 的强弱
            DrawCheckerboard(rect);
            Widgets.DrawBox(rect, 1);

            TrailMaterialParams? material = Params;
            Texture2D? tex = current != null ? TrailMaterial.TextureFor(current) : null;

            if (material == null || tex == null)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                Widgets.Label(rect, "GunplayTrailMaterialNoTexture".Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            // 竖向预算：上方两列标题 + 贴图本体 + 头尾图例
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

            DrawTrailColumn(detailRect, tex, material.trailColor, "GunplayTrailTexturePreviewActual".Translate()
                + "  " + TrailTexture.Width + "×" + TrailTexture.Height);

            // 右：实际形态——游戏里轨迹又细又长（宽 0.1~0.3 格、长可达十几格），
            // 这里按细长比例拉满可用高度，效果更接近肉眼所见（也是唯一能看出收缩/放大的视角）
            float realW = Mathf.Clamp(rect.width * 0.08f, 16f, 26f);
            Rect realRect = new Rect(columnRightX - realW * 0.5f, top, realW, availableHeight);

            DrawTrailColumn(realRect, tex, material.trailColor, "GunplayTrailTexturePreviewShape".Translate());

            // 头尾图例（两列共用）
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.7f, 0.7f, 0.7f);
            Widgets.Label(new Rect(rect.x, realRect.yMax + 1f, rect.width, LegendRow),
                "↑ " + "GunplayTrailTexturePreviewHead".Translate() + "      " + "GunplayTrailTexturePreviewTail".Translate() + " ↓");
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // 一列轨迹：标题 + 贴图本体（头在上）
        void DrawTrailColumn(Rect trailRect, Texture2D tex, Color color, string title)
        {
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = new Color(0.85f, 0.85f, 0.85f);
            Widgets.Label(new Rect(trailRect.x - 60f, trailRect.y - 22f, trailRect.width + 120f, 20f), title);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            GUI.color = color;
            GUI.DrawTexture(trailRect, tex);
            GUI.color = Color.white;
            Widgets.DrawBox(trailRect, 1);
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
