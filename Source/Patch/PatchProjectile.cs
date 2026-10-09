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
        static PropertyInfo StartingTicksToImpactProp = typeof(Projectile).GetProperty("StartingTicksToImpact", BindingFlags.NonPublic | BindingFlags.Instance);

        // 无武器投射物的特效开关：总开关 + 仅子弹伤害过滤
        internal static bool UnarmedEffectsAllowed(Thing projectile)
        {
            if (!Gunplay.settings.unarmedProjectileEffects) return false;
            if (Gunplay.settings.unarmedOnlyBullets && projectile.def.projectile.damageDef != RimWorld.DamageDefOf.Bullet) return false;
            return true;
        }

        public static void Postfix(Projectile __instance, Vector3 ___destination, Thing launcher, ref Vector3 ___origin, LocalTargetInfo intendedTarget, Thing equipment, ref int ___ticksToImpact)
        {
            CompGun comp = equipment?.TryGetComp<CompGun>();
            if (comp != null)
            {
                float angle = (___destination - ___origin).AngleFlat() - (intendedTarget.CenterVector3 - ___origin).AngleFlat();
                if (angle < -180) {
                    angle += 360;
                }
                comp.RotationOffset = (angle + 180) % 360 - 180;
            }

            // 枪口前移仍由武器侧 barrelLength 决定
            GunPropDef gunProp = GunplaySetup.GunProp(equipment);
            if (launcher as Pawn != null && gunProp != null)
            {
                ___origin += (___destination - ___origin).normalized * gunProp.barrelLength;
                ___ticksToImpact = Mathf.CeilToInt((float) StartingTicksToImpactProp.GetValue(__instance));
                if (___ticksToImpact < 1) ___ticksToImpact = 1;
            }

            // 轨迹由投射物 def 决定（与发射武器无关）。
            // 直线投射物与弧线投射物共用这一条生成路径，具体用哪种绘制方式由
            // ProjectileTrailState 内部按“是否弧线”决定，因此多数（直线）投射物不会执行弧线相关代码。
            if (Gunplay.settings.enableTrails)
            {
                // 无武器判定口径 = equipment 为 null（Launch 体内执行完后等价于 EquipmentDef == null）
                bool unarmed = equipment == null;
                if (!unarmed || UnarmedEffectsAllowed(__instance))
                {
                    // 投射物在 Launch 之前已 Spawn，本体的 Map 可作为无发射者时的兜底。
                    // 轨迹不再 spawn Thing：登记到该地图的轨迹集合，由批量绘制统一处理。
                    Map map = __instance.Map ?? launcher?.Map;
                    ProjectilePropDef prop = GunplaySetup.ProjectileProp(__instance.def) ?? GunplaySetup.DefaultProjectileProp;
                    if (map != null && prop?.trail != null)
                    {
                        MapComponent_ProjectileTrails.For(map)?.Register(__instance, ___destination, prop.trail);
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
            ProjectilePropDef prop = GunplaySetup.ProjectileProp(__instance.def);
            if (prop != null && prop.preserveSpeed) return value;

            // 独立排除机制：弧线投射物（手雷/炮弹）不受全局速度缩放影响
            if (!ProjectileRules.ShouldScaleSpeed(__instance.def)) return value;

            return value / Gunplay.settings.projectileSpeed;
        }

    }

    [HarmonyPatch(typeof(Projectile), "Impact")]
    class PatchProjectileImpact
    {
        static void Prefix(Projectile __instance, Thing hitThing, Vector3 ___origin)
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
            ProjectilePropDef prop = GunplaySetup.ProjectileProp(projectile.def) ?? GunplaySetup.DefaultProjectileProp;
            if (prop == null) return;

            MaterialKind kind = MaterialKind.None;

            if (hitThing != null)
            {
                kind = MaterialKindGetter.Get(hitThing);
            }

            if (kind == MaterialKind.None)
            {
                TerrainDef terrainDef = map.terrainGrid.TerrainAt(CellIndicesUtility.CellToIndex(projectile.Position, map.Size.x));
                kind = MaterialKindGetter.Get(terrainDef);
            }

            if (Gunplay.settings.enableSounds)
            {
                SoundDef sound = prop.projectileImpactSound == null ? null : prop.projectileImpactSound.Effect(kind);
                if (sound != null) sound.PlayOneShot(new TargetInfo(projectile.Position, map, false));
            }

            if (Gunplay.settings.enableEffects)
            {
                EffecterDef effecterDef = prop.projectileImpactEffect == null ? null : prop.projectileImpactEffect.Effect(kind);
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
