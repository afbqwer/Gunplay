using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹宽度的统一口径：运行时与设置界面共用同一份公式，避免两处各写一遍后慢慢跑偏。
    //
    // 口径是「四边形宽度」——几何就是一块按 (width, 1, len) 缩放的 plane10，alpha 轮廓只在其中决定
    // 哪些像素看得见。材质里的宽度轮廓参数是**相对比例**（见 TrailMaterialParams.headWidth / tailWidth），
    // 生成时按两端较大者归一化到 1，最宽端必然贴住四边形边缘，因此这里不需要为轮廓做任何补偿。
    //
    // 优先级（高 → 低）：
    //   1) 单项覆写（覆写窗口设置）—— 绝对值，完全独立：既不看伤害/贴图，也不乘全局系数；
    //   2) 材质（ProjectileTrailDef 的 material.trailWidth，XML 或材质窗口编辑）—— 绝对值，之后仍乘全局系数；
    //   3) 基础宽度 —— 按伤害 min(damage,50)*0.006 或按贴图 drawSize.x*0.1，之后乘全局系数。
    public static class TrailWidth
    {

        // 覆写是否命中（命中即完全接管，调用方不应再叠加任何全局系数）。
        // 与 TrailLength / TrailFade 同形：命中判定与 0 哨兵的处理都在 ProjectileOverrides 里。
        // 以下入参的 projectileDef 一律可为 null：命中多态/CE 投射物时不一定拿得到 ThingDef，
        // 传 null 表示「无法解析」，各方法按 0 宽度或全局默认值处理，不抛异常。
        public static bool TryOverride(ThingDef? projectileDef, out float width)
        {
            return ProjectileOverrides.TryGetTrailWidth(projectileDef, out width);
        }

        // 基础宽度（伤害 / 贴图口径），不含材质覆盖与全局系数。
        public static float Base(ThingDef? projectileDef, float damage)
        {
            if (projectileDef == null) return 0f;

            if (GunplaySettings.trailWidthMode == TrailWidthMode.Texture)
            {
                // 投射物贴图经 GridPlane(drawSize) 旋转绘制，drawSize.x 为垂直于飞行方向的横向宽度
                GraphicData graphicData = projectileDef.graphicData;
                return (graphicData != null ? graphicData.drawSize.x : 1f) * 0.1f;
            }

            return Mathf.Min(damage, 50f) * 0.006f;
        }

        // 运行时最终宽度。trailDef 为该投射物解析出的 ProjectileTrailDef（可能为 null）。
        public static float Effective(ThingDef? projectileDef, float damage, ProjectileTrailDef? trailDef)
        {
            if (TryOverride(projectileDef, out float w)) return w;

            float width = Base(projectileDef, damage);

            // 材质层：绝对值口径，-1 表示未设定（继续走基础宽度）
            float materialWidth = TrailMaterial.Effective(trailDef).trailWidth;
            if (materialWidth > 0f) width = materialWidth;

            return width * GunplaySettings.trailWidthFactor;
        }

        // 设置界面口径：无法知道实际开火的武器，因此按无武器加成解析伤害（见 BaseDamage）；
        // 材质同样按该投射物当前生效的材质解析（含单项覆写选的材质）。
        public static float EffectiveForDef(ThingDef? projectileDef)
        {
            if (TryOverride(projectileDef, out float w)) return w;

            return Effective(projectileDef, BaseDamage(projectileDef), ProjectileRules.TrailDefFor(projectileDef));
        }

        // 伤害必须走游戏自己的解析，不能直接读 ProjectileProperties.damageAmountBase：
        // 该字段未声明时默认值是 -1，含义是「取 damageDef.defaultDamage」（本体 ProjectileProperties
        // 字段默认值 -1，GetDamageAmount 里 if (damageAmountBase != -1) ... else num = damageDef.defaultDamage）。
        // 直接读字段会把 -1 当成伤害，算出 min(-1,50)*0.006 = -0.006 的负宽度。
        //
        // 这里用公开重载 GetDamageAmount(1f, null, null)：伤害倍率取 1、不填 explanation，
        // 等价于「无武器加成的基础伤害」，与运行时（带武器 RangedWeapon_DamageMultiplier）
        // 可能有小幅差异；覆写值本身是绝对值，一旦开启就不受该近似影响。
        static float BaseDamage(ThingDef? projectileDef)
        {
            if (projectileDef?.projectile == null || projectileDef?.projectile.damageDef == null) return 0f;
            return projectileDef.projectile.GetDamageAmount(1f, null, null);
        }
    }
}
