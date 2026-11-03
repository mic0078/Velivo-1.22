using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Velivo
{
    // 🛋 Tryb Kanapa: Velivo na pełnym ekranie telewizora - duże kafelki (Oglądaj dalej, Ulubione kanały, Nowe w bibliotece),
    // sterowanie samymi strzałkami, Enter i Esc (pilot, klawiatura bezprzewodowa). Zaznaczony kafelek - złota ramka i powiększenie.
    // Tylko czyta bibliotekę i kanały i woła te same funkcje odtwarzania co biblioteka / ulubione (PlayVideos) - niczego nie zmienia.
    public partial class MainWindow
    {
        Window _kanapa;
        static readonly Brush KnGold = KnBrush(Color.FromRgb(0xF5, 0xB5, 0x44)), KnFg = KnBrush(Color.FromRgb(0xEE, 0xF2, 0xF8)), KnDim = KnBrush(Color.FromRgb(0x8E, 0x9B, 0xB5)),
            KnCard = KnBrush(Color.FromRgb(0x17, 0x20, 0x36)), KnLive = KnBrush(Color.FromRgb(0xEF, 0x44, 0x44));
        static Brush KnBrush(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        internal void ShowKanapa()
        {
            if (_kanapa != null) { _kanapa.Activate(); return; }
            var w = new Window
            {
                Title = "Velivo – " + L.T("Kanapa"), WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, WindowState = WindowState.Maximized, Topmost = true,
                Background = new RadialGradientBrush(Color.FromRgb(0x1D, 0x2B, 0x4D), Color.FromRgb(0x0B, 0x0F, 0x17)) { Center = new Point(0.8, 0), GradientOrigin = new Point(0.8, 0), RadiusX = 1.1, RadiusY = 1.0 },
                Foreground = KnFg, FontFamily = new FontFamily("Segoe UI"),
            };
            _kanapa = w;
            var root = new DockPanel { Margin = new Thickness(48, 30, 48, 24) };
            // góra: logo, zakładki, zegar
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 22) }; DockPanel.SetDock(top, Dock.Top);
            var logo = new TextBlock { FontSize = 34, FontWeight = FontWeights.ExtraBold, VerticalAlignment = VerticalAlignment.Center };
            logo.Inlines.Add(new System.Windows.Documents.Run("Veli")); logo.Inlines.Add(new System.Windows.Documents.Run("vo") { Foreground = KnGold });
            var clock = new TextBlock { FontSize = 30, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Text = DateTime.Now.ToString("HH:mm") };
            DockPanel.SetDock(logo, Dock.Left); DockPanel.SetDock(clock, Dock.Right);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            top.Children.Add(logo); top.Children.Add(clock); top.Children.Add(tabs);
            var hint = new TextBlock { Foreground = KnDim, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0),
                Text = L.T("←↑→↓ wybór   ·   Enter – oglądaj   ·   Esc – wstecz / wyjście   ·   Home – Kanapa") + "      🎮 " + L.T("A – oglądaj · B – wstecz · Y – szukaj · LB / RB – zakładki") };
            DockPanel.SetDock(hint, Dock.Bottom);
            var body = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
            // ℹ opis wybranego kafelka (jak Jellyfin): tytuł, rok · ocena · gatunek · długość, opis; kanał - co teraz leci
            var detTitle = new TextBlock { FontSize = 30, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            var detMeta = new TextBlock { FontSize = 17, Foreground = KnGold, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            var detDesc = new TextBlock { FontSize = 16, Foreground = KnDim, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.WordEllipsis, MaxHeight = 66, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            var det = new StackPanel { Height = 140, Margin = new Thickness(8, 0, 0, 10) }; det.Children.Add(detTitle); det.Children.Add(detMeta); det.Children.Add(detDesc); DockPanel.SetDock(det, Dock.Top);
            void Opis(string t, string meta, string desc) { detTitle.Text = t ?? ""; detMeta.Text = meta ?? ""; detDesc.Text = desc ?? ""; }
            var infoOf = new Dictionary<Button, Action>();
            root.Children.Add(top); root.Children.Add(hint); root.Children.Add(det); root.Children.Add(body);
            w.Content = root;

            var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) }; tm.Tick += (s, e) => clock.Text = DateTime.Now.ToString("HH:mm"); tm.Start();
            string tab = "start";
            void Wyjdz() { tm.Stop(); w.Close(); }
            void Graj(Action a) { Wyjdz(); try { a(); } catch (Exception ex) { App.LogError(ex); } }

            // kafelek: Button z własnym szablonem - złota ramka i powiększenie, gdy wybrany (z kanapy widać, gdzie jesteś)
            Button Tile(double wd, double ht, UIElement content, Action act)
            {
                var b = new Button { Width = wd, Height = ht, Margin = new Thickness(0, 0, 18, 0), Content = content, Focusable = true, Cursor = Cursors.Hand, RenderTransformOrigin = new Point(0.5, 0.5) };
                b.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>" +
                    "<Border x:Name='bd' CornerRadius='12' BorderThickness='4' BorderBrush='Transparent' Background='#172036' ClipToBounds='True' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                    "<ContentPresenter/></Border>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#F5B544'/></Trigger>" +
                    "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#80F5B544'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
                var sc = new ScaleTransform(1, 1); b.RenderTransform = sc;
                b.GotKeyboardFocus += (s, e) => { sc.ScaleX = sc.ScaleY = 1.07; Panel.SetZIndex(b, 2); b.BringIntoView(new Rect(-40, -40, wd + 80, ht + 80)); if (infoOf.TryGetValue(b, out var inf)) inf(); };
                b.LostKeyboardFocus += (s, e) => { sc.ScaleX = sc.ScaleY = 1; Panel.SetZIndex(b, 0); };
                b.MouseEnter += (s, e) => b.Focus();
                b.Click += (s, e) => act();
                return b;
            }
            UIElement Caption(string t, string sub = null)
            {
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xCC, 0, 0, 0), 90) };
                sp.Children.Add(new TextBlock { Text = t, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = KnFg, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 14, 10, sub == null ? 8 : 0) });
                if (sub != null) sp.Children.Add(new TextBlock { Text = sub, FontSize = 13, Foreground = KnDim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(10, 0, 10, 8) });
                return sp;
            }
            ImageSource Poster(string path)
            {
                try { var m = MetaFor(path); if (m?.Poster != null) { var f = Path.Combine(PosterDir, m.Poster); if (File.Exists(f)) return LoadLogo(f, 360); } } catch (Exception) { }
                return null;
            }
            string Tytul(LibraryVideo v) { try { return ShownTitle(v, MetaFor(v.Path)); } catch (Exception) { return Path.GetFileNameWithoutExtension(v.Path); } }
            string Krotko(LibraryVideo v) { var m = MetaFor(v.Path); return string.Join(" · ", new[] { m?.Year > 0 ? m.Year.ToString() : null, m?.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null }.Where(x => x != null)); }
            void FilmOpis(LibraryVideo v)
            {
                var m = MetaFor(v.Path); var parts = new List<string>();
                if (m?.Year > 0) parts.Add(m.Year.ToString());
                if (m?.Rating > 0) parts.Add("★ " + m.Rating.ToString("0.0"));
                if (m?.Genres?.Count > 0) parts.Add(string.Join(", ", m.Genres.Take(3)));
                if (v.Len > 0) parts.Add((int)(v.Len / 60000) + L.T(" min"));
                if (v.Done) parts.Add(L.T("✓ obejrzany")); else if (v.Len > v.Pos && v.Pos > 0) parts.Add(L.T("Zostało ") + (int)((v.Len - v.Pos) / 60000) + L.T(" min"));
                if (!string.IsNullOrEmpty(m?.Age)) parts.Add(m.Age);
                Opis(Tytul(v), string.Join("   ·   ", parts), string.IsNullOrWhiteSpace(m?.Overview) ? L.T("Brak opisu – uzupełnij dane filmu w bibliotece (Jellyfin).") : m.Overview);
            }
            EpgItem Teraz(IptvChannel ch) { try { return _iptvEpg == null ? null : EpgAt(EpgFor(_iptvEpg, ch), DateTime.Now); } catch (Exception) { return null; } }
            Button FilmTile(LibraryVideo v, double wd = 170)
            {
                var g = new Grid(); var img = Poster(v.Path);
                if (img != null) g.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill });
                else g.Children.Add(new TextBlock { Text = "🎬", FontSize = 46, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = KnDim });
                var kr = Krotko(v); g.Children.Add(Caption(Tytul(v), kr.Length > 0 ? kr : null));
                if (v.Len > 0 && v.Pos > 0 && !v.Done)
                    g.Children.Add(new ProgressBar { Height = 5, VerticalAlignment = VerticalAlignment.Bottom, Minimum = 0, Maximum = v.Len, Value = v.Pos, Foreground = KnGold, Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0) });
                var path = v.Path;
                var b = Tile(wd, wd * 1.5, g, () => Graj(() => PlayVideos(new[] { path }, 0))); infoOf[b] = () => FilmOpis(v); return b;
            }
            List<IptvChannel> FavChannels()
            {
                var all = KnownPlaylist()?.Channels ?? new List<IptvChannel>();
                return IptvSt.Fav.Select(u => _iptvPlaying.TryGetValue(u, out var c1) ? c1 : all.FirstOrDefault(c => c.Url == u)).Where(c => c != null).ToList();
            }
            Button ChannelTile(List<IptvChannel> list, int i)
            {
                var ch = list[i]; var g = new Grid();
                var name = new TextBlock { Text = ch.Name, Foreground = KnFg, FontSize = 24, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10) };
                var logoImg = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(24, 18, 24, 30) };
                g.Children.Add(name); g.Children.Add(logoImg);
                var now = Teraz(ch);
                if (now != null) g.Children.Add(new Border { Background = KnLive, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 2, 7, 2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10),
                    Child = new TextBlock { Text = L.T("NA ŻYWO"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White } });
                if (!string.IsNullOrEmpty(ch.Logo)) _ = SetLogo(ch.Logo, logoImg, name);
                if (now != null) g.Children.Add(new TextBlock { Text = now.Title, FontSize = 13, Foreground = KnFg, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(10, 12, 10, 6),
                    Background = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0xCC, 0, 0, 0), 90) });
                var b = Tile(260, 146, g, () => Graj(() => { foreach (var x in list) _iptvPlaying[x.Url] = x; PlayVideos(list.Select(x => x.Url).ToArray(), i); }));
                infoOf[b] = () =>
                {
                    var p = Teraz(ch);
                    if (p == null) { Opis(ch.Name, string.IsNullOrEmpty(ch.Group) ? L.T("Na żywo") : "📺 " + ch.Group, _iptvEpg == null ? L.T("Wczytuję program TV…") : L.T("Brak programu TV dla tego kanału.")); return; }
                    var left = p.Stop - DateTime.Now;
                    EpgItem nx = null; try { nx = EpgFor(_iptvEpg, ch)?.FirstOrDefault(x => x.Start >= p.Stop); } catch (Exception) { }
                    Opis(ch.Name + "  –  " + p.Title, p.Start.ToString("HH:mm") + "–" + p.Stop.ToString("HH:mm") + "   ·   " + L.T("zostało ") + Math.Max(1, (int)left.TotalMinutes) + L.T(" min") + (p.Cats?.Count > 0 ? "   ·   " + string.Join(", ", p.Cats.Take(2)) : "")
                        + (nx != null ? "   ·   " + L.T("potem: ") + nx.Start.ToString("HH:mm") + " " + nx.Title : ""), p.Desc);
                };
                return b;
            }
            async System.Threading.Tasks.Task SetLogo(string url, Image img, TextBlock name)
            {
                try { var src = await GetLogo(url); if (src != null) { img.Source = src; name.Visibility = Visibility.Collapsed; } } catch (Exception) { }
            }
            UIElement Row(string title, IEnumerable<UIElement> tiles)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 26) };
                sp.Children.Add(new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
                var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
                foreach (var t in tiles) line.Children.Add(t);
                if (line.Children.Count == 0) line.Children.Add(new TextBlock { Text = L.T("Pusto"), Foreground = KnDim, FontSize = 16 });
                sp.Children.Add(new ScrollViewer { Content = line, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false });
                return sp;
            }
            UIElement Wrap(IEnumerable<UIElement> tiles)
            {
                var wp = new WrapPanel { Margin = new Thickness(8) };
                foreach (var t in tiles) { if (t is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 18, 18); wp.Children.Add(t); }
                return wp;
            }

            var tabBtns = new Dictionary<string, Button>();
            int kSort = int.TryParse(GetPref("kanapa-sort"), out var ks0) ? ks0 : 0, kGen = 0, kSortTv = int.TryParse(GetPref("kanapa-sorttv"), out var ks1) ? ks1 : 0, kGrp = 0; Action odswiez = null; bool wyborGrupy = false;   // wybór zapamiętany (grupa, gatunek - po nazwie)
            var szukaj = new TextBox { Width = 380, FontSize = 18, Padding = new Thickness(12, 6, 12, 6), Background = KnCard, Foreground = KnFg, CaretBrush = KnFg, BorderBrush = KnGold, BorderThickness = new Thickness(0, 0, 0, 2), VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = L.T("Szukaj: tytuł, aktor albo kanał i program – zacznij pisać") };
            var tSzukaj = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            tSzukaj.Tick += (s, e) => { tSzukaj.Stop(); odswiez?.Invoke(); };
            szukaj.TextChanged += (s, e) => { tSzukaj.Stop(); tSzukaj.Start(); };
            StackPanel Pasek()
            {
                var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 18) };
                if (szukaj.Parent is Panel old) old.Children.Remove(szukaj);
                bar.Children.Add(new TextBlock { Text = "🔍", FontSize = 22, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }); bar.Children.Add(szukaj);
                return bar;
            }
            void Lista(string title, List<string> items, int cur, Action<int> pick)
            {
                var wp = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) }; var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = title, FontSize = 26, FontWeight = FontWeights.Bold, Margin = new Thickness(8, 0, 0, 16) });
                for (int i = 0; i < items.Count; i++) { int k = i; Przycisk(wp, () => items[k], _ => { pick(k); wyborGrupy = false; Pokaz(tab); }); }
                foreach (var x in wp.Children.OfType<Button>()) x.Margin = new Thickness(8, 0, 8, 12);
                sp.Children.Add(wp); body.Content = sp; body.ScrollToTop(); wyborGrupy = true; odswiez = null;
                w.Dispatcher.BeginInvoke(new Action(() => { var b0 = wp.Children.OfType<Button>().ElementAtOrDefault(cur) ?? wp.Children.OfType<Button>().FirstOrDefault(); b0?.Focus(); b0?.BringIntoView(); }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            void Przycisk(Panel bar, Func<string> text, Action<Button> act)
            {
                var b = new Button { Content = text(), FontSize = 17, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand, Foreground = KnFg, Background = KnCard };
                b.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                    "<Border x:Name='bd' CornerRadius='18' Background='{TemplateBinding Background}' BorderThickness='3' BorderBrush='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#F5B544'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
                b.Click += (s, e) => act(b); bar.Children.Add(b);
            }
            void Pokaz(string t)
            {
                tab = t; var lib = new List<LibraryVideo>(); odswiez = null; wyborGrupy = false; Opis("", "", ""); if (szukaj.Text.Length > 0) szukaj.Text = "";
                try
                {   // ten sam film kilka razy (plik na dysku + strumień z Jellyfin, kopia z innego komputera, kilka wersji) - jeden kafelek:
                    // obejrzany ostatnio (postęp), potem ten z plikiem na dysku
                    lib = LoadLibrary().Where(v => !string.IsNullOrEmpty(v.Path))
                        .GroupBy(v => { var m = MetaFor(v.Path); return !string.IsNullOrEmpty(m?.Id) ? "j:" + m.Id : "t:" + Tytul(v).ToLowerInvariant() + "|" + (m?.Year ?? 0); })
                        .Select(g => g.Count() == 1 ? g.First() : g.OrderByDescending(v => v.Last).ThenBy(v => v.Peer != null).ThenByDescending(v => File.Exists(v.Path)).First()).ToList();
                }
                catch (Exception ex) { App.LogError(ex); }
                foreach (var kv in tabBtns) { bool on = kv.Key == t; kv.Value.Background = on ? KnFg : Brushes.Transparent; kv.Value.Foreground = on ? new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x17)) : KnDim; }
                var st = new StackPanel();
                if (t == "start")
                {
                    var cont = lib.Where(v => v.Pos > 0 && !v.Done && v.Last > 0).OrderByDescending(v => v.Last).Take(12).ToList();
                    if (cont.Count > 0)
                    {   // duży kafelek: ostatni film, ile zostało i o której się skończy
                        var v = cont[0]; var hero = new DockPanel { Margin = new Thickness(24, 18, 8, 30) };   // miejsce na powiększenie (1,07) - złota ramka nie ucięta
                        var g = new Grid(); var img = Poster(v.Path);
                        if (img != null) g.Children.Add(new Image { Source = img, Stretch = Stretch.UniformToFill });
                        g.Children.Add(Caption(Tytul(v)));
                        var path = v.Path; var big = Tile(420, 236, g, () => Graj(() => PlayVideos(new[] { path }, 0))); DockPanel.SetDock(big, Dock.Left);
                        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(14, 0, 0, 6) };
                        info.Children.Add(new TextBlock { Text = L.T("Oglądaj dalej"), FontSize = 28, FontWeight = FontWeights.Bold });
                        if (v.Len > v.Pos) { var left = TimeSpan.FromMilliseconds(v.Len - v.Pos); info.Children.Add(new TextBlock { Text = L.T("Zostało ") + (int)left.TotalMinutes + L.T(" min · skończy się o ") + DateTime.Now.Add(left).ToString("HH:mm"), FontSize = 17, Foreground = KnDim, Margin = new Thickness(0, 6, 0, 0) }); }
                        var m = MetaFor(v.Path); var meta = string.Join(" · ", new[] { m?.Year > 0 ? m.Year.ToString() : null, m?.Rating > 0 ? "★ " + m.Rating.ToString("0.0") : null }.Where(x => x != null));
                        if (meta.Length > 0) info.Children.Add(new TextBlock { Text = meta, FontSize = 15, Foreground = KnDim, Margin = new Thickness(0, 4, 0, 0) });
                        info.Children.Add(new Border { Background = KnGold, CornerRadius = new CornerRadius(18), Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0),
                            Child = new TextBlock { Text = L.T("▶ Wznów (Enter)"), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x04)) } });
                        hero.Children.Add(big); hero.Children.Add(info); st.Children.Add(hero);
                        if (cont.Count > 1) st.Children.Add(Row(L.T("Oglądaj dalej"), cont.Skip(1).Select(x => (UIElement)FilmTile(x, 150))));
                    }
                    var fav = FavChannels();
                    st.Children.Add(Row(L.T("Ulubione kanały"), fav.Select((c, i) => (UIElement)ChannelTile(fav, i))));
                    st.Children.Add(Row(L.T("Nowe w bibliotece"), lib.OrderByDescending(v => v.Added).Take(20).Select(v => (UIElement)FilmTile(v))));
                }
                else if (t == "tv")
                {
                    var bar = Pasek(); var box = new ContentControl { Focusable = false };
                    var allCh = KnownPlaylist()?.Channels ?? new List<IptvChannel>();
                    var groups = allCh.Select(c => c.Group ?? "").Where(g => g.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
                    { var sg = GetPref("kanapa-grupa"); kGrp = string.IsNullOrEmpty(sg) ? 0 : groups.FindIndex(g => string.Equals(g, sg, StringComparison.CurrentCultureIgnoreCase)) + 1; }
                    var tvSorts = new[] { L.T("jak na liście"), L.T("A–Z"), L.T("ulubione najpierw"), L.T("co leci (A–Z)") }; kSortTv = Math.Max(0, kSortTv) % tvSorts.Length;
                    void Fill()
                    {
                        var q = szukaj.Text.Trim(); var sp = new StackPanel(); string grp = kGrp > 0 && kGrp <= groups.Count ? groups[kGrp - 1] : null;
                        if (q.Length == 0 && grp == null) { var fav = FavChannels(); sp.Children.Add(Row(L.T("Ulubione kanały"), fav.Select((c, i) => (UIElement)ChannelTile(fav, i)))); }
                        var sel = allCh.Where(c => (grp == null || string.Equals(c.Group, grp, StringComparison.CurrentCultureIgnoreCase))
                            && (q.Length == 0 || (c.Name ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || (Teraz(c)?.Title ?? "").IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0));
                        sel = kSortTv switch
                        {
                            1 => sel.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
                            2 => sel.OrderBy(c => IptvSt.Fav.Contains(c.Url) ? 0 : 1),
                            3 => sel.OrderBy(c => Teraz(c)?.Title ?? "\uffff", StringComparer.CurrentCultureIgnoreCase),
                            _ => sel,
                        };
                        var all = sel.Take(300).ToList();
                        sp.Children.Add(new TextBlock { Text = L.T("Wszystkie kanały") + " (" + all.Count + ")", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
                        sp.Children.Add(Wrap(all.Select((c, i) => (UIElement)ChannelTile(all, i))));
                        box.Content = sp;
                    }
                    Przycisk(bar, () => L.T("Sortuj: ") + tvSorts[kSortTv], b => { kSortTv = (kSortTv + 1) % tvSorts.Length; SetPref("kanapa-sorttv", kSortTv.ToString()); b.Content = L.T("Sortuj: ") + tvSorts[kSortTv]; Fill(); });
                    Przycisk(bar, () => L.T("Grupa: ") + (kGrp == 0 || kGrp > groups.Count ? L.T("wszystkie") : groups[kGrp - 1]), _ =>
                        Lista(L.T("Wybierz grupę kanałów"), new[] { L.T("wszystkie") + "  (" + allCh.Count + ")" }.Concat(groups.Select(g => g + "  (" + allCh.Count(c => string.Equals(c.Group, g, StringComparison.CurrentCultureIgnoreCase)) + ")")).ToList(), kGrp, k => { kGrp = k; SetPref("kanapa-grupa", k == 0 ? "" : groups[k - 1]); }));
                    if (_iptvEpg == null) bar.Children.Add(new TextBlock { Text = L.T("Wczytuję program TV…"), FontSize = 16, Foreground = KnDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) });
                    odswiez = Fill; Fill(); st.Children.Add(bar); st.Children.Add(box);
                }
                else if (t == "film")
                {
                    var bar = Pasek(); var box = new ContentControl { Focusable = false };
                    var genres = lib.SelectMany(v => MetaFor(v.Path)?.Genres ?? new List<string>()).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
                    { var sg = GetPref("kanapa-gatunek"); kGen = string.IsNullOrEmpty(sg) ? 0 : genres.FindIndex(g => string.Equals(g, sg, StringComparison.CurrentCultureIgnoreCase)) + 1; }
                    var sorts = new[] { L.T("A–Z"), L.T("Najnowsze"), L.T("Ocena"), L.T("Rok"), L.T("Nieobejrzane") }; kSort = Math.Max(0, kSort) % sorts.Length;
                    var count = new TextBlock { FontSize = 16, Foreground = KnDim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                    void Fill()
                    {
                        var q = szukaj.Text.Trim(); string gen = kGen > 0 && kGen <= genres.Count ? genres[kGen - 1] : null;
                        var l = lib.Where(v => (q.Length == 0 || Tytul(v).IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 || (MetaFor(v.Path)?.People?.Any(p => p.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0) ?? false))
                            && (gen == null || (MetaFor(v.Path)?.Genres?.Contains(gen, StringComparer.CurrentCultureIgnoreCase) ?? false)) && (kSort != 4 || !v.Done));
                        l = kSort switch
                        {
                            1 => l.OrderByDescending(v => v.Added),
                            2 => l.OrderByDescending(v => MetaFor(v.Path)?.Rating ?? 0),
                            3 => l.OrderByDescending(v => MetaFor(v.Path)?.Year ?? 0),
                            _ => l.OrderBy(Tytul, StringComparer.CurrentCultureIgnoreCase),
                        };
                        var list = l.ToList(); count.Text = list.Count + L.T(" filmów");
                        box.Content = Wrap(list.Select(v => (UIElement)FilmTile(v)));
                    }
                    Przycisk(bar, () => L.T("Sortuj: ") + sorts[kSort], b => { kSort = (kSort + 1) % sorts.Length; SetPref("kanapa-sort", kSort.ToString()); b.Content = L.T("Sortuj: ") + sorts[kSort]; Fill(); });
                    Przycisk(bar, () => L.T("Gatunek: ") + (kGen == 0 || kGen > genres.Count ? L.T("wszystkie") : genres[kGen - 1]), _ =>
                        Lista(L.T("Wybierz gatunek"), new[] { L.T("wszystkie") + "  (" + lib.Count + ")" }.Concat(genres.Select(g => g + "  (" + lib.Count(v => MetaFor(v.Path)?.Genres?.Contains(g, StringComparer.CurrentCultureIgnoreCase) ?? false) + ")")).ToList(), kGen, k => { kGen = k; SetPref("kanapa-gatunek", k == 0 ? "" : genres[k - 1]); }));
                    bar.Children.Add(count);
                    odswiez = Fill; Fill(); st.Children.Add(bar); st.Children.Add(box);
                }
                body.Content = st; body.ScrollToTop();
                w.Dispatcher.BeginInvoke(new Action(() => { var first = FindTile(st); (first ?? (UIElement)tabBtns[t]).Focus(); }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            Button TabBtn(string key, string text, Action act)
            {
                var b = new Button { Content = text, FontSize = 19, Padding = new Thickness(20, 8, 20, 8), Margin = new Thickness(4, 0, 4, 0), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Foreground = KnDim, Background = Brushes.Transparent };
                b.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate TargetType='Button' xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                    "<Border x:Name='bd' CornerRadius='22' Background='{TemplateBinding Background}' BorderThickness='3' BorderBrush='Transparent' Padding='{TemplateBinding Padding}'><ContentPresenter/></Border>" +
                    "<ControlTemplate.Triggers><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='bd' Property='BorderBrush' Value='#F5B544'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
                b.Click += (s, e) => act();
                if (key != null) tabBtns[key] = b;
                tabs.Children.Add(b); return b;
            }
            TabBtn("start", L.T("Start"), () => Pokaz("start"));
            TabBtn("tv", "📺 " + L.T("Telewizja"), () => Pokaz("tv"));
            TabBtn("film", "🎬 " + L.T("Filmy"), () => Pokaz("film"));
            TabBtn(null, "🎲 " + L.T("Co dziś?"), () =>
            {   // to samo losowanie co w bibliotece
                List<LibraryVideo> pool; try { pool = LoadLibrary().Where(v => v.Peer == null && !string.IsNullOrEmpty(v.Path)).ToList(); } catch (Exception ex) { App.LogError(ex); pool = new List<LibraryVideo>(); }
                if (pool.Count == 0) { Opis(L.T("Biblioteka jest pusta"), "", L.T("Dodaj filmy w bibliotece (🎬 → Pobierz / uzupełnij z Jellyfin), a losowanie zadziała.")); return; }
                var before = Application.Current.Windows.OfType<Window>().ToList();
                ShowRandomPicker(w, pool, null, ps => Graj(() => PlayVideos(ps.ToArray(), 0)));
                foreach (var pw in Application.Current.Windows.OfType<Window>().Except(before).ToList())
                {   // okno losowania „zwykłe” (Independent) - nad Kanapą na pełnym ekranie byłoby niewidoczne
                    pw.Topmost = true; pw.Activate();
                    pw.Closed += (s2, e2) => { if (_kanapa == w) w.Activate(); };
                }
            });
            TabBtn(null, "✕ " + L.T("Zamknij"), Wyjdz);

            // strzałki: przejście do sąsiedniego kafelka (ScrollViewer nie przewija sam); Enter - jak klik; Esc - Start, potem wyjście
            w.PreviewKeyDown += (s, e) =>
            {
                bool inBox = Keyboard.FocusedElement is TextBox;
                if (inBox && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Back || e.Key == Key.Space || e.Key == Key.Home)) return;
                if (inBox && e.Key == Key.Escape && szukaj.Text.Length > 0) { szukaj.Clear(); e.Handled = true; return; }
                if (inBox && e.Key == Key.Enter) { var ft = FindTile((DependencyObject)body.Content ?? body); ft?.Focus(); e.Handled = true; return; }
                if (!inBox && odswiez != null && szukaj.IsVisible && ((e.Key >= Key.A && e.Key <= Key.Z) || (e.Key >= Key.D0 && e.Key <= Key.D9)) && Keyboard.Modifiers == ModifierKeys.None) { szukaj.Focus(); return; }   // pisanie od razu szuka
                var dir = e.Key switch { Key.Left => FocusNavigationDirection.Left, Key.Right => FocusNavigationDirection.Right, Key.Up => FocusNavigationDirection.Up, Key.Down => FocusNavigationDirection.Down, _ => (FocusNavigationDirection?)null };
                if (dir != null && Keyboard.FocusedElement is UIElement fe) { fe.MoveFocus(new TraversalRequest(dir.Value)); e.Handled = true; return; }
                if ((e.Key == Key.Enter || e.Key == Key.Space) && Keyboard.FocusedElement is Button fb) { fb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; return; }
                if (e.Key == Key.Escape || e.Key == Key.BrowserBack || e.Key == Key.Back) { e.Handled = true; if (wyborGrupy) { wyborGrupy = false; Pokaz(tab); } else if (tab != "start") Pokaz("start"); else Wyjdz(); return; }
                if (e.Key == Key.Home) { e.Handled = true; Wyjdz(); }
            };
            // 🎮 kontroler (Xbox i zgodne XInput): krzyżak / lewa gałka - strzałki, A - Enter, B - wstecz, Y - szukaj, LB / RB - poprzednia / następna zakładka
            var pad = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            ushort padPrev = 0; Key padKey = Key.None; DateTime padNext = DateTime.MinValue; int padErr = 0;
            void PadKey(Key k)
            {
                var src = PresentationSource.FromVisual(w); if (src == null) return;
                w.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, src, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }
            void Zakladka(int d)
            {
                var keys = new[] { "start", "tv", "film" }; int i = Array.IndexOf(keys, tab);
                Pokaz(keys[((i < 0 ? 0 : i) + d + keys.Length) % keys.Length]);
            }
            pad.Tick += (s, e) =>
            {
                if (!w.IsActive || padErr > 3) return;
                XInputState st; int r;
                try { r = KnPadGet(0, out st); } catch (Exception) { padErr = 99; return; }   // brak XInput w systemie - bez kontrolera
                if (r != 0) { padPrev = 0; padKey = Key.None; return; }   // nic nie podłączone
                ushort b = st.Buttons; short lx = st.LX, ly = st.LY;
                const int dead = 16000;
                Key dirKey = (b & 0x0001) != 0 || ly > dead ? Key.Up : (b & 0x0002) != 0 || ly < -dead ? Key.Down : (b & 0x0004) != 0 || lx < -dead ? Key.Left : (b & 0x0008) != 0 || lx > dead ? Key.Right : Key.None;
                if (dirKey == Key.None) padKey = Key.None;
                else if (dirKey != padKey) { padKey = dirKey; padNext = DateTime.UtcNow.AddMilliseconds(380); PadKey(dirKey); }
                else if (DateTime.UtcNow >= padNext) { padNext = DateTime.UtcNow.AddMilliseconds(110); PadKey(dirKey); }   // przytrzymane - przewija
                ushort down = (ushort)(b & ~padPrev); padPrev = b;
                if ((down & 0x1000) != 0) PadKey(Key.Enter);           // A
                if ((down & 0x2000) != 0) PadKey(Key.Escape);          // B
                if ((down & 0x8000) != 0 && odswiez != null) szukaj.Focus();   // Y - szukaj
                if ((down & 0x0010) != 0) PadKey(Key.Enter);           // Start
                if ((down & 0x0100) != 0) Zakladka(-1);                // LB
                if ((down & 0x0200) != 0) Zakladka(1);                 // RB
            };
            pad.Start();
            w.Closed += (s, e) => { tm.Stop(); pad.Stop(); if (_kanapa == w) _kanapa = null; };
            w.Show(); w.Activate();
            // 📺 film / kanał, który gra, w okienku pod zegarem (jak PiP) zamiast samego dźwięku; po wyjściu z Kanapy odtwarzacz wraca, jak był
            var pv = _vlcWin;
            if (pv != null && pv.IsVisible && pv.WindowState != WindowState.Minimized && PlayerBusy)
            {
                var was = (State: pv.WindowState, Style: pv.WindowStyle, Resize: pv.ResizeMode, Top: pv.Topmost, L: pv.Left, T: pv.Top, W: pv.Width, H: pv.Height);
                try
                {
                    var wa = SystemParameters.WorkArea;
                    pv.WindowState = WindowState.Normal; pv.WindowStyle = WindowStyle.None; pv.ResizeMode = ResizeMode.NoResize;
                    pv.Width = 448; pv.Height = 252; pv.Left = wa.Right - 448 - 40; pv.Top = wa.Top + 92;
                    pv.Topmost = false; pv.Topmost = true;   // nad Kanapą (też na wierzchu)
                    w.Activate();
                }
                catch (Exception ex) { App.LogError(ex); }
                w.Closed += (s2, e2) =>
                {
                    try
                    {
                        if (_vlcWin != pv) return;   // odtwarzacz zamknięty w międzyczasie
                        pv.Topmost = was.Top; pv.WindowStyle = was.Style; pv.ResizeMode = was.Resize;
                        pv.Left = was.L; pv.Top = was.T; pv.Width = was.W; pv.Height = was.H; pv.WindowState = was.State;
                        pv.Activate();
                    }
                    catch (Exception ex) { App.LogError(ex); }
                };
            }
            if (_iptvEpg == null) LoadIptvInBackground(() => Dispatcher.BeginInvoke(new Action(() =>
            {   // program TV z pamięci podręcznej (bez otwierania okna TV był pusty) - odświeżenie widoku
                if (_kanapa != w) return;
                if (tab == "tv") Pokaz("tv"); else if (tab == "start") Pokaz("start");
            })));
            Pokaz("start");
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct XInputState { public uint Packet; public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
        [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] static extern int KnPadGet14(int user, out XInputState st);
        [System.Runtime.InteropServices.DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")] static extern int KnPadGet91(int user, out XInputState st);
        static bool _knPadOld;
        static int KnPadGet(int user, out XInputState st)
        {   // Windows 8+ - xinput1_4, starsze - xinput9_1_0
            if (!_knPadOld) try { return KnPadGet14(user, out st); } catch (DllNotFoundException) { _knPadOld = true; } catch (EntryPointNotFoundException) { _knPadOld = true; }
            return KnPadGet91(user, out st);
        }

        static Button FindTile(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var ch = VisualTreeHelper.GetChild(d, i);
                if (ch is Button b && b.Focusable) return b;
                var r = FindTile(ch); if (r != null) return r;
            }
            return null;
        }
    }
}
