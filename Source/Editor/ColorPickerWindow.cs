using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ASQPicker;

public class ColorPickerWindow : Dialog_ColorPickerBase
{
    private readonly Action<Color> onSave;

    private static readonly List<Color> pickableColors = new List<Color>
    {
        new Color(0.30f, 0.69f, 0.20f), // 绿
        new Color(0.95f, 0.85f, 0.20f), // 黄
        new Color(0.85f, 0.20f, 0.20f), // 红
        new Color(1.00f, 0.50f, 0.00f), // 橙
        new Color(0.20f, 0.50f, 1.00f), // 蓝
        new Color(0.50f, 0.20f, 0.80f), // 紫
        Color.white,
        new Color(0.55f, 0.55f, 0.55f), // 灰
        Color.black,
        Color.red,
        Color.green,
        Color.blue,
        Color.yellow,
        Color.cyan,
        Color.magenta,
    };

    protected override Color DefaultColor => oldColor;
    protected override bool ShowDarklight => false;
    protected override List<Color> PickableColors => pickableColors;
    protected override float ForcedColorValue => -1f;
    protected override bool ShowColorTemperatureBar => false;
    public override Vector2 InitialSize => new Vector2(600f, 520f);

    public ColorPickerWindow(Color initialColor, Action<Color> onSave)
        : base(
            Widgets.ColorComponents.All,
            Widgets.ColorComponents.Red | Widgets.ColorComponents.Green | Widgets.ColorComponents.Blue)
    {
        color = initialColor;
        oldColor = initialColor;
        this.onSave = onSave;
    }

    protected override void SaveColor(Color color)
    {
        // 取色器不编辑透明度，保存时保留原有 alpha。
        onSave(new Color(color.r, color.g, color.b, oldColor.a));
    }

    /// <summary>以 HTML 十六进制字符串形式读写颜色（颜色无法直接用 Scribe_Values 序列化）。
    public static void LookColor(ref Color color, string label, Color defaultColor)
    {
        string text = "#" + ColorUtility.ToHtmlStringRGBA(color);
        Scribe_Values.Look(ref text, label, "#" + ColorUtility.ToHtmlStringRGBA(defaultColor));
        if (ColorUtility.TryParseHtmlString(text, out Color parsed))
        {
            color = parsed;
        }
        else
        {
            color = defaultColor;
        }
    }

    /// <summary>同上，但读写的是「以字符串为键、Color 为值」的字典。
    /// 运行时直接存 Color，只在存取这一刻转成 HTML 字符串中转；解析失败的条目跳过（该键回到默认色）。
    /// 用于 Scribe_Collections 无法直接处理 Color 的场合（如 mod 设置里的 defName → 颜色 表）。
    public static void LookColorDictionary(ref Dictionary<string, Color> colors, string label)
    {
        Dictionary<string, string>? text = null;

        if (Scribe.mode == LoadSaveMode.Saving)
        {
            text = new Dictionary<string, string>(colors?.Count ?? 0);
            if (colors != null)
            {
                foreach (KeyValuePair<string, Color> entry in colors)
                {
                    text[entry.Key] = "#" + ColorUtility.ToHtmlStringRGBA(entry.Value);
                }
            }
        }

        Scribe_Collections.Look(ref text, label, LookMode.Value, LookMode.Value);

        Dictionary<string, Color> result = new Dictionary<string, Color>(text?.Count ?? 0);
        if (text != null)
        {
            foreach (KeyValuePair<string, string> entry in text)
            {
                if (entry.Key != null && ColorUtility.TryParseHtmlString(entry.Value, out Color parsed))
                {
                    result[entry.Key] = parsed;
                }
            }
        }

        colors = result;
    }
}
