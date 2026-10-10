using System;
using System.Collections.Generic;
using Gunplay.Patch;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 单条投射物轨迹的状态与几何生成。
    //
    // 与旧实现（ProjectileTrail : ThingWithComps）的区别仅在于"载体"：
    // 本类不是 Thing——不进地图网格、不参与 DynamicDrawManager 的注册与剔除、不自行 tick、不序列化。
    // 它由 MapComponent_ProjectileTrails 统一持有与驱动，由 ProjectileTrailBatcher 批量绘制。
    //
    // 逻辑本身（直线/弧线、CE 绘制位置跟踪、尾部淡出、宽度口径）与旧实现逐行对齐，
    // 只有两处有意的行为变化：
    //   1) 轨迹 Y 加了一个极小负偏移（DepthEpsilon），使轨迹稳定处于弹体之下（见该常量注释）；
    //   2) 剔除改由外部按"整条轨迹包围盒"判定（修复"头出屏、尾在屏内却被整条剔除"的缺陷）。
    // 相机视野的世界坐标范围。由 CellRect 展开：格 (x,z) 占据世界 [x, x+1) × [z, z+1)。
    // 每帧只构造一次，避免在逐条剔除里反复访问 CellRect.Min / Max（那是会构造 IntVec3 的属性）。
    internal readonly struct TrailViewRect
    {
        public readonly float MinX;
        public readonly float MinZ;
        public readonly float MaxX;
        public readonly float MaxZ;

        public TrailViewRect(CellRect view)
        {
            MinX = view.Min.x;
            MinZ = view.Min.z;
            MaxX = view.Max.x + 1;
            MaxZ = view.Max.z + 1;
        }
    }

    // 弧线采样段数的取值约定。放在类外是因为三个地方都要读它：
    // 本类的几何生成、GunplaySettings 的滑杆区间、存档读写的默认值。
    // 顶点数 = (段数 + 1) × 2，代价线性增长；弧线投射物稀少，默认取适中值，
    // 上限不放太高——64 段已远超肉眼能分辨的平滑度。
    internal static class ArcSegments
    {
        internal const int Default = 16;
        internal const int Min = 2;
        internal const int Max = 64;
    }

    internal class ProjectileTrailState
    {
        // 当前生效段数（全局设置 + 夹取）。做成属性而非常量，是为了让 AppendArcGeometry
        // 的三处循环保持原样；夹取放在这里，设置项即使被外部改成越界值也不会炸几何。
        static int ArcSegmentCount => Mathf.Clamp(GunplaySettings.arcSegmentCount, ArcSegments.Min, ArcSegments.Max);

        // 尾部至少占整段弧线的比例，避免低速抛射物（如手雷）的轨迹过短而不明显。
        const float ArcMinTailFraction = 0.1f;

        // 弧线的"上升"方向。RimWorld 的地图相机是正交俯视（沿 -Y 观察），
        // 世界 +Y 只承担深度/排序，在屏幕上不可见；屏幕竖直方向对应世界 +Z（北）。
        // 必须与 Vanilla 保持一致：Projectile.DrawAt 用 (0,0,1)，PawnFlyer 用 Vector3.forward。
        static readonly Vector3 ArcUp = new Vector3(0f, 0f, 1f);

        // 深度微调。轨迹与投射物同处 Projectile 高度层（同一个 Y），深度测试无法区分先后，
        // 平局由同深度下的绘制规则决定；让轨迹 Y 略小于投射物即可稳定地"轨迹在弹体之下"。
        // 取值远小于层间距 0.3658，不会跨层，也不会影响与 Pawn/建筑/微粒的相对关系。
        internal const float DepthEpsilon = -0.002f;

        // 归属信息
        internal ProjectileTrailDef def = null!;   // 材质来源；由 Initialize* 首个赋值，之后恒非空
        internal Color color = Color.white;        // 轨迹颜色，登记时按 TrailColor 口径解析一次（覆写 > def 颜色）
        internal Thing? LookupKey;                 // 登记时的投射物引用（捕获后仍保留），用于从集合移除
        internal bool Dead;                        // 生命周期结束，由集合回收

        // ---------------- 直线/通用状态 ----------------
        int ticksUtilDeath;
        int ticksIdle;
        Thing? projectile;
        Func<Vector3>? exactPositionGetter;
        Vector3 a;
        Vector3 exactPosition;
        Vector3 previousPosition;
        float speed;
        Vector3 dir;
        // 拖尾满长（格）。与 fadeTicks 无关：长度是几何量，淡出是时间量，见 TrailLength / TrailFade。
        float length;
        float width;
        float traveled;
        float altitude;
        // 弹体消失后尾巴收缩到零所用的 tick 数
        float fadeTicks;

        // ---------------- 弧线轨迹专用状态（仅 arcing == true 时使用） ----------------
        bool arcing;
        Vector3 arcSide;
        float arcHorizontalLength;
        float arcHeight;
        float arcTailFraction;
        // 弧线整条的 AABB（初始化时算一次）。存成裸 Vector3 分量，避免逐帧构造 Bounds，
        // 更避免反复访问 Bounds.min / max —— 那两个属性每次都会做一遍向量加减。
        Vector3 arcAabbMin;
        Vector3 arcAabbMax;
        // 直线轨迹相对头部可能延伸的最大水平距离（length + width），用于 O(1) 快速否定剔除
        float maxReach;

        // ---------------- CE 专用状态 ----------------
        // CE 投射物（直线与弧线）每 tick 都跟踪"绘制位置"（含 CE 的高度屏幕偏移），
        // 使拖尾/弧线贴着弹体而不是地面投影。采样失败回退解析弧线时退回地面口径。
        bool ceTrackDrawnPosition;
        // 弧线 CE 投射物：发射时按 CE 绘制映射采样的整条弹道绘制点（世界坐标，y 为 altitude 层）
        // CE 投射物：null 表示未/无法采样，此时按解析抛物线取点（ceArcCumLen 同样可能仍为 null）
        List<Vector3>? ceArcSamples;
        float[]? ceArcCumLen;   // 采样点间绘制路径的累计长度（与 traveled 的绘制口径一致）
        float ceArcTotalLen;

        // ---------------- 初始化 ----------------

        // 原版投射物入口
        public void Initialize(Projectile proj, Vector3 destination, ProjectileTrailDef trailDef)
        {
            def = trailDef;
            color = TrailColor.Effective(proj.def, trailDef);
            ticksUtilDeath = -1;
            ticksIdle = 0;
            projectile = proj;
            exactPositionGetter = () => proj.ExactPosition;
            a = proj.ExactPosition;
            Vector3 b = destination;
            altitude = proj.def.Altitude + DepthEpsilon;
            a.y = b.y = altitude;
            speed = proj.def.projectile.SpeedTilesPerTick;
            exactPosition = a;
            previousPosition = a;
            traveled = 0f;

            // 拖尾长度（格）与淡出时长（tick）是两个独立的量，不再互相派生，见 TrailLength / TrailFade
            length = speed * TrailLength.Effective(proj.def, trailDef);
            fadeTicks = TrailFade.Effective(proj.def, trailDef);
            dir = (b - a).normalized;
            ComputeWidth(proj.def, proj.DamageAmount);
            maxReach = length + width;

            // 只有弧线投射物才需要额外的弧线几何；直线投射物到此为止，不执行任何弧线代码
            arcing = ProjectileRules.IsArcing(proj.def);
            if (arcing)
            {
                // 与 Vanilla Projectile.DrawAt 一致的弧高取值（收缩在 InitializeArc 内统一处理）
                InitializeArc(b, proj.def.projectile.arcHeightFactor);
            }
        }

        // CE（Combat Extended）投射物入口。ProjectileCE 不是 Projectile 子类，位置/伤害/
        // 出射仰角等信息全部经由 PatchCECompat 的反射读取。
        public void InitializeCE(Thing proj, Vector3 destination, ProjectileTrailDef trailDef)
        {
            def = trailDef;
            color = TrailColor.Effective(proj.def, trailDef);
            ticksUtilDeath = -1;
            ticksIdle = 0;
            projectile = proj;
            exactPositionGetter = PatchCECompat.CreateExactPositionGetter(proj);
            a = exactPositionGetter();
            Vector3 b = destination;
            altitude = proj.def.Altitude + DepthEpsilon;
            a.y = b.y = altitude;
            speed = proj.def.projectile.SpeedTilesPerTick;
            exactPosition = a;
            previousPosition = a;
            traveled = 0f;

            // CE 弹速是真实弹道速度（子弹一 tick 可飞数格），沿用「弹速 × 长度系数」会拉出十余格的
            // "激光束"，CE 路径对拖尾长度做上限截断。注意这只截长度，不再连带截短淡出时长。
            length = Mathf.Min(speed * TrailLength.Effective(proj.def, trailDef), PatchCECompat.MaxTrailLength);
            fadeTicks = TrailFade.Effective(proj.def, trailDef);
            dir = (b - a).normalized;
            ComputeWidth(proj.def, PatchCECompat.GetDamageAmount(proj));
            maxReach = length + width;

            // CE 轨迹样式由设置开关决定（ceStraightTrails，默认开启）：
            //   * 开启 → CE 投射物绘制直线拖尾（跟随弹体绘制位置）；抛射类投射物
            //     （出射仰角超过 ArcAngleThreshold）在入口处被拦截，不显示轨迹；
            //   * 关闭 → 全部 CE 投射物绘制弧线轨迹：按 CE 自己的绘制映射采样整条弹道的绘制位置。
            // 弧线采样失败时回退为地面解析抛物线（原版算法，弧高 tan(θ)*L/4）。
            float shotAngle = PatchCECompat.GetShotAngle(proj);
            arcing = !GunplaySettings.ceStraightTrails;
            ceTrackDrawnPosition = true;
            if (arcing)
            {
                List<Vector3>? samples = PatchCECompat.PredictDrawPositions(proj, PatchCECompat.GetStartingTicksToImpact(proj));
                if (samples == null || !InitializeArcFromSamples(samples))
                {
                    ceTrackDrawnPosition = false;
                    // 采样退化：必须清空，否则绘制仍会走上面的采样分支（且 ceArcCumLen 可能为 null）
                    ceArcSamples = null;
                    InitializeArc(b, Mathf.Tan(shotAngle) * (b - a).magnitude / 4f);
                }
            }
        }

        // 宽度口径统一走 TrailWidth：单项覆写 > ProjectileTrailDef.trailWidth > 伤害/贴图基础宽度。
        // 覆写是绝对值且完全独立（不再乘全局系数），命中时直接采用。
        void ComputeWidth(ThingDef projDef, float damage)
        {
            width = TrailWidth.Effective(projDef, damage, def);
        }

        // ---------------- 运行时推进 ----------------

        // 由 MapComponent_ProjectileTrails.MapComponentTick 每 tick 调用
        public void Tick()
        {
            if (projectile != null)
            {
                if (projectile.Destroyed || (ticksUtilDeath == -1 && ticksIdle > 10))
                {
                    projectile = null;
                    exactPositionGetter = null;
                    ticksUtilDeath = Mathf.CeilToInt(fadeTicks);
                }
                else
                {
                    // 只在投射物存活时读取位置；销毁后不再访问它。
                    // CE 直线投射物跟踪"绘制位置"（含高度屏幕偏移），其余跟踪地面投影。
                    Vector3? drawn = ceTrackDrawnPosition
                        ? PatchCECompat.GetDrawPosition(projectile)
                        : exactPositionGetter?.Invoke();
                    if (drawn.HasValue) ApplyPosition(drawn.Value);
                }
            }

            previousPosition = exactPosition;

            if (ticksUtilDeath > 0)
            {
                ticksUtilDeath--;
            }
            else if (ticksUtilDeath == 0)
            {
                Dead = true;
            }
        }

        void ApplyPosition(Vector3 pos)
        {
            pos.y = altitude;
            Vector3 delta = pos - previousPosition;
            if (delta.sqrMagnitude > 0.000001f)
            {
                traveled += delta.magnitude;
                dir = delta.normalized;
            }
            else
            {
                ticksIdle++;
            }
            exactPosition = pos;
        }

        // 投射物销毁前锁定最终位置（命中补丁调用），之后轨迹只做尾部淡出
        public void CaptureFinalPosition(Vector3 pos)
        {
            if (projectile == null) return;

            ApplyPosition(pos);
            projectile = null;
            exactPositionGetter = null;
            ticksUtilDeath = Mathf.CeilToInt(fadeTicks);
        }

        // CE 投射物销毁前锁定最终位置。取位置口径与飞行中一致：直线取绘制位置，弧线取地面投影。
        public void CaptureFinalPositionCE(Thing proj)
        {
            if (projectile == null) return;

            Vector3? drawn = ceTrackDrawnPosition
                ? PatchCECompat.GetDrawPosition(proj)
                : exactPositionGetter?.Invoke();
            if (!drawn.HasValue) return;

            CaptureFinalPosition(drawn.Value);
        }

        // ---------------- 剔除 ----------------

        // 弧线轨迹的 AABB（初始化时算一次）。仅弧线使用——直线走实例化，不需要合并网格的 bounds。
        public void GetArcAabb(out Vector3 min, out Vector3 max)
        {
            min = arcAabbMin;
            max = arcAabbMax;
        }

        // 视野剔除。view 是已展开为世界坐标范围的相机视野（见 TrailViewRect）。
        //
        // 这里是逐条执行的，也是本系统最热的一段：列表里含全图所有在飞轨迹，
        // 而 Draw 只对可见的那部分执行，所以剔除一旦写得重，耗时就会远超 Draw，
        // 且随场景弹量线性波动。因此：
        //   弧线 —— 用初始化时缓存好的 AABB，四次比较；
        //   直线 —— 先用头部做两次 O(1) 判定，只有跨在视野边缘的少数轨迹才落到精确求交。
        public bool IsVisible(TrailViewRect view)
        {
            if (arcing)
            {
                return arcAabbMax.x >= view.MinX && arcAabbMin.x <= view.MaxX
                    && arcAabbMax.z >= view.MinZ && arcAabbMin.z <= view.MaxZ;
            }

            float hx = exactPosition.x;
            float hz = exactPosition.z;

            // 快速肯定：头部就在视野内 ⇒ 轨迹头段必然可见
            if (hx >= view.MinX && hx <= view.MaxX && hz >= view.MinZ && hz <= view.MaxZ) return true;

            // 快速否定：头部到视野的距离超过轨迹的最大延伸 ⇒ 整条轨迹必然不可见
            if (hx < view.MinX - maxReach || hx > view.MaxX + maxReach
                || hz < view.MinZ - maxReach || hz > view.MaxZ + maxReach) return false;

            // 精确：按实际首尾点求交（只有跨越视野边缘的少数轨迹会走到这里）
            float len = DrawLength();
            float tx = hx - dir.x * len;
            float tz = hz - dir.z * len;
            return Mathf.Max(hx, tx) + width >= view.MinX && Mathf.Min(hx, tx) - width <= view.MaxX
                && Mathf.Max(hz, tz) + width >= view.MinZ && Mathf.Min(hz, tz) - width <= view.MaxZ;
        }

        // ---------------- 几何生成 ----------------

        // 是否为弧线轨迹。直线轨迹走 GPU 实例化（只提供矩阵），弧线走合并网格。
        public bool IsArc => arcing;

        // 直线轨迹的绘制矩阵：网格固定为 MeshPool.plane10，逐条只提供 TRS 矩阵。
        // 返回 false 表示当前无可绘制内容（长度退化或尚未定向）。
        public bool TryGetStraightMatrix(out Matrix4x4 matrix)
        {
            matrix = default;
            if (length <= 0f || dir.sqrMagnitude < 0.000001f) return false;

            float len = DrawLength();
            if (len <= 0f) return false;

            Vector3 drawPos = exactPosition - dir * len / 2f;
            matrix = Matrix4x4.TRS(drawPos, Quaternion.LookRotation(dir), new Vector3(width, 1f, len));
            return true;
        }

        // 当前实际拖尾长度（含投掷物消亡后的收缩）
        float DrawLength()
        {
            float len = Mathf.Min(traveled, length);
            if (ticksUtilDeath != -1)
            {
                len *= fadeTicks > 0f ? ticksUtilDeath / fadeTicks : 0f;
            }
            return Mathf.Max(0f, len);
        }

        public void AppendArcGeometry(List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            if (arcHorizontalLength <= 0.0001f) return;

            // traveled 即沿飞行方向已经过的距离，可直接换算成弧线比例
            float head = Mathf.Clamp01(traveled / arcHorizontalLength);
            float tail = head - arcTailFraction;

            if (ticksUtilDeath != -1)
            {
                // 投射物已消失：尾部向前收缩，模拟淡出（与直线轨迹一致）
                float k = fadeTicks > 0f ? Mathf.Clamp01(ticksUtilDeath / fadeTicks) : 0f;
                tail = head - (head - tail) * k;
            }

            tail = Mathf.Clamp01(tail);
            if (head - tail <= 0.0001f) return;

            float halfWidth = width * 0.5f;
            int baseIndex = verts.Count;

            // CE 采样点列存在时沿绘制路径取点，否则按解析抛物线取点
            int seg = 0;
            for (int i = 0; i <= ArcSegmentCount; i++)
            {
                float f = i / (float)ArcSegmentCount;
                float t = Mathf.Lerp(tail, head, f);

                Vector3 center;
                if (ceArcSamples != null && ceArcCumLen != null)
                {
                    // 按绘制路径累计长度在采样点间插值（t 单调递增，段指针只前进）
                    float d = Mathf.Clamp01(t) * ceArcTotalLen;
                    while (seg < ceArcCumLen.Length - 2 && ceArcCumLen[seg + 1] < d) seg++;
                    float span = ceArcCumLen[seg + 1] - ceArcCumLen[seg];
                    float k = span > 0.000001f ? Mathf.Clamp01((d - ceArcCumLen[seg]) / span) : 0f;
                    center = Vector3.Lerp(ceArcSamples[seg], ceArcSamples[seg + 1], k);
                }
                else
                {
                    float height = arcHeight * 4f * t * (1f - t);
                    center = a + dir * (arcHorizontalLength * t) + ArcUp * height;
                }

                verts.Add(center - arcSide * halfWidth);
                verts.Add(center + arcSide * halfWidth);
                uvs.Add(new Vector2(0f, f));
                uvs.Add(new Vector2(1f, f));
            }

            for (int i = 0; i < ArcSegmentCount; i++)
            {
                int v = baseIndex + i * 2;
                tris.Add(v + 0); tris.Add(v + 1); tris.Add(v + 3);
                tris.Add(v + 0); tris.Add(v + 3); tris.Add(v + 2);
            }
        }

        // ---------------- 弧线几何预计算 ----------------

        // 预计算弧线几何参数（顶点在绘制时按可见窗口生成）。
        // rawArcHeight：弧高（原版口径），过大时按距离收缩，与 Vanilla Projectile.DrawAt 完全一致。
        void InitializeArc(Vector3 b, float rawArcHeight)
        {
            Vector3 delta = b - a;
            delta.y = 0f;
            arcHorizontalLength = delta.magnitude;
            arcSide = new Vector3(-dir.z, 0f, dir.x);

            float distSq = delta.sqrMagnitude;
            if (rawArcHeight * rawArcHeight > distSq * 0.04f)
            {
                rawArcHeight = Mathf.Sqrt(distSq) * 0.2f;
            }
            arcHeight = rawArcHeight;

            // 尾部长度与直线轨迹同为「弹速 × 时长」世界单位，换算成弧线比例并保留下限
            arcTailFraction = arcHorizontalLength > 0.0001f
                ? Mathf.Clamp(length / arcHorizontalLength, ArcMinTailFraction, 1f)
                : 1f;

            ComputeAnalyticArcBounds();
        }

        // CE 弧线：直接用采样得到的绘制位置序列参数化。
        // 头尾窗口参数化沿"绘制路径长度"（与 traveled 的绘制口径一致），因此
        // head = traveled / arcHorizontalLength 时头部精确落在弹体当前绘制位置。
        // 返回 false 表示采样点退化（不足两个或无路径跨度），调用方应回退解析抛物线。
        bool InitializeArcFromSamples(List<Vector3> samples)
        {
            ceArcSamples = samples;
            if (!RebuildSampledArcLengths()) return false;

            // 弧线参数化分母 = 绘制路径总长（注意：此时 traveled 按绘制位置累计，口径一致）
            arcHorizontalLength = ceArcTotalLen;
            arcSide = new Vector3(-dir.z, 0f, dir.x);

            // 尾部长度与直线轨迹同为「弹速 × 时长」世界单位，换算成弧线比例并保留下限
            arcTailFraction = arcHorizontalLength > 0.0001f
                ? Mathf.Clamp(length / arcHorizontalLength, ArcMinTailFraction, 1f)
                : 1f;

            ComputeSampledArcBounds(samples);
            return true;
        }

        // 由采样点重建绘制路径累计长度；总跨度为零时返回 false
        bool RebuildSampledArcLengths()
        {
            if (ceArcSamples == null || ceArcSamples.Count < 2) return false;

            ceArcCumLen = new float[ceArcSamples.Count];
            float total = 0f;
            for (int i = 1; i < ceArcSamples.Count; i++)
            {
                total += Vector3.Distance(ceArcSamples[i - 1], ceArcSamples[i]);
                ceArcCumLen[i] = total;
            }
            ceArcTotalLen = total;
            return total > 0.01f;
        }

        // 解析抛物线的包围盒（原版与 CE 采样失败回退共用）。
        // 固定包围盒覆盖整条弧线：免去逐帧重算，同时保证不被错误剔除。
        void ComputeAnalyticArcBounds()
        {
            Vector3 mid = (a + dir * (arcHorizontalLength * 0.5f)) + ArcUp * (arcHeight * 0.5f);
            // 等价于原先的 Bounds(mid, size)：half = size * 0.5
            Vector3 half = new Vector3(
                (Mathf.Abs(dir.x) * arcHorizontalLength + Mathf.Abs(ArcUp.x) * arcHeight) * 0.5f + width + 1f,
                Mathf.Abs(ArcUp.y) * arcHeight * 0.5f + 1f,
                (Mathf.Abs(dir.z) * arcHorizontalLength + Mathf.Abs(ArcUp.z) * arcHeight) * 0.5f + width + 1f);
            arcAabbMin = mid - half;
            arcAabbMax = mid + half;
        }

        // CE 采样点列的包围盒
        void ComputeSampledArcBounds(List<Vector3> samples)
        {
            if (samples.Count == 0) return;

            Vector3 lo = samples[0];
            Vector3 hi = samples[0];
            for (int i = 1; i < samples.Count; i++)
            {
                lo = Vector3.Min(lo, samples[i]);
                hi = Vector3.Max(hi, samples[i]);
            }
            // 等价于原先的 Bounds((lo+hi)/2, hi-lo+pad)：即外扩 pad 的一半
            Vector3 pad = new Vector3(width + 1f, 1f, width + 1f);
            arcAabbMin = lo - pad;
            arcAabbMax = hi + pad;
        }
    }
}
