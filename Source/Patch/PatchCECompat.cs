using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Gunplay.Patch
{
    // Combat Extended 兼容补丁。纯反射软依赖（不硬引用 CE 程序集），CE 未加载时自动跳过。
    //
    // 背景：CE 的 ProjectileCE 直接继承 ThingWithComps 而非原版 Projectile，并自带一整套弹道
    // 物理（独立的 Launch / Throw / Impact / ExactPosition），因此 Gunplay 挂在原版 Projectile
    // 上的各补丁对 CE 投射物完全不生效。这里把轨迹与命中特效/音效补到 CE 自己的方法上：
    //   * Launch(Thing, Vector2, Thing) —— CE 全部发射路径（枪械/迫击炮/破片/空爆弹等）最终都
    //     汇聚到这个三参重载（8 参重载与其子类重写末尾都会调用它）；
    //   * Throw(Thing, Vector3, Vector3, Thing) —— 手雷等投掷路径，自行赋值字段、不经过 Launch；
    //   * Impact(Thing) —— 命中特效/音效，并在投射物销毁前锁定轨迹最终位置
    //     （CE 各投射物子类的 Impact 重写均会调用 base.Impact，因此只需补基类一处）。
    // 按需求不改弹速：CE 有自己的弹道物理，全局 projectileSpeed 缩放不应叠加。
    //
    // 绘制高度：CE 把弹道高度映射为屏幕偏移的方式随 TrajectoryWorker 而异（默认的
    // LerpedTrajectoryWorker 为 z += max(0, height) * 0.84，且在前数 tick 内渐升；
    // BallisticsTrajectoryWorker 等则完全不平移），因此轨迹直接调用 worker 自己的
    // ExactPosToDrawPos 取绘制位置，保证与投射物本体的绘制逐帧一致。
    static class PatchCECompat
    {
        // 出射仰角（弧度）高于此值视为抛射（迫击炮弹/榴弹等）：直线轨迹模式下不显示轨迹
        internal const float ArcAngleThreshold = 0.45f;

        // CE 是否已安装且兼容补丁挂载成功（设置界面的条件显示用）
        public static bool Enabled { get; private set; }

        // 是否检测到 CE，不看补丁挂载是否成功。
        //
        // 与 Enabled 分开，是因为两者回答的不是同一个问题：
        //   Enabled  —— 「Gunplay 的 CE 集成是否生效」（轨迹与命中特效要不要按 CE 路径走）；
        //   Detected —— 「CE 是否在场」。CE 在场就意味着它那套弹道物理接管了投射物，
        //               原版弹速缩放不再适用——这一点与我们的补丁有没有挂上无关。
        // 弹速相关 UI 的隐藏判据必须用 Detected：CE 改 API 导致反射成员找不到时 Enabled 会是 false，
        // 但那时若把弹速设置又放出来，用户调了也不生效（而且轨迹已经先坏了，更容易误判）。
        public static bool Detected { get; private set; }

        // CE 弹速是真实弹道速度（一发子弹一 tick 可飞数格），speed * 15 的拖尾长度会拉出
        // 十余格的“激光束”，CE 路径对拖尾长度做此上限（4 格）。
        internal const float MaxTrailLength = 40f;

        // ---------------- 反射成员（TryPatch 时解析；均为 CE 的 public 成员）----------------
        // CE 未安装或成员改名时全部为 null，各读取点必须能承受缺失。
        static PropertyInfo? exactPositionProp;      // ProjectileCE.ExactPosition : Vector3（virtual，反射 GetValue 走虚分派）
        static PropertyInfo? damageAmountProp;       // ProjectileCE.DamageAmount : float（含武器品质倍率）
        static FieldInfo? destinationField;          // ProjectileCE.Destination : Vector2（预测落点，Launch/Throw 内赋值）
        static FieldInfo? shotAngleField;            // ProjectileCE.shotAngle : float（出射仰角，弧度）
        static FieldInfo? originIV3Field;            // ProjectileCE.OriginIV3 : IntVec3（发射点所在格）
        static FieldInfo? equipmentDefField;         // ProjectileCE.equipmentDef : ThingDef（无武器投射物为 null）
        static PropertyInfo? trajectoryWorkerProp;   // ProjectilePropertiesCE.TrajectoryWorker（决定绘制高度映射）
        static FieldInfo? flightTicksField;          // ProjectileCE.FlightTicks : int
        static FieldInfo? ticksToTruePositionField;  // ProjectileCE.ticksToTruePosition : int（出膛渐升 tick 数）
        static FieldInfo? startingTicksToImpactField;// ProjectileCE.startingTicksToImpact : float

        // ---------------- 免装箱访问器（TryPatch 时构建，失败即回落反射）----------------
        //
        // 下面这些读取落在「每个 CE 投射物 × 每 tick」上：轨迹每 tick 都要问一次弹体的绘制位置，
        // 离屏也照跑。而 PropertyInfo.GetValue / FieldInfo.GetValue 每次都要装箱、MethodInfo.Invoke
        // 每次都要分配 object[]，弹幕密集时是实打实的 GC 压力。一次性编成委托后这条路径
        // 零分配、零装箱。
        //
        // 为什么不用 Delegate.CreateDelegate：开放实例委托要求第一个参数就是成员的声明类型，
        // 而这里第一个参数只能是基类 Thing —— Mono（RimWorld 的运行时）会直接拒绝并报
        // "method arguments are incompatible"（CoreCLR 宽松一些，所以这个坑只在游戏里现形）。
        // 因此自己发一小段带 castclass 的 IL：既跨运行时可用，又让虚属性仍然走虚分派（callvirt）。
        //
        // 构建失败一律留 null 并回落反射：优化不能变成新的失败源——CE 改名、或运行时不允许
        // 动态方法时，行为与优化前完全一致。
        static Func<Thing, Vector3>? exactPositionGetter;   // ProjectileCE.ExactPosition（虚属性）
        static Func<Thing, int>? flightTicksGetter;         // ProjectileCE.FlightTicks
        static Func<Thing, int>? truePosTicksGetter;        // ProjectileCE.ticksToTruePosition

        // 投射物 def 侧的成员：接收者是 ProjectileProperties 而不是 Thing，所以与上面三个分开一类。
        // worker 是「每个 def 一个实例」的常量，本该随 def 解析一次（见 ResolveDrawMapper）。
        static Func<ProjectileProperties, object>? trajectoryWorkerGetter;  // ProjectilePropertiesCE.TrajectoryWorker

        // 下面三个原先走裸反射（每次 GetValue 都装箱一个 float）。它们虽然只在登记时读一次，
        // 但没有理由比同一路径上的其它成员更贵——统一编成委托。
        static Func<Thing, float>? damageAmountGetter;      // ProjectileCE.DamageAmount（虚属性，含武器品质倍率）
        static Func<Thing, float>? shotAngleGetter;         // ProjectileCE.shotAngle
        static Func<Thing, float>? startingTicksGetter;     // ProjectileCE.startingTicksToImpact

        // worker 的 ExactPosToDrawPos 签名：第 1 个参数是 worker 实例（开放实例委托）。
        // internal：轨迹状态要按发缓存「worker + 它的绘制委托」，见 ResolveDrawMapper / GetDrawPosition。
        internal delegate Vector3 DrawPosAccessor(object worker, Vector3 exactPos, int flightTicks, int ticksToTruePosition, float altitude);

        // 按 worker 运行时类型缓存（worker 实例随 def 而异，方法随类型固定）。null 值也要缓存：
        // 它表示「该类型构建不出委托」，避免每次调用都重试一遍并重复打日志。
        static readonly Dictionary<Type, DrawPosAccessor?> drawAccessorCache = new Dictionary<Type, DrawPosAccessor?>();

        static DrawPosAccessor? GetDrawAccessor(Type workerType)
        {
            if (drawAccessorCache.TryGetValue(workerType, out DrawPosAccessor? accessor)) return accessor;

            accessor = BuildDrawAccessor(workerType);
            drawAccessorCache[workerType] = accessor;
            return accessor;
        }

        static DrawPosAccessor? BuildDrawAccessor(Type workerType)
        {
            MethodInfo? method = AccessTools.Method(workerType, "ExactPosToDrawPos");
            if (method == null) return null;

            // 同样绕开 Delegate.CreateDelegate：第一个参数是 object，与 DeclaringType 不同，
            // Mono 会拒绝。这里显式 castclass 到 worker 的实际类型。
            try
            {
                var accessor = new DynamicMethod(
                    "GunplayCE_DrawPos", typeof(Vector3),
                    new[] { typeof(object), typeof(Vector3), typeof(int), typeof(int), typeof(float) },
                    typeof(Thing).Module, skipVisibility: true);

                ILGenerator il = accessor.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, workerType);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldarg_2);
                il.Emit(OpCodes.Ldarg_3);
                il.Emit(OpCodes.Ldarg_S, (byte)4);
                il.Emit(OpCodes.Callvirt, method);
                il.Emit(OpCodes.Ret);

                return (DrawPosAccessor)accessor.CreateDelegate(typeof(DrawPosAccessor));
            }
            catch (Exception e)
            {
                Log.Warning("[Gunplay] Could not build a direct call to " + workerType.Name
                    + ".ExactPosToDrawPos; CE trail positions will use reflection. " + e.Message);
                return null;
            }
        }

        // internal：轨迹状态在 CE 分支里直接读它（原版投射物读自己的 ExactPosition，见 ProjectileTrailState.Tick）
        internal static Vector3 GetExactPosition(Thing proj)
        {
            // 反射取值失败时退回本体绘制位置，而不是在某次版本失配后每 tick 抛异常
            Func<Thing, Vector3>? fast = exactPositionGetter;
            if (fast != null) return fast(proj);

            return exactPositionProp?.GetValue(proj) is Vector3 pos ? pos : proj.DrawPos;
        }

        static int GetFlightTicks(Thing proj)
        {
            Func<Thing, int>? fast = flightTicksGetter;
            if (fast != null) return fast(proj);

            return flightTicksField?.GetValue(proj) is int ticks ? ticks : 0;
        }

        static int GetTicksToTruePosition(Thing proj)
        {
            Func<Thing, int>? fast = truePosTicksGetter;
            if (fast != null) return fast(proj);

            return ticksToTruePositionField?.GetValue(proj) is int ticks ? ticks : 0;
        }

        // 逐项独立构建：某一项解析不了只影响它自己（回落反射），并在日志里点名是哪一个成员。
        // 上一次这三个挤在同一个 try/catch 里，一条 "method arguments are incompatible"
        // 完全看不出是谁的问题。整段都不影响补丁是否挂上。
        static void BuildFastAccessors()
        {
            if (exactPositionProp != null) exactPositionGetter = BuildAccessor<Thing, Vector3>(exactPositionProp);
            if (flightTicksField != null) flightTicksGetter = BuildAccessor<Thing, int>(flightTicksField);
            if (ticksToTruePositionField != null) truePosTicksGetter = BuildAccessor<Thing, int>(ticksToTruePositionField);

            if (trajectoryWorkerProp != null) trajectoryWorkerGetter = BuildAccessor<ProjectileProperties, object>(trajectoryWorkerProp);
            if (damageAmountProp != null) damageAmountGetter = BuildAccessor<Thing, float>(damageAmountProp);
            if (shotAngleField != null) shotAngleGetter = BuildAccessor<Thing, float>(shotAngleField);
            if (startingTicksToImpactField != null) startingTicksGetter = BuildAccessor<Thing, float>(startingTicksToImpactField);
        }

        // 把「接收者参数 → 读成员」编成无装箱委托。成员声明类型与接收者类型之间的那一步由 castclass 完成，
        // 所以不要求第一个参数等于声明类型（这正是 CreateDelegate 在 Mono 上做不到的）；
        // 虚属性用 callvirt，分派语义与原来的 GetValue 一致。
        //
        // 接收者类型是参数化的：CE 投射物成员挂在 ProjectileCE（以 Thing 传入），
        // 而 TrajectoryWorker 挂在 ProjectilePropertiesCE（以 ProjectileProperties 传入）。
        static Func<TReceiver, T>? BuildAccessor<TReceiver, T>(MemberInfo member)
        {
            Type? declaring = member.DeclaringType;
            if (declaring == null) return null;

            MethodInfo? getter = member is PropertyInfo prop ? prop.GetGetMethod(true) : null;
            if (member is PropertyInfo && getter == null) return null;
            if (member is FieldInfo f && f.IsStatic) return null;

            try
            {
                var accessor = new DynamicMethod(
                    "GunplayCE_" + member.Name, typeof(T), new[] { typeof(TReceiver) },
                    typeof(Thing).Module, skipVisibility: true);

                ILGenerator il = accessor.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, declaring);
                if (member is FieldInfo field) il.Emit(OpCodes.Ldfld, field);
                else il.Emit(OpCodes.Callvirt, getter!);
                il.Emit(OpCodes.Ret);

                return (Func<TReceiver, T>)accessor.CreateDelegate(typeof(Func<TReceiver, T>));
            }
            catch (Exception e)
            {
                Log.Warning("[Gunplay] Could not build a direct accessor for " + declaring.Name + "." + member.Name
                    + "; it will be read via reflection instead. " + e.Message);
                return null;
            }
        }

        internal static float GetDamageAmount(Thing proj)
        {
            Func<Thing, float>? fast = damageAmountGetter;
            if (fast != null) return fast(proj);

            return damageAmountProp?.GetValue(proj) is float damage ? damage : 10f;
        }

        internal static float GetShotAngle(Thing proj)
        {
            Func<Thing, float>? fast = shotAngleGetter;
            if (fast != null) return fast(proj);

            return shotAngleField?.GetValue(proj) is float angle ? angle : 0f;
        }

        // ---------------- 绘制位置（CE 高度映射）----------------

        // 解析该 def 的绘制映射器：worker 实例 + 它 ExactPosToDrawPos 的直调委托。
        // 两者都只由投射物 def 决定（worker 来自 def.projectile，委托又由 worker 的类型决定），
        // 所以每发解析一次、由轨迹状态持有——原先每 tick 都要重做一次反射取值和一次字典查找。
        internal static void ResolveDrawMapper(ProjectileProperties props, out object? worker, out DrawPosAccessor? accessor)
        {
            worker = GetWorker(props);
            accessor = worker != null ? GetDrawAccessor(worker.GetType()) : null;
        }

        static object? GetWorker(ProjectileProperties props)
        {
            Func<ProjectileProperties, object>? fast = trajectoryWorkerGetter;
            if (fast != null) return fast(props);

            return trajectoryWorkerProp?.GetValue(props);
        }

        // 经 worker 的 ExactPosToDrawPos 计算投射物当前的实际绘制位置（y 为 altitude 层，z 含高度偏移）。
        // worker / accessor 由调用方缓存（见 ResolveDrawMapper），这里只读每 tick 会变的那几项。
        internal static Vector3 GetDrawPosition(Thing proj, object? worker, DrawPosAccessor? accessor)
        {
            Vector3 exact = GetExactPosition(proj);
            int flightTicks = GetFlightTicks(proj);
            return DrawViaWorker(proj, worker, accessor, exact, flightTicks);
        }

        // 一次性路径（PredictDrawPositions，每发最多调一次）用的形态：accessor 现查。
        // GetDrawAccessor 自带按类型的缓存，这里多一次字典查找，但调用频率低，不值得为此多传一组状态。
        static Vector3 DrawViaWorker(Thing proj, object? worker, Vector3 exact, int flightTicks)
        {
            DrawPosAccessor? accessor = worker != null ? GetDrawAccessor(worker.GetType()) : null;
            return DrawViaWorker(proj, worker, accessor, exact, flightTicks);
        }

        static Vector3 DrawViaWorker(Thing proj, object? worker, DrawPosAccessor? accessor, Vector3 exact, int flightTicks)
        {
            float altitude = proj.def.Altitude;
            if (worker == null) return new Vector3(exact.x, altitude, exact.z); // 无 worker 兜底：地面投影

            if (accessor != null) return accessor(worker, exact, flightTicks, GetTicksToTruePosition(proj), altitude);

            // 兜底：委托构建失败时走反射。这条路径正常不会走到，只在 CE 改签名时兜一次
            MethodInfo? draw = AccessTools.Method(worker.GetType(), "ExactPosToDrawPos");
            if (draw == null) return new Vector3(exact.x, altitude, exact.z);

            object[] args =
            {
                exact,
                flightTicks,
                GetTicksToTruePosition(proj),
                altitude
            };
            // 用 is 模式而不是 (Vector3)…! ：CE 万一改了签名、Invoke 返回别的类型，
            // 这里安全回退成地面投影，而不是抛 InvalidCastException 把整帧绘制掀掉。
            return draw.Invoke(worker, args) is Vector3 drawn ? drawn : new Vector3(exact.x, altitude, exact.z);
        }

        // 发射时的预测飞行时长（tick），用于弧线采样
        internal static int GetStartingTicksToImpact(Thing proj)
        {
            Func<Thing, float>? fast = startingTicksGetter;
            if (fast != null) return Mathf.CeilToInt(fast(proj));

            // 同上：反射值类型不符时返回 0（调用方按"取不到"处理），不给 InvalidCastException 机会
            if (startingTicksToImpactField?.GetValue(proj) is float ticks) return Mathf.CeilToInt(ticks);
            return 0;
        }

        // 采样整条弹道的实际绘制位置（含高度偏移与出膛渐升），供弧线轨迹构建带状网格。
        // 仅在 Launch/Throw 之后、首次 Tick 之前调用：此时投射物 FlightTicks 为 0，
        // LerpedTrajectoryWorker 的 PredictPositions 只写临时列表，不会影响其自身推进。
        // 采样点数不足或反射缺失时返回 null，调用方回退解析抛物线
        internal static List<Vector3>? PredictDrawPositions(Thing proj, int tickCount)
        {
            object? worker = trajectoryWorkerProp != null ? trajectoryWorkerProp.GetValue(proj.def.projectile) : null;
            MethodInfo? predict = worker != null ? AccessTools.Method(worker.GetType(), "PredictPositions") : null;
            if (worker == null || predict == null || tickCount < 1) return null;

            IEnumerable<Vector3>? exacts = predict.Invoke(worker, new object[] { proj, tickCount }) as IEnumerable<Vector3>;
            if (exacts == null) return null;

            // 统一为“第 j 个点 = 第 j tick 的绘制位置”（两种 worker 的起点约定不同，统一补发射点）
            List<Vector3> result = new List<Vector3>(tickCount + 2);
            Vector3 launchExact = GetExactPosition(proj);
            Vector3 last = DrawViaWorker(proj, worker, launchExact, 0);
            result.Add(last);

            foreach (Vector3 exact in exacts)
            {
                if (HorizontalDist(last, exact) < 0.01f) continue; // 跳过与发射点重复的采样
                last = DrawViaWorker(proj, worker, exact, result.Count);
                result.Add(last);
                if (result.Count > tickCount + 8) break; // 防御异常实现无限吐点
            }

            // 补上终点（PredictPositions 的覆盖范围可能差一 tick）
            Vector2 dest2 = destinationField?.GetValue(proj) is Vector2 dest ? dest : new Vector2(last.x, last.z);
            Vector3 destExact = new Vector3(dest2.x, 0f, dest2.y);
            if (HorizontalDist(last, destExact) > 0.05f)
            {
                result.Add(DrawViaWorker(proj, worker, destExact, result.Count));
            }
            return result;
        }

        static float HorizontalDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        public static void TryPatch(Harmony harmony)
        {
            Type? ceType = AccessTools.TypeByName("CombatExtended.ProjectileCE");
            if (ceType == null) return; // 未安装 CE

            // 早于成员解析就置位：下面任何一步反射失配都不会让「CE 在场」这个事实变成假
            Detected = true;

            exactPositionProp = AccessTools.Property(ceType, "ExactPosition");
            damageAmountProp = AccessTools.Property(ceType, "DamageAmount");
            destinationField = AccessTools.Field(ceType, "Destination");
            shotAngleField = AccessTools.Field(ceType, "shotAngle");
            originIV3Field = AccessTools.Field(ceType, "OriginIV3");
            equipmentDefField = AccessTools.Field(ceType, "equipmentDef");

            // 绘制高度映射相关成员；缺失时 GetDrawPosition 回退为地面投影
            Type? propsType = AccessTools.TypeByName("CombatExtended.ProjectilePropertiesCE");
            trajectoryWorkerProp = propsType != null ? AccessTools.Property(propsType, "TrajectoryWorker") : null;
            flightTicksField = AccessTools.Field(ceType, "FlightTicks");
            ticksToTruePositionField = AccessTools.Field(ceType, "ticksToTruePosition");
            startingTicksToImpactField = AccessTools.Field(ceType, "startingTicksToImpact");

            BuildFastAccessors();

            MethodBase? launch = AccessTools.Method(ceType, "Launch", new[] { typeof(Thing), typeof(Vector2), typeof(Thing) });
            MethodBase? throwLaunch = AccessTools.Method(ceType, "Throw", new[] { typeof(Thing), typeof(Vector3), typeof(Vector3), typeof(Thing) });
            MethodBase? impact = AccessTools.Method(ceType, "Impact", new[] { typeof(Thing) });

            if (launch == null || impact == null || exactPositionProp == null)
            {
                Log.Warning("[Gunplay] Detected Combat Extended, but expected ProjectileCE members were not found. CE compatibility is disabled.");
                return;
            }

            // Launch 与 Throw 的公共 postfix：两个目标都带有 launcher / equipment 形参，Harmony 按名注入
            HarmonyMethod spawnTrail = new HarmonyMethod(AccessTools.Method(typeof(PatchCECompat), nameof(SpawnTrailPostfix)));
            harmony.Patch(launch, postfix: spawnTrail);
            if (throwLaunch != null) harmony.Patch(throwLaunch, postfix: spawnTrail);
            harmony.Patch(impact, prefix: new HarmonyMethod(AccessTools.Method(typeof(PatchCECompat), nameof(ImpactPrefix))));

            Enabled = true;
            Log.Message("[Gunplay] Combat Extended detected: trails, impact effects and impact sounds are now applied to CE projectiles.");
        }

        static void SpawnTrailPostfix(Thing __instance, Thing launcher, Thing equipment)
        {
            // 与 PatchProjectileLaunch.Postfix 同一理由：后缀抛异常会中断 CE 的 Launch/Throw，
            // 异常会顺着 TickList 掀掉整个 tick。轨迹登记失败只该是「这一发没轨迹」。
            try
            {
                RunSpawnTrail(__instance, launcher, equipment);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Gunplay] CE trail registration failed for projectile '" + (__instance.def?.defName ?? "?")
                    + "'; this shot has no trail. " + e,
                    ("GunplayCESpawnTrail" + __instance.def?.defName).GetHashCode());
            }
        }

        static void RunSpawnTrail(Thing __instance, Thing launcher, Thing equipment)
        {
            // 覆写记录只在这里取一次：门控（全局开关 + 单项覆写 + XML 是否配了 trail）
            // 与随后的材质解析共用同一个 Resolve 结果，见 TrailResolve。
            TrailResolve r = ProjectileRules.Resolve(__instance.def);
            if (!ProjectileRules.TrailEnabled(r, __instance.def)) return;

            // 无武器投射物判定口径与原版一致：equipment 为 null
            bool unarmed = equipment == null;
            if (unarmed && !PatchProjectileLaunch.UnarmedEffectsAllowed(__instance)) return;

            // 直线轨迹模式下，抛射类投射物（迫击炮弹/榴弹等大仰角发射）不显示轨迹
            if (GunplaySettings.ceStraightTrails && GetShotAngle(__instance) > ArcAngleThreshold) return;

            // 投射物在 Launch/Throw 之前已 Spawn，本体的 Map 可作为无发射者时的兜底
            Map? map = __instance.Map ?? launcher?.Map;
            if (map == null || r.trailDef == null) return;

            // 轨迹不再 spawn Thing：登记到该地图的轨迹集合，由批量绘制统一处理。
            // CE 的 Destination 是发射时预测的落点（零高度），仅提供轨迹初始方向/弧线几何；
            // 直线轨迹的实际方向随后每 tick 由投射物真实位移校正。
            Vector2 dest2 = destinationField?.GetValue(__instance) is Vector2 dest ? dest : new Vector2(__instance.DrawPos.x, __instance.DrawPos.z);
            MapComponent_ProjectileTrails.For(map)?.RegisterCE(__instance, new Vector3(dest2.x, 0f, dest2.y), in r);
        }

        static void ImpactPrefix(Thing __instance, Thing hitThing)
        {
            // 与 PatchProjectileImpact.Prefix 同一理由：前缀抛异常会中断 CE 的 Impact，
            // Destroy() 就写在里面，投射物会永生且每 tick 再抛一次。
            try
            {
                RunImpact(__instance, hitThing);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Gunplay] CE impact prefix failed for projectile '" + (__instance.def?.defName ?? "?")
                    + "'; impact effects and final trail capture were skipped for this hit. " + e,
                    ("GunplayCEImpactPrefix" + __instance.def?.defName).GetHashCode());
            }
        }

        static void RunImpact(Thing __instance, Thing hitThing)
        {
            Map map = __instance.Map;
            if (map == null) return;

            // Impact 在 Destroy 之前执行，此时投射物仍存活，取到的就是最终落点
            //（由轨迹自行决定取地面位置还是绘制位置，与飞行中的跟踪口径一致）
            MapComponent_ProjectileTrails.For(map)?.CaptureFinalPositionCE(__instance);

            // 无武器投射物受独立开关控制（与轨迹开关保持同一判定口径）
            object? equipmentDef = equipmentDefField?.GetValue(__instance);
            if (equipmentDef == null && !PatchProjectileLaunch.UnarmedEffectsAllowed(__instance)) return;

            IntVec3 originCell = originIV3Field?.GetValue(__instance) is IntVec3 origin ? origin : __instance.Position;
            PatchProjectileImpact.PlayImpactEffects(__instance, hitThing, originCell);
        }
    }
}
