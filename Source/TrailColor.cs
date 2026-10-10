using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹颜色的统一口径（与 TrailWidth 同构）：运行时与设置界面共用同一份解析，避免两处跑偏。
    //
    // 优先级（高 → 低）：
    //   1) 单项覆写（覆写窗口设置，存 ProjectileOverride.hasColor/trailColor）—— 完全独立；
    //   2) 材质（ProjectileTrailDef 的 material.trailColor，XML 或「轨迹材质」窗口编辑）；
    //   3) 无 —— 材质颜色本身有出厂默认值（白），所以这里不存在"再往下回落"的一层。
    //
    // 颜色不进材质：它经 MaterialPropertyBlock 的 _Color 逐组传入（见 ProjectileTrailBatcher），
    // 因此改颜色不需要重建贴图，已在飞的轨迹也不受影响（颜色在登记时已确定）。
    public static class TrailColor
    {
        // 单项覆写是否命中（false = 未设自定义颜色，走材质颜色）。
        public static bool TryOverride(ThingDef? projectileDef, out Color color)
        {
            return ProjectileOverrides.TryGetTrailColor(projectileDef, out color);
        }

        // 材质层有效颜色：设置里的编辑快照 > def 模板。轨迹颜色窗口与覆写窗口的「继承色」预览共用。
        public static Color EffectiveForDef(ProjectileTrailDef? trailDef)
        {
            return TrailMaterial.Effective(trailDef).trailColor;
        }

        // 运行时最终颜色。trailDef 为该投射物解析出的 ProjectileTrailDef（可能为 null，此时返回材质默认白）。
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
