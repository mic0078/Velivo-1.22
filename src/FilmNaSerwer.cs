using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Velivo
{
    // ➕ film z innego źródła (dysk, Eksplorator, torrent, pobrany) - zamiast dodawać sam do biblioteki: pytanie w rogu odtwarzacza
    // (🔒 moja / 📚 wspólna / 🗄 na serwer / nie), makieta dokumentacja/makieta-dodaj-do-biblioteki.html. Ustawienie „film-obcy”: pytaj / sam / nie.
    // 🗄 na serwer: wybór serwera (ten komputer albo komputer w sieci domowej z Velivo Serwer filmów) i jego folderu; serwer sam POBIERA plik
    // z tego komputera (ten sam bezpieczny strumień co „Odtwórz na”, z wznawianiem po przerwie), po pobraniu przegląda foldery (opis, plakat, obsada).
    public partial class MainWindow
    {
        static string FilmObcyTryb => GetPref("film-obcy") ?? "pytaj";
        static List<string> FilmObcyNiePytaj => (GetPref("film-obcy-nie") ?? "").Split('|').Where(x => x.Length > 0).ToList();

        // wołane z PlayVideos: pliki spoza biblioteki (nie z serwera, TV, innego komputera, komunikatora)
        void FilmObcy(string[] files)
        {
            var lib = LoadLibrary();
            var nowe = files.Where(f => !string.IsNullOrEmpty(f) && File.Exists(f) && !IsAudioFile(f) && IsVlcVideoFile(f) && !_remoteFilms.ContainsKey(f) && !_iptvPlaying.ContainsKey(f) && !WFolderze(f) && FindEntry(lib, f, null) == null).ToList();
            if (nowe.Count == 0) return;
            var tryb = FilmObcyTryb;
            if (tryb == "sam") { if (AddToLibrary(lib, nowe) > 0) { SaveLibrary(lib); _libRefresh?.Invoke(); } return; }   // jak dawniej
            if (tryb == "nie") return;
            var dir = Path.GetDirectoryName(nowe[0]) ?? "";
            if (FilmObcyNiePytaj.Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase))) return;
            Dispatcher.BeginInvoke(new Action(() => FilmObcyPytanie(nowe)), System.Windows.Threading.DispatcherPriority.Background);
        }

        Window _obcyWin;
        void FilmObcyPytanie(List<string> nowe)
        {
            try { _obcyWin?.Close(); } catch (Exception) { }
            var owner = _vlcWin; if (owner == null || !owner.IsLoaded) return;
            var st = new StackPanel { Margin = new Thickness(12, 10, 12, 10), MaxWidth = 360 };
            var tytul = nowe.Count == 1 ? Path.GetFileNameWithoutExtension(nowe[0]) : nowe.Count + L.T(" filmów");
            st.Children.Add(new TextBlock { Text = L.T("➕ Dodać do biblioteki: ") + tytul + "?", Foreground = SFg, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            st.Children.Add(new TextBlock { Text = L.T("Film z: ") + Path.GetDirectoryName(nowe[0]), Foreground = SDim, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) });
            var w = new Window { WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Owner = owner, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x14, 0x18, 0x21)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x3F, 0x6B)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = st } };
            _obcyWin = w;
            void Dodaj(bool wspolna) { var l = LoadLibrary(); int n = AddToLibrary(l, nowe, wspolna); if (n > 0) { SaveLibrary(l); _libRefresh?.Invoke(); } ShowToast((wspolna ? L.T("📚 Dodano do wspólnej biblioteki: ") : L.T("🔒 Dodano do mojej biblioteki: ")) + n, null); w.Close(); }
            var r1 = new WrapPanel(); r1.Children.Add(SBtnMake(L.T("🔒 Moja (ten komputer)"), () => Dodaj(false))); r1.Children.Add(SBtnMake(L.T("📚 Wspólna"), () => Dodaj(true)));
            r1.Children.Add(SBtnMake(L.T("🗄 Na serwer…"), () => { w.Close(); FilmNaSerwerOkno(owner, nowe); }, new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x06, 0xB6, 0xD4), 0)));
            r1.Children.Add(SBtnMake(L.T("Nie"), () => w.Close()));
            st.Children.Add(r1);
            var nie = new CheckBox { Content = L.T("Nie pytaj o filmy z tego folderu"), Foreground = SDim, FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0) };
            nie.Click += (s, e) => { var l = FilmObcyNiePytaj; var d = Path.GetDirectoryName(nowe[0]) ?? ""; l.RemoveAll(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase)); if (nie.IsChecked == true) l.Add(d); SetPref("film-obcy-nie", string.Join("|", l)); };
            st.Children.Add(nie);
            st.Children.Add(new TextBlock { Text = L.T("Bez wyboru – po 20 s znika, nic nie dodane. Zmiana: ⚙ → Biblioteka → Nowy film."), Foreground = SDim, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            tm.Tick += (s, e) => { if (w.IsMouseOver) return; tm.Stop(); w.Close(); };
            w.Closed += (s, e) => { tm.Stop(); if (_obcyWin == w) _obcyWin = null; };
            w.Loaded += (s, e) =>
            {   // prawy dolny róg odtwarzacza (nad paskiem); zmaksymalizowany / pełny ekran - róg ekranu
                var a = SystemParameters.WorkArea;
                bool pelny = owner.WindowState == WindowState.Maximized;
                double r = pelny ? (owner.WindowStyle == WindowStyle.None ? SystemParameters.PrimaryScreenWidth : a.Right) : owner.Left + owner.ActualWidth, b = pelny ? (owner.WindowStyle == WindowStyle.None ? SystemParameters.PrimaryScreenHeight : a.Bottom) : owner.Top + owner.ActualHeight;
                w.Left = r - w.ActualWidth - 24; w.Top = b - w.ActualHeight - 120;
            };
            w.Show(); tm.Start();
        }

        // ---------- 🗄 wybór serwera i folderu ----------
        readonly ConcurrentDictionary<string, List<(LanPeerInfo Peer, List<LanFilmItem> Foldery)>> _folderOdp = new ConcurrentDictionary<string, List<(LanPeerInfo, List<LanFilmItem>)>>();

        static long WolneMiejsce(string folder) { try { return new DriveInfo(Path.GetPathRoot(folder)).AvailableFreeSpace; } catch (Exception) { return -1; } }
        static string Rozmiar(long b) => b < 0 ? "?" : b >= 1L << 40 ? (b / (double)(1L << 40)).ToString("0.0") + " TB" : b >= 1 << 30 ? (b / (double)(1 << 30)).ToString("0.0") + " GB" : (b / (double)(1 << 20)).ToString("0") + " MB";

        async void FilmNaSerwerOkno(Window owner, List<string> pliki)
        {
            var w = new Window { Title = L.T("🗄 Wyślij film na serwer"), Width = 560, SizeToContent = SizeToContent.Height, Owner = owner, Topmost = owner?.Topmost ?? false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = SBg, ResizeMode = ResizeMode.NoResize };
            var st = new StackPanel { Margin = new Thickness(14) }; w.Content = st;
            long suma = pliki.Sum(p => { try { return new FileInfo(p).Length; } catch (Exception) { return 0L; } });
            st.Children.Add(new TextBlock { Text = (pliki.Count == 1 ? Path.GetFileName(pliki[0]) : pliki.Count + L.T(" filmów")) + " · " + Rozmiar(suma), Foreground = SDim, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
            ListBox Lista(double h) => new ListBox { Height = h, Background = SCard, Foreground = SFg, BorderBrush = SLine, Margin = new Thickness(0, 4, 0, 10) };
            st.Children.Add(new TextBlock { Text = L.T("Serwer"), Foreground = SFg, FontWeight = FontWeights.SemiBold });
            var serwery = Lista(80); st.Children.Add(serwery);
            st.Children.Add(new TextBlock { Text = L.T("Folder na serwerze"), Foreground = SFg, FontWeight = FontWeights.SemiBold });
            var foldery = Lista(110); st.Children.Add(foldery);
            var kopiuj = new RadioButton { Content = L.T("Kopiuj (oryginał zostaje)"), Foreground = SFg, IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
            var przenies = new RadioButton { Content = L.T("Przenieś (oryginał do Kosza po sprawdzeniu kopii na serwerze)"), Foreground = SFg };
            var tryb = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) }; tryb.Children.Add(kopiuj); tryb.Children.Add(przenies); st.Children.Add(tryb);
            var info = new TextBlock { Foreground = SDim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Text = L.T("Szukam serwerów w sieci domowej…") }; st.Children.Add(info);
            var btns = new WrapPanel(); st.Children.Add(btns);
            var wyslij = SBtnMake(L.T("Wyślij"), () => { }, new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x06, 0xB6, 0xD4), 0)); wyslij.IsEnabled = false;
            btns.Children.Add(wyslij); btns.Children.Add(SBtnMake(L.T("Anuluj"), () => w.Close()));
            var lista = new List<(LanPeerInfo Peer, string Nazwa, List<(string Folder, long Wolne)> F)>();
            void Pokaz()
            {
                serwery.Items.Clear(); foreach (var s in lista) serwery.Items.Add("● " + s.Nazwa);
                if (serwery.Items.Count > 0 && serwery.SelectedIndex < 0) serwery.SelectedIndex = 0;
            }
            serwery.SelectionChanged += (s, e) =>
            {
                foldery.Items.Clear(); int i = serwery.SelectedIndex; if (i < 0 || i >= lista.Count) return;
                foreach (var f in lista[i].F) foldery.Items.Add(f.Folder + "   ·   " + L.T("wolne: ") + Rozmiar(f.Wolne));
                if (foldery.Items.Count > 0) foldery.SelectedIndex = 0;
            };
            foldery.SelectionChanged += (s, e) => wyslij.IsEnabled = foldery.SelectedIndex >= 0;
            wyslij.Click += (s, e) =>
            {
                int i = serwery.SelectedIndex, j = foldery.SelectedIndex; if (i < 0 || j < 0) return;
                var cel = lista[i]; var folder = cel.F[j].Folder;
                if (cel.F[j].Wolne >= 0 && cel.F[j].Wolne < suma + (200L << 20)) { info.Text = L.T("⚠ Za mało miejsca w tym folderze – wybierz inny."); return; }
                bool move = przenies.IsChecked == true; w.Close();
                foreach (var p in pliki) { if (cel.Peer == null) KopiujLokalnie(owner, p, folder, move); else WyslijNaSerwer(owner, cel.Peer, p, folder, move); }
            };
            w.Show();
            // ten komputer - gdy działa na nim Velivo Serwer filmów
            var lok = SerwerFolderyTeraz().Where(Directory.Exists).ToList();
            if (lok.Count > 0) lista.Add((null, L.T("Ten komputer (") + Environment.MachineName + ")", lok.Select(f => (f, WolneMiejsce(f))).ToList()));
            Pokaz();
            // komputery w sieci domowej: zapytanie o foldery serwera (odpowiadają tylko te z Velivo Serwer filmów)
            var req = Guid.NewGuid().ToString("N"); _folderOdp[req] = new List<(LanPeerInfo, List<LanFilmItem>)>();
            foreach (var p in SendTargets().Where(p => !(p.Device ?? "").StartsWith("📱", StringComparison.Ordinal) && NetIdx(p.Id) == null)) SendFilmMsg(p, new LanFilmMsg { kind = "folders", req = req });
            for (int k = 0; k < 8 && w.IsVisible; k++)
            {
                await Task.Delay(500);
                List<(LanPeerInfo Peer, List<LanFilmItem> Foldery)> odp; lock (_folderOdp[req]) odp = _folderOdp[req].ToList();
                foreach (var o in odp) if (!lista.Any(x => x.Peer != null && x.Peer.Id == o.Peer.Id)) { lista.Add((o.Peer, o.Peer.Device, o.Foldery.Select(f => (f.t, f.l)).ToList())); Pokaz(); }
            }
            _folderOdp.TryRemove(req, out _);
            if (w.IsVisible) info.Text = lista.Count == 0 ? L.T("Nie znaleziono serwera filmów – włącz Velivo Serwer filmów na komputerze w sieci domowej i dodaj w nim foldery.") : L.T("Wybierz serwer i folder. Po wysłaniu serwer sam pobierze opis, plakat i obsadę.");
        }

        // ---------- postęp wysyłania (małe okienko, można dalej oglądać) ----------
        sealed class Wysylka { public Window W; public TextBlock Txt; public ProgressBar Pas; public string Plik; public bool Move; public DateTime Start = DateTime.UtcNow; }
        readonly ConcurrentDictionary<string, Wysylka> _wysylki = new ConcurrentDictionary<string, Wysylka>();

        Wysylka OknoPostepu(Window owner, string tytul, string plik, bool move)
        {
            var st = new StackPanel { Margin = new Thickness(14) };
            var txt = new TextBlock { Foreground = SDim, TextWrapping = TextWrapping.Wrap, Text = L.T("Łączę z serwerem…") };
            var pas = new ProgressBar { Height = 8, Maximum = 1000, Margin = new Thickness(0, 8, 0, 8), Foreground = new LinearGradientBrush(Color.FromRgb(0x7C, 0x3A, 0xED), Color.FromRgb(0x06, 0xB6, 0xD4), 0), Background = SCard, BorderThickness = new Thickness(0) };
            st.Children.Add(new TextBlock { Text = tytul, Foreground = SFg, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap }); st.Children.Add(pas); st.Children.Add(txt);
            var w = new Window { Title = L.T("🗄 Wysyłanie na serwer"), Width = 440, SizeToContent = SizeToContent.Height, Background = SBg, Content = st, Owner = owner != null && owner.IsLoaded ? owner : null, Topmost = owner?.Topmost ?? false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            w.Show();
            return new Wysylka { W = w, Txt = txt, Pas = pas, Plik = plik, Move = move };
        }

        void Postep(Wysylka u, long pos, long len)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (len <= 0) return; u.Pas.Value = 1000.0 * pos / len;
                double s = (DateTime.UtcNow - u.Start).TotalSeconds, v = s > 1 ? pos / s : 0;
                u.Txt.Text = Rozmiar(pos) + L.T(" z ") + Rozmiar(len) + (v > 0 ? " · " + (v / (1 << 20)).ToString("0") + " MB/s · " + L.T("zostało ok. ") + TimeSpan.FromSeconds(Math.Max(0, (len - pos) / v)).ToString(@"m\:ss") : "") + L.T(" · można dalej oglądać");
            }));
        }

        void Koniec(Wysylka u, bool ok, string tekst, string hashSerwera)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                u.Pas.Value = ok ? 1000 : u.Pas.Value; u.Txt.Text = tekst; u.Txt.Foreground = ok ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)) : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                if (ok && u.Move)
                {   // przeniesienie: oryginał do Kosza dopiero, gdy suma pliku na serwerze = suma oryginału
                    u.Txt.Text = tekst + "\n" + L.T("Sprawdzam kopię na serwerze…");
                    var moj = await Task.Run(() => SumaPliku(u.Plik));
                    if (moj != null && string.Equals(moj, hashSerwera, StringComparison.OrdinalIgnoreCase))
                    {
                        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(u.Plik, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); u.Txt.Text = tekst + "\n" + L.T("🗑 Oryginał przeniesiony do Kosza."); }
                        catch (Exception ex) { u.Txt.Text = tekst + "\n" + L.T("Oryginał został (nie dało się go usunąć: ") + ex.Message + ")"; }
                    }
                    else u.Txt.Text = tekst + "\n" + L.T("⚠ Kopia na serwerze różni się od oryginału – oryginał zostaje.");
                }
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(ok ? 15 : 60) }; t.Tick += (s, e) => { t.Stop(); try { u.W.Close(); } catch (Exception) { } }; t.Start();
            }));
        }

        static string SumaPliku(string p)
        {
            try { using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan)) using (var sha = SHA256.Create()) return Convert.ToHexString(sha.ComputeHash(fs)); }
            catch (Exception) { return null; }
        }

        // ten komputer: kopia do folderu serwera z postępem, potem przegląd folderów przez Serwer filmów
        void KopiujLokalnie(Window owner, string plik, string folder, bool move)
        {
            var u = OknoPostepu(owner, Path.GetFileName(plik) + " → " + folder, plik, move);
            Task.Run(() =>
            {
                try
                {
                    var cel = WolnaNazwa(folder, Path.GetFileName(plik), new FileInfo(plik).Length); if (cel == null) { Koniec(u, true, L.T("✔ Ten film już jest w tym folderze."), u.Move ? SumaPliku(Path.Combine(folder, Path.GetFileName(plik))) : null); return; }
                    var part = cel + ".velivo-part"; long len = new FileInfo(plik).Length, pos = 0; var buf = new byte[1 << 20]; var last = DateTime.MinValue;
                    using (var a = new FileStream(plik, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20)) using (var b = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                    { int n; while ((n = a.Read(buf, 0, buf.Length)) > 0) { b.Write(buf, 0, n); pos += n; if ((DateTime.UtcNow - last).TotalMilliseconds > 500) { last = DateTime.UtcNow; Postep(u, pos, len); } } }
                    File.Move(part, cel);
                    SerwerPrzegladnij();
                    Koniec(u, true, L.T("✔ Gotowe – Serwer filmów pobiera opis, plakat i obsadę."), u.Move ? SumaPliku(cel) : null);
                }
                catch (Exception ex) { Koniec(u, false, L.T("✖ Nie udało się skopiować: ") + ex.Message, null); }
            });
        }

        // nazwa w folderze serwera: ten sam plik (ta sama wielkość) = już jest (null); inny o tej samej nazwie - „(2)”
        static string WolnaNazwa(string folder, string nazwa, long len)
        {
            nazwa = string.Concat(Path.GetFileName(nazwa ?? "film").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var cel = Path.Combine(folder, nazwa);
            for (int i = 2; File.Exists(cel); i++)
            {
                if (new FileInfo(cel).Length == len) return null;
                cel = Path.Combine(folder, Path.GetFileNameWithoutExtension(nazwa) + " (" + i + ")" + Path.GetExtension(nazwa));
            }
            return cel;
        }

        // komputer w sieci domowej: serwer pobiera plik z tego komputera (udostępniony tylko jemu, 12 h, z wznawianiem)
        void WyslijNaSerwer(Window owner, LanPeerInfo peer, string plik, string folder, bool move)
        {
            var url = ShareFilm(plik, peer);
            if (url == null) { ShowToast(L.T("🗄 Nie udało się udostępnić filmu serwerowi (sieć / port)"), null); return; }
            var id = Guid.NewGuid().ToString("N");
            var u = OknoPostepu(owner, Path.GetFileName(plik) + " → " + peer.Device + " · " + folder, plik, move); _wysylki[id] = u;
            u.W.Closed += (s, e) => _wysylki.TryRemove(id, out _);
            SendFilmMsg(peer, new LanFilmMsg { kind = "upload", req = id, url = url, act = folder, title = Path.GetFileName(plik), len = new FileInfo(plik).Length });
            Task.Delay(20000).ContinueWith(_ => { if (_wysylki.TryGetValue(id, out var w0) && w0.Pas.Dispatcher.Invoke(() => w0.Pas.Value) == 0) Koniec(w0, false, L.T("✖ Serwer nie odpowiada – sprawdź, czy ma najnowsze Velivo i działa Serwer filmów."), null); });
        }

        // ---------- odbiór (LanVideo): foldery serwera, pobieranie pliku, postęp i wynik ----------
        void FilmSerwerMsg(LanFilmMsg m, LanPeerInfo peer)
        {
            switch (m.kind)
            {
                case "folders":
                    if (m.items != null) { if (m.req != null && _folderOdp.TryGetValue(m.req, out var lst)) lock (lst) lst.Add((peer, m.items)); return; }   // odpowiedź serwera
                    var fol = SerwerFolderyTeraz().Where(Directory.Exists).ToList();
                    if (fol.Count > 0) SendFilmMsg(peer, new LanFilmMsg { kind = "folders", req = m.req, items = fol.Select(f => new LanFilmItem { t = f, l = WolneMiejsce(f) }).ToList() });
                    return;
                case "upload":
                    _ = Task.Run(() => PobierzOdKlienta(peer, m));
                    return;
                case "upst":
                    if (m.req != null && _wysylki.TryGetValue(m.req, out var u)) Postep(u, m.pos, m.len);
                    return;
                case "upok":
                    if (m.req != null && _wysylki.TryGetValue(m.req, out var u2))
                        Koniec(u2, m.done, m.done ? L.T("✔ Gotowe – ") + peer.Device + L.T(" pobiera opis, plakat i obsadę.") : L.T("✖ Nie udało się: ") + m.title, m.done ? m.title : null);
                    return;
            }
        }

        static readonly System.Net.Http.HttpClient _pobHttp = new System.Net.Http.HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        async Task PobierzOdKlienta(LanPeerInfo peer, LanFilmMsg m)
        {
            void Odp(bool ok, string tekst) => SendFilmMsg(peer, new LanFilmMsg { kind = "upok", req = m.req, done = ok, title = tekst });
            try
            {
                var folder = SerwerFolderyTeraz().FirstOrDefault(f => string.Equals(f.TrimEnd('\\'), (m.act ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                if (folder == null || !Directory.Exists(folder)) { Odp(false, L.T("to nie jest folder tego serwera")); return; }
                if (m.url == null || !m.url.StartsWith("http://")) { Odp(false, L.T("zły adres")); return; }
                var cel = WolnaNazwa(folder, m.title, m.len);
                if (cel == null) { var ist = Path.Combine(folder, Path.GetFileName(m.title)); Odp(true, SumaPliku(ist) ?? ""); return; }   // już jest
                var part = cel + ".velivo-part";
                App.LogInfo("Serwer filmów: pobieram od " + peer.Device + " – " + Path.GetFileName(cel) + " (" + Rozmiar(m.len) + ")");
                var last = DateTime.MinValue; var buf = new byte[1 << 20];
                for (int proba = 0; proba < 40; proba++)   // przerwa w sieci - wznowienie od miejsca przerwania (do ok. 10 min)
                {
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (m.len > 0 && have >= m.len) break;
                    try
                    {
                        using (var cts = new CancellationTokenSource(60000))
                        using (var rq = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, m.url))
                        {
                            if (have > 0) rq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                            using (var rs = await _pobHttp.SendAsync(rq, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token))
                            {
                                if (!rs.IsSuccessStatusCode) throw new IOException("HTTP " + (int)rs.StatusCode);
                                if (have > 0 && rs.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0;   // bez wznawiania - od nowa
                                using (var src = await rs.Content.ReadAsStreamAsync(cts.Token))
                                using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                                {
                                    int n; long pos = have;
                                    while ((n = await src.ReadAsync(buf, 0, buf.Length, cts.Token)) > 0)
                                    {
                                        await dst.WriteAsync(buf, 0, n); pos += n; cts.CancelAfter(60000);   // 60 s bez danych = przerwa
                                        if ((DateTime.UtcNow - last).TotalSeconds >= 1) { last = DateTime.UtcNow; SendFilmMsg(peer, new LanFilmMsg { kind = "upst", req = m.req, pos = pos, len = m.len }); }
                                    }
                                }
                            }
                        }
                        if (m.len <= 0 || new FileInfo(part).Length >= m.len) break;
                    }
                    catch (Exception ex) { App.LogInfo("Serwer filmów: przerwa przy pobieraniu " + Path.GetFileName(cel) + " – " + ex.Message + " (ponawiam)"); await Task.Delay(15000); }
                }
                if (!File.Exists(part) || m.len > 0 && new FileInfo(part).Length != m.len) { Odp(false, L.T("przerwane – plik niepełny (spróbuj ponownie)")); return; }
                File.Move(part, cel);
                var hash = SumaPliku(cel);
                App.LogInfo("Serwer filmów: pobrano od " + peer.Device + " – " + cel);
                Odp(true, hash ?? "");
                SerwerPrzegladnij();
            }
            catch (Exception ex) { App.LogError(ex); Odp(false, ex.Message); }
        }

        // przegląd folderów serwera (nowy plik → biblioteka, opis, plakat): ten program jest Serwerem - od razu; inaczej sygnał do Velivo Serwer filmów
        void SerwerPrzegladnij()
        {
            if (Wariant.Serwer) { Dispatcher.BeginInvoke(new Action(() => SerwerSkanuj(false))); return; }
            try { if (EventWaitHandle.TryOpenExisting("Velivo.SerwerSkanuj", out var ev)) using (ev) ev.Set(); } catch (Exception) { }
        }
        // Velivo Serwer filmów czeka na sygnał „przejrzyj foldery” od innych programów Velivo na tym komputerze
        void SerwerSygnalStart()
        {
            if (!Wariant.Serwer) return;
            try
            {
                var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "Velivo.SerwerSkanuj");
                new Thread(() => { while (true) { ev.WaitOne(); Dispatcher.BeginInvoke(new Action(() => SerwerSkanuj(false))); } }) { IsBackground = true }.Start();
            }
            catch (Exception ex) { App.LogError(ex); }
        }
    }
}
