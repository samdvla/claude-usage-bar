using System.Drawing;
using Microsoft.Win32;
using ClaudeUsage.Core;

namespace ClaudeUsageBar;

public sealed class Theme
{
    public bool Dark { get; private set; }

    public Theme() => Refresh();

    public void Refresh()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            Dark = (k?.GetValue("AppsUseLightTheme") as int? ?? 1) == 0;
        }
        catch { Dark = false; }
    }

    public Color Panel      => Dark ? Color.FromArgb(28, 28, 30)   : Color.FromArgb(250, 250, 252);
    public Color Primary    => Dark ? Color.FromArgb(235, 235, 240) : Color.FromArgb(20, 20, 24);
    public Color Secondary  => Dark ? Color.FromArgb(150, 150, 158) : Color.FromArgb(110, 110, 120);
    public Color Track      => Dark ? Color.FromArgb(58, 58, 62)   : Color.FromArgb(225, 225, 230);

    public static Color Health(HealthLevel level) => level switch
    {
        HealthLevel.Red    => Color.FromArgb(255, 69, 58),
        HealthLevel.Orange => Color.FromArgb(255, 159, 10),
        _                  => Color.FromArgb(48, 209, 88),
    };
}
