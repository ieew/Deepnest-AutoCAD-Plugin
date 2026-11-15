using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DeepNest.Cad.UI;

/// <summary>
/// 跟随 AutoCAD 的界面主题（COLORTHEME 系统变量，只读不写）给面板着色。
/// AutoCAD 深色主题下 WPF 默认是黑字，会出现"看不见字"的问题。
/// </summary>
public static class ThemePalette
{
    public sealed class Palette
    {
        public required Brush Background { get; init; }
        public required Brush Foreground { get; init; }
        public required Brush Field { get; init; }
        public required Brush Border { get; init; }
        public required Brush Button { get; init; }
        public required Brush Accent { get; init; }
        public required bool IsDark { get; init; }
    }

    public static bool IsDarkTheme()
    {
        try
        {
            var value = AcadApp.GetSystemVariable("COLORTHEME");
            return Convert.ToInt16(value) == 0; // 0 = 深色，1 = 浅色
        }
        catch
        {
            return true;
        }
    }

    public static Palette Current()
    {
        bool dark = IsDarkTheme();

        return dark
            ? new Palette
            {
                Background = Brush(0x2B, 0x2B, 0x2B),
                Foreground = Brush(0xE6, 0xE6, 0xE6),
                Field = Brush(0x3A, 0x3A, 0x3A),
                Border = Brush(0x5A, 0x5A, 0x5A),
                Button = Brush(0x3F, 0x3F, 0x3F),
                Accent = Brush(0x4F, 0x9C, 0xD6),
                IsDark = true
            }
            : new Palette
            {
                Background = Brush(0xF2, 0xF2, 0xF2),
                Foreground = Brush(0x1A, 0x1A, 0x1A),
                Field = Brush(0xFF, 0xFF, 0xFF),
                Border = Brush(0xC0, 0xC0, 0xC0),
                Button = Brush(0xE8, 0xE8, 0xE8),
                Accent = Brush(0x0B, 0x5C, 0xAB),
                IsDark = false
            };
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>把主题应用到控件及其隐式样式（文本框/按钮/列表/复选框/进度条）。</summary>
    public static void Apply(Control root)
    {
        var palette = Current();

        root.Background = palette.Background;
        root.Foreground = palette.Foreground;

        root.Resources[typeof(TextBlock)] = StyleOf(typeof(TextBlock),
            (TextBlock.ForegroundProperty, palette.Foreground));

        root.Resources[typeof(TextBox)] = StyleOf(typeof(TextBox),
            (Control.BackgroundProperty, palette.Field),
            (Control.ForegroundProperty, palette.Foreground),
            (Control.BorderBrushProperty, palette.Border),
            (TextBoxBase.CaretBrushProperty, palette.Foreground),
            (Control.PaddingProperty, new Thickness(3, 2, 3, 2)),
            (Control.BorderThicknessProperty, new Thickness(1)));

        root.Resources[typeof(Button)] = StyleOf(typeof(Button),
            (Control.BackgroundProperty, palette.Button),
            (Control.ForegroundProperty, palette.Foreground),
            (Control.BorderBrushProperty, palette.Border),
            (Control.PaddingProperty, new Thickness(8, 4, 8, 4)),
            (Control.MarginProperty, new Thickness(0, 3, 6, 3)),
            (Control.BorderThicknessProperty, new Thickness(1)));

        root.Resources[typeof(CheckBox)] = StyleOf(typeof(CheckBox),
            (Control.ForegroundProperty, palette.Foreground),
            (Control.MarginProperty, new Thickness(0, 3, 0, 3)));

        root.Resources[typeof(ListBox)] = StyleOf(typeof(ListBox),
            (Control.BackgroundProperty, palette.Field),
            (Control.ForegroundProperty, palette.Foreground),
            (Control.BorderBrushProperty, palette.Border),
            (Control.BorderThicknessProperty, new Thickness(1)));

        root.Resources[typeof(ListBoxItem)] = StyleOf(typeof(ListBoxItem),
            (Control.ForegroundProperty, palette.Foreground),
            (Control.PaddingProperty, new Thickness(4, 2, 4, 2)));

        root.Resources[typeof(ProgressBar)] = StyleOf(typeof(ProgressBar),
            (Control.BackgroundProperty, palette.Field),
            (Control.ForegroundProperty, palette.Accent),
            (Control.BorderBrushProperty, palette.Border),
            (Control.BorderThicknessProperty, new Thickness(1)));

        root.Resources[typeof(Separator)] = StyleOf(typeof(Separator),
            (Control.BackgroundProperty, palette.Border));
    }

    private static Style StyleOf(Type target, params (DependencyProperty Property, object Value)[] setters)
    {
        var style = new Style(target);
        foreach (var (property, value) in setters)
        {
            style.Setters.Add(new Setter(property, value));
        }
        style.Seal();
        return style;
    }
}
