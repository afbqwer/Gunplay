using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace Gunplay
{
    public class Gunplay : Mod
    {
        public static GunplaySettings? settings;
        public Gunplay(ModContentPack pack) : base(pack)
        {
            settings = GetSettings<GunplaySettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            GunplaySettings.DoSettingsWindowContents(inRect);
        }

        public override void WriteSettings()
        {
            settings!.Write();
        }

        public override string SettingsCategory()
        {
            return "GunplayTitle".Translate();
        }
    }
}
