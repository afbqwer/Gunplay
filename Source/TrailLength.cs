using UnityEngine;
using Verse;

namespace Gunplay
{
    // 拖尾长度的统一口径（与 TrailWidth / TrailColor 同构）：运行时与设置窗口共用同一份解析。
    //
    // 单位是「格」（世界单位），公式：length = 投射物 def 的原始弹速 × 系数。
    //
    // 这里刻意用**未缩放**的 defSpeed，而不是实际每 tick 位移。直线投射物的实际位移被全局
    // projectileSpeed 放大，若按实际速度算，调弹速就会线性拉长拖尾——默认 300% 下长度会从
    // 10 格级涨到 30 格级，普遍超过枪械射程，等于把「曳光带」变成「铺满全程的轨迹线」。
    // 用 defSpeed 的代价是长度不再等于「覆盖多少 tick 的飞行距离」，换来的是「快弹长拖尾、
    // 慢弹短拖尾」这个逐 def 自适应——而这正是曳光带观感要的。
    //
    // 时间量（淡出 tick）不在这里，见 TrailFade。两者曾在旧实现里由同一个常数 15 共用：
    // length = speed * 15，ticksPerLength = length / speed 恰好也等于 15，于是「长度」看起来
    // 带着 tick 语义。拆开后各自只有一个单位。
    //
    // 优先级（高 → 低）：
    //   1) 单项覆写（覆写窗口设置）—— 绝对值，不看材质与全局；
    //   2) 材质（ProjectileTrailDef 的 material.trailLengthFactor）—— 绝对值，-1 表示未设定；
    //   3) 全局设置 GunplaySettings.trailLengthFactor。
    // internal：运行时与设置界面共用的内部口径，不对外暴露（入参携带内部解析状态 TrailResolve）。
    internal static class TrailLength
    {
        public const float Default = 15f;
        public const float Min = 1f;
        public const float Max = 120f;

        // 单项覆写是否命中（false = 未设定，走材质 / 全局）。
        public static bool TryOverride(in TrailResolve r, out float factor)
        {
            return ProjectileOverrides.TryGetTrailLength(r.over, out factor);
        }

        // 运行时最终系数。r 为该投射物一次解析的结果（见 TrailResolve）。
        public static float Effective(in TrailResolve r)
        {
            if (TryOverride(r, out float factor))
            {
                return Mathf.Clamp(factor, Min, Max);
            }

            float materialFactor = r.material.trailLengthFactor;
            if (materialFactor > 0f) return Mathf.Clamp(materialFactor, Min, Max);

            return Mathf.Clamp(GunplaySettings.trailLengthFactor, Min, Max);
        }

        // 仅有 def 与「该行缓存的材质」时（覆写窗口的数值列：它每帧逐行取值，
        // 手上已有 row.trailDef，不该为了重解析材质再查一次字典）。
        public static float Effective(ThingDef? projectileDef, ProjectileTrailDef? trailDef)
        {
            return Effective(new TrailResolve(ProjectileOverrides.Get(projectileDef), trailDef, TrailMaterial.Effective(trailDef)));
        }
    }
}
