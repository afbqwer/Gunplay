using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Gunplay.Patch
{
    // 涂抹时在光标旁跟着画一个提示：材质列画材质名，颜色列画色块，数值列画正在涂的那个值。
    // 与本体复选框 / 下拉涂抹时那个跟随光标的图标是同一套机制（GenUI.DrawMouseAttachment → ImmediateWindow）。
    //
    // 为什么必须挂在 Widgets.WidgetsOnGUI 上，而不能在自己的窗口里顺手画：
    // GenUI.DrawMouseAttachment 用 Event.current.mousePosition 定位，而它是「当前 GUI 组」内的局部坐标
    // （Verse.Mouse.IsOver 就是 rect.Contains(Event.current.mousePosition)，滚动视图里传局部 Rect 能对上，
    // 正说明这一点）。窗口内容是在 Window.InnerWindowOnGUI 的 BeginGroup 里画的，列表行外面还套了一层
    // BeginScrollView，在那里画出来的图标会偏到窗口外去。
    // 而 UIRoot_Play.UIRootOnGUI 是在画完窗口栈之后才调 Widgets.WidgetsOnGUI 的顶层调用
    // （本体自己也正好在这里画 checkboxPainting / dropdownPainting 的图标），那才是屏幕坐标系。
    //
    // 挂载方式：本类不使用 [HarmonyPatch] 属性，由 GunplaySetup 显式调用 TryPatch ——
    // 图标只是装饰，目标方法万一改名不该连累整个 mod 加载失败。
    internal static class PatchDrawDragPaintIcon
    {
        public static bool TryPatch(Harmony harmony)
        {
            MethodInfo? target = AccessTools.Method(typeof(Widgets), nameof(Widgets.WidgetsOnGUI));
            if (target == null)
            {
                Log.Error("[Gunplay] Could not find Widgets.WidgetsOnGUI; the drag-paint cursor icon is disabled.");
                return false;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(AccessTools.Method(typeof(PatchDrawDragPaintIcon), nameof(Postfix))));
            return true;
        }

        static void Postfix()
        {
            // 这里读到的 Active 是上一帧留下的（WidgetsOnGUI 跑在窗口绘制之前），
            // 松开鼠标那一帧它仍是 true，所以再判一次左键，否则图标会在松开后多留一帧。
            if (!DragPaint.Active || !Input.GetMouseButton(0)) return;

            switch (DragPaint.Tag)
            {
                case ProjectileOverrideWindow.MaterialPaintTag:
                    // 材质显示名字而不是色块：材质是「形状 + 宽度轮廓 + 颜色」一整套，
                    // 单画一个色块会与颜色列的涂抹分不清。iconTex 传 null 时本方法只画文字。
                    ProjectileTrailDef? def = DragPaint.Value as ProjectileTrailDef;
                    GenUI.DrawMouseAttachment(iconTex: (Texture)null!, text: def != null ? def.defName : "—");
                    break;

                case ProjectileOverrideWindow.ColorPaintTag:
                    if (DragPaint.Value is Color color)
                    {
                        // 白色贴图 + iconColor 就是色块，再描一圈边，免得浅色在浅底上看不见。
                        // 必须显式转成 Texture：留成 Texture2D 会命中只画图标、没有 iconColor 的那个重载。
                        GenUI.DrawMouseAttachment(
                            iconTex: (Texture)BaseContent.WhiteTex,
                            iconColor: color,
                            postDrawAction: r => Widgets.DrawBox(r, 1));
                    }
                    break;

                // 数值三列：显示正在涂的那个值。
                // 这一列不像开关/材质/颜色那样有现成的"样子"，没有这行字就完全没有反馈——
                // 用户根本无从判断涂抹到底有没有生效。
                case ProjectileOverrideWindow.WidthPaintTag:
                case ProjectileOverrideWindow.LengthPaintTag:
                case ProjectileOverrideWindow.FadePaintTag:
                    if (DragPaint.Value is float number)
                    {
                        bool integer = DragPaint.Tag != ProjectileOverrideWindow.WidthPaintTag;
                        string text = integer ? Mathf.RoundToInt(number).ToString() : number.ToString("0.###");
                        GenUI.DrawMouseAttachment(iconTex: (Texture)null!, text: text);
                    }
                    break;
            }
        }
    }
}
