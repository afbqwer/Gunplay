using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹颜色的统一口径（与 TrailWidth 同构）：运行时与设置界面共用同一份解析，避免两处跑偏。
    //
    // 优先级（高 → 低）：
    //   1) 单项覆写（覆写窗口设置，存 ProjectileOverride.hasColor/trailColor）—— 完全独立；
    //   2) 轨迹 def 颜色覆写（设置窗口「轨迹颜色」设置，存 GunplaySettings.trailColors）；
    //   3) ProjectileTrailDef.trailColor（XML，默认白 = 贴图原色）。
    public static class TrailColor
    {
        // 单项覆写是否命中（false = 未设自定义颜色，走 def 颜色）。
        public static bool TryOverride(ThingDef? projectileDef, out Color color)
        {
            return ProjectileOverrides.TryGetTrailColor(projectileDef, out color);
        }

        // def 层有效颜色：设置覆写 > XML trailColor。轨迹颜色窗口与覆写窗口的「继承色」预览共用。
        public static Color EffectiveForDef(ProjectileTrailDef? trailDef)
        {
            if (trailDef != null && GunplaySettings.trailColors.TryGetValue(trailDef.defName, out Color color))
            {
                return color;
            }

            return trailDef?.trailColor ?? Color.white;
        }

        // 运行时最终颜色。trailDef 为该投射物解析出的 ProjectileTrailDef（可能为 null，此时返回白）。
        public static Color Effective(ThingDef? projectileDef, ProjectileTrailDef? trailDef)
        {
            if (TryOverride(projectileDef, out Color color)) return color;

            return EffectiveForDef(trailDef);
        }

        // 颜色以 "#RRGGBBAA" 形式进出存档（与 LookColor 同一口径）；运行时一律存 Color，不做实时解析。
        public static string ToHtml(Color color)
        {
            return "#" + ColorUtility.ToHtmlStringRGBA(color);
        }
    }
}
