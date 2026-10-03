using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace Przegladarka
{
    // Motywy samej przegladarki (pasek kart, pasek narzedzi, zakladki) - jak motywy tla w Szybkim Dostepie.
    public partial class MainWindow
    {
        sealed class BrowserTheme
        {
            public string Name;
            public string TabBar, ToolBar, ActiveTab, Fg, AddressBg, AddressBorder, Hover, Pressed, Window;
        }

        static readonly Dictionary<string, BrowserTheme> BrowserThemes = new Dictionary<string, BrowserTheme>(StringComparer.OrdinalIgnoreCase)
        {
            { "jasny",   new BrowserTheme { Name = "Jasny (domyślny)", TabBar = "#E5E7EB", ToolBar = "#FFFFFF", ActiveTab = "#FFFFFF", Fg = "#111827", AddressBg = "#FFFFFF", AddressBorder = "#9CA3AF", Hover = "#E0E7FF", Pressed = "#C7D2FE", Window = "#F3F4F6" } },
            { "grafit",  new BrowserTheme { Name = "Grafit",           TabBar = "#1F2328", ToolBar = "#2D333B", ActiveTab = "#2D333B", Fg = "#E6EDF3", AddressBg = "#22272E", AddressBorder = "#444C56", Hover = "#3B444F", Pressed = "#4B5563", Window = "#1F2328" } },
            { "granat",  new BrowserTheme { Name = "Granat",           TabBar = "#0B1A33", ToolBar = "#132A4D", ActiveTab = "#132A4D", Fg = "#E2E8F0", AddressBg = "#0E2140", AddressBorder = "#2B4A7A", Hover = "#1E3A66", Pressed = "#284B80", Window = "#0B1A33" } },
            { "fiolet",  new BrowserTheme { Name = "Nocny fiolet",     TabBar = "#1E1033", ToolBar = "#2E1A4D", ActiveTab = "#2E1A4D", Fg = "#EDE9FE", AddressBg = "#24143E", AddressBorder = "#5B3E8C", Hover = "#3E2766", Pressed = "#4C2F80", Window = "#1E1033" } },
            { "las",     new BrowserTheme { Name = "Las",              TabBar = "#0F2A1D", ToolBar = "#17402C", ActiveTab = "#17402C", Fg = "#DCFCE7", AddressBg = "#123322", AddressBorder = "#2F6B4A", Hover = "#215A3E", Pressed = "#2A6E4C", Window = "#0F2A1D" } },
            { "ocean",   new BrowserTheme { Name = "Ocean",            TabBar = "#062A33", ToolBar = "#0B3C48", ActiveTab = "#0B3C48", Fg = "#CFFAFE", AddressBg = "#08323D", AddressBorder = "#1F6475", Hover = "#145566", Pressed = "#1A6A7E", Window = "#062A33" } },
            { "zachod",  new BrowserTheme { Name = "Zachód słońca",    TabBar = "#3B1D2E", ToolBar = "#5A2A3D", ActiveTab = "#5A2A3D", Fg = "#FFE4E6", AddressBg = "#4A2333", AddressBorder = "#9A4A5E", Hover = "#7A3A50", Pressed = "#8E4560", Window = "#3B1D2E" } },
            { "oled",    new BrowserTheme { Name = "Czerń (OLED)",     TabBar = "#000000", ToolBar = "#0A0A0A", ActiveTab = "#1A1A1A", Fg = "#F5F5F5", AddressBg = "#111111", AddressBorder = "#333333", Hover = "#262626", Pressed = "#333333", Window = "#000000" } },
            { "papier",  new BrowserTheme { Name = "Papier",           TabBar = "#EDE6D6", ToolBar = "#F8F3E7", ActiveTab = "#F8F3E7", Fg = "#3B3326", AddressBg = "#FFFCF5", AddressBorder = "#C9BC9F", Hover = "#E6DCC5", Pressed = "#D9CBAD", Window = "#EDE6D6" } },
            { "mgla",    new BrowserTheme { Name = "Mgła",             TabBar = "#D5DCE4", ToolBar = "#E8EDF2", ActiveTab = "#F4F7FA", Fg = "#1E293B", AddressBg = "#FFFFFF", AddressBorder = "#A7B4C2", Hover = "#CBD5E1", Pressed = "#B6C3D1", Window = "#D5DCE4" } },
        };

        static Brush ThemeBrush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

        BrowserTheme CurrentTheme
        {
            get
            {
                BrowserTheme t;
                return _settings != null && BrowserThemes.TryGetValue(_settings.Theme ?? "", out t) ? t : BrowserThemes["jasny"];
            }
        }

        void ApplyBrowserTheme()
        {
            try
            {
                var t = CurrentTheme;
                Resources["VelivoFg"] = ThemeBrush(t.Fg);
                Resources["VelivoHover"] = ThemeBrush(t.Hover);
                Resources["VelivoPressed"] = ThemeBrush(t.Pressed);
                Background = ThemeBrush(t.Window);
                TabBarPanel.Background = ThemeBrush(t.TabBar);
                ToolBarPanel.Background = ThemeBrush(t.ToolBar);
                BookmarkBorder.Background = ThemeBrush(t.ToolBar);
                BookmarkBorder.BorderBrush = ThemeBrush(t.TabBar);
                Address.Background = ThemeBrush(t.AddressBg);
                Address.Foreground = ThemeBrush(t.Fg);
                Address.CaretBrush = ThemeBrush(t.Fg);
                Address.BorderBrush = ThemeBrush(t.AddressBorder);
                if (_current != null) SelectTabColors();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        Brush ActiveTabBrush { get { return ThemeBrush(CurrentTheme.ActiveTab); } }
    }
}
