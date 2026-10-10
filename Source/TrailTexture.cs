using UnityEngine;
using Verse;

namespace Gunplay
{
    // 运行时生成的轨迹贴图（按材质参数生成，一份参数一张贴图）。
    //
    // 存在理由：轨迹本体只是「一条中间实、两头虚」的白色渐变，用文件贴图既浪费装载、
    // 又无法调虚化参数。生成后 RGB 恒为纯白（颜色由材质 _Color 乘出，见 ProjectileTrailBatcher），
    // 造型全部落在 alpha 上，形状参数集中在 TrailMaterialParams，改一处即可。
    //
    // Textures/Gunplay/Effects/Guntrail.png 只作为本类的形状参考，运行时不再读取
    // （ProjectileTrailDef.material.useGeneratedTexture 为 false 时才会退回文件贴图）。
    //
    // 坐标约定：
    //   * V=1 是轨迹头部所在端（plane10 经 LookRotation 后 +Z 指向飞行方向，其 +Z 端 uv.y=1；
    //     弧线几何的 uv.y 也是从尾 0 递增到头 1）；
    //   * U = 宽度方向，两侧为轨迹边缘（左右对称，方向无所谓）。
    //
    // 注意运行时贴图与导入贴图的数据方向相反：SetPixels32 的数组第 0 行是 v=0（Unity 的贴图
    // 数据自下而上），而导入的 PNG 是文件顶行 = v=1。所以 Fill 里 y 直接就是 v，
    // 想"照着参考 PNG 的样子写"必须反过来看（生成第 y 行 = PNG 第 Height-1-y 行）。
    internal static class TrailTexture
    {
        // 尺寸：横截面 64 × 长度 256。
        // 轨迹本身又细又长（宽度通常 0.1~0.3 格，长度可达十几格），纵向采样比横向值钱得多：
        // 128×128 在长拖尾上会被拉得很糊，256 高够用；横向 64 对一条单调的横向渐变换算绰绰有余。
        // 总像素 16k，与 128×128 相同，生成开销不变。
        internal const int Width = 64;
        internal const int Height = 256;

        // 轮廓外那条软边的宽度（单位是 d，d = |2u-1| ∈ [0,1]）。取小值即可：
        // 它的作用只是让"可见宽度"的边界不出现像素级硬切，不参与可见宽度的定义。
        const float SoftEdgeBand = 0.18f;

        // 只负责建对象与采样设置，不填像素——调用方随后统一走 Fill，
        // 这样"首次生成"与"参数改动后就地重填"共用同一条像素写入路径。
        // name 带上 defName：贴图现在按材质一份，留个能认出来的名字便于在显存/Profiler 里排查。
        public static Texture2D Create(string name)
        {
            return new Texture2D(Width, Height, TextureFormat.RGBA32, false)
            {
                name = name,
                // 首尾不重复：轨迹是有头有尾的，Clamp 才能保证末端不会卷回来
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 1
            };
        }

        // 按参数写像素。参数改动时就地重填同一张贴图而不是新建：材质持有的是这张贴图，
        // 换实例就得同时去改材质（还要处理旧贴图的销毁），原地重写最省事也最安全。
        // 因此贴图必须保持可读（见下面的 Apply）——一旦 makeNoLongerReadable，
        // 下次 SetPixels32 就会抛 "texture data is either not readable"。
        public static void Fill(Texture2D tex, TrailMaterialParams p)
        {
            Color32[] pixels = new Color32[Width * Height];

            // 两端取较大者归一化：最宽端恒为 1，轮廓必然贴住四边形边缘。
            // 于是"可见宽度"与"四边形宽度"两种口径恒等，trailWidth 的含义无须改变。
            float maxWidth = Mathf.Max(0.0001f, Mathf.Max(p.headWidth, p.tailWidth));

            for (int y = 0; y < Height; y++)
            {
                // SetPixels32 的数组第 0 行就是 v=0（贴图上"下"），所以 y 直接换算成 v：
                // y=0 → v=0 = 尾部，y=Height-1 → v=1 = 头部（与参考 PNG 的文件顶行对应）
                float v = y / (float)(Height - 1);
                float lengthAlpha = LengthAlpha(v, p);
                float profileWidth = WidthProfile(v, p, maxWidth);

                for (int x = 0; x < Width; x++)
                {
                    // d = 0 在轨迹中轴，d = 1 在四边形边缘
                    float d = Mathf.Abs(2f * (x / (float)(Width - 1)) - 1f);
                    float alpha = p.peakAlpha * lengthAlpha * CrossAlphaAt(d, profileWidth, p);

                    pixels[y * Width + x] = new Color32(
                        255, 255, 255,
                        (byte)Mathf.Clamp(alpha * 255f + 0.5f, 0f, 255f));
                }
            }

            // makeNoLongerReadable 必须为 false：重填还要再写一次像素，
            // 置为 true 会释放 CPU 侧数据，下次 SetPixels32 直接抛异常。
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
        }

        // 沿长度方向的 alpha：头部（v=1）半透明 → 峰值 → 向尾部（v=0）衰减
        static float LengthAlpha(float v, TrailMaterialParams p)
        {
            if (v >= 1f - p.topFade)
            {
                // t: 0 在虚化带靠尾一侧（峰值），1 在 v=1 的头部端点（最透）
                float t = (v - (1f - p.topFade)) / p.topFade;
                return Mathf.Lerp(1f, p.topAlpha, Mathf.Pow(t, p.topFadeCurve));
            }

            return Mathf.Pow(v / (1f - p.topFade), p.tailFalloff);
        }

        // 该长度处的可见半宽比例（0~1）。两端参数是相对值，这里已经除过 maxWidth，
        // 因此最宽端恒为 1（贴住四边形边缘），最窄端按两者的比值收细。
        static float WidthProfile(float v, TrailMaterialParams p, float maxWidth)
        {
            // s：0 在头部、1 在尾部。曲线指数 >1 时 s 在头部一大段都接近 0，
            // 变化被推到尾部（"头部几乎不变、只在尾尖收"），<1 则相反。
            float s = Mathf.Pow(1f - v, p.widthProfileCurve);
            return Mathf.Clamp(Mathf.Lerp(p.headWidth, p.tailWidth, s) / maxWidth, 0f, 1f);
        }

        // 横截面 alpha：轮廓内沿用原横截面形状（按 1/w 归一化，保持"中间实、两侧虚"的比例关系不变），
        // 轮廓外一条窄软边淡出。
        //
        // w = 1（等宽材质）时直接走原路径，逐像素与旧实现一致——所有内置旧 def 的外观不会因为
        // 引入宽度轮廓而发生任何变化。这也是"遮罩法"相对"整体拉伸法"的关键优势：
        // 收缩方向不会把边缘 alpha 抬成实心块，也不会在轮廓外侧留下硬切。
        static float CrossAlphaAt(float d, float w, TrailMaterialParams p)
        {
            if (w >= 0.999f) return CrossAlpha(d, p);

            float inner = CrossAlpha(Mathf.Min(d / w, 1f), p);
            float soft = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(w, Mathf.Min(1f, w + SoftEdgeBand), d));
            return inner * soft;
        }

        // 横向 alpha：中间平、两侧虚化（d = |2u-1|）
        static float CrossAlpha(float d, TrailMaterialParams p)
        {
            if (d <= p.flatCore) return 1f;

            float t = (d - p.flatCore) / (1f - p.flatCore);
            return Mathf.Lerp(1f, p.edgeAlpha, Mathf.Pow(t, p.edgeFalloff));
        }
    }
}
