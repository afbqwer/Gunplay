using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Gunplay.Patch;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 投射物轨迹。直线投射物与弧线投射物共用同一个 Thing、同一条生成路径和同一份配置，
    // 只在“绘制”这一层分开：
    //   * 直线投射物（多数）沿用原来的单四边形绘制（MeshPool.plane10）；
    //   * 弧线投射物（手雷/炮弹等少数）按 Vanilla 抛物线采样，绘制一条跟随弧线弯曲的带状网格。
    // 弧线相关的几何与顶点缓冲只在 arcing == true 时才创建/更新，因此多数情况的性能不受影响。
    // 兼容说明：原版与 CE（Combat Extended）投射物共用本类。CE 的 ProjectileCE 不是 Projectile
    // 子类，因此 projectile 字段声明为 Thing，实时位置通过 exactPositionGetter 读取
    // （原版走类型转换，CE 走 PatchCECompat 的反射读取器；读档后重建）。
    class ProjectileTrail : ThingWithComps
    {
        // ---------------- 弧线绘制参数 ----------------
        // 弧线采样段数：越大越平滑，代价线性增长。弧线投射物稀少，取适中值即可。
        const int ArcSegmentCount = 16;
        static readonly int ArcVertexCount = (ArcSegmentCount + 1) * 2;

        // 尾部至少占整段弧线的比例，避免低速抛射物（如手雷）的轨迹过短而不明显。
        const float ArcMinTailFraction = 0.1f;

        // 弧线的“上升”方向。RimWorld 的地图相机是正交俯视（沿 -Y 观察），
        // 世界 +Y 只承担深度/排序，在屏幕上不可见；屏幕竖直方向对应世界 +Z（北）。
        // 必须与 Vanilla 保持一致：Projectile.DrawAt 用 (0,0,1)，PawnFlyer 用 Vector3.forward。
        static readonly Vector3 ArcUp = new Vector3(0f, 0f, 1f);

        Matrix4x4 drawingMatrix = default;

        int ticksUtilDeath;
        int ticksIdle;
        Thing projectile;
        Func<Vector3> exactPositionGetter;
        Vector3 a;
        Vector3 exactPosition;
        Vector3 previousPosition;
        float speed;
        Vector3 dir;
        float length;
        float width;
        float traveled;
        float altitude;
        float ticksPerLength;

        // ---------------- 弧线轨迹专用状态（仅 arcing == true 时使用） ----------------
        bool arcing;
        Vector3 arcSide;
        float arcHorizontalLength;
        float arcHeight;
        float arcTailFraction;
        Mesh arcMesh;
        Bounds arcBounds;
        Vector3[] arcVertices;
        Vector2[] arcUvs;
        int[] arcTriangles;

        // ---------------- CE 专用状态 ----------------
        // CE 投射物（直线与弧线）每 tick 都跟踪“绘制位置”（含 CE 的高度屏幕偏移），
        // 使拖尾/弧线贴着弹体而不是地面投影。采样失败回退解析弧线时退回地面口径。
        bool ceTrackDrawnPosition;
        // 弧线 CE 投射物：发射时按 CE 绘制映射采样的整条弹道绘制点（世界坐标，y 为 altitude 层）
        List<Vector3> ceArcSamples;
        float[] ceArcCumLen;   // 采样点间绘制路径的累计长度（与 traveled 的绘制口径一致）
        float ceArcTotalLen;

        // 按投射物类型构建实时位置读取器；原版投射物直接类型转换，CE 走反射
        static Func<Vector3> BuildPositionGetter(Thing proj)
        {
            Projectile vanilla = proj as Projectile;
            if (vanilla != null) return () => vanilla.ExactPosition;
            return PatchCECompat.CreateExactPositionGetter(proj);
        }

        public void Initialize(Projectile proj, Vector3 destination, Thing equipment)
        {
            ticksUtilDeath = -1;
            ticksIdle = 0;
            projectile = proj;
            exactPositionGetter = BuildPositionGetter(proj);
            a = proj.ExactPosition;
            Vector3 b = destination;
            altitude = proj.def.Altitude;
            a.y = b.y = altitude;
            speed = proj.def.projectile.SpeedTilesPerTick;
            exactPosition = a;
            previousPosition = a;
            traveled = 0f;

            length = speed * 15f;
            ticksPerLength = speed > 0f ? length / speed : 0f;
            dir = (b - a).normalized;
            if (Gunplay.settings.trailWidthMode == TrailWidthMode.Texture)
            {
                // 投射物贴图经 GridPlane(drawSize) 旋转绘制，drawSize.x 为垂直于飞行方向的横向宽度
                GraphicData graphicData = proj.def.graphicData;
                width = (graphicData != null ? graphicData.drawSize.x : 1f) * 0.1f;
            }
            else
            {
                width = Mathf.Min(proj.DamageAmount, 50f) * 0.006f;
            }

            ProjectileTrailDef trailDef = def as ProjectileTrailDef;
            if (trailDef != null && trailDef.trailWidth != -1) width = trailDef.trailWidth;

            width *= Gunplay.settings.trailWidthFactor;

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
        public void InitializeCE(Thing proj, Vector3 destination, Thing equipment)
        {
            ticksUtilDeath = -1;
            ticksIdle = 0;
            projectile = proj;
            exactPositionGetter = BuildPositionGetter(proj);
            a = exactPositionGetter();
            Vector3 b = destination;
            altitude = proj.def.Altitude;
            a.y = b.y = altitude;
            speed = proj.def.projectile.SpeedTilesPerTick;
            exactPosition = a;
            previousPosition = a;
            traveled = 0f;

            // CE 弹速是真实弹道速度（子弹一 tick 可飞数格），沿用 speed * 15 会拉出十余格的
            // “激光束”，CE 路径对拖尾长度做上限截断。
            length = Mathf.Min(speed * 15f, PatchCECompat.MaxTrailLength);
            ticksPerLength = speed > 0f ? length / speed : 0f;
            dir = (b - a).normalized;
            if (Gunplay.settings.trailWidthMode == TrailWidthMode.Texture)
            {
                GraphicData graphicData = proj.def.graphicData;
                width = (graphicData != null ? graphicData.drawSize.x : 1f) * 0.1f;
            }
            else
            {
                width = Mathf.Min(PatchCECompat.GetDamageAmount(proj), 50f) * 0.006f;
            }

            ProjectileTrailDef trailDef = def as ProjectileTrailDef;
            if (trailDef != null && trailDef.trailWidth != -1) width = trailDef.trailWidth;

            width *= Gunplay.settings.trailWidthFactor;

            // CE 轨迹样式由设置开关决定（ceStraightTrails，默认开启）：
            //   * 开启 → CE 投射物绘制直线拖尾（跟随弹体绘制位置）；抛射类投射物
            //     （出射仰角超过 ArcAngleThreshold，如迫击炮弹/榴弹）在入口处被拦截，不显示轨迹；
            //   * 关闭 → 全部 CE 投射物绘制弧线轨迹：按 CE 自己的绘制映射（worker.ExactPosToDrawPos）
            //     采样整条弹道的绘制位置构建带状网格。
            // 两种模式的 traveled 都按绘制位置累计，与采样点列的路径长度同口径，
            // 因此弧线头部始终精确落在弹体当前绘制位置上。
            // 弧线采样失败时回退为地面解析抛物线（原版算法，弧高 tan(θ)*L/4），
            // 此时跟踪口径退回地面，保持 traveled 与弧线参数化一致。
            float shotAngle = PatchCECompat.GetShotAngle(proj);
            arcing = !Gunplay.settings.ceStraightTrails;
            ceTrackDrawnPosition = true;
            if (arcing)
            {
                List<Vector3> samples = PatchCECompat.PredictDrawPositions(proj, PatchCECompat.GetStartingTicksToImpact(proj));
                if (samples == null || !InitializeArcFromSamples(samples))
                {
                    ceTrackDrawnPosition = false;
                    InitializeArc(b, Mathf.Tan(shotAngle) * (b - a).magnitude / 4f);
                }
            }
        }

        // 预计算弧线几何参数（顶点缓冲在绘制时按可见段重建）。
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

            // 尾部长度与直线轨迹同为 speed * 15 世界单位，换算成弧线比例并保留下限
            arcTailFraction = arcHorizontalLength > 0.0001f
                ? Mathf.Clamp(length / arcHorizontalLength, ArcMinTailFraction, 1f)
                : 1f;

            ComputeAnalyticArcBounds();
            BuildArcMesh();
        }

        // CE 弧线：直接用采样得到的绘制位置序列构建带状网格几何。
        // 头尾窗口参数化沿“绘制路径长度”（与 traveled 的绘制口径一致），因此
        // head = traveled / arcHorizontalLength 时头部精确落在弹体当前绘制位置。
        // 返回 false 表示采样点退化（不足两个或无路径跨度），调用方应回退解析抛物线。
        bool InitializeArcFromSamples(List<Vector3> samples)
        {
            ceArcSamples = samples;
            if (!RebuildSampledArcLengths()) return false;

            // 弧线参数化分母 = 绘制路径总长（注意：此时 traveled 按绘制位置累计，口径一致）
            arcHorizontalLength = ceArcTotalLen;
            arcSide = new Vector3(-dir.z, 0f, dir.x);

            // 尾部长度与直线轨迹同为 speed * 15 世界单位，换算成弧线比例并保留下限
            arcTailFraction = arcHorizontalLength > 0.0001f
                ? Mathf.Clamp(length / arcHorizontalLength, ArcMinTailFraction, 1f)
                : 1f;

            ComputeSampledArcBounds();
            BuildArcMesh();
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

        // 解析抛物线的包围盒（原版与 CE 采样失败回退共用）
        void ComputeAnalyticArcBounds()
        {
            // 固定包围盒覆盖整条弧线：免去逐帧 RecalculateBounds，同时保证不被错误剔除。
            // 弧线沿 ArcUp（+Z）鼓起，包围盒须相应扩张。
            Vector3 mid = (a + dir * (arcHorizontalLength * 0.5f)) + ArcUp * (arcHeight * 0.5f);
            Vector3 size = new Vector3(
                Mathf.Abs(dir.x) * arcHorizontalLength + Mathf.Abs(ArcUp.x) * arcHeight + width * 2f + 2f,
                Mathf.Abs(ArcUp.y) * arcHeight + 2f,
                Mathf.Abs(dir.z) * arcHorizontalLength + Mathf.Abs(ArcUp.z) * arcHeight + width * 2f + 2f);
            arcBounds = new Bounds(mid, size);
        }

        // CE 采样点列的包围盒
        void ComputeSampledArcBounds()
        {
            Vector3 min = ceArcSamples[0];
            Vector3 max = ceArcSamples[0];
            for (int i = 1; i < ceArcSamples.Count; i++)
            {
                min = Vector3.Min(min, ceArcSamples[i]);
                max = Vector3.Max(max, ceArcSamples[i]);
            }
            Vector3 pad = new Vector3(width * 2f + 2f, 2f, width * 2f + 2f);
            arcBounds = new Bounds((min + max) * 0.5f, max - min + pad);
        }


        public override void ExposeData()
        {
            base.ExposeData();

            Scribe_References.Look(ref projectile, "projectile");
            Scribe_Values.Look(ref a, "a");
            Scribe_Values.Look(ref exactPosition, "exactPosition");
            Scribe_Values.Look(ref previousPosition, "previousPosition");
            Scribe_Values.Look(ref speed, "speed");
            Scribe_Values.Look(ref dir, "dir");
            Scribe_Values.Look(ref length, "length");
            Scribe_Values.Look(ref width, "width");
            Scribe_Values.Look(ref traveled, "traveled");
            Scribe_Values.Look(ref altitude, "altitude");
            Scribe_Values.Look(ref ticksPerLength, "ticksPerLength");
            Scribe_Values.Look(ref ticksIdle, "ticksIdle");
            Scribe_Values.Look(ref ticksUtilDeath, "ticksUtilDeath");

            Scribe_Values.Look(ref arcing, "arcing");
            Scribe_Values.Look(ref arcSide, "arcSide");
            Scribe_Values.Look(ref arcHorizontalLength, "arcHorizontalLength");
            Scribe_Values.Look(ref arcHeight, "arcHeight");
            Scribe_Values.Look(ref arcTailFraction, "arcTailFraction");
            Scribe_Values.Look(ref ceTrackDrawnPosition, "ceTrackDrawn");
            Scribe_Collections.Look(ref ceArcSamples, "ceArcSamples", LookMode.Value);

            // 读档后：实时位置读取器与弧线 Mesh 都需要重建；
            // CE 采样点列还在时按采样重建（累计长度由采样点重算，口径不变），否则（旧存档）回退解析
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (projectile != null) exactPositionGetter = BuildPositionGetter(projectile);
                if (arcing)
                {
                    if (ceArcSamples != null && RebuildSampledArcLengths())
                    {
                        ComputeSampledArcBounds();
                    }
                    else
                    {
                        ceArcSamples = null;
                        ComputeAnalyticArcBounds();
                    }
                    BuildArcMesh();
                }
            }
        }

        public override Vector3 DrawPos
        {
            get
            {
                return exactPosition;
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

        public void CaptureFinalPosition(Vector3 pos)
        {
            if (projectile == null) return;

            ApplyPosition(pos);
            projectile = null;
            ticksUtilDeath = Mathf.CeilToInt(ticksPerLength);
        }

        // CE 投射物销毁前锁定最终位置（PatchCECompat.ImpactPrefix 调用）。
        // 取位置口径与飞行中一致：直线取绘制位置，弧线取地面投影。
        public void CaptureFinalPositionCE(Thing proj)
        {
            if (projectile == null) return;
            CaptureFinalPosition(ceTrackDrawnPosition ? PatchCECompat.GetDrawPosition(proj) : exactPositionGetter());
        }

        protected override void Tick()
        {
            base.Tick();

            if (projectile != null)
            {
                if (projectile.Destroyed || (ticksUtilDeath == -1 && ticksIdle > 10))
                {
                    projectile = null;
                    ticksUtilDeath = Mathf.CeilToInt(ticksPerLength);
                }
                else
                {
                    // 只在投射物存活时读取位置；销毁后不再访问它。
                    // CE 直线投射物跟踪“绘制位置”（含高度屏幕偏移），其余跟踪地面投影。
                    ApplyPosition(ceTrackDrawnPosition ? PatchCECompat.GetDrawPosition(projectile) : exactPositionGetter());
                }
            }

            previousPosition = exactPosition;

            IntVec3 cell = exactPosition.ToIntVec3();
            // CE 直线模式跟踪的绘制位置带高度屏幕偏移（z 方向），地图边缘附近可能越界；
            // Position 仅用于网格注册/剔除，钳制到地图范围内，绘制用的 exactPosition 不受影响
            Map trailMap = Map;
            if (trailMap != null)
            {
                cell.x = Mathf.Clamp(cell.x, 0, trailMap.Size.x - 1);
                cell.z = Mathf.Clamp(cell.z, 0, trailMap.Size.z - 1);
            }
            if (cell != Position)
                Position = cell;

            if (ticksUtilDeath > 0)
                ticksUtilDeath--;
            else if (ticksUtilDeath == 0)
                Destroy(DestroyMode.Vanish);
        }

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            // 绘制在这一层分开：直线走原实现，弧线走弯曲带状网格
            if (arcing)
                DrawArcTrail();
            else
                DrawStraightTrail();

            Comps_PostDraw();
        }

        void DrawStraightTrail()
        {
            if (length <= 0f || dir.sqrMagnitude < 0.000001f)
            {
                return;
            }

            float len = Mathf.Min(traveled, length);
            if (ticksUtilDeath != -1)
                len *= ticksUtilDeath / ticksPerLength;

            if (len <= 0f)
            {
                return;
            }

            Vector3 drawPos = exactPosition - dir * len / 2;
            Vector3 drawingScale = new Vector3(width, 1f, len);
            drawingMatrix.SetTRS(drawPos, Quaternion.LookRotation(dir), drawingScale);
            Graphics.DrawMesh(MeshPool.plane10, drawingMatrix, Graphic.MatSingle, 0);
        }

        void DrawArcTrail()
        {
            if (arcMesh == null || arcHorizontalLength <= 0.0001f)
            {
                return;
            }

            // traveled 即沿水平飞行方向已经过的距离，可直接换算成弧线比例
            float head = Mathf.Clamp01(traveled / arcHorizontalLength);
            float tail = head - arcTailFraction;

            if (ticksUtilDeath != -1)
            {
                // 投射物已消失：尾部向前收缩，模拟淡出（与直线轨迹一致）
                float k = ticksPerLength > 0f ? Mathf.Clamp01(ticksUtilDeath / ticksPerLength) : 0f;
                tail = head - (head - tail) * k;
            }

            tail = Mathf.Clamp01(tail);
            if (head - tail <= 0.0001f)
            {
                return;
            }

            UpdateArcRibbon(tail, head);
            Graphics.DrawMesh(arcMesh, Matrix4x4.identity, Graphic.MatSingle, 0);
        }

        // 在固定缓冲上重建可见部分的带状网格（顶点为世界坐标，故使用单位矩阵绘制）
        void UpdateArcRibbon(float t0, float t1)
        {
            float halfWidth = width * 0.5f;

            // CE 采样点列存在时沿绘制路径取点，否则按解析抛物线取点
            int seg = 0;
            for (int i = 0; i <= ArcSegmentCount; i++)
            {
                float f = i / (float)ArcSegmentCount;
                float t = Mathf.Lerp(t0, t1, f);

                Vector3 center;
                if (ceArcSamples != null)
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

                int v = i * 2;
                arcVertices[v] = center - arcSide * halfWidth;
                arcVertices[v + 1] = center + arcSide * halfWidth;

                arcUvs[v] = new Vector2(0f, f);
                arcUvs[v + 1] = new Vector2(1f, f);
            }

            arcMesh!.vertices = arcVertices;
            arcMesh.uv = arcUvs;
            // 顶点为世界坐标，重新赋值后显式回写缓存包围盒，保证剔除判定稳定
            arcMesh.bounds = arcBounds;
        }

        void BuildArcMesh()
        {
            if (arcVertices == null) arcVertices = new Vector3[ArcVertexCount];
            if (arcUvs == null) arcUvs = new Vector2[ArcVertexCount];
            if (arcTriangles == null)
            {
                arcTriangles = new int[ArcSegmentCount * 6];
                for (int i = 0; i < ArcSegmentCount; i++)
                {
                    int v = i * 2;
                    int t = i * 6;
                    arcTriangles[t + 0] = v + 0;
                    arcTriangles[t + 1] = v + 1;
                    arcTriangles[t + 2] = v + 3;
                    arcTriangles[t + 3] = v + 0;
                    arcTriangles[t + 4] = v + 3;
                    arcTriangles[t + 5] = v + 2;
                }
            }

            // 包围盒由调用方预先算好（解析或按 CE 采样点），见 ComputeAnalyticArcBounds / ComputeSampledArcBounds
            if (arcMesh != null)
            {
                arcMesh.bounds = arcBounds;
                return;
            }

            arcMesh = new Mesh { name = "GunplayArcProjectileTrail" };
            arcMesh.MarkDynamic();
            arcMesh.vertices = arcVertices;
            arcMesh.uv = arcUvs;
            arcMesh.SetTriangles(arcTriangles, 0);

            // 法线统一朝上：轨迹近似水平，Mote 着色器不参与光照，写死一次即可
            Vector3[] normals = new Vector3[ArcVertexCount];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            arcMesh.normals = normals;

            arcMesh.bounds = arcBounds;
        }

        public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
        {
            if (arcMesh != null)
            {
                UnityEngine.Object.Destroy(arcMesh);
                arcMesh = null;
            }
            base.Destroy(mode);
        }

    }
}
