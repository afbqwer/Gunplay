using UnityEngine;
using Verse;

namespace Gunplay
{
    // 拖拽涂抹（drag-paint）：在本格上按下并拖动，把「按下那一格的值」涂到鼠标扫过的每一格上。
    // 用于本体没有现成控件、但需要批量改的格子（材质列 / 颜色列 / 数值输入格）。
    //
    // 先说本体已经给好的两套，能用就别自己写：
    //   * 开关 —— Widgets.Checkbox(..., paintable: true)，或把绘制与输入拆开的
    //     Widgets.ToggleInvisibleDraggable(rect, ref state, paintable: true)；
    //   * 下拉 —— Widgets.Dropdown(..., paintable: true)。
    // 它们靠 Widgets.checkboxPainting / dropdownPainting 两个全局状态工作，
    // 由 Widgets.WidgetsOnGUI 在松开鼠标时清掉，并顺带画一个跟随光标的图标。
    // 本窗口的三个开关列用的就是前者（见 ProjectileOverrideWindow.DrawToggleBox）。
    //
    // 本类只服务剩下那种情况：格子不是复选框也不是下拉，而是一个自绘的按钮 / 色块 / 输入框。
    // 判定方式参考 telardo.DragSelect 的 DraggingUtility —— 只用 Input 与 Mouse.IsOver
    // 自己数鼠标状态，**不碰** Widgets.ButtonInvisibleDraggable。原因：后者以
    // GUIUtility.GetControlID 认「按下的那个矩形」，同一个格子上再叠一层判定会互抢
    // activeControl，把同格子上原有控件（材质按钮的 Widgets.ButtonText、数值格的输入框）
    // 的点击一起吃掉。自己数状态则与其它控件完全无关，两者可以并存。
    //
    // 方向不限：只要位移越过阈值就算拖，横竖斜都行。涂抹的语义是"鼠标扫到哪格就涂哪格"，
    // 本来就不该对方向提要求。
    public static class DragPaint
    {
        // 判定为「拖」的位移平方阈值。取本体 Widgets.DragStartDistanceSquared 同值（20，约 4.5 像素），
        // 这样自绘格子的起拖手感与复选框 / 下拉的涂抹一致，不会一处灵敏一处迟钝。
        const float DragThresholdSquared = 20f;

        static Vector3 pressPos;
        static bool pressed;     // 左键在某格上按下过
        static bool dragging;    // 按下后位移已越过阈值
        static int paintTag;     // 本轮涂抹的格子种类，防止跨列误涂
        static object? paintValue;

        public static bool Active { get; private set; }

        // 本轮要涂的值。仅在本轮有效。类型由调用方约定（材质是 ProjectileTrailDef、
        // 颜色是 Color、数值是 float），所以只能是 object，取用时自行转换。
        public static object? Value => Active ? paintValue : null;

        // 本轮的格子种类。非涂抹时返回 0。
        // 光标图标的补丁按它决定画什么，0 不匹配任何一列 → 什么都不画。
        public static int Tag => Active ? paintTag : 0;

        // 一个可涂抹的格子每帧调一次，返回「本格是否应被涂成 Value」。
        //
        //   rect         —— 命中区（滚动视图局部坐标，与 Mouse.IsOver 同一空间即可）。
        //                   给整个格子而不是里面那个小控件：拖拽时鼠标不会精准压在每个控件上。
        //   tag          —— 格子种类。只有与起始格同 tag 的格子才会被涂：
        //                   材质列与颜色列紧挨着，横向拖出去时若不认种类，
        //                   颜色格会拿到一个 ProjectileTrailDef 并转换失败。
        //   ownValue     —— 若本轮从本格起涂，要涂的值是什么。
        //   requireShift —— 本列是否要求按住 Shift 才算涂抹。数值输入格必须传 true：
        //                   在它上面直接拖是"选中文字"（拖过数字就能全选替换），抢掉是净损失；
        //                   按住 Shift 则是用户明确表态"我要涂"，此时方向也不必再限制。
        //                   材质 / 颜色格没这个顾虑，直接拖就是涂，传 false。
        //
        // 只有鼠标底下那一格会推进按压状态：
        // 同帧对每一行都会调用本方法，若不先判 Mouse.IsOver，
        // 遍历到的第一行会冒充「起始格」，涂出去的就不是用户按下那一格的值了。
        public static bool Cell(Rect rect, int tag, object? ownValue, bool requireShift = false)
        {
            // 左键没按着即本轮结束。放在最前面，免得松开后残留的状态
            // 让下一次「只是悬停」也被当成涂抹。
            if (!Input.GetMouseButton(0))
            {
                Reset();
                return false;
            }

            if (Mouse.IsOver(rect))
            {
                // 按下：记下起点
                if (Input.GetMouseButtonDown(0))
                {
                    pressed = true;
                    dragging = false;
                    Active = false;
                    pressPos = Input.mousePosition;
                    return false;
                }

                if (pressed)
                {
                    // 修饰键每帧重读，不在按下那一刻定死。
                    // 定死的写法有两个坑：① 先按下鼠标再按修饰键就整轮作废，之后再怎么拖都进不来；
                    // ② 读到修饰键的那一帧正好是不带修饰键信息的事件（如 Repaint），同样作废。
                    // 实时读则只要"起拖那一刻"按住就算数，容错大得多，而语义不变——
                    // 越过阈值之前（4.5 像素内）本来也谈不上"已经开始的手势"。
                    bool armed = !requireShift || ModifierHeld;

                    if (!dragging && armed && (pressPos - Input.mousePosition).sqrMagnitude > DragThresholdSquared)
                    {
                        dragging = true;
                    }

                    if (dragging && !Active)
                    {
                        Active = true;
                        paintTag = tag;
                        paintValue = ownValue;
                        // 起始格这一帧不涂。它本来就是那个值；之后它会随"鼠标仍在本格上"
                        // 与其它格一样被涂上——重复写同一个值是同一份值，不必特判谁是起点。
                        return false;
                    }
                }
            }

            // 本轮是涂抹，且鼠标扫到了本格（种类还要对得上）
            return Active && tag == paintTag && Mouse.IsOver(rect);
        }

        // 结束本轮。由 Cell 在松开左键时自动调用，窗口关闭时也由窗口兜一次底
        // （见 ProjectileOverrideWindow.PostClose）。
        internal static void Reset()
        {
            pressed = false;
            dragging = false;
            Active = false;
        }

        // 修饰键（Shift）的读法。两处都读：
        //   * Event.current.shift 是本体的口径——UnityGUIBugsFixer.FixShift 就是拿 Input.GetKey
        //     去补它（仅 Linux 生效），说明本体自己也用 Event.current.shift；
        //     但它在 Repaint 这类事件上不一定带修饰键信息。
        //   * Input.GetKey 任何一帧都准，可作为兜底。
        // 只读一处都出现过"明明按着却没读到"的情况，两处取或最稳。
        static bool ModifierHeld => Event.current.shift
            || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }
}
