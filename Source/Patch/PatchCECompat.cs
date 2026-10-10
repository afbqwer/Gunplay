using System;
using System.Collections.Generic;
using System.Reflection;
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

        // ExactPosToDrawPos 按 worker 运行时类型缓存（worker 实例随 def 而异，方法随类型固定）
        static readonly Dictionary<Type, MethodInfo> drawMethodCache = new Dictionary<Type, MethodInfo>();

        // CE 投射物存活期间的实时位置读取器（由轨迹每 tick 调用）
        internal static Func<Vector3> CreateExactPositionGetter(Thing proj)
        {
            // 反射取值失败时退回本体绘制位置，而不是在某次版本失配后每 tick 抛异常
            return () => exactPositionProp?.GetValue(proj) is Vector3 pos ? pos : proj.DrawPos;
        }

        internal static float GetDamageAmount(Thing proj)
        {
            return damageAmountProp?.GetValue(proj) is float damage ? damage : 10f;
        }

        internal static float GetShotAngle(Thing proj)
        {
            return shotAngleField?.GetValue(proj) is float angle ? angle : 0f;
        }

        // ---------------- 绘制位置（CE 高度映射）----------------

        static MethodInfo? GetDrawMethod(Type workerType)
        {
            if (!drawMethodCache.TryGetValue(workerType, out MethodInfo? method))
            {
                method = AccessTools.Method(workerType, "ExactPosToDrawPos");
                drawMethodCache[workerType] = method;
            }
            return method;
        }

        // 经 worker 的 ExactPosToDrawPos 计算投射物当前的实际绘制位置（y 为 altitude 层，z 含高度偏移）
        internal static Vector3 GetDrawPosition(Thing proj)
        {
            Vector3 exact = exactPositionProp?.GetValue(proj) is Vector3 pos ? pos : proj.DrawPos;
            object? worker = trajectoryWorkerProp?.GetValue(proj.def.projectile);
            int flightTicks = flightTicksField?.GetValue(proj) is int ticks ? ticks : 0;
            return DrawViaWorker(proj, worker, exact, flightTicks);
        }

        static Vector3 DrawViaWorker(Thing proj, object? worker, Vector3 exact, int flightTicks)
        {
            float altitude = proj.def.Altitude;
            MethodInfo? draw = worker != null ? GetDrawMethod(worker.GetType()) : null;
            if (draw == null) return new Vector3(exact.x, altitude, exact.z); // 反射失败兜底：地面投影

            object[] args =
            {
                exact,
                flightTicks,
                ticksToTruePositionField?.GetValue(proj) is int trueTicks ? trueTicks : 0,
                altitude
            };
            return (Vector3)draw.Invoke(worker, args);
        }

        // 发射时的预测飞行时长（tick），用于弧线采样
        internal static int GetStartingTicksToImpact(Thing proj)
        {
            return startingTicksToImpactField != null ? Mathf.CeilToInt((float)startingTicksToImpactField.GetValue(proj)) : 0;
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
            Vector3 launchExact = exactPositionProp?.GetValue(proj) is Vector3 launch ? launch : proj.DrawPos;
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
            Vector2 dest2 = destinationField != null ? (Vector2)destinationField.GetValue(proj) : new Vector2(last.x, last.z);
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
            // 是否绘制（全局开关 + 单项覆写 + XML 是否配了 trail）统一由 ProjectileRules.TrailEnabled 决定
            if (!ProjectileRules.TrailEnabled(__instance.def)) return;

            // 无武器投射物判定口径与原版一致：equipment 为 null
            bool unarmed = equipment == null;
            if (unarmed && !PatchProjectileLaunch.UnarmedEffectsAllowed(__instance)) return;

            // 直线轨迹模式下，抛射类投射物（迫击炮弹/榴弹等大仰角发射）不显示轨迹
            if (GunplaySettings.ceStraightTrails && GetShotAngle(__instance) > ArcAngleThreshold) return;

            // 投射物在 Launch/Throw 之前已 Spawn，本体的 Map 可作为无发射者时的兜底
            Map? map = __instance.Map ?? launcher?.Map;
            ProjectileTrailDef? trailDef = ProjectileRules.TrailDefFor(__instance.def);
            if (map == null || trailDef == null) return;

            // 轨迹不再 spawn Thing：登记到该地图的轨迹集合，由批量绘制统一处理。
            // CE 的 Destination 是发射时预测的落点（零高度），仅提供轨迹初始方向/弧线几何；
            // 直线轨迹的实际方向随后每 tick 由投射物真实位移校正。
            Vector2 dest2 = destinationField?.GetValue(__instance) is Vector2 dest ? dest : new Vector2(__instance.DrawPos.x, __instance.DrawPos.z);
            MapComponent_ProjectileTrails.For(map)?.RegisterCE(__instance, new Vector3(dest2.x, 0f, dest2.y), trailDef);
        }

        static void ImpactPrefix(Thing __instance, Thing hitThing)
        {
            Map map = __instance.Map;
            if (map == null) return;

            // Impact 在 Destroy 之前执行，此时投射物仍存活，取到的就是最终落点
            //（由轨迹自行决定取地面位置还是绘制位置，与飞行中的跟踪口径一致）
            MapComponent_ProjectileTrails.For(map)?.CaptureFinalPositionCE(__instance);

            // 无武器投射物受独立开关控制（与轨迹开关保持同一判定口径）
            object? equipmentDef = equipmentDefField?.GetValue(__instance);
            if (equipmentDef == null && !PatchProjectileLaunch.UnarmedEffectsAllowed(__instance)) return;

            IntVec3 originCell = originIV3Field != null ? (IntVec3)originIV3Field.GetValue(__instance) : __instance.Position;
            PatchProjectileImpact.PlayImpactEffects(__instance, hitThing, originCell);
        }
    }
}
