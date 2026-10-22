using System.Drawing;

namespace PureRDP.UI;

/// <summary>
/// Shared visual language for the connection library. Keeping the palette and spacing
/// in one place makes the WinForms UI behave like a small design system rather than a
/// collection of unrelated controls.
/// </summary>
internal static class UiDesignTokens
{
    // Restore the screenshot's airy mint/sky palette while retaining readable
    // text, white cards and stronger boundaries for editable controls.
    public static readonly Color PageBackground = Color.FromArgb(241, 249, 246);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceSubtle = Color.FromArgb(247, 252, 249);
    public static readonly Color SurfaceSoft = Color.FromArgb(237, 248, 242);
    public static readonly Color SurfacePressed = Color.FromArgb(220, 239, 227);
    public static readonly Color TextPrimary = Color.FromArgb(24, 57, 48);
    public static readonly Color TextSecondary = Color.FromArgb(61, 96, 86);
    public static readonly Color TextMuted = Color.FromArgb(80, 108, 96);
    public static readonly Color Border = Color.FromArgb(218, 235, 226);
    public static readonly Color BorderStrong = Color.FromArgb(111, 142, 125);
    public static readonly Color Accent = Color.FromArgb(37, 124, 75);
    public static readonly Color AccentHover = Color.FromArgb(31, 111, 66);
    public static readonly Color AccentPressed = Color.FromArgb(24, 91, 54);
    public static readonly Color AccentSoft = Color.FromArgb(228, 245, 234);
    public static readonly Color HeroSky = Color.FromArgb(218, 244, 252);
    public static readonly Color HeroGrass = Color.FromArgb(226, 245, 225);
    public static readonly Color Danger = Color.FromArgb(177, 53, 48);
    public static readonly Color DangerSoft = Color.FromArgb(242, 223, 220);
    public static readonly Color Warning = Color.FromArgb(130, 83, 16);

    public const int PagePadding = 16;
    public const int HeaderHeight = 170;
    public const int ControlHeight = 36;
    public const int CardGap = 6;
    public const int ListCardMinimumWidth = 560;
    public const string FontFamily = "Microsoft YaHei UI";
    public const int ButtonRadius = 8;
    public const int SurfaceRadius = 12;
}
