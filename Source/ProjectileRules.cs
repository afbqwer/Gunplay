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
    }
}
