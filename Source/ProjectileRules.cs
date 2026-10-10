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

        // 该投射物「自己」是否受全局弹速缩放（不含全局总开关）。
        //
        // 这是弹速侧的唯一口径：补丁走 ShouldScaleSpeed，覆写窗口的弹速列直接读本方法。
        // 窗口读的是它而不是 ShouldScaleSpeed，是因为总开关关闭时所有投射物都不缩放——
        // 若显示 ShouldScaleSpeed，关掉总开关会让每一行的开关都变成未勾选，
        // 看起来像用户逐行改过，其实是总开关在起作用。
        //
        // 优先级与 TrailEnabled 一致：单项覆写（命中即完全接管）> 各条「默认」规则。
        public static bool SpeedAffected(ThingDef projectileDef)
        {
            if (projectileDef?.projectile == null) return false;

            // 单项覆写命中即接管，可双向覆盖下面的「默认」规则
            if (ProjectileOverrides.TryGetSpeedAffected(projectileDef, out bool affected)) return affected;

            // ---- 以下为该投射物的「默认」是否受全局弹速缩放 ----

            if (IsArcing(projectileDef)) return false; // 弧线投射物（手雷/炮弹）不缩放

            // 配置包显式豁免（preserveSpeed，如燃烧弹）
            ProjectilePropDef? prop = GunplaySetup.ProjectileProp(projectileDef);
            return prop == null || !prop.preserveSpeed;
        }

        // 是否真的对该投射物应用全局 projectileSpeed 缩放。补丁只读这一处。
        // 全局总开关优先，与 TrailEnabled 同一优先级：总开关 > 单项覆写 > 「默认」规则。
        public static bool ShouldScaleSpeed(ThingDef projectileDef)
        {
            if (projectileDef?.projectile == null) return false;

            // 全局开关（设置里的「投射物受全局弹速影响」）：关闭后整项缩放停用，
            // 全部投射物回到各自 def 的原始弹速。
            if (!GunplaySettings.projectileSpeedEnabled) return false;

            return SpeedAffected(projectileDef);
        }

        // 投射物本体是否配有贴图。无 graphicData 或 texPath 为空即视为「无贴图」——
        // 这类投射物本体不可见，默认不为其绘制轨迹（仍可由单项覆写重新打开）。
        public static bool HasTexture(ThingDef? projectileDef)
        {
            GraphicData? graphicData = projectileDef?.graphicData;
            return graphicData != null && !graphicData.texPath.NullOrEmpty();
        }

        // ---------------- 一次解析 ----------------

        // 把「一发投射物」需要的东西一次性解析出来：覆写记录、生效材质、材质参数。
        // 轨迹创建路径（Launch 后缀 / CE 的 spawn 补丁 → Register → Initialize）只调这一个入口，
        // 之后所有口径类都读返回值里的字段，不再按 defName 查字典——原先同一发里这条记录被取七八次。
        //
        // 刻意**不**在这里判定「要不要画」：门控是下面的 TrailEnabled(r, def)，与解析分开。
        // 因为覆写窗口的轨迹列与过滤器要在不构造任何状态、不 spawn 任何东西的前提下按 def 问
        // 「会不会画」，那条路径不需要材质参数，不该被拖来解析材质。
        //
        // internal：只服务于本程序集的轨迹创建路径，不对外暴露（它携带的是内部解析状态）。
        internal static TrailResolve Resolve(ThingDef? projectileDef)
        {
            ProjectileOverride? over = ProjectileOverrides.Get(projectileDef);
            ProjectileTrailDef? trailDef = TrailDefFor(over, projectileDef);

            return new TrailResolve(over, trailDef, TrailMaterial.Effective(trailDef));
        }

        // 该投射物实际使用的轨迹材质（ProjectileTrailDef，「材质类型」就是它）。
        //
        // 优先级：单项覆写里选的材质 > ProjectilePropDef.trail（XML 显式配置 / 名称关键字匹配 / default）。
        // 运行时登记轨迹、轨迹门控、四个口径类（宽度/长度/淡出/颜色）与两个窗口全部只走这一处，
        // 避免同一件事在多处各写一遍后慢慢跑偏。
        public static ProjectileTrailDef? TrailDefFor(ThingDef? projectileDef)
        {
            return TrailDefFor(ProjectileOverrides.Get(projectileDef), projectileDef);
        }

        // 已取出记录时的形态：不再查覆写字典（见 TrailResolve）
        static ProjectileTrailDef? TrailDefFor(ProjectileOverride? over, ThingDef? projectileDef)
        {
            if (projectileDef == null) return null;

            if (ProjectileOverrides.TryGetTrailMaterial(over, out ProjectileTrailDef selected)) return selected;

            return (GunplaySetup.ProjectileProp(projectileDef) ?? GunplaySetup.DefaultProjectileProp)?.trail;
        }

        // 该投射物「默认」是否会绘制轨迹。这是「是否显示轨迹」的唯一口径：
        // 运行时登记轨迹、单项覆写窗口的轨迹列与过滤、以及新建覆写时的默认值都走这里，
        // 避免同一件事在多处各写一遍后慢慢跑偏。
        //
        // 优先级：全局总开关 > 单项覆写（存在即完全接管）> 各条「默认」规则。
        //
        // 已解析结果在手时用这个重载：over / trailDef 都在 r 里，一次字典都不查。
        internal static bool TrailEnabled(in TrailResolve r, ThingDef? projectileDef)
        {
            return TrailEnabledCore(r.over, projectileDef, r.trailDef);
        }

        // 仅有 def 时（覆写窗口的轨迹列 / 过滤器、新建覆写取默认值）。
        public static bool TrailEnabled(ThingDef? projectileDef)
        {
            ProjectileOverride? over = ProjectileOverrides.Get(projectileDef);
            return TrailEnabledCore(over, projectileDef, TrailDefFor(over, projectileDef));
        }

        // 门控规则本体。两个入口共用，只有这一份，避免「同一件事在两处各写一遍后跑偏」。
        static bool TrailEnabledCore(ProjectileOverride? over, ThingDef? projectileDef, ProjectileTrailDef? trailDef)
        {
            if (projectileDef == null) return false;
            if (!GunplaySettings.enableTrails) return false;

            // 没解析出 ProjectileTrailDef 的投射物根本不会登记轨迹（见两条 Launch 补丁）。
            // 这条是「能不能画」的硬前提，与是否覆写无关——但解析必须走 TrailDefFor：
            // 单项覆写可以自己指定材质，此时「没配 XML 材质」不再是不可画的理由。
            if (trailDef == null) return false;

            // 单项覆写存在即完全接管，可覆盖下面所有「默认」规则（见 ProjectileOverrides）
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
