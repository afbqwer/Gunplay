using Verse;

namespace Gunplay
{
    // 弧线投射物（炮弹/手雷等）的统一门控。
    // 轨迹生成与速度缩放共用“是否弧线”的判断，便于未来替换判定方式（如改为弧线拖尾时只改此处）。
    public static class ProjectileRules
    {
        // 与既有轨迹门控保持一致
        public const float ArcHeightThreshold = 0.1f;

        public static bool IsArcing(ThingDef projectileDef)
        {
            return projectileDef?.projectile != null
                && projectileDef.projectile.arcHeightFactor > ArcHeightThreshold;
        }

        // 是否对该投射物应用全局 projectileSpeed 加速。
        // 独立于配置包的 preserveSpeed；未来换机制只改此方法。
        public static bool ShouldScaleSpeed(ThingDef projectileDef)
        {
            if (projectileDef?.projectile == null) return false;

            return !IsArcing(projectileDef); // 弧线投射物（手雷/炮弹）不缩放
        }

        // 投射物本体是否配有贴图。无 graphicData 或 texPath 为空即视为「无贴图」——
        // 这类投射物本体不可见，默认不为其绘制轨迹（仍可由单项覆写重新打开）。
        public static bool HasTexture(ThingDef? projectileDef)
        {
            GraphicData? graphicData = projectileDef?.graphicData;
            return graphicData != null && !graphicData.texPath.NullOrEmpty();
        }

        // 该投射物「默认」是否会绘制轨迹。这是「是否显示轨迹」的唯一口径：
        // 运行时登记轨迹、单项覆写窗口的轨迹列与过滤、以及新建覆写时的默认值都走这里，
        // 避免同一件事在多处各写一遍后慢慢跑偏。
        //
        // 优先级：全局总开关 > 单项覆写（存在即完全接管）> 各条「默认」规则。
        public static bool TrailEnabled(ThingDef projectileDef)
        {
            if (projectileDef == null) return false;
            if (!GunplaySettings.enableTrails) return false;

            // 没解析出 ProjectileTrailDef 的投射物根本不会登记轨迹（见两条 Launch 补丁）。
            // 这条是「能不能画」的硬前提，与是否覆写无关。
            ProjectilePropDef? prop = GunplaySetup.ProjectileProp(projectileDef) ?? GunplaySetup.DefaultProjectileProp;
            if (prop?.trail == null) return false;

            // 单项覆写存在即完全接管，可覆盖下面所有「默认」规则（见 ProjectileOverrides）
            ProjectileOverride? over = ProjectileOverrides.Get(projectileDef);
            if (over != null) return over.trailEnabled;

            // ---- 以下为该投射物的「默认」是否显示轨迹 ----

            // 本体无贴图默认不显示（覆写可重新打开）
            if (!HasTexture(projectileDef)) return false;

            // 非 CE 的弧线投射物（炮弹/手雷等）由独立开关控制：关闭后不再绘制轨迹
            if (IsArcing(projectileDef) && !GunplaySettings.enableArcTrails) return false;

            return true;
        }
    }
}
