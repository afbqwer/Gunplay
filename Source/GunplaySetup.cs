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

            // 拖拽涂抹的光标图标：同样是可缺省的装饰，指标丢了不影响功能
            PatchDrawDragPaintIcon.TryPatch(harmony);

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

        // 构建投射物映射表。解析优先级：defTargets > weaponTargets > 名称关键字规则 > default。
        static void BuildProjectileProps()
        {
            if (defaultProjectileDef == null)
            {
                Log.Error("[Gunplay] Missing ProjectilePropDef 'default'; projectile trails and impact effects are disabled.");
                return;
            }

            // 1) 显式配置：defTargets 直接给投射物，weaponTargets 给武器（经其射击 verb 反查投射物）。
            //    两个列表互相不排斥，可以同时写，解析结果一起登记；键始终是投射物——
            //    粒度没变：多把武器共用同一投射物时共享同一条记录（见 PatchProjectileLaunch）。
            foreach (ProjectilePropDef def in DefDatabase<ProjectilePropDef>.AllDefs)
            {
                if (def == defaultProjectileDef) continue;

                foreach (ThingDef projectile in ResolveExplicitTargets(def))
                {
                    AssignExplicitTarget(projectile, def);
                }
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
                    Log.Message("[Gunplay] Projectile '" + def.defName + "' matched multiple name-match keywords. "
                        + "The longer keyword '" + winnerKeyword + "' wins; overridden keyword(s): '"
                        + string.Join("', '", shadowed) + "'. Adjust ProjectilePropDef.matchKeywords to remove the ambiguity.");
                }

                projectilePropMap[def] = matched ?? defaultProjectileDef;
            }
        }

        // 把一条显式配置解析成它要作用的一组投射物。defTargets / weaponTargets 是仅有的两条显式途径，
        // 解析结果为空就说明本包没有显式目标，交给后面的名称关键字规则。
        // 契约：返回的每个元素都满足 projectile != null。
        //
        // 这里刻意**不**回退到「与本包同名的投射物」：defTargets 已经把「显式指定」表达完整了，
        // 再留一条按名字隐式命中的路只会多一种说不清的绑定方式（同名恰好指向一个投射物时悄悄生效）。
        // 武器侧的同名约定仍然保留——那边的 XML 正是靠它绑定原版 ThingDef（如 Gun_Minigun）。
        //
        // 所有失败都只报错并跳过该条目，不抛异常：配置写错不该让整个投射物侧解析中断，
        // 剩下的投射物照常套 default。
        static List<ThingDef> ResolveExplicitTargets(ProjectilePropDef def)
        {
            List<ThingDef> targets = new List<ThingDef>();

            if (def.defTargets != null)
            {
                foreach (string name in def.defTargets)
                {
                    if (name.NullOrEmpty()) continue;

                    ThingDef? projectile = DefDatabase<ThingDef>.GetNamed(name, false);
                    if (projectile == null)
                    {
                        Log.Error("[Gunplay] ProjectilePropDef '" + def.defName
                            + "': defTargets entry '" + name + "' not found.");
                        continue;
                    }
                    if (projectile.projectile == null)
                    {
                        Log.Error("[Gunplay] ProjectilePropDef '" + def.defName + "': defTargets entry '" + name
                            + "' is not a projectile (no <projectile> block); skipped.");
                        continue;
                    }

                    if (!targets.Contains(projectile)) targets.Add(projectile);
                }
            }

            if (def.weaponTargets != null)
            {
                foreach (string name in def.weaponTargets)
                {
                    if (name.NullOrEmpty()) continue;

                    ThingDef? weapon = DefDatabase<ThingDef>.GetNamed(name, false);
                    if (weapon == null)
                    {
                        Log.Error("[Gunplay] ProjectilePropDef '" + def.defName
                            + "': weaponTargets entry '" + name + "' not found.");
                        continue;
                    }

                    ThingDef? projectile = ProjectileOfWeapon(weapon);
                    if (projectile == null)
                    {
                        Log.Error("[Gunplay] ProjectilePropDef '" + def.defName + "': weaponTargets entry '" + name
                            + "' has no Verb_Shoot with a defaultProjectile; cannot resolve a projectile.");
                        continue;
                    }

                    if (!targets.Contains(projectile)) targets.Add(projectile);
                }
            }

            return targets;
        }

        // 从一把武器解析出它发射的投射物：取第一个 Verb_Shoot 的 defaultProjectile。
        // 判据与上面武器侧注册（typeof(Verb_Shoot).IsAssignableFrom(v.verbClass)）保持一致。
        // 一把武器挂多个 shoot verb 时只认第一个——投射物才是本层的键，按 verb 区分表达不了。
        static ThingDef? ProjectileOfWeapon(ThingDef weapon)
        {
            VerbProperties? shoot = weapon.Verbs.FirstOrDefault(v =>
                typeof(Verb_Shoot).IsAssignableFrom(v.verbClass));

            return shoot?.defaultProjectile;
        }

        // 登记一条显式配置。同一个投射物被两条显式配置命中时后者覆盖前者并告警——
        // 轨迹只由投射物决定，静默覆盖会让人以为两条都生效了。
        static void AssignExplicitTarget(ThingDef projectile, ProjectilePropDef def)
        {
            if (projectilePropMap.TryGetValue(projectile, out ProjectilePropDef? existing) && existing != def)
            {
                Log.Warning("[Gunplay] Projectile '" + projectile.defName + "' is explicitly targeted by both '"
                    + existing.defName + "' and '" + def.defName + "'; the latter wins.");
            }

            projectilePropMap[projectile] = def;
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
