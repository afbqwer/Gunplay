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
        public Gunplay(ModContentPack pack) : base(pack)
        {
            // 只把实例交给 GunplaySettings 当存档句柄；设置本身是静态的，读的时候不经过这里。
            GunplaySettings.Handle = GetSettings<GunplaySettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            GunplaySettings.DoSettingsWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "GunplayTitle".Translate();
        }
    }
}
