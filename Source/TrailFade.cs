using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹淡出时长的统一口径。单位是 tick（60 tick = 1 秒），纯时间量。
    //
    // 含义：投射物消失（命中、飞出行外或被拦截）后，尾巴从满长收缩到零所用的 tick 数。
    //
    // 与拖尾长度（TrailLength）之间没有任何换算关系。旧实现里它是 length / speed 派生出来的，
    // 因为两者共用同一个常数而恰好恒等于那个数，看起来像「由长度推出时间」；实际上
    // ticksPerLength 里 speed 被完整约掉，它是一个纯 tick 量，本来就不该从长度推导。
    // 拆开后还有一个附带修复：CE 分支的 length 会被 MaxTrailLength 截断，
    // 派生写法下淡出会跟着被截短，现在不会了。
    //
    // 优先级（高 → 低）：
    //   1) 单项覆写（覆写窗口设置）—— 绝对值，不看全局；
    //   2) 全局设置 GunplaySettings.trailFadeTicks。
    public static class TrailFade
    {
        public const float Default = 15f;
        public const float Min = 1f;
        public const float Max = 120f;

        // 单项覆写是否命中（false = 未设定，走全局）。
        public static bool TryOverride(ThingDef? projectileDef, out float ticks)
        {
            return ProjectileOverrides.TryGetTrailFade(projectileDef, out ticks);
        }

        public static float Effective(ThingDef? projectileDef)
        {
            return TryOverride(projectileDef, out float ticks)
                ? Mathf.Clamp(ticks, Min, Max)
                : Mathf.Clamp(GunplaySettings.trailFadeTicks, Min, Max);
        }
    }
}
