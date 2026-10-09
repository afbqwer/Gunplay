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
        public static float projectileSpeed = 3f;
        public static TrailWidthMode trailWidthMode = TrailWidthMode.Damage;
        public static float trailWidthFactor = 1f;
        public static bool ceStraightTrails = true;

        // 投射物单项覆写：键为投射物 defName。存在于字典中即代表该投射物开启了覆写。
        public static Dictionary<string, ProjectileOverride> projectileOverrides = new Dictionary<string, ProjectileOverride>();

        // 存档读写句柄。由 Gunplay 构造时注入；在此之前调用 Save() 是空操作。
        internal static GunplaySettings? Handle;

        public static void Save() => Handle?.Write();

        override public void ExposeData()
        {
            Scribe_Values.Look(ref enableTrails, "enableTrails");
            Scribe_Values.Look(ref enableSounds, "enenableSoundsableTrails");
            Scribe_Values.Look(ref enableWeaponAnimations, "enableWeaponAnimations");
            Scribe_Values.Look(ref enableEffects, "enableEffects");
            Scribe_Values.Look(ref showPuffs, "showPuffs", true);
            Scribe_Values.Look(ref unarmedProjectileEffects, "unarmedProjectileEffects", true);
            Scribe_Values.Look(ref unarmedOnlyBullets, "unarmedOnlyBullets", true);
            Scribe_Values.Look(ref particleMultiplier, "particleMultiplier", 1f);
            Scribe_Values.Look(ref projectileSpeed, "projectileSpeed");
            Scribe_Values.Look(ref trailWidthMode, "trailWidthMode", TrailWidthMode.Damage);
            Scribe_Values.Look(ref trailWidthFactor, "trailWidthFactor", 1f);
            Scribe_Values.Look(ref ceStraightTrails, "ceStraightTrails", true);

            Scribe_Collections.Look(ref projectileOverrides, "projectileOverrides", LookMode.Value, LookMode.Deep);
            if (projectileOverrides == null) projectileOverrides = new Dictionary<string, ProjectileOverride>();
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

}
