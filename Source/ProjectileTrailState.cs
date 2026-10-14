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

    // 顶点/UV/索引的写入缓冲：预分配数组 + 手写计数，取代 List<T>。
    //
    // 为什么不用 List<T>：List.Add 每次都要比一次容量、自增一次 _version，再对底层数组做一次
    // 带边界检查的写入——在"每条轨迹 × 每个顶点"的路径上是纯固定开销，弹幕密集时正好是热点。
    //
    // 这里只提供 Ensure（一次性预留容量，仅在不足时扩容），顶点/索引的写入由调用方用局部数组
    // 引用直接完成（见 AppendStraightGeometry / AppendArcGeometry）。之所以不再提供逐顶点的
    // AddVert/AddTri：Mono 不内联这类带分支的小方法，逐顶点一次调用本身就是可观的固定开销，
    // 写成真方法反而把方法调用搬到了热路径上。
    //
    // 顶点与 UV 一一对应，共用 vertCount；三角形索引单独 triCount。容量跨帧保留
    //（MeshGroup.Reset 只清计数），稳态下扩容基本不再发生。
    internal struct GeometryBuffer
    {
        internal Vector3[] verts;
        internal Vector2[] uvs;
        internal int[] tris;
        internal int vertCount;
        internal int triCount;

        internal GeometryBuffer(int initialVerts)
        {
            verts = new Vector3[initialVerts];
            uvs = new Vector2[initialVerts];
            // 索引数上限约是顶点数的 3 倍（弧线每段 2 顶点 / 6 索引，最坏趋近 3.0），一次留足
            tris = new int[initialVerts * 3];
            vertCount = 0;
            triCount = 0;
        }

        // 清空计数、保留数组容量，供下一帧复用
        internal void Clear()
        {
            vertCount = 0;
            triCount = 0;
        }

        // 为"接下来要写 extraVerts 个顶点、extraTris 个索引"预留容量，仅在不足时按 2 倍扩容。
        // 每条轨迹只调一次（不是每个顶点一次），调用开销可忽略。
        internal void Ensure(int extraVerts, int extraTris)
        {
            int needV = vertCount + extraVerts;
            if (needV > verts.Length)
            {
                int cap = verts.Length == 0 ? 64 : verts.Length;
                while (cap < needV) cap *= 2;
                Array.Resize(ref verts, cap);
                Array.Resize(ref uvs, cap);
            }

            int needT = triCount + extraTris;
            if (needT > tris.Length)
            {
                int cap = tris.Length == 0 ? 192 : tris.Length;
                while (cap < needT) cap *= 2;
                Array.Resize(ref tris, cap);
            }
        }
    }

    internal class ProjectileTrailState
    {
        // 当前生效段数（全局设置 + 夹取）。夹取放在这里，设置项即使被外部改成越界值也不会炸几何。
        // 代价是每次求值都走一遍属性 + Mathf.Clamp，所以调用方（AppendArcGeometry）先取一次存局部再用。
        static int ArcSegmentCount => Mathf.Clamp(GunplaySettings.arcSegmentCount, ArcSegments.Min, ArcSegments.Max);

        // 尾部至少占整段弧线的比例，避免低速抛射物（如手雷）的轨迹过短而不明显。
        const float ArcMinTailFraction = 0.1f;

        // 运行时路径采样的数量上限。超出即丢弃最旧的一半并重建累计长度（见 AppendPathSample），
        // 均摊 O(1)。512 已覆盖「拖尾长度系数拉满 + 弹速缩放拉到最小」之外的一切配置；
        // 真出现那种病态组合时轨迹会短于设定值，但依然完整可画（见该方法注释）。
        const int MaxPathSamples = 512;

        // 自动检测曲线投射物的偏离阈值（格）：当前位置相对「发射点 + 发射方向」的垂直距离。
        // 原版投射物严格沿发射直线飞行——ExactPosition 就是 origin→destination 的线性插值，
        // 弧线只是绘制效果（见 Verse.Projectile.ExactPosition），偏离量恒为 0；
        // 自定义弹道的 mod 投射物（追踪弹、波浪弹等）会持续偏离。
        // 阈值远大于浮点噪声（位置分量可达数百格，float 精度约 1e-4 格），也小于一格——
        // 肉眼可辨的弯曲才触发，轻微的绘制偏移不会误判。
        const float CurveDetectThreshold = 0.2f;

        // 弧线的"上升"方向。RimWorld 的地图相机是正交俯视（沿 -Y 观察），
        // 世界 +Y 只承担深度/排序，在屏幕上不可见；屏幕竖直方向对应世界 +Z（北）。
        // 必须与 Vanilla 保持一致：Projectile.DrawAt 用 (0,0,1)，PawnFlyer 用 Vector3.forward。
        static readonly Vector3 ArcUp = new Vector3(0f, 0f, 1f);

        // 直线四边形四角的 UV（顺序与 plane10 一致，v 从尾到头）。做成静态常量直接复用：
        // 这些值固定不变，没必要在逐条轨迹的热路径上反复构造 Vector2。
        static readonly Vector2 UvTailLeft = new Vector2(0f, 0f);
        static readonly Vector2 UvHeadLeft = new Vector2(0f, 1f);
        static readonly Vector2 UvHeadRight = new Vector2(1f, 1f);
        static readonly Vector2 UvTailRight = new Vector2(1f, 0f);

        // 深度微调。轨迹与投射物同处 Projectile 高度层（同一个 Y），深度测试无法区分先后，
        // 平局由同深度下的绘制规则决定；让轨迹 Y 略小于投射物即可稳定地"轨迹在弹体之下"。
        // 取值远小于层间距 0.3658，不会跨层，也不会影响与 Pawn/建筑/微粒的相对关系。
        internal const float DepthEpsilon = -0.002f;

        // 归属信息
        internal ProjectileTrailDef def = null!;   // 材质来源；由 Initialize* 首个赋值，之后恒非空
        internal Color color = Color.white;        // 轨迹颜色，登记时按 TrailColor 口径解析一次（覆写 > def 颜色）
        internal Thing? LookupKey;                 // 登记时的投射物引用（捕获后仍保留），用于从集合移除
        internal bool Dead;                        // 生命周期结束，由集合回收

        // 本轨迹所属的绘制分组，登记时由 ProjectileTrailBatcher.ResolveGroup 解析一次。
        // 之后每帧的 Draw 直接用，省掉「材质解析 + (材质,颜色) 字典查找」这套逐帧固定开销。
        // null 表示无 def / 无材质，不绘制。
        internal ProjectileTrailBatcher.MeshGroup? drawGroup;

        // ---------------- 直线/通用状态 ----------------
        int ticksUtilDeath;
        // 连续没读到位置变化的 tick 数（移动即清零）。判定「投射物停住」的唯一依据，
        // 阈值口径见 StationaryTicksBeforeDrop。
        int ticksIdle;
        Thing? projectile;
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
        // 直线轨迹相对头部可能延伸的最大水平距离（length + width）。
        // 作为以头部为中心的保守 AABB 半边长用于剔除：必然包住整条轨迹，绝不会漏。
        float maxReach;

        // ---------------- 运行时路径跟踪（自动检测曲线投射物 / 强制曲线绘制的非弧线投射物）----------------
        // 非弧线投射物没有可信的解析弧高（arcHeightFactor 为 0，InitializeArc 会退化成直线），
        // 「曲线绘制」只能沿实际飞行路径取点——与 CE 的采样带状网格（pathSamples）是同一套机制，
        // 区别只在采样时机：CE 在 Launch 时按绘制映射一次预测整条弹道（见 InitializeCE），
        // 这里在发射后逐 tick 记录真实位置（见 AppendPathSample）。
        bool pathTracking;      // 是否正在记录飞行路径（发射时决定，之后不再关闭）
        Vector3 trackDir;       // 发射方向：偏离检测的基准（dir 每 tick 被实际位移覆盖，必须另存）

        // ---------------- CE 专用状态 ----------------
        // CE 投射物（直线与弧线）每 tick 都跟踪"绘制位置"（含 CE 的高度屏幕偏移），
        // 使拖尾/弧线贴着弹体而不是地面投影。采样失败回退解析弧线时退回地面口径。
        bool ceTrackDrawnPosition;
        // 该 def 的绘制映射器（worker 实例 + 它 ExactPosToDrawPos 的直调委托）。
        // 两者都只由 def 决定，在 InitializeCE 里解析一次存这儿；Tick 直接用，不再每 tick
        // 反射取 worker、也不再每 tick 查一次「worker 类型 → 委托」的字典（见 PatchCECompat.ResolveDrawMapper）。
        object? ceWorker;
        PatchCECompat.DrawPosAccessor? ceDrawAccessor;
        // 弧线轨迹的采样点列（绘制坐标，y 为 altitude 层）。两个来源：
        //   * CE —— 发射时按 CE 绘制映射预测的整条弹道（见 InitializeCE / InitializeArcFromSamples）；
        //   * 原版非弧线投射物 —— 自动检测或强制曲线时逐 tick 记录的真实飞行路径
        //     （见 AppendPathSample）。
        // null = 未/无法采样，按解析抛物线取点（pathTotalLen 同样可能仍为 null）
        List<Vector3>? pathSamples;
        float[]? pathCumLen;   // 采样点间绘制路径的累计长度（与 traveled 的绘制口径一致）
        float pathTotalLen;

        // ---------------- 初始化 ----------------

        // 原版投射物入口
        public void Initialize(Projectile proj, Vector3 destination, in TrailResolve r)
        {
            def = r.trailDef!;
            color = TrailColor.Effective(r);
            ticksUtilDeath = -1;
            ticksIdle = 0;
            projectile = proj;
            a = proj.ExactPosition;
            Vector3 b = destination;
            altitude = proj.def.Altitude + DepthEpsilon;
            a.y = b.y = altitude;
            speed = proj.def.projectile.SpeedTilesPerTick;
            exactPosition = a;
            previousPosition = a;
            traveled = 0f;

            // 拖尾长度（格）与淡出时长（tick）是两个独立的量，不再互相派生，见 TrailLength / TrailFade
            length = speed * TrailLength.Effective(r);
            fadeTicks = TrailFade.Effective(r);
            dir = (b - a).normalized;
            ComputeWidth(r, proj.def, proj.DamageAmount);
            maxReach = length + width;

            // 是否曲线绘制，见 ProjectileRules.CurveEnabled（单项覆写 > 自动检测 > 默认规则）。
            // 只有原版弧线投射物有可信的解析弧高（arcHeightFactor）；强制曲线、或自动检测
            // 命中的非弧线投射物没有弧高可解析，改为沿实际飞行路径采样绘制（与 CE 同一套
            // 带状网格）。自动检测开启时，按直线绘制的投射物也先记下路径，待偏离时切换。
            arcing = ProjectileRules.CurveEnabled(proj.def);
            if (arcing)
            {
                if (ProjectileRules.IsArcing(proj.def))
                {
                    // 与 Vanilla Projectile.DrawAt 一致的弧高取值（收缩在 InitializeArc 内统一处理）
                    InitializeArc(b, proj.def.projectile.arcHeightFactor);
                }
                else
                {
                    BeginPathTracking();
                }
            }
            else if (GunplaySettings.autoDetectCurvedProjectiles && !ProjectileOverrides.HasCurveOverride(r.over))
            {
                // 按默认规则画直线时才参与自动检测：用户显式覆写过绘制方式（无论曲线还是直线）
                // 的，运行时观测不该改写它——强制直线就是要直线。
                // 判定直接读本发已解析的覆写记录（r.over），不再按 defName 查一遍字典。
                BeginPathTracking();
            }
        }

        // CE（Combat Extended）投射物入口。ProjectileCE 不是 Projectile 子类，位置/伤害/
        // 出射仰角等信息全部经由 PatchCECompat 的反射读取。
        public void InitializeCE(Thing proj, Vector3 destination, in TrailResolve r)
        {
            def = r.trailDef!;
            color = TrailColor.Effective(r);
            ticksUtilDeath = -1;
            ticksIdle = 0;
            projectile = proj;
            a = PatchCECompat.GetExactPosition(proj);
            // 绘制映射器只由 def 决定：这一发解析一次，之后每 tick 直接用（见字段注释）
            PatchCECompat.ResolveDrawMapper(proj.def.projectile, out ceWorker, out ceDrawAccessor);
            Vector3 b = destination;
            altitude = proj.def.Altitude + DepthEpsilon;
            a.y = b.y = altitude;
            speed = proj.def.projectile.SpeedTilesPerTick;
            exactPosition = a;
            previousPosition = a;
            traveled = 0f;

            // CE 弹速是真实弹道速度（子弹一 tick 可飞数格），沿用「弹速 × 长度系数」会拉出十余格的
            // "激光束"，CE 路径对拖尾长度做上限截断。注意这只截长度，不再连带截短淡出时长。
            length = Mathf.Min(speed * TrailLength.Effective(r), PatchCECompat.MaxTrailLength);
            fadeTicks = TrailFade.Effective(r);
            dir = (b - a).normalized;
            ComputeWidth(r, proj.def, PatchCECompat.GetDamageAmount(proj));
            maxReach = length + width;

            // CE 轨迹样式由设置开关与单项覆写共同决定（见 ProjectileRules.CurveEnabled）：
            //   * 直线（默认）→ CE 投射物绘制直线拖尾（跟随弹体绘制位置）；抛射类投射物
            //     （出射仰角超过 ArcAngleThreshold）在登记处被拦截，不显示轨迹；
            //   * 曲线 → 全部 CE 投射物绘制弧线轨迹：按 CE 自己的绘制映射采样整条弹道的绘制位置。
            // 弧线采样失败时回退为地面解析抛物线（原版算法，弧高 tan(θ)*L/4）。
            float shotAngle = PatchCECompat.GetShotAngle(proj);
            arcing = ProjectileRules.CurveEnabled(proj.def);
            ceTrackDrawnPosition = true;
            if (arcing)
            {
                List<Vector3>? samples = PatchCECompat.PredictDrawPositions(proj, PatchCECompat.GetStartingTicksToImpact(proj));
                if (samples == null || !InitializeArcFromSamples(samples))
                {
                    ceTrackDrawnPosition = false;
                    // 采样退化：必须清空，否则绘制仍会走上面的采样分支（且 pathCumLen 可能为 null）
                    pathSamples = null;
                    InitializeArc(b, Mathf.Tan(shotAngle) * (b - a).magnitude / 4f);
                }
            }
        }

        // 宽度口径统一走 TrailWidth：单项覆写 > ProjectileTrailDef.trailWidth > 伤害/贴图基础宽度。
        // 覆写是绝对值且完全独立（不再乘全局系数），命中时直接采用。
        void ComputeWidth(in TrailResolve r, ThingDef projDef, float damage)
        {
            width = TrailWidth.Effective(r, projDef, damage);
        }

        // ---------------- 运行时推进 ----------------

        // 判停阈值：「位置连续不变」达到多少个 tick 才算投射物真的停住了。
        //
        // 15 是原版 Projectile 在视野外限流位置更新的间隔（它的 UpdateRateTicks
        // 只有两档：视野内 1、其余一律 15），而 Thing.DoTick 保证每 15 tick 至少调一次 TickInterval，
        // 于是两次位置更新之间位置不变的连续读数最多 14 个（15 - 1）。门槛取 15 以上即安全
        const int StationaryTicksBeforeDrop = 15;

        // 由 MapComponent_ProjectileTrails.MapComponentTick 每 tick 调用
        public void Tick()
        {
            if (projectile != null)
            {
                if (projectile.Destroyed || (ticksUtilDeath == -1 && ticksIdle > StationaryTicksBeforeDrop))
                {
                    // 单向结束：此后不再跟踪这个投射物，它即使又开始动也不会复活——全 mod 只有
                    // Launch / Throw 补丁会登记轨迹，飞行途中没有补登记路径；而且「停了再动」意味着
                    // 位置是跳变的，把旧尾巴接回去只会画出一条弹体根本没飞过的假轨迹（真要支持这种
                    // 弹体，得从新位置重开一条尾巴，即把位移累积清零）。原版与 CE 现有的投射物都没有
                    // 这条路：唯一的冻结入口是 Projectile_Explosive 的 landed，而它只通向爆炸。
                    projectile = null;
                    ticksUtilDeath = Mathf.CeilToInt(fadeTicks);
                }
                else
                {
                    // 只在投射物存活时读取位置；销毁后不再访问它。
                    // 位置来源按运行时类型分流，而不是看哪个 Initialize 被调用过：
                    //   * 原版投射物本身就是 Projectile —— 直接读 projectile.ExactPosition；
                    //   * CE 的 ProjectileCE 不是 Projectile 子类 —— 直线取绘制位置（含高度偏移），
                    //     弧线采样失败时退回它的 ExactPosition。两条都走已缓存好的映射器，无反射。
                    Vector3? drawn = ceTrackDrawnPosition
                        ? PatchCECompat.GetDrawPosition(projectile, ceWorker, ceDrawAccessor)
                        : (projectile is Projectile vanilla ? vanilla.ExactPosition : PatchCECompat.GetExactPosition(projectile));
                    if (drawn.HasValue) ApplyPosition(drawn.Value);

                    // 自动检测：实际飞行路线偏离发射直线即切换为曲线绘制（某些 mod 投射物
                    // 不设 arcHeightFactor 却沿曲线飞行）。强制曲线的轨迹已在画弧线，不参与判定。
                    if (pathTracking && !arcing && DetectPathCurvature(exactPosition)) SwitchToArcDrawing();
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
                // 静止计数必须是「连续」的：一旦动了就清零。少了这一句它就成了全生命周期累计值，
                // 离屏飞行每 15 tick 攒 14 个静止 tick，累计值迟早越过阈值把还在飞的轨迹判死。
                ticksIdle = 0;
                // 记录飞行路径采样（自动检测 / 强制曲线时开启，见 BeginPathTracking）
                if (pathTracking) AppendPathSample(pos);
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
            ticksUtilDeath = Mathf.CeilToInt(fadeTicks);
        }

        // CE 投射物销毁前锁定最终位置。取位置口径与飞行中一致：直线取绘制位置，弧线取地面位置。
        public void CaptureFinalPositionCE(Thing proj)
        {
            if (projectile == null) return;

            Vector3 drawn = ceTrackDrawnPosition
                ? PatchCECompat.GetDrawPosition(proj, ceWorker, ceDrawAccessor)
                : PatchCECompat.GetExactPosition(proj);

            CaptureFinalPosition(drawn);
        }

        // ---------------- 剔除 ----------------

        // 剔除总体开关。目前关闭：CE 弧线的 AABB 计算有误，剔除会误杀仍在视野内的轨迹。
        // 用 static readonly 而不是 const、也不是把下面的实现删掉/注释掉：
        //   - 后者会让实现变成编译器眼里的死代码（CS0162），前面那句 return 一加就报警告；
        //   - 用 static readonly 编译器无法常量折叠，下面的分支保持"可达"，恢复时改回 true 即可。
        static readonly bool CullingEnabled = false;

        // 视野剔除。view 是已展开为世界坐标范围的相机视野（见 TrailViewRect）。
        public bool IsVisible(TrailViewRect view)
        {
            if (!CullingEnabled) return true;

            if (arcing)
            {
                return arcAabbMax.x >= view.MinX && arcAabbMin.x <= view.MaxX
                    && arcAabbMax.z >= view.MinZ && arcAabbMin.z <= view.MaxZ;
            }

            float hx = exactPosition.x;
            float hz = exactPosition.z;
            return hx + maxReach >= view.MinX && hx - maxReach <= view.MaxX
                && hz + maxReach >= view.MinZ && hz - maxReach <= view.MaxZ;
        }

        // ---------------- 几何生成 ----------------

        // 追加本帧要绘制的几何。直线/弧线共用这一入口，由 batcher 统一合并进
        // 按 (材质, 颜色) 分桶的网格。
        //
        // 几何写进调用方的 GeometryBuffer（ref 传入，可扩容）；同时用 out 带出这段几何的 AABB：
        // 顶点在生成时就在寄存器里，顺手比较出包围盒，省掉了「写入后再逐顶点回读」那一趟。
        // 返回 false 表示本帧该轨迹无可绘制内容（未写入任何顶点，min/max 无意义）。
        public bool AppendGeometry(ref GeometryBuffer buffer, out Vector3 min, out Vector3 max)
        {
            if (arcing) return AppendArcGeometry(ref buffer, out min, out max);
            return AppendStraightGeometry(ref buffer, out min, out max);
        }

        // 直线轨迹的几何：一个四边形，与 MeshPool.plane10 逐顶点/逐 UV/缠绕一致。
        // 旧实现走 Graphics.DrawMeshInstanced，直接把这个平面用
        //   TRS(exactPosition - dir * len / 2, LookRotation(dir), (width, 1, len))
        // 变换到世界；这里把该变换展开成分量、直接写出世界坐标顶点（外观逐像素不变）：
        //   局部 +Z → dir（沿飞行方向），局部 +X → right = (dir.z, 0, -dir.x)（宽度方向）。
        bool AppendStraightGeometry(ref GeometryBuffer buffer, out Vector3 min, out Vector3 max)
        {
            min = default;
            max = default;
            if (length <= 0f || dir.sqrMagnitude < 0.000001f) return false;

            float len = DrawLength();
            if (len <= 0f) return false;

            Vector3 right = new Vector3(dir.z, 0f, -dir.x) * (width * 0.5f);
            Vector3 head = exactPosition;                   // 头部恒在弹体当前绘制位置
            Vector3 tail = exactPosition - dir * len;

            // 一次性预留 4 顶点 / 6 索引，随后用局部数组引用直接写入：不逐顶点走方法调用。
            buffer.Ensure(4, 6);
            Vector3[] verts = buffer.verts;
            Vector2[] uvs = buffer.uvs;
            int[] tris = buffer.tris;
            int v = buffer.vertCount;
            int t = buffer.triCount;
            int b = v;

            // 顶点顺序与 plane10 相同：(-x,-z)(-x,+z)(+x,+z)(+x,-z)，UV 的 v 从尾到头
            verts[v] = tail - right; uvs[v] = UvTailLeft;  v++;
            verts[v] = head - right; uvs[v] = UvHeadLeft;  v++;
            verts[v] = head + right; uvs[v] = UvHeadRight; v++;
            verts[v] = tail + right; uvs[v] = UvTailRight; v++;

            tris[t] = b; tris[t + 1] = b + 1; tris[t + 2] = b + 2; t += 3;
            tris[t] = b; tris[t + 1] = b + 2; tris[t + 2] = b + 3; t += 3;

            buffer.vertCount = v;
            buffer.triCount = t;

            // 包围盒直接由 head/tail 与半宽推得，不回读刚写入的 4 个顶点：
            // 宽度方向 ±right 只在 X/Z 上张开（right.y 恒为 0），各轴取 |right| 即两端极值。
            // Min/Max/Abs 一律展开成三元式：这些 Mathf 方法在 Mono 下不会被内联，逐次都是真实调用。
            float rx = right.x < 0f ? -right.x : right.x;
            float rz = right.z < 0f ? -right.z : right.z;
            min = new Vector3(
                (head.x < tail.x ? head.x : tail.x) - rx,
                head.y < tail.y ? head.y : tail.y,
                (head.z < tail.z ? head.z : tail.z) - rz);
            max = new Vector3(
                (head.x > tail.x ? head.x : tail.x) + rx,
                head.y > tail.y ? head.y : tail.y,
                (head.z > tail.z ? head.z : tail.z) + rz);
            return true;
        }

        // 当前实际拖尾长度（含投掷物消亡后的收缩）
        float DrawLength()
        {
            // Mathf.Min/Max 展开成三元式，理由同 AppendStraightGeometry（Mono 不内联 Mathf）
            float len = traveled < length ? traveled : length;
            if (ticksUtilDeath != -1)
            {
                len *= fadeTicks > 0f ? ticksUtilDeath / fadeTicks : 0f;
            }
            return len > 0f ? len : 0f;
        }

        // 弧线轨迹的几何：沿弹道路径在尾部窗口内生成一串四边形。
        bool AppendArcGeometry(ref GeometryBuffer buffer, out Vector3 min, out Vector3 max)
        {
            min = default;
            max = default;
            if (arcHorizontalLength <= 0.0001f) return false;

            // 段数先取一次存局部：ArcSegmentCount 是带 Mathf.Clamp 的属性，而下面有三处都要用它，
            // 若写在循环条件/循环体里，每次迭代都会重算一遍属性并连带一次 Clamp。
            int segCount = ArcSegmentCount;

            // traveled 即沿飞行方向已经过的距离，可直接换算成弧线比例。
            // Clamp01 展开成三元式：Mathf.* 在 Mono 下都是真实调用。
            float head = traveled / arcHorizontalLength;
            head = head < 0f ? 0f : (head > 1f ? 1f : head);
            float tail = head - arcTailFraction;

            if (ticksUtilDeath != -1)
            {
                // 投射物已消失：尾部向前收缩，模拟淡出（与直线轨迹一致）
                float k = fadeTicks > 0f ? ticksUtilDeath / fadeTicks : 0f;
                k = k < 0f ? 0f : (k > 1f ? 1f : k);
                tail = head - (head - tail) * k;
            }

            tail = tail < 0f ? 0f : (tail > 1f ? 1f : tail);
            if (head - tail <= 0.0001f) return false;

            float halfWidth = width * 0.5f;
            float headMinusTail = head - tail;
            Vector3 side = arcSide * halfWidth;   // 循环不变量（每段两次用到），移出循环

            // 一次性预留 (段数+1)*2 顶点 / 段数*6 索引，随后用局部数组引用直接写入
            buffer.Ensure((segCount + 1) * 2, segCount * 6);
            Vector3[] verts = buffer.verts;
            Vector2[] uvs = buffer.uvs;
            int[] tris = buffer.tris;
            int v = buffer.vertCount;
            int t = buffer.triCount;
            int baseIndex = v;
            float invSeg = 1f / segCount;

            // 段中心的围盒（不是顶点围盒）。顶点 = 中心 ± side，宽度方向是常量偏移，
            // 所以逐段只维护中心的 min/max，循环结束后一次性外扩半宽即得顶点 AABB。
            // 这样每段只比较 3 个分量，且完全不必回读刚写入缓冲的顶点。
            float cLoX = float.PositiveInfinity, cLoY = float.PositiveInfinity, cLoZ = float.PositiveInfinity;
            float cHiX = float.NegativeInfinity, cHiY = float.NegativeInfinity, cHiZ = float.NegativeInfinity;

            // 采样点列存在时（CE 的发射时预测 / 运行时的真实路径记录）沿绘制路径取点，
            // 否则按解析抛物线取点。
            // 循环上界取采样点列的长度而不是 pathCumLen.Length：运行时采样的数组容量
            // 允许长于点列（2 倍扩容 + 上限丢弃后不收缩），用数组长度会把段指针
            // 带过点列末尾（见 AppendPathSample）。
            int sampleLast = pathSamples != null ? pathSamples.Count - 2 : -1;
            int seg = 0;
            for (int i = 0; i <= segCount; i++)
            {
                float f = i * invSeg;
                // 等价于 Mathf.Lerp(tail, head, f)：f 本就在 [0,1]，省掉 Clamp01 与一次方法调用
                float tt = tail + headMinusTail * f;

                Vector3 center;
                if (pathSamples != null && pathCumLen != null)
                {
                    // 按绘制路径累计长度在采样点间插值（tt 单调递增，段指针只前进）
                    float d = tt * pathTotalLen;
                    while (seg < sampleLast && pathCumLen[seg + 1] < d) seg++;
                    float span = pathCumLen[seg + 1] - pathCumLen[seg];
                    float k = 0f;
                    if (span > 0.000001f)
                    {
                        k = (d - pathCumLen[seg]) / span;
                        k = k < 0f ? 0f : (k > 1f ? 1f : k);
                    }
                    // 展开 Vector3.Lerp（其内部会调 Mathf.Clamp01，同样不内联）
                    Vector3 p0 = pathSamples[seg];
                    Vector3 p1 = pathSamples[seg + 1];
                    center = new Vector3(p0.x + (p1.x - p0.x) * k, p0.y + (p1.y - p0.y) * k, p0.z + (p1.z - p0.z) * k);
                }
                else
                {
                    float height = arcHeight * 4f * tt * (1f - tt);
                    center = a + dir * (arcHorizontalLength * tt) + ArcUp * height;
                }

                verts[v] = center - side; uvs[v] = new Vector2(0f, f); v++;
                verts[v] = center + side; uvs[v] = new Vector2(1f, f); v++;

                if (center.x < cLoX) cLoX = center.x; else if (center.x > cHiX) cHiX = center.x;
                if (center.y < cLoY) cLoY = center.y; else if (center.y > cHiY) cHiY = center.y;
                if (center.z < cLoZ) cLoZ = center.z; else if (center.z > cHiZ) cHiZ = center.z;
            }

            for (int i = 0; i < segCount; i++)
            {
                int i0 = baseIndex + i * 2;
                tris[t] = i0;     tris[t + 1] = i0 + 1; tris[t + 2] = i0 + 3; t += 3;
                tris[t] = i0;     tris[t + 1] = i0 + 3; tris[t + 2] = i0 + 2; t += 3;
            }

            buffer.vertCount = v;
            buffer.triCount = t;

            // 由中心围盒外扩半宽（arcSide.y 恒为 0，Y 不受影响）
            float ox = (arcSide.x < 0f ? -arcSide.x : arcSide.x) * halfWidth;
            float oz = (arcSide.z < 0f ? -arcSide.z : arcSide.z) * halfWidth;
            min = new Vector3(cLoX - ox, cLoY, cLoZ - oz);
            max = new Vector3(cHiX + ox, cHiY, cHiZ + oz);
            return true;
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
            pathSamples = samples;
            if (!RebuildSampledArcLengths()) return false;

            // 弧线参数化分母 = 绘制路径总长（注意：此时 traveled 按绘制位置累计，口径一致）
            arcHorizontalLength = pathTotalLen;
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
            if (pathSamples == null || pathSamples.Count < 2) return false;

            pathCumLen = new float[pathSamples.Count];
            float total = 0f;
            for (int i = 1; i < pathSamples.Count; i++)
            {
                total += Vector3.Distance(pathSamples[i - 1], pathSamples[i]);
                pathCumLen[i] = total;
            }
            pathTotalLen = total;
            return total > 0.01f;
        }

        // ---------------- 运行时路径跟踪（自动检测 / 强制曲线）----------------

        // 开始记录实际飞行路径（见 Initialize 的两处调用：强制曲线绘制的非弧线投射物、
        // 以及自动检测开启时按直线绘制的投射物）。发射点即第一个采样点，
        // 之后每 tick 由 ApplyPosition 追加当前位置（见 AppendPathSample）。
        //
        // 采样缓冲是每发一份的分配（初始约 350 字节，飞行超过 15 tick 扩容一次）。
        // 刻意不惰性化：切换曲线时尾部窗口要回溯到发射点，中途才开始记录会出现
        // 「切换那一帧轨迹突然缩短再长回来」的跳变；这个量级与每发必建的
        // ProjectileTrailState、CE 每发整条弹道的预测采样（PredictDrawPositions）相同。
        void BeginPathTracking()
        {
            pathTracking = true;
            pathSamples = new List<Vector3>(16) { a };
            pathCumLen = new float[16];
            pathCumLen[0] = 0f;
            pathTotalLen = 0f;
            // 偏离检测的基准：发射点与发射方向（dir 随后会被每 tick 的实际位移覆盖，必须另存）
            trackDir = dir;
            // 宽度方向垂直于发射方向（与 InitializeArc 同一取法；检测切换时按当前方向刷新）
            arcSide = new Vector3(-dir.z, 0f, dir.x);
            // 采样参数化初值：路径总长 0 时 AppendArcGeometry 直接返回，写入首个采样点后才开始绘制
            arcHorizontalLength = 0f;
            arcTailFraction = 1f;
            // 包围盒从发射点起增量维护（与 ComputeSampledArcBounds 同一口径）
            Vector3 pad = new Vector3(width + 1f, 1f, width + 1f);
            arcAabbMin = a - pad;
            arcAabbMax = a + pad;
        }

        // 追加一个路径采样点，由 ApplyPosition 在「这一 tick 移动了」时调用。
        // 与 traveled 同步推进：pathTotalLen 与 traveled 是同一个增量序列（步长都是
        // 这一 tick 的位移量），恒相等——因此 AppendArcGeometry 里
        // head = traveled / arcHorizontalLength 恒为 1，弹体永远在采样路径末端。
        // 触发上限丢弃后二者不再相等（保留窗口变短），那时 head 被夹到 1，结论不变。
        void AppendPathSample(Vector3 pos)
        {
            if (pathSamples == null || pathCumLen == null) return;

            if (pathSamples.Count >= MaxPathSamples)
            {
                // 超出上限：丢弃最旧的一半并重建累计长度。保留窗口仍覆盖整个拖尾长度，
                // 只有「拖尾长度系数拉满 + 弹速缩放拉到最小」这种病态组合才会短于设定值。
                // 均摊 O(1)：每丢一半才付一次 O(n)。
                pathSamples.RemoveRange(0, pathSamples.Count / 2);
                RebuildPathCumLen();
            }

            // 容量不足才扩容（2 倍）。pathCumLen 允许长于采样点列——插值循环的上界取
            // 采样点列的长度而不是数组长度（见 AppendArcGeometry）。
            if (pathSamples.Count >= pathCumLen.Length)
            {
                Array.Resize(ref pathCumLen, Mathf.Max(pathCumLen.Length * 2, 16));
            }

            float step = Vector3.Distance(pathSamples[pathSamples.Count - 1], pos);
            pathTotalLen += step;
            pathCumLen[pathSamples.Count] = pathTotalLen;
            pathSamples.Add(pos);

            // 采样参数化随路径增长更新：尾窗比例 = 拖尾长度 / 已走过的路径长
            arcHorizontalLength = pathTotalLen;
            arcTailFraction = pathTotalLen > 0.0001f
                ? Mathf.Clamp(length / pathTotalLen, ArcMinTailFraction, 1f)
                : 1f;

            // 包围盒增量维护（剔除总开关当前关闭，见 CullingEnabled；真开了也不必逐帧重算）
            Vector3 lo = pos - new Vector3(width + 1f, 1f, width + 1f);
            Vector3 hi = pos + new Vector3(width + 1f, 1f, width + 1f);
            if (lo.x < arcAabbMin.x) arcAabbMin.x = lo.x;
            if (lo.y < arcAabbMin.y) arcAabbMin.y = lo.y;
            if (lo.z < arcAabbMin.z) arcAabbMin.z = lo.z;
            if (hi.x > arcAabbMax.x) arcAabbMax.x = hi.x;
            if (hi.y > arcAabbMax.y) arcAabbMax.y = hi.y;
            if (hi.z > arcAabbMax.z) arcAabbMax.z = hi.z;
        }

        // 重建累计长度（丢弃旧采样后调用）。O(n)，只在触发上限丢弃时走。
        // 之后 pathTotalLen 只覆盖保留窗口，与 traveled 不再相等（见 AppendPathSample）。
        void RebuildPathCumLen()
        {
            if (pathSamples == null || pathCumLen == null) return;

            float total = 0f;
            for (int i = 1; i < pathSamples.Count; i++)
            {
                total += Vector3.Distance(pathSamples[i - 1], pathSamples[i]);
                pathCumLen[i] = total;
            }
            pathCumLen[0] = 0f;
            pathTotalLen = total;
        }

        // 自动检测命中：切换为曲线绘制。几何参数已由 AppendPathSample 逐 tick 维护
        // （采样点列、累计长度、尾窗比例、包围盒），这里只补方向量并记住这个 def。
        void SwitchToArcDrawing()
        {
            arcing = true;
            // 宽度方向垂直于当前飞行方向（与 InitializeArc 同一取法）
            arcSide = new Vector3(-dir.z, 0f, dir.x);
            // 记住这个 def：后续发射直接按曲线绘制，不必每发都重新经历「先画一小段直线
            // 再切曲线」。仅内存、不进存档；重开游戏重新检测（见 ProjectileRules）。
            ProjectileRules.MarkAutoDetectedCurved(projectile?.def);
        }

        // 自动检测：当前位置是否已偏离「发射点 + 发射方向」确定的直线。
        // 原版投射物严格沿该直线飞行（ExactPosition 就是 origin→destination 的线性插值，
        // 弧线只是绘制效果），偏离量恒为 0；自定义弹道的 mod 投射物（追踪弹、波浪弹等）
        // 会持续偏离。阈值见 CurveDetectThreshold。
        bool DetectPathCurvature(Vector3 pos)
        {
            float rx = pos.x - a.x;
            float rz = pos.z - a.z;
            float along = rx * trackDir.x + rz * trackDir.z;
            float px = rx - trackDir.x * along;
            float pz = rz - trackDir.z * along;
            return px * px + pz * pz > CurveDetectThreshold * CurveDetectThreshold;
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
