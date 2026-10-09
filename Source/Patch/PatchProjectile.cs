using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
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

        static readonly ConditionalWeakTable<Projectile, ProjectileTrail> trails = new ConditionalWeakTable<Projectile, ProjectileTrail>();

        public static bool TryGetTrail(Projectile proj, out ProjectileTrail trail)
        {
            return trails.TryGetValue(proj, out trail);
        }

        // 无武器投射物的特效开关：总开关 + 仅子弹伤害过滤
        internal static bool UnarmedEffectsAllowed(Projectile projectile)
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

            GunPropDef prop = GunplaySetup.GunProp(equipment);
            if (launcher as Pawn != null && prop != null)
            {
                ___origin += (___destination - ___origin).normalized * prop.barrelLength;
                ___ticksToImpact = Mathf.CeilToInt((float) StartingTicksToImpactProp.GetValue(__instance));
                if (___ticksToImpact < 1) ___ticksToImpact = 1;
            }

            if (Gunplay.settings.enableTrails && __instance.def.projectile.arcHeightFactor <= 0.1f)
            {
                // GunProp(Thing) 对任何有 def 的装备都有默认回退，故 prop == null ⟺ 无武器投射物
                bool unarmed = prop == null;
                if (!unarmed || UnarmedEffectsAllowed(__instance))
                {
                    // 投射物在 Launch 之前已 Spawn，本体的 Map 可作为无发射者时的兜底
                    Map map = __instance.Map ?? launcher?.Map;
                    GunPropDef trailProp = prop ?? GunplaySetup.DefaultProp;
                    if (map != null && trailProp?.trail != null)
                    {
                        ProjectileTrail trail = GenSpawn.Spawn(trailProp.trail, ___origin.ToIntVec3(), map, WipeMode.Vanish) as ProjectileTrail;
                        if (trail != null)
                        {
                            trail.Initialize(__instance, ___destination, equipment);
                            trails.Remove(__instance);
                            trails.Add(__instance, trail);
                        }
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
            GunPropDef prop = GunplaySetup.GunProp(__instance.EquipmentDef);
            if (prop == null || prop.preserveSpeed) return value;

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
            if (PatchProjectileLaunch.TryGetTrail(__instance, out ProjectileTrail trail))
                trail.CaptureFinalPosition(__instance.ExactPosition);

            // 无武器投射物受独立开关控制（与轨迹开关保持同一判定口径）
            if (__instance.EquipmentDef == null && !PatchProjectileLaunch.UnarmedEffectsAllowed(__instance)) return;

            // 无发射者/无装备的投射物（EquipmentDef 为 null 或未配置）回退到默认属性
            GunPropDef prop = GunplaySetup.GunProp(__instance.EquipmentDef) ?? GunplaySetup.DefaultProp;
            if (prop == null) return;

            MaterialKind kind = MaterialKind.None;

            if (hitThing != null)
            {
                kind = MaterialKindGetter.Get(hitThing);
            }

            if(kind == MaterialKind.None)
            {
                TerrainDef terrainDef = map.terrainGrid.TerrainAt(CellIndicesUtility.CellToIndex(__instance.Position, map.Size.x));
                kind = MaterialKindGetter.Get(terrainDef);
            }

            if (Gunplay.settings.enableSounds) {
                SoundDef sound = prop.projectileImpactSound == null ? null : prop.projectileImpactSound.Effect(kind);
                if (sound != null) sound.PlayOneShot(new TargetInfo(__instance.Position, map, false));
            }

            if (Gunplay.settings.enableEffects)
            {
                EffecterDef effecterDef = prop.projectileImpactEffect == null ? null : prop.projectileImpactEffect.Effect(kind);
                if (effecterDef != null)
                {
                    Effecter effecter = new Effecter(effecterDef);
                    effecter.Trigger(__instance, new TargetInfo(___origin.ToIntVec3(), __instance.Map));
                    effecter.Cleanup();
                }
            }
        }
    }


}
