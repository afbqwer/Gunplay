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
    // internal：运行时与设置界面共用的内部口径，不对外暴露（入参携带内部解析状态 TrailResolve）。
    internal static class TrailWidth
    {
        // 手动宽度输入（材质绝对值 / 单项覆写）的合法区间，与两个编辑窗口共用；
        // 运行时也用它给所有输入（含基础宽度）设上限。
        // 上限不是物理极限，而是「异常配置别把合并网格撑爆」的保险：编辑器本来就只允许到 2。
        internal const float MinWidth = 0.01f;
        internal const float MaxWidth = 2f;

        // 全局宽度倍率的合法区间，与设置界面的滑杆共用（见 SanitizeFactor）。
        internal const float MinFactor = 0.05f;
        internal const float MaxFactor = 4f;

        // 覆写是否命中（命中即完全接管，调用方不应再叠加任何全局系数）。
        // 与 TrailLength / TrailFade 同形：命中判定与 0 哨兵的处理都在 ProjectileOverrides 里。
        // 入参是「已解析的结果」（见 TrailResolve），所以这里不再按 defName 查字典。
        // projectileDef 一律可为 null：命中多态/CE 投射物时不一定拿得到 ThingDef，
        // 传 null 表示「无法解析」，各方法按 0 宽度或全局默认值处理，不抛异常。
        public static bool TryOverride(in TrailResolve r, out float width)
        {
            if (!ProjectileOverrides.TryGetTrailWidth(r.over, out width)) return false;

            // 存档或外部改出来的 NaN / 无穷值不能进几何：NaN 视为未设定（回落到材质/基础宽度），
            // 正无穷按输入上限截断。见 SanitizeWidth。
            width = SanitizeWidth(width);
            return width > 0f;
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

        // 运行时最终宽度。r 为该投射物一次解析的结果（见 TrailResolve）。
        public static float Effective(in TrailResolve r, ThingDef? projectileDef, float damage)
        {
            if (TryOverride(r, out float w)) return w;

            float width = Base(projectileDef, damage);

            // 材质层：绝对值口径，-1（或任何非正值）表示未设定（继续走基础宽度，见 TrailMaterialParams.Clamp）
            float materialWidth = r.material.trailWidth;
            if (materialWidth > 0f) width = materialWidth;

            // 输入宽度与全局倍率各夹一次：坏值在这里止步，不进入顶点数组。
            return SanitizeWidth(width) * SanitizeFactor(GunplaySettings.trailWidthFactor);
        }

        // 设置界面口径：无法知道实际开火的武器，因此按无武器加成解析伤害（见 BaseDamage）。
        // 已解析结果在手时用这个重载（窗口同步输入框、新建覆写取默认值都走它）。
        public static float EffectiveForDef(in TrailResolve r, ThingDef? projectileDef)
        {
            // Effective 内部已处理覆写命中，这里不再重复判断。
            return Effective(r, projectileDef, BaseDamage(projectileDef));
        }

        // 宽度输入的最终防线：NaN / 负数 / 0 归零，正无穷与超大值截到 MaxWidth。
        // 0 是合法结果（不绘制），只是不允许坏值进入顶点数组——合并网格里一条坏几何会拖垮整组。
        static float SanitizeWidth(float width)
        {
            if (!(width > 0f)) return 0f;   // 同时挡住 NaN（NaN 与任何数比较都是 false）
            return width < MaxWidth ? width : MaxWidth;
        }

        // 全局倍率的最终防线。Mathf.Clamp 对 NaN 会原样返回（UnityStubs 里是比较式的实现），
        // 所以 NaN 单独回落默认值 1；±Infinity 则由 Clamp 自然截到区间端点。
        static float SanitizeFactor(float factor)
        {
            if (float.IsNaN(factor)) return 1f;
            return Mathf.Clamp(factor, MinFactor, MaxFactor);
        }

        // 仅有 def 时（覆写窗口的宽度列只读显示）。
        public static float EffectiveForDef(ThingDef? projectileDef)
        {
            return EffectiveForDef(ProjectileRules.Resolve(projectileDef), projectileDef);
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
