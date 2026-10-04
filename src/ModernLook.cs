using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Przegladarka
{
    // Wyglad "Nowoczesny": spokojny pasek bez kolorowych tel, jednokolorowe ikony Windows 11,
    // jeden kolor akcentu (niebieski Velivo), kolor tylko przy najechaniu albo gdy cos jest wlaczone.
    // Wyglad "Kolorowy" (dotychczasowy) zostaje do wyboru w ustawieniach.
    public partial class MainWindow
    {
        bool Modern { get { return _settings == null || !string.Equals(_settings.UiStyle, "colorful", StringComparison.OrdinalIgnoreCase); } }

        bool DarkThemeActive
        {
            get
            {
                var c = (Color)ColorConverter.ConvertFromString(CurrentTheme.ToolBar);
                return c.R * 0.299 + c.G * 0.587 + c.B * 0.114 < 128;
            }
        }

        Color AccentColor { get { return DarkThemeActive ? Color.FromRgb(0x60, 0xA5, 0xFA) : Color.FromRgb(0x25, 0x63, 0xEB); } }
        Brush AccentBrush { get { return new SolidColorBrush(AccentColor); } }
        Brush AccentTint { get { var c = AccentColor; return new SolidColorBrush(Color.FromArgb(DarkThemeActive ? (byte)0x40 : (byte)0x22, c.R, c.G, c.B)); } }

        readonly Dictionary<Control, Tuple<Brush, Brush>> _colorfulLook = new Dictionary<Control, Tuple<Brush, Brush>>();
        ControlTemplate _addressColorfulTemplate;
        ResourceDictionary _modernAppStyles;

        IEnumerable<Button> ToolbarButtons()
        {
            foreach (var p in new Panel[] { ToolBarPanel, TabBarPanel })
                foreach (var b in AllButtons(p)) yield return b;
        }

        IEnumerable<Button> AllButtons(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                var b = child as Button;
                if (b != null) { yield return b; continue; }   // wnetrza przyciskow (np. tarczy) nie przegladamy
                var sp = child as StackPanel;
                if (sp != null && (sp.Name == "ExtBar")) continue;   // ikonki dodatkow maja wlasne kolory
                if (sp != null && sp.Name == "TabStrip") { yield return NewTabBtn; continue; }   // karty koloruje SelectTabColors
                foreach (var x in AllButtons(child)) yield return x;
            }
        }

        // Wywolywane przy starcie, po zmianie motywu i po zmianie stylu w ustawieniach.
        void ApplyUiStyle()
        {
            try
            {
                if (_colorfulLook.Count == 0)
                    foreach (var b in ToolbarButtons())
                        if (b != null && !_colorfulLook.ContainsKey(b)) _colorfulLook[b] = Tuple.Create(b.Background, b.Foreground);
                if (_addressColorfulTemplate == null) _addressColorfulTemplate = Address.Template;

                foreach (var kv in _colorfulLook)
                {
                    var b = kv.Key;
                    if (Modern) { b.Background = Brushes.Transparent; b.SetResourceReference(Control.ForegroundProperty, "VelivoFg"); }
                    else { b.Background = kv.Value.Item1; b.Foreground = kv.Value.Item2; }
                }
                Resources["VelivoHover"] = Modern ? AccentTint : ThemeBrush(CurrentTheme.Hover);

                Address.Template = Modern ? ModernAddressTemplate() : _addressColorfulTemplate;
                Address.BorderThickness = new Thickness(1);

                // ujednolicone okna (pobrane, historia, ustawienia, okienka) - style dla calego programu
                var app = Application.Current;
                if (app != null)
                {
                    if (_modernAppStyles == null) _modernAppStyles = (ResourceDictionary)XamlReader.Parse(ModernAppStylesXaml);
                    bool has = app.Resources.MergedDictionaries.Contains(_modernAppStyles);
                    if (Modern && !has) app.Resources.MergedDictionaries.Add(_modernAppStyles);
                    if (!Modern && has) app.Resources.MergedDictionaries.Remove(_modernAppStyles);
                    if (Modern) _modernAppStyles["VAccent"] = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                }

                UpdateDarkButton(); UpdateStar(); UpdateCounter(); UpdateProfileBadge();
                if (_current != null) SelectTabColors();
                UpdateModernReadButtons();
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        ControlTemplate ModernAddressTemplate()
        {
            var accent = AccentColor;
            var xaml = @"<ControlTemplate TargetType='TextBox' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <Border x:Name='Bd' CornerRadius='17' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' Padding='8,0,8,0'>
    <ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center' Focusable='False' HorizontalScrollBarVisibility='Hidden' VerticalScrollBarVisibility='Hidden'/>
  </Border>
  <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='" + ToHex(Color.FromArgb(0xAA, accent.R, accent.G, accent.B)) + @"'/></Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Bd' Property='BorderBrush' Value='" + ToHex(accent) + @"'/><Setter TargetName='Bd' Property='BorderThickness' Value='2'/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>";
            return (ControlTemplate)XamlReader.Parse(xaml);
        }

        static string ToHex(Color c) { return "#" + c.A.ToString("X2") + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        // ---------- stany przyciskow w nowoczesnym wygladzie (wywolywane na koncu dotychczasowych metod) ----------

        void ModernDarkButton()
        {
            if (!Modern) return;
            DarkBtn.Background = _settings.NightLight || _settings.DarkPages ? AccentTint : Brushes.Transparent;
            if (_settings.NightLight) DarkBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xEA, 0x8A, 0x1A));
            else if (_settings.DarkPages) DarkBtn.Foreground = AccentBrush;
            else DarkBtn.SetResourceReference(Control.ForegroundProperty, "VelivoFg");
        }

        void ModernStar(bool marked)
        {
            if (!Modern) return;
            if (marked) StarBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));   // zlota gwiazdka = zakladka
            else StarBtn.SetResourceReference(Control.ForegroundProperty, "VelivoFg");
        }

        void ModernShield()
        {
            if (!Modern) return;
            if (_blocker.Enabled)
            {
                AdToggle.Background = Brushes.Transparent;
                AdIcon.Foreground = new SolidColorBrush(DarkThemeActive ? Color.FromRgb(0x4A, 0xDE, 0x80) : Color.FromRgb(0x16, 0xA3, 0x4A));
                AdCounter.SetResourceReference(TextBlock.ForegroundProperty, "VelivoFg");
            }
            else
            {
                AdToggle.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xDC, 0x26, 0x26));
                AdIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
                AdCounter.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            }
        }

        void ModernProfileBadge()
        {
            if (!Modern) return;
            ProfileBadgeBtn.Background = AccentTint;
            ProfileBadgeBtn.Foreground = AccentBrush;
        }

        void UpdateModernReadButtons()
        {
            if (!Modern) return;
            if (ReadStopBtn.Visibility == Visibility.Visible)
            {
                ReadBtn.Background = AccentTint; ReadBtn.Foreground = AccentBrush;
                ReadStopBtn.Background = Brushes.Transparent; ReadStopBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
            }
            else { ReadBtn.Background = Brushes.Transparent; ReadBtn.SetResourceReference(Control.ForegroundProperty, "VelivoFg"); }
            if (KeyBtn.Visibility == Visibility.Visible) { KeyBtn.Background = AccentTint; KeyBtn.Foreground = AccentBrush; }
        }

        // Karta: aktywna wyrozniona pogrubionym tytulem.
        void ModernTabLook(BrowserTab t, bool on)
        {
            if (t.Title == null) return;
            t.Title.FontWeight = Modern && on ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // ---------- ikony menu prawego przycisku (zamiast emoji) ----------

        static readonly Regex LeadingSymbols = new Regex(@"^[^\p{L}\p{N}„""(]+");

        // etykieta bez emoji na poczatku (w nowoczesnym wygladzie)
        string MenuText(string label) { return Modern ? LeadingSymbols.Replace(label, "") : label; }

        readonly Dictionary<string, byte[]> _glyphCache = new Dictionary<string, byte[]>();

        // Ikona Segoe Fluent jako obrazek PNG dla menu silnika (w kolorze akcentu - czytelna w jasnym i ciemnym menu).
        Stream GlyphIcon(string glyph)
        {
            if (!Modern || string.IsNullOrEmpty(glyph)) return null;
            try
            {
                byte[] png;
                if (!_glyphCache.TryGetValue(glyph, out png))
                {
                    var tb = new TextBlock
                    {
                        Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 24,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)), Width = 32, Height = 32,
                        TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 4, 0, 0)
                    };
                    tb.Measure(new Size(32, 32)); tb.Arrange(new Rect(0, 0, 32, 32));
                    var bmp = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render(tb);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var ms = new MemoryStream()) { enc.Save(ms); png = ms.ToArray(); }
                    _glyphCache[glyph] = png;
                }
                return new MemoryStream(png);
            }
            catch (Exception) { return null; }
        }

        // Ikona dla menu WPF (karty, dodatki) - w obu wygladach
        static TextBlock MenuGlyph(string glyph)
        {
            return new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        }

        // ---------- wspolny wyglad okien ----------

        const string ModernAppStylesXaml = @"<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <SolidColorBrush x:Key='VAccent' Color='#2563EB'/>
  <Style TargetType='Window'>
    <Setter Property='Background' Value='#F8FAFC'/>
    <Setter Property='FontFamily' Value='Segoe UI Variable Text, Segoe UI'/>
  </Style>
  <Style TargetType='Button'>
    <Setter Property='Background' Value='#FFFFFF'/>
    <Setter Property='Foreground' Value='#0F172A'/>
    <Setter Property='BorderBrush' Value='#CBD5E1'/>
    <Setter Property='BorderThickness' Value='1'/>
    <Setter Property='Padding' Value='12,5'/>
    <Setter Property='MinHeight' Value='28'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='Button'>
          <Border x:Name='Bd' CornerRadius='8' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}'>
            <ContentPresenter Margin='{TemplateBinding Padding}' HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center' RecognizesAccessKey='True'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='#EFF6FF'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='#93C5FD'/>
            </Trigger>
            <Trigger Property='IsPressed' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='#DBEAFE'/>
            </Trigger>
            <Trigger Property='IsDefault' Value='True'>
              <Setter TargetName='Bd' Property='Background' Value='#2563EB'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='#2563EB'/>
              <Setter Property='Foreground' Value='White'/>
            </Trigger>
            <MultiTrigger>
              <MultiTrigger.Conditions>
                <Condition Property='IsDefault' Value='True'/>
                <Condition Property='IsMouseOver' Value='True'/>
              </MultiTrigger.Conditions>
              <Setter TargetName='Bd' Property='Background' Value='#1D4ED8'/>
              <Setter TargetName='Bd' Property='BorderBrush' Value='#1D4ED8'/>
            </MultiTrigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.5'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='TextBox'>
    <Setter Property='BorderBrush' Value='#CBD5E1'/>
    <Setter Property='Background' Value='#FFFFFF'/>
    <Setter Property='Padding' Value='4,3'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='TextBox'>
          <Border x:Name='Bd' CornerRadius='8' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'>
            <ScrollViewer x:Name='PART_ContentHost' Margin='{TemplateBinding Padding}' Focusable='False'/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsKeyboardFocused' Value='True'>
              <Setter TargetName='Bd' Property='BorderBrush' Value='#2563EB'/>
            </Trigger>
            <Trigger Property='IsEnabled' Value='False'>
              <Setter Property='Opacity' Value='0.6'/>
            </Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='ListBox'>
    <Setter Property='BorderBrush' Value='#E2E8F0'/>
    <Setter Property='Background' Value='#FFFFFF'/>
  </Style>
  <Style TargetType='ListBoxItem'>
    <Setter Property='Padding' Value='8,4'/>
    <Setter Property='Template'>
      <Setter.Value>
        <ControlTemplate TargetType='ListBoxItem'>
          <Border x:Name='Bd' CornerRadius='6' Background='Transparent' Padding='{TemplateBinding Padding}' Margin='2,1'>
            <ContentPresenter/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bd' Property='Background' Value='#F1F5F9'/></Trigger>
            <Trigger Property='IsSelected' Value='True'><Setter TargetName='Bd' Property='Background' Value='#DBEAFE'/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style TargetType='CheckBox'>
    <Setter Property='Margin' Value='0,3,0,3'/>
    <Setter Property='Cursor' Value='Hand'/>
  </Style>
</ResourceDictionary>";
    }
}
