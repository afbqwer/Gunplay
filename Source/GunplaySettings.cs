using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
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

        // 是否让投射物受全局弹速缩放（projectileSpeed）影响。关闭后 projectileSpeed 完全不起作用，
        // 所有投射物保持各自 def 的原始弹速。这是总开关，优先于单项覆写。
        // 判定统一收在 ProjectileRules.ShouldScaleSpeed（补丁只读那一处）：
        // 总开关 > 单项覆写（覆写窗口的「弹速」列，命中即接管）> 默认规则（弧线不缩放 / 配置包 preserveSpeed）。
        public static bool projectileSpeedEnabled = true;
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

        // 原版（非 CE）弧线投射物（炮弹/手雷等）是否绘制弧线轨迹。关闭后这些投射物不再绘制轨迹，
        // 直线投射物与 CE 投射物不受影响。由 ProjectileRules.TrailEnabled 统一读取。
        public static bool enableArcTrails = true;

        // 投射物单项覆写：键为投射物 defName。存在于字典中即代表该投射物开启了覆写。
        public static Dictionary<string, ProjectileOverride> projectileOverrides = new Dictionary<string, ProjectileOverride>();

        // 轨迹材质的编辑快照：键为 ProjectileTrailDef.defName，值为该材质的整份参数。
        //
        // 与 ProjectileTrailDef.material（XML 模板）共用同一个数据类（TrailMaterialParams）：
        // 存在快照 → 快照就是该 def 的生效参数；不存在 → 直接用模板。
        // 只有窗口里真正改过某个 def 才会产生条目（见 TrailMaterial.EnsureEdit），
        // 「重置为 Def 模板」即删除条目（TrailMaterial.ResetEdit）。
        public static Dictionary<string, TrailMaterialParams> trailMaterialEdits = new Dictionary<string, TrailMaterialParams>();




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
            Scribe_Values.Look(ref projectileSpeedEnabled, "projectileSpeedEnabled", true);
            Scribe_Values.Look(ref projectileSpeed, "projectileSpeed");
            Scribe_Values.Look(ref trailWidthMode, "trailWidthMode", TrailWidthMode.Damage);
            Scribe_Values.Look(ref trailWidthFactor, "trailWidthFactor", 1f);
            Scribe_Values.Look(ref ceStraightTrails, "ceStraightTrails", true);
            Scribe_Values.Look(ref trailLengthFactor, "trailLengthFactor", TrailLength.Default);
            Scribe_Values.Look(ref trailFadeTicks, "trailFadeTicks", TrailFade.Default);
            Scribe_Values.Look(ref arcSegmentCount, "arcSegmentCount", ArcSegments.Default);
            Scribe_Values.Look(ref enableArcTrails, "enableArcTrails", true);

            Scribe_Collections.Look(ref projectileOverrides, "projectileOverrides", LookMode.Value, LookMode.Deep);
            if (projectileOverrides == null) projectileOverrides = new Dictionary<string, ProjectileOverride>();

            // 整份对象序列化（LookMode.Deep）：材质参数是一个数据类实例，
            // 内部哪一项没改过由它自己的哨兵值表达，不需要额外的"是否覆写"标记。
            Scribe_Collections.Look(ref trailMaterialEdits, "trailMaterialEdits", LookMode.Value, LookMode.Deep);
            if (trailMaterialEdits == null) trailMaterialEdits = new Dictionary<string, TrailMaterialParams>();

            // 读档后生效参数可能已变：材质缓存（贴图已生成的那些）按新参数就地重填。
            // 只在读档时做——存档时参数没变，没必要去重写贴图像素。
            if (Scribe.mode == LoadSaveMode.LoadingVars) TrailMaterial.InvalidateAll();
        }

        // 设置窗口的滚动状态。设置项较多而窗口高度有限，内容超出时需滚动。
        static Vector2 settingsScroll;

        // 内容高度缓存。Listing.CurHeight 就是 curY（=> curY），会随 GetRect/Gap 自动累加，
        // 前提是「不分栏」——见下面 maxOneColumn。这里在 Layout 阶段量取一次，供同一帧的
        // Repaint 使用（IMGUI 每帧先 Layout 再 Repaint），因此不会出现「滚动条晚一帧」的抖动。
        // 初值 0：首帧按视口高度处理（无滚动条），Layout 后立即稳定。
        static float settingsContentHeight;

        public static void DoSettingsWindowContents(Rect inRect)
        {
            // 预留滚动条宽度，避免内容贴到滚动条上
            const float ScrollbarWidth = 16f;
            Rect viewRect = new Rect(0f, 0f, inRect.width - ScrollbarWidth, Mathf.Max(settingsContentHeight, inRect.height));

            Widgets.BeginScrollView(inRect, ref settingsScroll, viewRect);

            Listing_Standard listing_Standard = new Listing_Standard();

            // 必须关掉自动分栏：Listing.GetRect 会调 NewColumnIfNeeded，
            // 当 curY + 行高 > listingRect.height 时执行 NewColumn()——把 curX 右移一列、curY 归零。
            // 后果有两个：后续控件被排到视口右侧裁掉，且 CurHeight（=> curY）被清零而不再等于内容总高。
            // 这正是「设置装不下、滚动无效」的根因，与 CurHeight 是否自动赋值无关。
            listing_Standard.maxOneColumn = true;

            listing_Standard.Begin(viewRect);
            listing_Standard.CheckboxLabeled("GunplayEnableTrailsName".Translate(), ref enableTrails, "GunplayEnableTrailsDesc".Translate());

            // 弹速缩放是玩法改动，与轨迹是否绘制无关（补丁里也一直独立生效），因此独立成组，
            // 不再藏在「启用子弹轨迹」下面——否则关掉轨迹后就无法调整/关闭弹速了。
            //
            // CE 下整项隐藏：CE 的 ProjectileCE 自成一套弹道物理、不继承原版 Projectile，
            // 那个「除以 projectileSpeed」的补丁对它根本不生效，留着开关只会让人以为调了有用
            // （见 PatchCECompat 顶部说明与 PatchProjectileStartingTicksToImpact）。
            // 判据用 Detected 而不是 Enabled：不兼容的原因是「CE 在场」，与我们的兼容补丁挂没挂上无关。
            // 两个 Gap 一起放进条件里，隐藏时不留下空档。
            if (!PatchCECompat.Detected)
            {
                listing_Standard.Gap(4f);
                listing_Standard.CheckboxLabeled("GunplayProjectileSpeedEnabledName".Translate(), ref projectileSpeedEnabled, "GunplayProjectileSpeedEnabledDesc".Translate());
                if (projectileSpeedEnabled)
                {
                    listing_Standard.Gap(4f);
                    listing_Standard.SliderLabeled("GunplayProjectileSpeedName".Translate(), ref projectileSpeed, "GunplayProjectileSpeedDesc".Translate(), 0.1f, 10f);
                    // 按 5% 步进吸附，与粒子倍率的处理方式一致
                    projectileSpeed = Mathf.Round(projectileSpeed * 20f) / 20f;
                }
                listing_Standard.Gap(4f);
            }

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
                // 三项都是整数刻度：分数在这里既没有视觉意义，也会让长度和淡出对不上整数 tick
                listing_Standard.SliderLabeled("GunplayTrailLengthName".Translate(), ref trailLengthFactor, "GunplayTrailLengthDesc".Translate(),
                    TrailLength.Min, TrailLength.Max, v => Mathf.RoundToInt(v).ToString());
                trailLengthFactor = Mathf.Round(trailLengthFactor);

                listing_Standard.SliderLabeled("GunplayTrailFadeName".Translate(), ref trailFadeTicks, "GunplayTrailFadeDesc".Translate(),
                    TrailFade.Min, TrailFade.Max, v => Mathf.RoundToInt(v).ToString());
                trailFadeTicks = Mathf.Round(trailFadeTicks);

                listing_Standard.CheckboxLabeled("GunplayEnableArcTrailsName".Translate(), ref enableArcTrails, "GunplayEnableArcTrailsDesc".Translate());
                listing_Standard.Gap(4f);

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
                //listing_Standard.Label("GunplayTrailMaterialDesc".Translate());
                if (listing_Standard.ButtonText("GunplayTrailMaterialName".Translate()))
                {
                    Find.WindowStack.Add(new TrailMaterialWindow());
                }

                listing_Standard.Gap(8f);
                //listing_Standard.Label("GunplayProjectileOverridesDesc".Translate());
                if (listing_Standard.ButtonText("GunplayProjectileOverridesName".Translate()))
                {
                    ProjectileOverrideWindow.Open();
                }
            }

            listing_Standard.End();

            // 内容高度 + 一行间距的底部留白；只在 Layout 阶段量取，供同一帧的 Repaint 使用
            if (Event.current.type == EventType.Layout)
            {
                settingsContentHeight = listing_Standard.CurHeight + listing_Standard.verticalSpacing;
            }

            Widgets.EndScrollView();

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
