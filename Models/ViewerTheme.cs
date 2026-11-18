using System.Windows;
using System.Windows.Media;

namespace LiteDocViewer.Models;

internal sealed record ViewerTheme(string Id, string Name, bool IsDark,
    string Page, string Panel, string Toolbar, string Border, string Text, string Muted,
    string Accent, string Hover, string Selection, string StringColor, string LiteralColor)
{
    public static IReadOnlyList<ViewerTheme> All { get; } =
    [
        new("light", "밝게", false, "#FFFFFF", "#FFFFFF", "#F7F8FA", "#D0D7DE", "#24292F", "#57606A",
            "#0969DA", "#F0F3F6", "#DDF0FF", "#116329", "#8250DF"),
        new("dark", "어둡게", true, "#0D1117", "#161B22", "#161B22", "#30363D", "#E6EDF3", "#8B949E",
            "#58A6FF", "#21262D", "#1C3652", "#7EE787", "#D2A8FF"),
        new("nord", "Nord", true, "#2E3440", "#2E3440", "#3B4252", "#4C566A", "#ECEFF4", "#D8DEE9",
            "#88C0D0", "#3B4252", "#434C5E", "#A3BE8C", "#B48EAD"),
        new("latte", "Catppuccin Latte", false, "#EFF1F5", "#E6E9EF", "#DCE0E8", "#ACB0BE", "#4C4F69", "#5C5F77",
            "#6432A8", "#DCE0E8", "#CCD0DA", "#286224", "#8A4A13"),
        new("mocha", "Catppuccin Mocha", true, "#1E1E2E", "#181825", "#181825", "#45475A", "#CDD6F4", "#A6ADC8",
            "#CBA6F7", "#313244", "#45475A", "#A6E3A1", "#FAB387")
    ];

    public ResourceDictionary CreateResources() => new()
    {
        ["WindowBackgroundBrush"] = Brush(Page),
        ["PanelBackgroundBrush"] = Brush(Panel),
        ["ToolbarBackgroundBrush"] = Brush(Toolbar),
        ["BorderBrush"] = Brush(Border),
        ["PrimaryTextBrush"] = Brush(Text),
        ["SecondaryTextBrush"] = Brush(Muted),
        ["AccentBrush"] = Brush(Accent),
        ["HoverBrush"] = Brush(Hover),
        [SystemColors.HighlightBrushKey] = Brush(Selection),
        [SystemColors.InactiveSelectionHighlightBrushKey] = Brush(Selection),
        [SystemColors.HighlightTextBrushKey] = Brush(Text),
        [SystemColors.InactiveSelectionHighlightTextBrushKey] = Brush(Text),
        [SystemColors.MenuTextBrushKey] = Brush(Text),
        [SystemColors.MenuBrushKey] = Brush(Panel)
    };

    public string PreviewCss => $$"""
        :root {
            color-scheme: {{(IsDark ? "dark" : "light")}};
            --page: {{Page}}; --text: {{Text}}; --muted: {{Muted}};
            --border: {{Border}}; --code: {{Hover}}; --accent: {{Accent}};
            --string: {{StringColor}}; --literal: {{LiteralColor}};
        }
        """;

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
