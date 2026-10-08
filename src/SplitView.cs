using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Przegladarka
{
    // Podzial ekranu (jak Split Screen w Edge): dwie karty obok siebie, srodkowy pasek przesuwany myszka.
    // Aktywna jest ta, w ktora klikniesz (adres, przyciski). Wlaczanie: prawy przycisk na karcie -> "Pokaz obok".
    public partial class MainWindow
    {
        BrowserTab _splitTab;   // karta pokazana obok aktywnej (null = bez podzialu)
        GridSplitter _splitter;

        // karta widoczna na ekranie: aktywna albo ta obok
        bool IsOnScreen(BrowserTab t) { return t == _current || t == _splitTab; }

        void ShowSideBySide(BrowserTab tab)
        {
            if (tab == null || tab == _current) return;
            _splitTab = tab;
            LayoutSplit();
            SelectTabColors();
        }

        void CloseSplit()
        {
            _splitTab = null;
            LayoutSplit();
            SelectTabColors();
        }

        // kolumny: [aktywna | pasek | obok]; bez podzialu jedna kolumna jak dotad
        void LayoutSplit()
        {
            Host.ColumnDefinitions.Clear();
            if (_splitter != null) { Host.Children.Remove(_splitter); _splitter = null; }
            if (_splitTab != null)
            {
                Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
                Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
                Host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
                _splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Background = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)), ToolTip = L.T("Przeciągnij, aby zmienić podział") };
                Grid.SetColumn(_splitter, 1);
                Host.Children.Add(_splitter);
            }
            foreach (var t in _tabs)
            {
                Grid.SetColumn(t.View, _splitTab != null && t == _splitTab ? 2 : 0);
                Grid.SetColumnSpan(t.View, 1);
            }
        }

        // klikniecie w strone obok: staje sie aktywna (SelectTab zamienia strony, obie zostaja na ekranie)
        void FocusSplitPane(BrowserTab tab)
        {
            if (_splitTab != null && tab == _splitTab) SelectTab(tab);
        }
    }
}
