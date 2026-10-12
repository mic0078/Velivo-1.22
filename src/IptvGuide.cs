using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Przegladarka
{
    // Pelny program TV (EPG): kanaly z lewej, caly program wybranego kanalu z opisami z prawej.
    // Prawy przycisk na audycji: oglądaj, przelacz o czasie, nagraj; na gorze odswiezanie (co ile i teraz).
    public partial class MainWindow
    {
        IptvPlaylist _iptvPl;              // ostatnio wczytana lista kanalow
        IptvSource _iptvSrc;               // jej zrodlo (ustawienie odswiezania)
        Func<bool, Task> _iptvReload;      // wczytaj ponownie (okno listy kanalow otwarte)
        Action _iptvLoaded;                // lista / program TV wczytane
        Window _guideWin;

        void ShowEpgGuide(Window owner, string channelUrl)
        {
            if (_guideWin != null && _guideWin.IsLoaded) { _guideWin.Activate(); return; }
            if (_iptvPl == null) ShowIptv(owner);   // lista jeszcze nie wczytana - okno kanalow ja wczyta, przewodnik odswiezy sie sam
            var w = new Window { Title = L.T("📅 Program TV"), Width = 900, Height = 620, Owner = owner, Topmost = owner.Topmost, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var search = new TextBox { Margin = new Thickness(8, 8, 0, 4), Padding = new Thickness(4), Width = 200 };
            var hours = new ComboBox { Margin = new Thickness(8, 8, 0, 4), Width = 190, ToolTip = L.T("Jak często odświeżać listę i program TV") };
            var hoursOpts = new[] { 3, 6, 12, 24, 48, 168, 0 };
            foreach (var t in new[] { "co 3 godziny", "co 6 godzin", "co 12 godzin", "codziennie", "co 2 dni", "co tydzień", "tylko ręcznie" }) hours.Items.Add(L.T("Odświeżanie: ") + L.T(t));
            var sort = new ComboBox { Margin = new Thickness(8, 8, 0, 4), Width = 170 };
            var sortOpts = new[] { "nr", "az", "za" };
            foreach (var t in new[] { "Sortuj: jak na liście", "Sortuj: nazwa A–Z", "Sortuj: nazwa Z–A" }) sort.Items.Add(L.T(t));
            sort.SelectedIndex = Math.Max(0, Array.IndexOf(sortOpts, GetPref("sort-program") ?? "nr"));
            var refresh = SmallButton(L.T("🔄 Odśwież teraz"), null); refresh.Margin = new Thickness(8, 8, 0, 4);
            var status = new TextBlock { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 8, 0, 4) };
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(new TextBlock { Text = "🔍", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 8, 0, 4) });
            foreach (var c in new FrameworkElement[] { search, sort, hours, refresh, status }) top.Children.Add(c);

            var chans = new ListBox { Width = 230, Margin = new Thickness(8, 0, 0, 8) };
            var progs = new ListBox { Margin = new Thickness(8, 0, 8, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            ScrollViewer.SetHorizontalScrollBarVisibility(progs, ScrollBarVisibility.Disabled);   // opisy zawijane do szerokosci

            Action fillChans = () =>
            {
                var sel = (chans.SelectedItem as ListBoxItem)?.Tag as IptvChannel;
                var keep = sel?.Url ?? channelUrl;
                chans.Items.Clear();
                var pl = _iptvPl; var epg = _iptvEpg;
                if (pl == null) { status.Text = L.T("Wczytuję kanały…"); return; }
                var q = search.Text.Trim();
                var found = pl.Channels.Where(c => EpgFor(epg, c) != null && (q.Length == 0 || c.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
                var so = sortOpts[Math.Max(0, sort.SelectedIndex)];
                if (so == "az") found = found.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
                else if (so == "za") found = found.OrderByDescending(c => c.Name, StringComparer.CurrentCultureIgnoreCase);
                foreach (var ch in found)
                    chans.Items.Add(new ListBoxItem { Content = "📺 " + ch.Name, Tag = ch });
                chans.SelectedItem = chans.Items.Cast<ListBoxItem>().FirstOrDefault(i => ((IptvChannel)i.Tag).Url == keep) ?? (chans.Items.Count > 0 ? chans.Items[0] : null);
                chans.ScrollIntoView(chans.SelectedItem);
                var src = _iptvSrc;
                if (src != null) hours.SelectedIndex = Math.Max(0, Array.IndexOf(hoursOpts, src.Hours));
                status.Text = epg == null ? L.T("Brak programu TV – dodaj adres EPG w liście kanałów („✏ Edytuj”).") : chans.Items.Count + L.T(" kanałów z programem");
            };
            Action fillProgs = () =>
            {
                progs.Items.Clear();
                var ch = (chans.SelectedItem as ListBoxItem)?.Tag as IptvChannel; if (ch == null) return;
                var l = EpgFor(_iptvEpg, ch); if (l == null) return;
                var now = DateTime.Now; ListBoxItem nowItem = null; DateTime day = DateTime.MinValue;
                var jobs = IptvSt.Jobs;
                foreach (var x in l)
                {
                    if (x.Start.Date != day) { day = x.Start.Date; progs.Items.Add(new ListBoxItem { Content = new TextBlock { Text = day.ToString("dddd, d MMMM"), FontWeight = FontWeights.Bold, Foreground = Brushes.SteelBlue, Margin = new Thickness(0, 6, 0, 2) }, IsEnabled = false }); }
                    bool live = x.Start <= now && x.Stop > now, past = x.Stop <= now;
                    var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4), Opacity = past ? 0.55 : 1 };
                    var mark = jobs.Any(j => j.Url == ch.Url && j.Kind == "rec" && j.Start <= UnixMs(x.Start) && j.Stop >= UnixMs(x.Stop)) ? "⏺ " : jobs.Any(j => j.Url == ch.Url && j.Kind == "switch" && j.Start == UnixMs(x.Start)) ? "⏰ " : "";
                    tb.Inlines.Add(new Run((live ? "▶ " : "") + mark + x.Start.ToString("HH:mm") + " (" + ((int)(x.Stop - x.Start).TotalMinutes / 60).ToString("00") + ":" + ((int)(x.Stop - x.Start).TotalMinutes % 60).ToString("00") + ") ") { FontWeight = FontWeights.Bold, Foreground = Brushes.SeaGreen });
                    tb.Inlines.Add(new Run(x.Title ?? "") { FontWeight = FontWeights.Bold });
                    if (!string.IsNullOrEmpty(x.Sub)) tb.Inlines.Add(new Run(" – " + x.Sub) { FontStyle = FontStyles.Italic });
                    if (!string.IsNullOrEmpty(x.Desc)) tb.Inlines.Add(new Run("\n" + x.Desc));
                    var meta = string.Join(" · ", x.Cats.Concat(string.IsNullOrEmpty(x.Year) ? new string[0] : new[] { x.Year.Length > 4 ? x.Year.Substring(0, 4) : x.Year }));
                    if (meta.Length > 0) tb.Inlines.Add(new Run("\n" + meta) { FontStyle = FontStyles.Italic, Foreground = Brushes.Gray });
                    var it = new ListBoxItem { Content = tb, Tag = x };
                    if (live) { it.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x3B, 0x82, 0xF6)); nowItem = it; }
                    progs.Items.Add(it);
                }
                if (nowItem != null) { progs.SelectedItem = nowItem; progs.ScrollIntoView(nowItem); }
            };
            chans.SelectionChanged += (s, a) => fillProgs();
            search.TextChanged += (s, a) => fillChans();
            sort.SelectionChanged += (s, a) => { SetPref("sort-program", sortOpts[Math.Max(0, sort.SelectedIndex)]); fillChans(); };
            hours.SelectionChanged += (s, a) =>
            {
                var src = _iptvSrc; if (src == null || hours.SelectedIndex < 0) return;
                var hv = hoursOpts[hours.SelectedIndex]; if (src.Hours == hv) return;
                src.Hours = hv; SaveIptv(IptvSt);
            };
            refresh.Click += (s, a) => { if (_iptvReload != null) _ = _iptvReload(true); else { ShowIptv(owner); _iptvReload?.Invoke(true); } };

            Func<IptvChannel> curCh = () => (chans.SelectedItem as ListBoxItem)?.Tag as IptvChannel;
            Action watch = () =>
            {
                var ch = curCh(); if (ch == null) return;
                _iptvPlaying[ch.Url] = ch;
                PlayVideos(new[] { ch.Url }, 0);
            };
            var menu = new ContextMenu();
            menu.Opened += (s, a) =>
            {
                menu.Items.Clear();
                var ch = curCh(); var x = (progs.SelectedItem as ListBoxItem)?.Tag as EpgItem;
                Action<string, Action, bool> add = (t, act, on) => { var mi = new MenuItem { Header = t, IsEnabled = on }; mi.Click += (q, e) => { act(); fillProgs(); }; menu.Items.Add(mi); };
                bool live = x != null && x.Start <= DateTime.Now && x.Stop > DateTime.Now, future = x != null && x.Start > DateTime.Now;
                add(L.T("▶ Oglądaj ten kanał teraz"), watch, ch != null);
                add(L.T("⏰ Przełącz na ten program o czasie"), () => SwitchAt(ch, x), ch != null && future);
                add(live ? L.T("⏺ Nagraj ten program (od teraz do końca)") : L.T("⏺ Nagraj ten program"), () => RecordProgramme(ch, live ? new EpgItem { Title = x.Title, Start = DateTime.Now.AddMinutes(2), Stop = x.Stop } : x), ch != null && (live || future));
                add(L.T("⏺ Zaplanuj nagrywanie…"), () => ScheduleDialog(ch, w), ch != null);
                menu.Items.Add(new Separator());
                add(L.T("📅 Zaplanowane…"), () => ShowJobs(w), true);
                add(L.T("🎞 Nagrania…"), () => ShowRecordings(w), true);
            };
            progs.ContextMenu = menu;
            progs.MouseDoubleClick += (s, a) =>
            {
                var ch = curCh(); var x = (progs.SelectedItem as ListBoxItem)?.Tag as EpgItem; if (ch == null || x == null) return;
                if (x.Start <= DateTime.Now) watch(); else { SwitchAt(ch, x); fillProgs(); }
            };
            chans.MouseDoubleClick += (s, a) => watch();

            var body = new DockPanel();
            DockPanel.SetDock(top, Dock.Top); body.Children.Add(top);
            DockPanel.SetDock(chans, Dock.Left); body.Children.Add(chans);
            body.Children.Add(progs);
            w.Content = body;
            Action onLoaded = () => fillChans(), onJobs = () => fillProgs();
            _iptvLoaded += onLoaded; _iptvJobsChanged += onJobs;
            w.Closed += (s, a) => { _iptvLoaded -= onLoaded; _iptvJobsChanged -= onJobs; _guideWin = null; };
            _guideWin = w;
            fillChans();
            w.Show();
        }
    }
}
