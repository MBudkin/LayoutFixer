using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LayoutFixer.Input;
using LayoutFixer.Settings;
using DrawingColor = System.Drawing.Color;

namespace LayoutFixer.UI;

internal static class AppIcons
{
    private static Icon? _color;
    private static Icon? _grayscale;
    private static Icon? _languageRussian;
    private static Icon? _languageEnglish;
    private static Icon? _languageRussianGrayscale;
    private static Icon? _languageEnglishGrayscale;
    private static Icon? _flagRussian;
    private static Icon? _flagEnglish;
    private static Icon? _flagRussianGrayscale;
    private static Icon? _flagEnglishGrayscale;

    public static Icon Current
    {
        get
        {
            return _color ??= Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
    }

    /// <summary>Same as <see cref="Current"/>; explicit color variant.</summary>
    public static Icon Color => Current;

    /// <summary>
    /// A desaturated copy of <see cref="Color"/>, used for the notification-area
    /// icon while auto-correction is off (so the state is visible at a glance).
    /// </summary>
    public static Icon Grayscale
    {
        get
        {
            return _grayscale ??= MakeGrayscale(Color);
        }
    }

    public static Icon ForAutoMode(bool autoMode) => autoMode ? Color : Grayscale;

    public static Icon ForTray(TrayIconMode mode, bool autoMode, ConvertedLanguage language)
    {
        Icon colorIcon = mode switch
        {
            TrayIconMode.Language => ForLanguage(language),
            TrayIconMode.Flag => ForFlag(language),
            _ => Color
        };

        if (autoMode)
        {
            return colorIcon;
        }

        return (mode, language) switch
        {
            (TrayIconMode.Language, ConvertedLanguage.Russian) =>
                _languageRussianGrayscale ??= MakeGrayscale(colorIcon),
            (TrayIconMode.Language, ConvertedLanguage.English) =>
                _languageEnglishGrayscale ??= MakeGrayscale(colorIcon),
            (TrayIconMode.Flag, ConvertedLanguage.Russian) =>
                _flagRussianGrayscale ??= MakeGrayscale(colorIcon),
            (TrayIconMode.Flag, ConvertedLanguage.English) =>
                _flagEnglishGrayscale ??= MakeGrayscale(colorIcon),
            _ => Grayscale
        };
    }

    private static Icon ForLanguage(ConvertedLanguage language)
    {
        return language switch
        {
            ConvertedLanguage.Russian => _languageRussian ??= MakeTextIcon("Ru", DrawingColor.FromArgb(0x00, 0x33, 0x70)),
            ConvertedLanguage.English => _languageEnglish ??= MakeTextIcon("En", DrawingColor.FromArgb(0x70, 0x00, 0x00)),
            _ => Color
        };
    }

    private static Icon ForFlag(ConvertedLanguage language)
    {
        return language switch
        {
            ConvertedLanguage.Russian => _flagRussian ??= MakeRussianFlagIcon(),
            ConvertedLanguage.English => _flagEnglish ??= MakeAmericanFlagIcon(),
            _ => Color
        };
    }

    private static Icon MakeGrayscale(Icon source)
    {
        try
        {
            using Bitmap original = source.ToBitmap();
            Bitmap gray = new Bitmap(original.Width, original.Height, original.PixelFormat);
            using (Graphics graphics = Graphics.FromImage(gray))
            {
                // Luminosity-weighted desaturation matrix.
                ColorMatrix matrix = new ColorMatrix(
                    new[]
                    {
                        new[] { 0.30f, 0.30f, 0.30f, 0f, 0f },
                        new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                        new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                        new[] { 0f, 0f, 0f, 1f, 0f },
                        new[] { 0f, 0f, 0f, 0f, 1f }
                    });

                using ImageAttributes attributes = new ImageAttributes();
                attributes.SetColorMatrix(matrix);

                graphics.DrawImage(
                    original,
                    new Rectangle(0, 0, original.Width, original.Height),
                    0, 0, original.Width, original.Height,
                    GraphicsUnit.Pixel,
                    attributes);
            }

            return CreateIcon(gray);
        }
        catch
        {
            return source;
        }
    }

    private static Icon MakeTextIcon(string text, DrawingColor background)
    {
        using Bitmap bitmap = new(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(background);
            using Pen border = new(DrawingColor.FromArgb(90, DrawingColor.White));
            graphics.DrawRectangle(border, 0, 0, 31, 31);

            TextRenderer.DrawText(
                graphics,
                text,
                new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Point),
                new Rectangle(0, 1, 32, 30),
                DrawingColor.White,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding);
        }

        return CreateIcon(bitmap);
    }

    private static Icon MakeRussianFlagIcon()
    {
        using Bitmap bitmap = new(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(DrawingColor.White);
            graphics.FillRectangle(Brushes.White, 0, 0, 32, 11);
            using SolidBrush blue = new(DrawingColor.FromArgb(0x00, 0x39, 0xA6));
            using SolidBrush red = new(DrawingColor.FromArgb(0xD5, 0x2B, 0x1E));
            graphics.FillRectangle(blue, 0, 11, 32, 10);
            graphics.FillRectangle(red, 0, 21, 32, 11);
            using Pen border = new(DrawingColor.FromArgb(120, 0, 0, 0));
            graphics.DrawRectangle(border, 0, 0, 31, 31);
        }

        return CreateIcon(bitmap);
    }

    private static Icon MakeAmericanFlagIcon()
    {
        using Bitmap bitmap = new(32, 32);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(DrawingColor.White);
            using SolidBrush red = new(DrawingColor.FromArgb(0xB3, 0x19, 0x42));
            for (int y = 0; y < 32; y += 5)
            {
                graphics.FillRectangle(red, 0, y, 32, 3);
            }

            using SolidBrush blue = new(DrawingColor.FromArgb(0x3C, 0x3B, 0x6E));
            graphics.FillRectangle(blue, 0, 0, 15, 17);
            using SolidBrush white = new(DrawingColor.White);
            for (int row = 0; row < 3; row++)
            {
                for (int col = 0; col < 4; col++)
                {
                    graphics.FillRectangle(white, 2 + col * 3, 2 + row * 5, 1, 1);
                }
            }

            using Pen border = new(DrawingColor.FromArgb(120, 0, 0, 0));
            graphics.DrawRectangle(border, 0, 0, 31, 31);
        }

        return CreateIcon(bitmap);
    }

    private static Icon CreateIcon(Bitmap bitmap)
    {
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
