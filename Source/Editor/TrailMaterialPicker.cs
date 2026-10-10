using System;
using System.Collections.Generic;
using System.Linq;
using ASQPicker;
using Verse;

namespace Gunplay
{
    // 轨迹材质的选择入口（顶栏按钮 / 覆写窗口的材质列共用）。
    //
    // 用现成的 DefSelectionWindow（带搜索、分组、清除），只把它的中文默认文案换成走本地化的键——
    // 那是个"整文件复制即用"的公用小窗口，不该由它决定本 mod 的语言。
    internal static class TrailMaterialPicker
    {
        // onConfirmed 收到 null 表示「清除」：调用方各自解释成「回到继承 / Def 模板」。
        public static void Open(ProjectileTrailDef? current, Action<ProjectileTrailDef?> onConfirmed)
        {
            DefSelectionWindow window = new DefSelectionWindow(
                "GunplayTrailMaterialPickerTitle".Translate(),
                DefDatabase<ProjectileTrailDef>.AllDefs
                    .OrderBy(d => d.defName, StringComparer.OrdinalIgnoreCase),
                current,
                def => onConfirmed(def as ProjectileTrailDef));

            window.ConfirmButtonText = "GunplayPickerConfirm".Translate();
            window.CancelButtonText = "GunplayPickerCancel".Translate();
            window.ClearButtonText = "GunplayPickerClear".Translate();
            window.CurrentText = "GunplayPickerCurrent".Translate();
            window.NoneText = "GunplayPickerNone".Translate();
            window.MiscGroupText = "GunplayPickerMisc".Translate();

            Find.WindowStack.Add(window);
        }
    }
}
