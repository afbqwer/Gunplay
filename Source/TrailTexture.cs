using UnityEngine;
using Verse;

namespace Gunplay
{
    // 运行时生成的轨迹贴图。
    //
    // 存在理由：轨迹本体只是「一条中间实、两头虚」的白色渐变，用文件贴图既浪费装载、
    // 又无法调虚化参数。生成后 RGB 恒为纯白（颜色由材质 _Color 乘出，见 ProjectileTrailBatcher），
    // 造型全部落在 alpha 上，形状参数集中在 TrailTextureParams，改一处即可。
    //
    // Textures/Gunplay/Effects/Guntrail.png 只作为本类的形状参考，运行时不再读取
    // （ProjectileTrailDef.useGeneratedTexture 为 false 时才会退回文件贴图）。
    //
    // 坐标约定：
    //   * V=1 是轨迹头部所在端（plane10 经 LookRotation 后 +Z 指向飞行方向，其 +Z 端 uv.y=1；
    //     弧线几何的 uv.y 也是从尾 0 递增到头 1）；
    //   * U = 宽度方向，两侧为轨迹边缘（左右对称，方向无所谓）。
    //
    // 注意运行时贴图与导入贴图的数据方向相反：SetPixels32 的数组第 0 行是 v=0（Unity 的贴图
    // 数据自下而上），而导入的 PNG 是文件顶行 = v=1。所以 Fill 里 y 直接就是 v，
    // 想“照着参考 PNG 的样子写”必须反过来看（生成第 y 行 = PNG 第 Height-1-y 行）。
    internal static class TrailTexture
    {
        // 尺寸：横截面 64 × 长度 256。
        // 轨迹本身又细又长（宽度通常 0.1~0.3 格，长度可达十几格），纵向采样比横向值钱得多：
        // 128×128 在长拖尾上会被拉得很糊，256 高够用；横向 64 对一条单调的横向渐变换算绰绰有余。
        // 总像素 16k，与 128×128 相同，生成开销不变。
        internal const int Width = 64;
        internal const int Height = 256;

        static Texture2D? cached;

        // 当前生效的参数（存在 mod 设置里，见 GunplaySettings.trailTextureParams）。
        static TrailTextureParams Params => GunplaySettings.trailTextureParams;

        // 首次用到时才生成（全局轨迹关闭时完全不产生开销）。
        public static Texture2D Get()
        {
            return cached ??= Generate();
        }

        // 参数改动后重填像素。刻意复用同一张贴图而不是新建：材质持有的是这张贴图，
        // 换实例就得同时去改材质（还要处理旧贴图的销毁），原地重写最省事也最安全。
        // 因此贴图必须保持可读（见 Fill 里的 Apply）——一旦 makeNoLongerReadable，
        // 下次 SetPixels32 就会抛 "texture data is either not readable"。
        public static void Invalidate()
        {
            if (cached != null) Fill(cached);
        }

        static Texture2D Generate()
        {
            Texture2D tex = new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                name = "GunplayTrailGenerated",
                // 首尾不重复：轨迹是有头有尾的，Clamp 才能保证末端不会卷回来
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 1
            };

            Fill(tex);
            return tex;
        }

        static void Fill(Texture2D tex)
        {
            TrailTextureParams p = Params;
            Color32[] pixels = new Color32[Width * Height];

            for (int y = 0; y < Height; y++)
            {
                // SetPixels32 的数组第 0 行就是 v=0（贴图上"下"），所以 y 直接换算成 v：
                // y=0 → v=0 = 尾部，y=Height-1 → v=1 = 头部（与参考 PNG 的文件顶行对应）
                float v = y / (float)(Height - 1);
                float lengthAlpha = LengthAlpha(v, p);

                for (int x = 0; x < Width; x++)
                {
                    float u = x / (float)(Width - 1);
                    float alpha = p.peakAlpha * lengthAlpha * CrossAlpha(u, p);

                    pixels[y * Width + x] = new Color32(
                        255, 255, 255,
                        (byte)Mathf.Clamp(alpha * 255f + 0.5f, 0f, 255f));
                }
            }

            // makeNoLongerReadable 必须为 false：重填（Invalidate）还要再写一次像素，
            // 置为 true 会释放 CPU 侧数据，下次 SetPixels32 直接抛异常。
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
        }

        // 沿长度方向的 alpha：头部（v=1）半透明 → 峰值 → 向尾部（v=0）衰减
        static float LengthAlpha(float v, TrailTextureParams p)
        {
            if (v >= 1f - p.topFade)
            {
                // t: 0 在虚化带靠尾一侧（峰值），1 在 v=1 的头部端点（最透）
                float t = (v - (1f - p.topFade)) / p.topFade;
                return Mathf.Lerp(1f, p.topAlpha, Mathf.Pow(t, p.topFadeCurve));
            }

            return Mathf.Pow(v / (1f - p.topFade), p.tailFalloff);
        }

        // 横向 alpha：中间平、两侧虚化
        static float CrossAlpha(float u, TrailTextureParams p)
        {
            float d = Mathf.Abs(2f * u - 1f);
            if (d <= p.flatCore) return 1f;

            float t = (d - p.flatCore) / (1f - p.flatCore);
            return Mathf.Lerp(1f, p.edgeAlpha, Mathf.Pow(t, p.edgeFalloff));
        }
    }

    // 生成贴图的形状参数。默认值对齐参考贴图 Guntrail.png（峰值 153/255、顶边 33、边缘 54）。
    // 取值范围在 Clamp() 里集中约束：滑块本身不会越界，但手改存档/跨版本可能读到越界值。
    public class TrailTextureParams : IExposable
    {
        // ---------------- 纵向（沿飞行方向）----------------

        // 峰值不透明度（最亮处）。参考贴图的峰值是 153/255 ≈ 0.6。
        public float peakAlpha = 0.6f;

        // 顶部（V=1，即贴图上边缘 = 轨迹头部所在端）保留的半透明程度。
        // 参考贴图顶边只有峰值的 22%：头部完全不透会在弹体上压出一个硬亮块，
        // 保留一点透明度让头部自然融入弹体。1 = 完全不虚化，0 = 头部全透。
        public float topAlpha = 0.22f;

        // 顶部虚化带占全长的比例：alpha 在 V=1 处为 topAlpha，向下到 V=1-topFade 升到峰值。
        // 参考贴图（128 高）该带约 9% 长、约 11 像素；本贴图 256 高，同样的比例约 23 像素。
        public float topFade = 0.09f;

        // 顶部虚化带的曲线指数：>1 表示透明度收缩在最顶端一小段（参考贴图正是这种形态——
        // 顶边 33，往下 4 像素就恢复到 102，11 像素处到峰值）。1 = 线性过渡。
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

        // ---------------- 取值范围（窗口滑块与 Clamp 共用）----------------

        public const float PeakAlphaMin = 0f, PeakAlphaMax = 1f;
        public const float TopAlphaMin = 0f, TopAlphaMax = 1f;
        public const float TopFadeMin = 0.005f, TopFadeMax = 0.5f;
        public const float TopFadeCurveMin = 0.5f, TopFadeCurveMax = 4f;
        public const float TailFalloffMin = 0.3f, TailFalloffMax = 4f;
        public const float FlatCoreMin = 0f, FlatCoreMax = 0.95f;
        public const float EdgeAlphaMin = 0f, EdgeAlphaMax = 1f;
        public const float EdgeFalloffMin = 0.5f, EdgeFalloffMax = 4f;

        public void ExposeData()
        {
            Scribe_Values.Look(ref peakAlpha, "peakAlpha", 0.6f);
            Scribe_Values.Look(ref topAlpha, "topAlpha", 0.22f);
            Scribe_Values.Look(ref topFade, "topFade", 0.09f);
            Scribe_Values.Look(ref topFadeCurve, "topFadeCurve", 1.7f);
            Scribe_Values.Look(ref tailFalloff, "tailFalloff", 1.15f);
            Scribe_Values.Look(ref flatCore, "flatCore", 0.5f);
            Scribe_Values.Look(ref edgeAlpha, "edgeAlpha", 0.35f);
            Scribe_Values.Look(ref edgeFalloff, "edgeFalloff", 1.6f);

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
        }

        public void ResetToDefaults()
        {
            peakAlpha = 0.6f;
            topAlpha = 0.22f;
            topFade = 0.09f;
            topFadeCurve = 1.7f;
            tailFalloff = 1.15f;
            flatCore = 0.5f;
            edgeAlpha = 0.35f;
            edgeFalloff = 1.6f;
        }
    }
}
