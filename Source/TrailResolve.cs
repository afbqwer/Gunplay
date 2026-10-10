namespace Gunplay
{
    // 一条投射物轨迹「一次解析」的结果。
    //
    // 存在的理由：轨迹创建路径（Launch 后缀 / CE 的 spawn 补丁 → Register → Initialize → 四个口径类）
    // 原本按 defName 反复取同一条覆写记录、反复解析同一个材质参数——这些量在「一发投射物」这个粒度内
    // 全是常量。由 ProjectileRules.Resolve 一次算出来、随参数下传，之后各口径只读字段，不再碰字典。
    //
    // 注意这不是「数据驱动的覆写框架」：覆写记录 ProjectileOverride 的形状没有任何变化，
    // 一个字段都没有变成可配置项。这里收敛的只是「读取」——把同一发内重复的字典查找合并成一次。
    // 与 ProjectileOverride 类注释里那句「等覆写项到八项以上再谈抽象」是两件事，互不影响。
    //
    // 不变量：material 恒非空（trailDef 为 null 时是 TrailMaterial 的兜底参数）。
    // 由 ProjectileRules.Resolve 保证——不要用 default(TrailResolve) 构造它。
    internal readonly struct TrailResolve
    {
        // 该投射物的覆写记录；null = 未覆写。四个口径类的「命中」判定都读它。
        internal readonly ProjectileOverride? over;

        // 该投射物生效的轨迹材质（覆写选的 > ProjectilePropDef.trail）。null = 不绘制。
        internal readonly ProjectileTrailDef? trailDef;

        // TrailMaterial.Effective(trailDef)，一次解析出来供宽度 / 长度 / 淡出 / 颜色四处共用。
        internal readonly TrailMaterialParams material;

        internal TrailResolve(ProjectileOverride? over, ProjectileTrailDef? trailDef, TrailMaterialParams material)
        {
            this.over = over;
            this.trailDef = trailDef;
            this.material = material;
        }
    }
}
