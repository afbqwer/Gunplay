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

        // 该投射物「默认」是否会绘制轨迹：全局开关 + XML 是否配了 trail + 单项覆写没关掉。
        // 这是「是否显示轨迹」的唯一口径：运行时登记轨迹、覆写窗口的轨迹列与过滤都走这里，
        // 避免同一件事在三处各写一遍后慢慢跑偏。
        public static bool TrailEnabled(ThingDef projectileDef)
        {
            if (projectileDef == null) return false;
            if (!Gunplay.settings.enableTrails) return false;
            if (!ProjectileOverrides.AllowsTrail(projectileDef)) return false;

            // 没解析出 ProjectileTrailDef 的投射物根本不会登记轨迹（见两条 Launch 补丁）
            ProjectilePropDef prop = GunplaySetup.ProjectileProp(projectileDef) ?? GunplaySetup.DefaultProjectileProp;

            return prop?.trail != null;
        }
    }
}
