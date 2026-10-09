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
        public bool enableTrails = true;
        public bool enableSounds = true;
        public bool enableWeaponAnimations = true;
        public bool enableEffects = true;
        public bool showPuffs = true;
        public bool unarmedProjectileEffects = true;
        public bool unarmedOnlyBullets = true;
        public float particleMultiplier = 1f;
        public float projectileSpeed = 3f;
        public TrailWidthMode trailWidthMode = TrailWidthMode.Damage;
        public float trailWidthFactor = 1f;
        public bool ceStraightTrails = true;

        // 投射物单项覆写：键为投射物 defName。存在于字典中即代表该投射物开启了覆写。
        public Dictionary<string, ProjectileOverride> projectileOverrides = new Dictionary<string, ProjectileOverride>();


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

        public void DoSettingsWindowContents(Rect inRect)
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
