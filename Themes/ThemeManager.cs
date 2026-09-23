using System;
using System.Windows;

namespace R2Cmd
{
    public static class ThemeManager
    {
        public static bool IsDarkTheme { get; private set; } = true;

        // Add an event that controls can listen to
        public static event EventHandler? ThemeChanged;

        public static void ApplyTheme(bool isDark)
        {
            IsDarkTheme = isDark;
            // FIXED: Added "Themes/" prefix because files were moved
            string themeFile = IsDarkTheme ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";

            var dict = new ResourceDictionary { Source = new Uri(themeFile, UriKind.Relative) };

            // Replaces the palette dictionary at Index 0 without touching styles at Index 1
            Application.Current.Resources.MergedDictionaries[0] = dict;

            // Notify custom controls like the Terminal that the dictionary changed
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void ToggleTheme()
        {
            ApplyTheme(!IsDarkTheme);
        }
    }
}
