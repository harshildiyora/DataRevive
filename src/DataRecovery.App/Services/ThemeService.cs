using System;
using System.Windows;
using System.Windows.Media;

namespace DataRecovery.App.Services
{
    public static class ThemeService
    {
        private static readonly BrushConverter Converter = new();

        public static void ApplyTheme(bool isDark)
        {
            var res = Application.Current.Resources;

            if (isDark)
            {
                // Dark Theme Palette
                SetBrush(res, "WindowBackgroundBrush", "#121212");
                SetBrush(res, "HeaderBackgroundBrush", "#181818");
                SetBrush(res, "PanelBackgroundBrush", "#1A1A1A");
                SetBrush(res, "CardBackgroundBrush", "#202020");
                SetBrush(res, "CardHoverBrush", "#2C2C2C");
                SetBrush(res, "CardSelectedBrush", "#142C44");
                SetBrush(res, "CardBorderBrush", "#2E2E2E");

                SetBrush(res, "TextPrimaryBrush", "#FFFFFF");
                SetBrush(res, "TextSecondaryBrush", "#B4B4B4");
                SetBrush(res, "TextMutedBrush", "#767676");
                SetBrush(res, "BorderBrushBase", "#2E2E2E");

                SetBrush(res, "AccentBrush", "#007ACC");
                SetBrush(res, "AccentHoverBrush", "#1C90ED");
                SetBrush(res, "ButtonBackgroundBrush", "#242424");
                SetBrush(res, "ButtonHoverBrush", "#333333");

                SetBrush(res, "DataGridHeaderBackgroundBrush", "#222222");
                SetBrush(res, "DataGridRowBackgroundBrush", "#181818");
                SetBrush(res, "DataGridRowAltBackgroundBrush", "#1E1E1E");
                SetBrush(res, "DataGridRowSelectedBrush", "#0E4A75");
                SetBrush(res, "DataGridRowSelectedTextBrush", "#FFFFFF");
                SetBrush(res, "DataGridLinesBrush", "#282828");

                SetBrush(res, "InputBackgroundBrush", "#1C1C1C");
                SetBrush(res, "InputBorderBrush", "#333333");
                SetBrush(res, "InputForegroundBrush", "#FFFFFF");

                SetBrush(res, "TabBackgroundBrush", "#1A1A1A");
                SetBrush(res, "TabSelectedBackgroundBrush", "#262626");
                SetBrush(res, "BadgeBackgroundBrush", "#16222F");
            }
            else
            {
                // Light Theme Palette (Fluent clean modern UI)
                SetBrush(res, "WindowBackgroundBrush", "#F3F4F6");
                SetBrush(res, "HeaderBackgroundBrush", "#FFFFFF");
                SetBrush(res, "PanelBackgroundBrush", "#FFFFFF");
                SetBrush(res, "CardBackgroundBrush", "#FFFFFF");
                SetBrush(res, "CardHoverBrush", "#F0F4F8");
                SetBrush(res, "CardSelectedBrush", "#E7F3FF");
                SetBrush(res, "CardBorderBrush", "#E2E8F0");

                SetBrush(res, "TextPrimaryBrush", "#1E293B");
                SetBrush(res, "TextSecondaryBrush", "#475569");
                SetBrush(res, "TextMutedBrush", "#94A3B8");
                SetBrush(res, "BorderBrushBase", "#E2E8F0");

                SetBrush(res, "AccentBrush", "#0078D4");
                SetBrush(res, "AccentHoverBrush", "#106EBE");
                SetBrush(res, "ButtonBackgroundBrush", "#F8FAFC");
                SetBrush(res, "ButtonHoverBrush", "#E2E8F0");

                SetBrush(res, "DataGridHeaderBackgroundBrush", "#F1F5F9");
                SetBrush(res, "DataGridRowBackgroundBrush", "#FFFFFF");
                SetBrush(res, "DataGridRowAltBackgroundBrush", "#F8FAFC");
                SetBrush(res, "DataGridRowSelectedBrush", "#CDE6FE");
                SetBrush(res, "DataGridRowSelectedTextBrush", "#0F172A");
                SetBrush(res, "DataGridLinesBrush", "#E2E8F0");

                SetBrush(res, "InputBackgroundBrush", "#FFFFFF");
                SetBrush(res, "InputBorderBrush", "#CBD5E1");
                SetBrush(res, "InputForegroundBrush", "#1E293B");

                SetBrush(res, "TabBackgroundBrush", "#F1F5F9");
                SetBrush(res, "TabSelectedBackgroundBrush", "#FFFFFF");
                SetBrush(res, "BadgeBackgroundBrush", "#EFF6FF");
            }
        }

        private static void SetBrush(ResourceDictionary res, string key, string hex)
        {
            if (Converter.ConvertFromString(hex) is Brush brush)
            {
                brush.Freeze();
                res[key] = brush;
            }
        }
    }
}
