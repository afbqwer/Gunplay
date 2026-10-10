using UnityEngine;
using Verse;

namespace Gunplay
{
    // 拖拽涂抹（drag-paint）：在某格上按下左键，拖过一排格子时，把「按下那一格的值」
    // 涂到随后扫过的每一格上。用于本体没有现成控件、但需要批量改的格子（材质列 / 颜色列）。
    //
    // 先说本体已经给好的两套，能用就别自己写：
    //   * 开关 —— Widgets.Checkbox(..., paintable: true)，或把绘制与输入拆开的
    //     Widgets.ToggleInvisibleDraggable(rect, ref state, paintable: true)；
    //   * 下拉 —— Widgets.Dropdown(..., paintable: true)。
    // 它们靠 Widgets.checkboxPainting / dropdownPainting 两个全局状态工作，
    // 由 Widgets.WidgetsOnGUI 在松开鼠标时清掉，并顺带画一个跟随光标的图标。
    // 本窗口的三个开关列用的就是前者（见 ProjectileOverrideWindow.DrawToggleBox）。
    //
    // 本类只服务剩下那种情况：格子不是复选框也不是下拉，而是一个自绘的按钮 / 色块。
    // 判定方式参考 telardo.DragSelect 的 DraggingUtility —— 只用 Input 与 Mouse.IsOver
    // 自己数鼠标状态，**不碰** Widgets.ButtonInvisibleDraggable。原因：后者以
    // GUIUtility.GetControlID 认「按下的那个矩形」，同一个格子上再叠一层判定会互抢
    // activeControl，把同格子上原有控件（材质按钮的 Widgets.ButtonText）的点击一起吃掉。
    // 自己数状态则与其它控件完全无关，两者可以并存。
    public static class DragPaint
    {
        // 判定为「拖」的位移平方阈值。取本体 Widgets.DragStartDistanceSquared 同值（20，约 4.5 像素），
        // 这样自绘格子的起拖手感与复选框 / 下拉的涂抹一致，不会一处灵敏一处迟钝。
        const float DragThresholdSquared = 20f;

        static Vector3 pressPos;
        static bool pressed;    // 左键在某格上按下过
        static bool dragging;   // 按下后位移已越过阈值
        static bool painting;   // 本轮涂抹已定下要涂的值
        static int paintTag;    // 本轮涂抹的格子种类，防止跨列误涂（见 Sweep 的 tag 参数）

        // 本轮正在涂的值。仅当 Active 为 true 时有意义。类型由调用方约定
        // （材质列是 ProjectileTrailDef，颜色列是 Color），所以只能是 object，取用时自行转换。
        public static object? Value { get; private set; }

        public static bool Active => painting;

        // 一个可涂抹的格子每帧调一次，返回「本格是否应被涂成 Value」。
        //
        //   rect     —— 命中区（在滚动视图坐标系里，与 Mouse.IsOver 同一空间即可）。
        //               给整个格子而不是里面那个小按钮：涂抹时鼠标不会精准压在每个按钮上。
        //   tag      —— 格子种类。只有与起始格同 tag 的格子才会被涂：
        //               材质列与颜色列紧挨着，横向拖出去时若不认种类，
        //               颜色格会拿到一个 ProjectileTrailDef 并转换失败。
        //   ownValue —— 若本轮从本格起涂，要涂的值是什么（材质格传当前生效材质，颜色格传当前生效色）。
        //
        // 只有鼠标底下那一格会推进状态：
        // 同帧对每一行都会调用本方法，若不先判 Mouse.IsOver，
        // 遍历到的第一行会冒充「起始格」，涂出去的就不是用户按下那一格的值了。
        public static bool Sweep(Rect rect, int tag, object? ownValue)
        {
            // 左键没按着即本轮结束。放在最前面，免得松开后残留的 painting 状态
            // 让下一次「只是悬停」也被当成涂抹。
            if (!Input.GetMouseButton(0))
            {
                Reset();
                return false;
            }

            if (!Mouse.IsOver(rect)) return false;

            // 按下：记下起点，本轮尚未开始
            if (Input.GetMouseButtonDown(0))
            {
                pressed = true;
                dragging = false;
                painting = false;
                pressPos = Input.mousePosition;
                return false;
            }

            if (!pressed) return false;

            if (!dragging && (pressPos - Input.mousePosition).sqrMagnitude > DragThresholdSquared)
            {
                dragging = true;
            }

            if (!dragging) return false;

            // 起始格定义本轮要涂的值与种类；它自己本来就是那个值，不必涂
            if (!painting)
            {
                painting = true;
                paintTag = tag;
                Value = ownValue;
                return false;
            }

            // 拖拽中扫过的其它格子：同种类就涂上同一个值。
            // 重复涂同一格是幂等的（都是写同一个值），所以不必记「本轮涂过哪些」。
            return tag == paintTag;
        }

        static void Reset()
        {
            pressed = false;
            dragging = false;
            painting = false;
        }
    }
}
