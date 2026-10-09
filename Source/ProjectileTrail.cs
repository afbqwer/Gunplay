using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 投射物轨迹。直线投射物与弧线投射物共用同一个 Thing、同一条生成路径和同一份配置，
    // 只在“绘制”这一层分开：
    //   * 直线投射物（多数）沿用原来的单四边形绘制（MeshPool.plane10）；
    //   * 弧线投射物（手雷/炮弹等少数）按 Vanilla 抛物线采样，绘制一条跟随弧线弯曲的带状网格。
    // 弧线相关的几何与顶点缓冲只在 arcing == true 时才创建/更新，因此多数情况的性能不受影响。
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
        Projectile projectile;
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

        public void Initialize(Projectile proj, Vector3 destination, Thing equipment)
        {
            ticksUtilDeath = -1;
            ticksIdle = 0;
            projectile = proj;
            a = projectile.ExactPosition;
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
                GraphicData graphicData = projectile.def.graphicData;
                width = (graphicData != null ? graphicData.drawSize.x : 1f) * 0.1f;
            }
            else
            {
                width = Mathf.Min(projectile.DamageAmount, 50f) * 0.006f;
            }

            ProjectileTrailDef trailDef = def as ProjectileTrailDef;
            if (trailDef != null && trailDef.trailWidth != -1) width = trailDef.trailWidth;

            width *= Gunplay.settings.trailWidthFactor;

            // 只有弧线投射物才需要额外的弧线几何；直线投射物到此为止，不执行任何弧线代码
            arcing = ProjectileRules.IsArcing(proj.def);
            if (arcing)
            {
                InitializeArc(proj, b);
            }
        }

        // 预计算弧线几何参数（顶点缓冲在绘制时按可见段重建）
        void InitializeArc(Projectile proj, Vector3 b)
        {
            Vector3 delta = b - a;
            delta.y = 0f;
            arcHorizontalLength = delta.magnitude;
            arcSide = new Vector3(-dir.z, 0f, dir.x);

            // 与 Vanilla Projectile.DrawAt 完全一致的弧高取值（含基于距离的收缩）
            arcHeight = proj.def.projectile.arcHeightFactor;
            float distSq = delta.sqrMagnitude;
            if (arcHeight * arcHeight > distSq * 0.04f)
            {
                arcHeight = Mathf.Sqrt(distSq) * 0.2f;
            }

            // 尾部长度与直线轨迹同为 speed * 15 世界单位，换算成弧线比例并保留下限
            arcTailFraction = arcHorizontalLength > 0.0001f
                ? Mathf.Clamp(length / arcHorizontalLength, ArcMinTailFraction, 1f)
                : 1f;

            BuildArcMesh();
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

            // 读档后弧线 Mesh 与缓冲需要重建
            if (Scribe.mode == LoadSaveMode.PostLoadInit && arcing)
            {
                BuildArcMesh();
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
                    // 只在投射物存活时读取位置；销毁后不再访问它
                    ApplyPosition(projectile.ExactPosition);
                }
            }

            previousPosition = exactPosition;

            IntVec3 cell = exactPosition.ToIntVec3();
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

            for (int i = 0; i <= ArcSegmentCount; i++)
            {
                float f = i / (float)ArcSegmentCount;
                float t = Mathf.Lerp(t0, t1, f);
                float height = arcHeight * 4f * t * (1f - t);

                Vector3 center = a + dir * (arcHorizontalLength * t) + ArcUp * height;

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

            // 固定包围盒覆盖整条弧线：免去逐帧 RecalculateBounds，同时保证不被错误剔除。
            // 弧线沿 ArcUp（+Z）鼓起，包围盒须相应扩张。
            Vector3 mid = (a + dir * (arcHorizontalLength * 0.5f)) + ArcUp * (arcHeight * 0.5f);
            Vector3 size = new Vector3(
                Mathf.Abs(dir.x) * arcHorizontalLength + Mathf.Abs(ArcUp.x) * arcHeight + width * 2f + 2f,
                Mathf.Abs(ArcUp.y) * arcHeight + 2f,
                Mathf.Abs(dir.z) * arcHorizontalLength + Mathf.Abs(ArcUp.z) * arcHeight + width * 2f + 2f);
            arcBounds = new Bounds(mid, size);

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
