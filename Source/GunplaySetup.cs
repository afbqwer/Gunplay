﻿﻿﻿using Gunplay.Patch;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

        // 缺该项时 BuildProjectileProps 会报错并跳过整个投射物侧解析。
        static ProjectilePropDef? defaultProjectileDef = DefDatabase<ProjectilePropDef>.GetNamed("default", false);

        static GunplaySetup()
        {
            var harmony = new Harmony("com.github.automatic1111.gunplay");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // CE 软兼容（反射，无硬依赖）：CE 的 ProjectileCE 不继承原版 Projectile，
            // 需要把轨迹与命中特效/音效补到 CE 自己的方法上；未安装 CE 时自动跳过
            PatchCECompat.TryPatch(harmony);

            // 轨迹批量绘制：显式挂载（目标方法缺失时只报错并禁用轨迹，不抛异常）
            PatchDynamicDrawTrails.TryPatch(harmony);

            foreach (GunPropDef def in DefDatabase<GunPropDef>.AllDefs)
            {
                ThingDef? target = null;

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

                // 上一行已保证键存在（未显式配置者填 defaultDef），这里用 BCL 的 TryGetValue：
                // Verse 的同名扩展对字典的可空签名有要求，传 null 兜底值会触发 CS8620。
                if (!propMap.TryGetValue(def, out GunPropDef? prop)) continue;

                if (prop.soundAiming != null) shoot.soundAiming = prop.soundAiming;
                if (prop.soundCast != null) shoot.soundCast = prop.soundCast;
                if (prop.spinner != null) def.comps.Add(prop.spinner);
                if (prop.primer != null) def.comps.Add(prop.primer);
                if (prop.bow != null) def.comps.Add(prop.bow);
            }

            BuildProjectileProps();

            ImpactEffectTuner.Apply();
        }

        // 构建投射物映射表。解析优先级：显式 defTarget / 同名 defName > 名称关键字规则 > default。
        static void BuildProjectileProps()
        {
            if (defaultProjectileDef == null)
            {
                Log.Error("[Gunplay] Missing ProjectilePropDef 'default'; projectile trails and impact effects are disabled.");
                return;
            }

            // 1) 显式配置：defTarget（缺省回退 defName）指向投射物 ThingDef
            foreach (ProjectilePropDef def in DefDatabase<ProjectilePropDef>.AllDefs)
            {
                if (def == defaultProjectileDef) continue;

                ThingDef? target = null;
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

            // 3) 收集名称匹配规则（来自 ProjectilePropDef.matchKeywords，即 XML 声明）
            //    关键字更长者优先匹配，因此更具体的关键字总能胜出（即：更长者会“覆盖”更短者）。
            List<(string keyword, ProjectilePropDef prop)> rules =
                (from def in DefDatabase<ProjectilePropDef>.AllDefs
                 where def.matchKeywords != null
                 from keyword in def.matchKeywords
                 where !string.IsNullOrEmpty(keyword)
                 select (keyword, prop: def))
                .OrderByDescending(r => r.keyword.Length)
                .ToList();

            // 4) 扫描全部投射物：未显式配置者按名称规则，仍未命中者套 default
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs)
            {
                if (def.projectile == null) continue;
                if (projectilePropMap.ContainsKey(def)) continue;

                // 收集 defName 命中的全部关键字；若命中多个，则更短者被更长者覆盖，需要报错提示。
                ProjectilePropDef? matched = null;
                string? winnerKeyword = null;
                List<string>? shadowed = null;

                if (def.defName != null)
                {
                    foreach (var rule in rules)
                    {
                        if (def.defName.IndexOf(rule.keyword, StringComparison.OrdinalIgnoreCase) < 0) continue;

                        if (matched == null)
                        {
                            matched = rule.prop;
                            winnerKeyword = rule.keyword;
                        }
                        else if (!string.Equals(rule.keyword, winnerKeyword, StringComparison.OrdinalIgnoreCase))
                        {
                            (shadowed ?? (shadowed = new List<string>())).Add(rule.keyword);
                        }
                    }
                }

                if (shadowed != null)
                {
                    Log.Warning("[Gunplay] Projectile '" + def.defName + "' matched multiple name-match keywords. "
                        + "The longer keyword '" + winnerKeyword + "' wins; overridden keyword(s): '"
                        + string.Join("', '", shadowed) + "'. Adjust ProjectilePropDef.matchKeywords to remove the ambiguity.");
                }

                projectilePropMap[def] = matched ?? defaultProjectileDef;
            }
        }

        // 未注册的武器返回 null（正常带 shoot verb 的武器在启动时已全部注册）
        public static GunPropDef? GunProp(ThingDef? equipment)
        {
            if (equipment == null) return null;

            return propMap.TryGetValue(equipment, out GunPropDef? prop) ? prop : null;
        }

        public static GunPropDef? GunProp(Thing? equipment)
        {
            if (equipment?.def == null) return null;

            return propMap.TryGetValue(equipment.def, out GunPropDef? prop) ? prop : defaultDef;
        }

        public static GunPropDef DefaultProp => defaultDef;

        // ---------------- 投射物侧入口 ----------------

        // 未注册的投射物返回 null（正常投射物在启动时已全部注册）
        public static ProjectilePropDef? ProjectileProp(ThingDef? projectileDef)
        {
            if (projectileDef == null) return null;

            return projectilePropMap.TryGetValue(projectileDef, out ProjectilePropDef? prop) ? prop : null;
        }

        public static ProjectilePropDef? DefaultProjectileProp => defaultProjectileDef;
    }
}
