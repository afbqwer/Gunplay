using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ASQPicker;
using Gunplay.Patch;
using UnityEngine;
using Verse;

namespace Gunplay
{
    public enum TrailWidthMode
    {
        Damage,
        Texture
    }

    public class GunplaySettings : ModSettings
    {
        // 设置项全部是静态字段：mod 设置在进程内天然只有一份，套一层实例（或单例）没有收益，
        // 还会让每次读取都要先经过一次「拿实例」的间接。
        //
        // ExposeData / Write 是 ModSettings 的实例成员，所以仍需一个实例作为存档读写句柄，
        // 见下方 Handle。它只用于存取，不承载任何状态。
        public static bool enableTrails = true;
        public static bool enableSounds = true;
        public static bool enableWeaponAnimations = true;
        public static bool enableEffects = true;
        public static bool showPuffs = true;
        public static bool unarmedProjectileEffects = true;
        public static bool unarmedOnlyBullets = true;
        public static float particleMultiplier = 1f;
        public static float projectileSpeed = 3f;
        public static TrailWidthMode trailWidthMode = TrailWidthMode.Damage;
        public static float trailWidthFactor = 1f;
        public static bool ceStraightTrails = true;

        // 拖尾长度系数：length = 投射物 def 原始弹速 × 该值（格），见 TrailLength。
        public static float trailLengthFactor = TrailLength.Default;

        // 弹体消失后尾巴收缩完的 tick 数，见 TrailFade。
        public static float trailFadeTicks = TrailFade.Default;

        // 弧线轨迹的采样段数：越大越平滑，顶点数与耗时线性增长。见 ProjectileTrailState.ArcSegmentCount。
        public static int arcSegmentCount = ArcSegments.Default;

        // 投射物单项覆写：键为投射物 defName。存在于字典中即代表该投射物开启了覆写。
        public static Dictionary<string, ProjectileOverride> projectileOverrides = new Dictionary<string, ProjectileOverride>();

        // 轨迹 def 颜色覆写（设置窗口「轨迹颜色」编辑）：键为 ProjectileTrailDef.defName，值为颜色。
        // 运行时直接以 Color 存储；持久化经 ColorPickerWindow.LookColorDictionary 做 HTML 字符串中转。
        // 缺失条目 = 使用 ProjectileTrailDef.trailColor（XML）。解析统一走 TrailColor。
        public static Dictionary<string, Color> trailColors = new Dictionary<string, Color>();

        // 运行时生成贴图的形状参数（设置窗口「轨迹贴图」编辑，见 TrailTextureParams）。
        public static TrailTextureParams trailTextureParams = new TrailTextureParams();




        override public void ExposeData()
        {
            Scribe_Values.Look(ref enableTrails, "enableTrails", true);
            Scribe_Values.Look(ref enableSounds, "enableSounds", true);
            Scribe_Values.Look(ref enableWeaponAnimations, "enableWeaponAnimations", true);
            Scribe_Values.Look(ref enableEffects, "enableEffects", true);
            Scribe_Values.Look(ref showPuffs, "showPuffs", true);
            Scribe_Values.Look(ref unarmedProjectileEffects, "unarmedProjectileEffects", true);
            Scribe_Values.Look(ref unarmedOnlyBullets, "unarmedOnlyBullets", true);
            Scribe_Values.Look(ref particleMultiplier, "particleMultiplier", 1f);
            Scribe_Values.Look(ref projectileSpeed, "projectileSpeed");
            Scribe_Values.Look(ref trailWidthMode, "trailWidthMode", TrailWidthMode.Damage);
            Scribe_Values.Look(ref trailWidthFactor, "trailWidthFactor", 1f);
            Scribe_Values.Look(ref ceStraightTrails, "ceStraightTrails", true);
            Scribe_Values.Look(ref trailLengthFactor, "trailLengthFactor", TrailLength.Default);
            Scribe_Values.Look(ref trailFadeTicks, "trailFadeTicks", TrailFade.Default);
            Scribe_Values.Look(ref arcSegmentCount, "arcSegmentCount", ArcSegments.Default);

            Scribe_Collections.Look(ref projectileOverrides, "projectileOverrides", LookMode.Value, LookMode.Deep);
            if (projectileOverrides == null) projectileOverrides = new Dictionary<string, ProjectileOverride>();

            ColorPickerWindow.LookColorDictionary(ref trailColors, "trailColors");
            if (trailColors == null) trailColors = new Dictionary<string, Color>();

            Scribe_Deep.Look(ref trailTextureParams, "trailTextureParams");
            if (trailTextureParams == null) trailTextureParams = new TrailTextureParams();

            // 读档后参数可能已变：贴图已生成的话就地重填（未生成则下次取用时按新参数生成）。
            // 只在读档时做——存档时参数没变，没必要去重写贴图像素。
            if (Scribe.mode == LoadSaveMode.LoadingVars) TrailTexture.Invalidate();
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing_Standard = new Listing_Standard();
            listing_Standard.Begin(inRect);
            listing_Standard.CheckboxLabeled("GunplayEnableTrailsName".Translate(), ref enableTrails, "GunplayEnableTrailsDesc".Translate());
            listing_Standard.CheckboxLabeled("GunplayEnableSoundsName".Translate(), ref enableSounds, "GunplayEnableSoundsDesc".Translate());
            listing_Standard.CheckboxLabeled("GunplayEnableWeaponAnimationsName".Translate(), ref enableWeaponAnimations, "GunplayEnableWeaponAnimationsDesc".Translate());
            listing_Standard.CheckboxLabeled("GunplayEnableEffectsName".Translate(), ref enableEffects, "GunplayEnableEffectsDesc".Translate());
            if (enableEffects)
            {
                listing_Standard.Gap(4f);
                listing_Standard.CheckboxLabeled("GunplayShowPuffsName".Translate(), ref showPuffs, "GunplayShowPuffsDesc".Translate());
                listing_Standard.SliderLabeled("GunplayParticleMultiplierName".Translate(), ref particleMultiplier, "GunplayParticleMultiplierDesc".Translate(), 0.2f, 4f);
                particleMultiplier = Mathf.Round(particleMultiplier * 20f) / 20f;
                listing_Standard.Gap(4f);
            }
            listing_Standard.CheckboxLabeled("GunplayUnarmedEffectsName".Translate(), ref unarmedProjectileEffects, "GunplayUnarmedEffectsDesc".Translate());
            if (unarmedProjectileEffects)
            {
                listing_Standard.Gap(4f);
                listing_Standard.CheckboxLabeled("GunplayUnarmedBulletsOnlyName".Translate(), ref unarmedOnlyBullets, "GunplayUnarmedBulletsOnlyDesc".Translate());
            }
            if (enableTrails)
            {
                listing_Standard.SliderLabeled("GunplayProjectileSpeedName".Translate(), ref projectileSpeed, "GunplayProjectileSpeedDesc".Translate(), 0.1f, 10f);
                listing_Standard.Gap(8f);

                // 三项都是整数刻度：分数在这里既没有视觉意义，也会让长度和淡出对不上整数 tick
                listing_Standard.SliderLabeled("GunplayTrailLengthName".Translate(), ref trailLengthFactor, "GunplayTrailLengthDesc".Translate(),
                    TrailLength.Min, TrailLength.Max, v => Mathf.RoundToInt(v).ToString());
                trailLengthFactor = Mathf.Round(trailLengthFactor);

                listing_Standard.SliderLabeled("GunplayTrailFadeName".Translate(), ref trailFadeTicks, "GunplayTrailFadeDesc".Translate(),
                    TrailFade.Min, TrailFade.Max, v => Mathf.RoundToInt(v).ToString());
                trailFadeTicks = Mathf.Round(trailFadeTicks);

                listing_Standard.SliderLabeledInt("GunplayArcSegmentCountName".Translate(), ref arcSegmentCount, "GunplayArcSegmentCountDesc".Translate(),
                    ArcSegments.Min, ArcSegments.Max);

                listing_Standard.Gap(8f);

                if (listing_Standard.RadioButton("GunplayTrailWidthModeDamage".Translate(), trailWidthMode == TrailWidthMode.Damage, tooltip: "GunplayTrailWidthModeDesc".Translate()))
                    trailWidthMode = TrailWidthMode.Damage;
                if (listing_Standard.RadioButton("GunplayTrailWidthModeTexture".Translate(), trailWidthMode == TrailWidthMode.Texture, tooltip: "GunplayTrailWidthModeDesc".Translate()))
                    trailWidthMode = TrailWidthMode.Texture;
                trailWidthFactor = Mathf.Round(trailWidthFactor * 20f) / 20f;
                listing_Standard.SliderLabeled("GunplayTrailWidthFactorName".Translate(), ref trailWidthFactor, "GunplayTrailWidthFactorDesc".Translate(), 0.05f, 4f);
                trailWidthFactor = Mathf.Round(trailWidthFactor * 20f) / 20f;

                // 仅在显示轨迹且检测到 CE（兼容补丁挂载成功）时显示
                if (PatchCECompat.Enabled)
                {
                    listing_Standard.Gap(4f);
                    listing_Standard.CheckboxLabeled("GunplayCEStraightTrailsName".Translate(), ref ceStraightTrails, "GunplayCEStraightTrailsDesc".Translate());
                }

                listing_Standard.Gap(8f);
                listing_Standard.Label("GunplayTrailColorsDesc".Translate());
                if (listing_Standard.ButtonText("GunplayTrailColorsName".Translate()))
                {
                    Find.WindowStack.Add(new TrailColorWindow());
                }

                listing_Standard.Gap(8f);
                listing_Standard.Label("GunplayTrailTextureDesc".Translate());
                if (listing_Standard.ButtonText("GunplayTrailTextureName".Translate()))
                {
                    Find.WindowStack.Add(new TrailTextureWindow());
                }

                listing_Standard.Gap(8f);
                listing_Standard.Label("GunplayProjectileOverridesDesc".Translate());
                if (listing_Standard.ButtonText("GunplayProjectileOverridesName".Translate()))
                {
                    ProjectileOverrideWindow.Open();
                }
            }

            listing_Standard.End();

            ImpactEffectTuner.Apply();
        }
    }

    public static class GunplaySettingsHelper
    {
        public static void SliderLabeled(this Listing_Standard listing, string label, ref float val, string tooltip, float min, float max)
        {
            listing.SliderLabeled(label, ref val, tooltip, min, max, v => v.ToStringPercent());
        }

        // 与上一版同布局，只是右侧数值的呈现方式可换：百分比之外的量（tick、段数）不该显示成 %。
        public static void SliderLabeled(this Listing_Standard listing, string label, ref float val, string tooltip, float min, float max, Func<float, string> format)
        {
            Rect rect = listing.GetRect(Text.LineHeight);

            TextAnchor anchor = Text.Anchor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(rect.LeftPart(0.5f), label);
            val = Widgets.HorizontalSlider(rect.RightPart(0.5f).LeftPart(0.8f), val, min, max, true);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(rect.RightPart(0.1f), format(val));
            if (!tooltip.NullOrEmpty()) TooltipHandler.TipRegion(rect, tooltip);
            Text.Anchor = anchor;

            listing.Gap(listing.verticalSpacing);
        }

        // 整数版：滑杆本身只认 float，取整后回填，避免把舍入逻辑散落到每个调用点。
        public static void SliderLabeledInt(this Listing_Standard listing, string label, ref int val, string tooltip, int min, int max)
        {
            float f = val;
            listing.SliderLabeled(label, ref f, tooltip, min, max, v => Mathf.RoundToInt(v).ToString());
            val = Mathf.RoundToInt(f);
        }
    }

}
