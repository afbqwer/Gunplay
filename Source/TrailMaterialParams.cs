using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹材质：一份完整的轨迹外观参数（贴图来源 + 形状 + 宽度 + 颜色 + 长度 + 淡出）。
    //
    // 本类是「用户编辑」与「XML 里的 Def」共用的唯一数据类：
    //   * ProjectileTrailDef.material 持有模板实例（XML 填充；XML 没写的字段就是本类的字段默认值）；
    //   * 设置里按 defName 存一份编辑快照（TrailMaterial.EnsureEdit 从模板克隆而来）；
    //   * 运行时一律经 TrailMaterial.Effective(def) 取生效实例（快照 > 模板）。
    // 于是「窗口调参」「XML 默认值」「运行时读取」三处同源，不会各自算一遍后慢慢跑偏。
    //
    // 「材质类型」就是 ProjectileTrailDef 本身：一个 def = 一种材质，谁用哪种材质由
    // ProjectilePropDef.trail（XML）与单项覆写（窗口里逐投射物选）决定，
    // 因此这里不需要再引入一层类型枚举。
    //
    // 「未设定」哨兵：宽度 / 长度 / 淡出用 -1（与 ProjectileTrailDef.trailWidth 的历史口径一致）。
    // 材质只是中间一层，未设定的字段必须能继续回落到下层口径
    // （宽度 → 伤害/贴图基础宽度 × 全局系数；长度 / 淡出 → 全局设置），
    // 而 0 在这些字段里是合法值（宽度 0 = 不绘制），不能拿来当哨兵。
    public class TrailMaterialParams : IExposable
    {
        // ---------------- 贴图来源 ----------------

        // true = 运行时生成的贴图（形状参数生效）；false = 用 ProjectileTrailDef.graphicData 的 texPath 贴图
        // （此时形状参数与宽度轮廓都不参与——文件贴图没法按参数重写像素）。
        public bool useGeneratedTexture = true;

        // ---------------- 纵向（沿飞行方向）----------------

        // 峰值不透明度（最亮处）。参考贴图的峰值是 153/255 ≈ 0.6。
        public float peakAlpha = 0.6f;

        // 顶部（V=1，即轨迹头部所在端）保留的半透明程度。头部完全不透会在弹体上压出一个硬亮块。
        public float topAlpha = 0.22f;

        // 头部虚化带占全长的比例：alpha 在 V=1 处为 topAlpha，向下到 V=1-topFade 升到峰值。
        public float topFade = 0.09f;

        // 头部虚化带的曲线指数：>1 表示透明度收缩在最顶端一小段。1 = 线性过渡。
        public float topFadeCurve = 1.7f;

        // 尾部衰减指数：1 = 线性衰减，越大尾部收得越快（拖尾更短促）。
        public float tailFalloff = 1.15f;

        // ---------------- 横向（轨迹宽度方向）----------------

        // 中间保持满值的宽度比例（|2u-1| ≤ 该值内为实心）。
        public float flatCore = 0.5f;

        // 最外侧边缘保留的不透明度（相对峰值）。0 = 边缘完全透明。
        public float edgeAlpha = 0.35f;

        // 边缘虚化的曲线指数：越大则透明收缩在更靠外的一小段，边缘更"硬"。
        public float edgeFalloff = 1.6f;

        // ---------------- 宽度轮廓（沿长度变化的可见宽度）----------------

        // 头部（弹体端）与尾部的可见宽度，单位是「四边形半宽的比例」（0~1）。
        //
        // 两个字段都是**相对值**：生成时按两者较大者归一化到 1，因此最宽的那一端必然贴住四边形边缘，
        // 两侧不会出现"看不见的空白"，trailWidth 也就仍然是「四边形宽度」这一直观口径
        // （等宽 = 两端都填 1，与旧外观逐像素一致）。
        //   尾部逐渐收缩：头 1 → 尾 0.15（放大档则相反）
        //   尾部逐渐放大：头 0.35 → 尾 1
        public float headWidth = 1f;
        public float tailWidth = 1f;

        // 轮廓过渡的曲线指数：s = (1 - v)^curve，v=1 是头部、v=0 是尾部。
        // 1 = 全长线性；>1 = 变化集中在尾部（头部一大段几乎不变，收尾更深）；
        // <1 = 变化集中在头部。取代了单独的"起始位置"参数——调这一个就够。
        public float widthProfileCurve = 1f;

        // ---------------- 几何与颜色 ----------------

        // 轨迹染色。生成贴图的本体是纯白，颜色完全由该字段乘出（经材质 _Color 逐组传入）。
        public Color trailColor = new Color(1f, 1f, 1f, 0.7f);

        // 轨迹宽度（四边形宽度，世界单位）。-1 = 未设定，回落基础宽度（伤害/贴图口径）× 全局系数。
        public float trailWidth = -1f;

        // 拖尾长度系数（length = 弹速 × 该值）。-1 = 未设定，回落全局设置。
        public float trailLengthFactor = -1f;

        // 投射物消失后尾巴收缩完的 tick 数。-1 = 未设定，回落全局设置。
        public float trailFadeTicks = -1f;

        // ---------------- 取值范围（窗口滑块与 Clamp 共用）----------------

        public const float PeakAlphaMin = 0f, PeakAlphaMax = 1f;
        public const float TopAlphaMin = 0f, TopAlphaMax = 1f;
        public const float TopFadeMin = 0.005f, TopFadeMax = 0.5f;
        public const float TopFadeCurveMin = 0.5f, TopFadeCurveMax = 4f;
        public const float TailFalloffMin = 0.3f, TailFalloffMax = 4f;
        public const float FlatCoreMin = 0f, FlatCoreMax = 0.95f;
        public const float EdgeAlphaMin = 0f, EdgeAlphaMax = 1f;
        public const float EdgeFalloffMin = 0.5f, EdgeFalloffMax = 4f;
        public const float WidthProfileMin = 0f, WidthProfileMax = 1f;
        public const float WidthProfileCurveMin = 0.25f, WidthProfileCurveMax = 6f;

        public void ExposeData()
        {
            Scribe_Values.Look(ref useGeneratedTexture, "useGeneratedTexture", true);

            Scribe_Values.Look(ref peakAlpha, "peakAlpha", 0.6f);
            Scribe_Values.Look(ref topAlpha, "topAlpha", 0.22f);
            Scribe_Values.Look(ref topFade, "topFade", 0.09f);
            Scribe_Values.Look(ref topFadeCurve, "topFadeCurve", 1.7f);
            Scribe_Values.Look(ref tailFalloff, "tailFalloff", 1.15f);
            Scribe_Values.Look(ref flatCore, "flatCore", 0.5f);
            Scribe_Values.Look(ref edgeAlpha, "edgeAlpha", 0.35f);
            Scribe_Values.Look(ref edgeFalloff, "edgeFalloff", 1.6f);

            Scribe_Values.Look(ref headWidth, "headWidth", 1f);
            Scribe_Values.Look(ref tailWidth, "tailWidth", 1f);
            Scribe_Values.Look(ref widthProfileCurve, "widthProfileCurve", 1f);

            Scribe_Values.Look(ref trailColor, "trailColor", Color.white);
            Scribe_Values.Look(ref trailWidth, "trailWidth", -1f);
            Scribe_Values.Look(ref trailLengthFactor, "trailLengthFactor", -1f);
            Scribe_Values.Look(ref trailFadeTicks, "trailFadeTicks", -1f);

            Clamp();
        }

        public void Clamp()
        {
            peakAlpha = Mathf.Clamp(peakAlpha, PeakAlphaMin, PeakAlphaMax);
            topAlpha = Mathf.Clamp(topAlpha, TopAlphaMin, TopAlphaMax);
            topFade = Mathf.Clamp(topFade, TopFadeMin, TopFadeMax);
            topFadeCurve = Mathf.Clamp(topFadeCurve, TopFadeCurveMin, TopFadeCurveMax);
            tailFalloff = Mathf.Clamp(tailFalloff, TailFalloffMin, TailFalloffMax);
            flatCore = Mathf.Clamp(flatCore, FlatCoreMin, FlatCoreMax);
            edgeAlpha = Mathf.Clamp(edgeAlpha, EdgeAlphaMin, EdgeAlphaMax);
            edgeFalloff = Mathf.Clamp(edgeFalloff, EdgeFalloffMin, EdgeFalloffMax);

            headWidth = Mathf.Clamp(headWidth, WidthProfileMin, WidthProfileMax);
            tailWidth = Mathf.Clamp(tailWidth, WidthProfileMin, WidthProfileMax);
            widthProfileCurve = Mathf.Clamp(widthProfileCurve, WidthProfileCurveMin, WidthProfileCurveMax);

            // 三个哨兵字段只做下限保护：-1 必须原样保留（它表示"未设定"）
            if (trailWidth < 0f) trailWidth = -1f;
            if (trailLengthFactor < 0f) trailLengthFactor = -1f;
            if (trailFadeTicks < 0f) trailFadeTicks = -1f;
        }

        // 全场反馈：是否处于"等宽"（轮廓遮罩完全不起作用）。等宽时贴图与旧实现逐像素一致。
        public bool IsUniformWidth => Mathf.Approximately(headWidth, tailWidth);

        // 归一化后的可见宽度（供窗口显示"实际生效比例"）：两端都乘 1/max，最宽端恒为 1。
        public void NormalizedWidths(out float head, out float tail)
        {
            float max = Mathf.Max(0.0001f, Mathf.Max(headWidth, tailWidth));
            head = headWidth / max;
            tail = tailWidth / max;
        }

        // 整份拷贝（模板 → 快照、出厂默认 → 参数）。逐字段赋值而不用反射或序列化中转，
        // 是为了让"新增字段但忘了在拷贝里带上"在编译期就能被发现。
        public void CopyFrom(TrailMaterialParams other)
        {
            useGeneratedTexture = other.useGeneratedTexture;

            peakAlpha = other.peakAlpha;
            topAlpha = other.topAlpha;
            topFade = other.topFade;
            topFadeCurve = other.topFadeCurve;
            tailFalloff = other.tailFalloff;
            flatCore = other.flatCore;
            edgeAlpha = other.edgeAlpha;
            edgeFalloff = other.edgeFalloff;

            headWidth = other.headWidth;
            tailWidth = other.tailWidth;
            widthProfileCurve = other.widthProfileCurve;

            trailColor = other.trailColor;
            trailWidth = other.trailWidth;
            trailLengthFactor = other.trailLengthFactor;
            trailFadeTicks = other.trailFadeTicks;
        }
    }
}
