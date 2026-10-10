using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Gunplay.Patch
{


    [HarmonyPatch(typeof(Projectile), "Launch", new Type[] { typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags), typeof(bool), typeof(Thing), typeof(ThingDef) })]
    class PatchProjectileLaunch
    {
        static PropertyInfo? StartingTicksToImpactProp = typeof(Projectile).GetProperty("StartingTicksToImpact", BindingFlags.NonPublic | BindingFlags.Instance);

        // 无武器投射物的特效开关：总开关 + 仅子弹伤害过滤
        internal static bool UnarmedEffectsAllowed(Thing projectile)
        {
            if (!GunplaySettings.unarmedProjectileEffects) return false;
            if (GunplaySettings.unarmedOnlyBullets && projectile.def.projectile.damageDef != RimWorld.DamageDefOf.Bullet) return false;
            return true;
        }

        public static void Postfix(Projectile __instance, Vector3 ___destination, Thing launcher, ref Vector3 ___origin, LocalTargetInfo intendedTarget, Thing equipment, ref int ___ticksToImpact)
        {
            // 异常不许冒出去：Harmony 的前缀/后缀抛异常会中断原方法，而 Launch 是在
            // Verb_Shoot.TryCastShot 里调用的，异常会顺着 TickList 一路掀掉整个 tick。
            // 「某个投射物的轨迹材质配错了 → 那把枪打不出子弹」这种耦合不该存在：
            // 捕获后最坏结果只是这一发没有枪口前移和轨迹。
            try
            {
                RunPostfix(__instance, ___destination, launcher, ref ___origin, intendedTarget, equipment, ref ___ticksToImpact);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Gunplay] Launch postfix failed for projectile '" + (__instance.def?.defName ?? "?")
                    + "'; muzzle shift and trail registration were skipped for this shot. " + e,
                    ("GunplayLaunchPostfix" + __instance.def?.defName).GetHashCode());
            }
        }

        static void RunPostfix(Projectile __instance, Vector3 ___destination, Thing launcher, ref Vector3 ___origin, LocalTargetInfo intendedTarget, Thing equipment, ref int ___ticksToImpact)
        {
            CompGun? comp = equipment?.TryGetComp<CompGun>();
            if (comp != null)
            {
                float angle = (___destination - ___origin).AngleFlat() - (intendedTarget.CenterVector3 - ___origin).AngleFlat();
                if (angle < -180) {
                    angle += 360;
                }
                comp.RotationOffset = (angle + 180) % 360 - 180;
            }

            // 枪口前移仍由武器侧 barrelLength 决定
            GunPropDef? gunProp = GunplaySetup.GunProp(equipment);
            if (launcher as Pawn != null && gunProp != null)
            {
                ___origin += (___destination - ___origin).normalized * gunProp.barrelLength;

                // 反射取值失败时（版本变动改名）保留原 tick 数，不在这条热路径上抛异常影响轨迹登记
                if (StartingTicksToImpactProp?.GetValue(__instance) is float startingTicksToImpact)
                {
                    ___ticksToImpact = Mathf.CeilToInt(startingTicksToImpact);
                    if (___ticksToImpact < 1) ___ticksToImpact = 1;
                }
            }

            // 轨迹由投射物 def 决定（与发射武器无关）。
            // 直线投射物与弧线投射物共用这一条生成路径，具体用哪种绘制方式由
            // ProjectileTrailState 内部按“是否弧线”决定，因此多数（直线）投射物不会执行弧线相关代码。
            // 是否绘制（全局开关 + 单项覆写 + 是否解析出材质）与用哪个材质，都在一次 Resolve 里定下来
            // （覆写记录只在这里取一次，随后登记与初始化共用，见 TrailResolve）。
            TrailResolve r = ProjectileRules.Resolve(__instance.def);
            if (ProjectileRules.TrailEnabled(r, __instance.def))
            {
                // 无武器判定口径 = equipment 为 null（Launch 体内执行完后等价于 EquipmentDef == null）
                bool unarmed = equipment == null;
                if (!unarmed || UnarmedEffectsAllowed(__instance))
                {
                    // 投射物在 Launch 之前已 Spawn，本体的 Map 可作为无发射者时的兜底。
                    // 轨迹不再 spawn Thing：登记到该地图的轨迹集合，由批量绘制统一处理。
                    Map? map = __instance.Map ?? launcher?.Map;
                    if (map != null && r.trailDef != null)
                    {
                        MapComponent_ProjectileTrails.For(map)?.Register(__instance, ___destination, in r);
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Projectile), "get_StartingTicksToImpact")]
    class PatchProjectileStartingTicksToImpact
    {
        static float Postfix(float value, Projectile __instance)
        {
            // 弹速是否缩放统一由 ProjectileRules.ShouldScaleSpeed 决定：
            // 全局总开关、单项覆写、弧线规则与配置包 preserveSpeed 都在那一处判定，
            // 补丁只读结论，不再自己重复一遍优先级。
            if (!ProjectileRules.ShouldScaleSpeed(__instance.def)) return value;

            return value / GunplaySettings.projectileSpeed;
        }

    }

    [HarmonyPatch(typeof(Projectile), "Impact")]
    class PatchProjectileImpact
    {
        static void Prefix(Projectile __instance, Thing hitThing, Vector3 ___origin)
        {
            // 这个前缀绝对不能抛异常。Harmony 前缀抛异常会中断原方法，而 Destroy() 就在原方法里：
            // 投射物会永远留在原地，ticksToImpact 又不会回退，于是每个 tick 再走一遍这条路径
            // ——「每 tick 抛一次 + 当 tick 后半张地图不 tick」（TickManager 并没有把
            // tickListNormal.Tick() 包在 try/catch 里）。捕获后原方法照常执行，最坏结果是
            // 这一发少了特效/音效，甚至只是少了最终落点捕获。
            try
            {
                RunPrefix(__instance, hitThing, ___origin);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Gunplay] Impact prefix failed for projectile '" + (__instance.def?.defName ?? "?")
                    + "'; impact effects and final trail capture were skipped for this hit. " + e,
                    ("GunplayImpactPrefix" + __instance.def?.defName).GetHashCode());
            }
        }

        static void RunPrefix(Projectile __instance, Thing hitThing, Vector3 ___origin)
        {
            Map map = __instance.Map;
            if (map == null) return;

            // Impact 在 Destroy 之前执行，此时投射物仍存活，取到的就是最终落点
            MapComponent_ProjectileTrails.For(map)?.CaptureFinalPosition(__instance, __instance.ExactPosition);

            // 无武器投射物受独立开关控制（与轨迹开关保持同一判定口径）
            if (__instance.EquipmentDef == null && !PatchProjectileLaunch.UnarmedEffectsAllowed(__instance)) return;

            PlayImpactEffects(__instance, hitThing, ___origin.ToIntVec3());
        }

        // 命中特效/音效。原版与 CE 兼容补丁（PatchCECompat）共用；
        // originCell 为发射点所在格，仅作为特效触发参考。
        internal static void PlayImpactEffects(Thing projectile, Thing hitThing, IntVec3 originCell)
        {
            Map map = projectile.Map;

            // 命中特效/音效由投射物 def 决定
            ProjectilePropDef? prop = GunplaySetup.ProjectileProp(projectile.def) ?? GunplaySetup.DefaultProjectileProp;
            if (prop == null) return;

            MaterialKind kind = MaterialKind.None;

            if (hitThing != null)
            {
                kind = MaterialKindGetter.Get(hitThing);
            }

            if (kind == MaterialKind.None)
            {
                // CellToIndex 不做边界检查，TerrainAt 是裸数组访问——越界即 IndexOutOfRangeException。
                // 原版在 TickInterval 开头有 !ExactPosition.InBounds 分支挡着（直接 Destroy、不走
                // Impact），但 Position 是在 TickInterval 末尾才更新的，中间那段
                // CheckForFreeInterceptBetween → Impact(thing) 用的还是上一 tick 的格子，
                // 加上 CE 路径与自己的 barrelLength 原点前移，这个不变量不值得赌。
                // 越界时保持 MaterialKind.None：不播特效/音效，仅此而已。
                IntVec3 impactCell = projectile.Position;
                if (impactCell.InBounds(map))
                {
                    TerrainDef terrainDef = map.terrainGrid.TerrainAt(CellIndicesUtility.CellToIndex(impactCell, map.Size.x));
                    kind = MaterialKindGetter.Get(terrainDef);
                }
            }

            if (GunplaySettings.enableSounds)
            {
                SoundDef? sound = prop.projectileImpactSound == null ? null : prop.projectileImpactSound.Effect(kind);
                if (sound != null) sound.PlayOneShot(new TargetInfo(projectile.Position, map, false));
            }

            if (GunplaySettings.enableEffects)
            {
                EffecterDef? effecterDef = prop.projectileImpactEffect == null ? null : prop.projectileImpactEffect.Effect(kind);
                if (effecterDef != null)
                {
                    Effecter effecter = new Effecter(effecterDef);
                    effecter.Trigger(projectile, new TargetInfo(originCell, map));
                    effecter.Cleanup();
                }
            }
        }
    }


}
