using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Verse;

namespace Gunplay
{
    [StaticConstructorOnStartup]
    public class GunplaySetup
    {
        // ---------------- 武器侧 ----------------
        static Dictionary<ThingDef, GunPropDef> propMap = new Dictionary<ThingDef, GunPropDef>();
        static GunPropDef defaultDef = DefDatabase<GunPropDef>.GetNamed("default");

        // ---------------- 投射物侧 ----------------
        static Dictionary<ThingDef, ProjectilePropDef> projectilePropMap = new Dictionary<ThingDef, ProjectilePropDef>();
        static ProjectilePropDef defaultProjectileDef = DefDatabase<ProjectilePropDef>.GetNamed("default", false);

        // 名称匹配规则：投射物 defName 含 keyword（忽略大小写）→ 对应 ProjectilePropDef。
        // 按顺序匹配，首个命中生效。这是唯一的扩展点：加规则只需加一行 + 补一个 ProjectilePropDef。
        static readonly (string keyword, string propDefName)[] projectileNameRules =
        {
            ("charge", "charge"),         // Bullet_ChargeRifle / ChargeLance / ChargeBlasterHeavy
            ("incendiary", "incendiary"), // Bullet_IncendiaryLauncher（Bullet_Shell_Incendiary 为弧线，不生成轨迹）
            ("smoke", "medium"),          // Bullet_SmokeLauncher（Bullet_Shell_Smoke 为弧线，不生成轨迹）
            ("shotgun", "wide"),          // Bullet_Shotgun / Bullet_MiniShotgun
            ("arrow", "arrow"),           // Arrow_Recurve / Arrow_Short / Arrow_Great / Proj_FireArrow
        };

        // 启动时把 propDefName 解析为 ProjectilePropDef，运行期只做字符串 contains。
        static readonly List<(string keyword, ProjectilePropDef prop)> resolvedProjectileRules
            = new List<(string keyword, ProjectilePropDef prop)>();

        static GunplaySetup()
        {
            var harmony = new Harmony("com.github.automatic1111.gunplay");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            foreach (ProjectileTrailDef def in DefDatabase<ProjectileTrailDef>.AllDefs)
            {
                if (!def.DrawMatSingle || !def.DrawMatSingle.mainTexture) continue;

                def.DrawMatSingle.mainTexture.wrapMode = TextureWrapMode.Clamp;
            }

            foreach (GunPropDef def in DefDatabase<GunPropDef>.AllDefs)
            {
                ThingDef target = null;

                if (def.defTarget != null) target = DefDatabase<ThingDef>.GetNamed(def.defTarget, false);
                if (target == null) target = DefDatabase<ThingDef>.GetNamed(def.defName, false);
                if (target != null) propMap[target] = def;

                if (def.barrelLength == -1) def.barrelLength = defaultDef.barrelLength;
            }

            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                VerbProperties shoot = def.Verbs.FirstOrDefault(v => 
                typeof(Verb_Shoot).IsAssignableFrom(v.verbClass));
                if (shoot == null) continue;
                if (!propMap.ContainsKey(def)) propMap[def] = defaultDef;

                def.comps.Add(new CompProperties() { compClass = typeof(CompGun) });

                GunPropDef prop = propMap.TryGetValue(def);
                if (prop != null)
                {
                    if (prop.soundAiming != null) shoot.soundAiming = prop.soundAiming;
                    if (prop.soundCast != null) shoot.soundCast = prop.soundCast;
                    if (prop.spinner != null) def.comps.Add(prop.spinner);
                    if (prop.primer != null) def.comps.Add(prop.primer);
                    if (prop.bow != null) def.comps.Add(prop.bow);
                }
            }

            BuildProjectileProps();

            ImpactEffectTuner.Apply();
        }

        // 构建投射物映射表。解析优先级：显式 defTarget / 同名 defName > 名称关键字规则 > default。
        static void BuildProjectileProps()
        {
            if (defaultProjectileDef == null)
            {
                Log.Error("[Gunplay] 缺少 ProjectilePropDef 'default'，投射物轨迹/命中特效已禁用。");
                return;
            }

            // 1) 显式配置：defTarget（缺省回退 defName）指向投射物 ThingDef
            foreach (ProjectilePropDef def in DefDatabase<ProjectilePropDef>.AllDefs)
            {
                if (def == defaultProjectileDef) continue;

                ThingDef target = null;
                if (def.defTarget != null) target = DefDatabase<ThingDef>.GetNamed(def.defTarget, false);
                if (target == null) target = DefDatabase<ThingDef>.GetNamed(def.defName, false);

                if (target != null && target.projectile != null) projectilePropMap[target] = def;
            }

            // 2) 包内字段级继承 default（保持既有“未配置即继承默认”的行为）
            foreach (ProjectilePropDef def in DefDatabase<ProjectilePropDef>.AllDefs)
            {
                if (def == defaultProjectileDef) continue;

                if (def.trail == null) def.trail = defaultProjectileDef.trail;
                if (def.projectileImpactEffect == null) def.projectileImpactEffect = defaultProjectileDef.projectileImpactEffect;
                if (def.projectileImpactSound == null) def.projectileImpactSound = defaultProjectileDef.projectileImpactSound;
            }

            // 3) 解析名称规则
            foreach (var (keyword, propDefName) in projectileNameRules)
            {
                ProjectilePropDef prop = DefDatabase<ProjectilePropDef>.GetNamed(propDefName, false);
                if (prop == null)
                {
                    Log.Warning("[Gunplay] 投射物规则 '" + keyword + "' 指向的 ProjectilePropDef '" + propDefName + "' 不存在，已跳过。");
                    continue;
                }
                resolvedProjectileRules.Add((keyword, prop));
            }

            // 4) 扫描全部投射物：未显式配置者按名称规则，仍未命中者套 default
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.projectile == null) continue;
                if (projectilePropMap.ContainsKey(def)) continue;

                ProjectilePropDef prop = null;
                if (def.defName != null)
                {
                    foreach (var (keyword, ruleProp) in resolvedProjectileRules)
                    {
                        if (def.defName.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            prop = ruleProp;
                            break;
                        }
                    }
                }

                projectilePropMap[def] = prop ?? defaultProjectileDef;
            }
        }

        public static GunPropDef GunProp(ThingDef equipment)
        {
            if (equipment == null) return null;

            return propMap.TryGetValue(equipment, null);
        }

        public static GunPropDef GunProp(Thing equipment)
        {
            if (equipment?.def == null) return null;

            return propMap.TryGetValue(equipment.def, defaultDef);
        }

        public static GunPropDef DefaultProp => defaultDef;

        // ---------------- 投射物侧入口 ----------------

        // 未注册的投射物返回 null（正常投射物在启动时已全部注册）
        public static ProjectilePropDef ProjectileProp(ThingDef projectileDef)
        {
            if (projectileDef == null) return null;

            return projectilePropMap.TryGetValue(projectileDef, null);
        }

        public static ProjectilePropDef DefaultProjectileProp => defaultProjectileDef;
    }
}
