using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace Przegladarka
{
    // Domyslna przegladarka + jedno okno programu.
    // Windows nie pozwala programowi samemu ustawic sie jako domyslny - program sie rejestruje,
    // a wybor robi uzytkownik w Ustawieniach Windows (otwieramy mu wlasciwa strone).
    // Link otwarty z innego programu trafia jako nowa karta do juz dzialajacego okna (nazwany potok).
    public partial class MainWindow
    {
        internal const string AppName = "Velivo";
        const string ProgId = "VelivoHTML";
        const string PdfProgId = "VelivoPDF";
        const string VideoProgId = "VelivoVideo";
        const string ClientKey = @"Software\Clients\StartMenuInternet\" + AppName;

        // ---------- jedno okno ----------

        static Mutex _instanceMutex;

        // Nazwa zalezy od folderu danych, zeby osobny profil (PRZEGLADARKA_DANE) dzialal niezaleznie.
        static string InstanceKey
        {
            get
            {
                using (var sha = SHA256.Create())
                {
                    var h = sha.ComputeHash(Encoding.UTF8.GetBytes(DataDir.ToLowerInvariant()));
                    return AppName + "-" + Environment.UserName + "-" + BitConverter.ToString(h, 0, 6).Replace("-", "");
                }
            }
        }

        internal static bool TryBecomePrimary()
        {
            bool created;
            _instanceMutex = new Mutex(true, @"Local\" + InstanceKey, out created);
            return created;
        }

        internal static bool SendToPrimary(string[] urls)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", InstanceKey, PipeDirection.Out))
                {
                    pipe.Connect(3000);
                    using (var w = new StreamWriter(pipe, Encoding.UTF8))
                    {
                        w.WriteLine(urls.Length);
                        foreach (var u in urls) w.WriteLine(u.Replace("\r", "").Replace("\n", ""));
                    }
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        void StartInstanceServer()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    try
                    {
                        using (var pipe = new NamedPipeServerStream(InstanceKey, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
                        {
                            await pipe.WaitForConnectionAsync();
                            using (var r = new StreamReader(pipe, Encoding.UTF8))
                            {
                                int n; int.TryParse(await r.ReadLineAsync(), out n);
                                var urls = new string[Math.Max(0, Math.Min(n, 50))];
                                for (int i = 0; i < urls.Length; i++) urls[i] = await r.ReadLineAsync() ?? "";
                                await Dispatcher.InvokeAsync(() => OpenFromOutside(urls.Where(u => u.Length > 0).ToArray()));
                            }
                        }
                    }
                    catch (Exception) { await Task.Delay(500); }
                }
            });
        }

        bool _mainClosed;   // okno zamkniete, a "film na wierzchu" jeszcze gra - proces zyje

        void OpenFromOutside(string[] urls)
        {
            // Velivo uruchomione ponownie, gdy gra juz tylko okienko z filmem: zamykamy je i startujemy pelne Velivo od nowa
            if (_mainClosed)
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = false };
                    psi.ArgumentList.Add("--czekaj-na");
                    psi.ArgumentList.Add(Environment.ProcessId.ToString());
                    foreach (var u in urls) psi.ArgumentList.Add(u);
                    if (Environment.GetEnvironmentVariable("PRZEGLADARKA_DANE") != null) psi.Environment["PRZEGLADARKA_DANE"] = DataDir;
                    System.Diagnostics.Process.Start(psi);
                }
                catch (Exception ex) { App.LogError(ex); }
                Application.Current.Shutdown();
                return;
            }
            if (_inTray) { ShowFromTray(); if (urls.Length == 0) return; }
            if (urls.Length == 0) AddTab(NewTabUrl);
            foreach (var u in urls) OpenArg(u);
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false; // wyciagnij okno na wierzch
        }

        // Argument z Windows: adres albo sciezka do pliku .html.
        internal static string ArgToUrl(string a)
        {
            try { if (File.Exists(a)) return new Uri(Path.GetFullPath(a)).AbsoluteUri; } catch (Exception) { }
            return a;
        }

        // ---------- domyslna przegladarka ----------

        static bool IsRegistered()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications"))
                return k != null && k.GetValue(AppName) != null;
        }

        // Rejestracja w Windows jako przegladarka (to samo robi instalator; tu na wypadek wersji bez instalacji).
        static void RegisterBrowser()
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string open = "\"" + exe + "\" \"%1\"";
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                k.SetValue("", AppName + " HTML Document");
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", exe + ",0");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
            }
            RegisterFileTypes(exe);
            using (var k = Registry.CurrentUser.CreateSubKey(ClientKey))
            {
                k.SetValue("", AppName);
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", exe + ",0");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", "\"" + exe + "\"");
                using (var cap = k.CreateSubKey("Capabilities"))
                {
                    cap.SetValue("ApplicationName", AppName);
                    cap.SetValue("ApplicationDescription", AppName + " – lekka i prywatna przeglądarka");
                    cap.SetValue("ApplicationIcon", exe + ",0");
                    using (var u = cap.CreateSubKey("URLAssociations")) { u.SetValue("http", ProgId); u.SetValue("https", ProgId); }
                    using (var f = cap.CreateSubKey("FileAssociations"))
                    {
                        foreach (var ext in new[] { ".htm", ".html", ".shtml", ".xhtml", ".svg" }) f.SetValue(ext, ProgId);
                        f.SetValue(".pdf", PdfProgId);   // PDF otwiera wbudowany czytnik silnika
                        foreach (var ext in VideoExts) f.SetValue(ext, VideoProgId);   // filmy - odtwarzacz Velivo
                    }
                    using (var s = cap.CreateSubKey("StartMenu")) s.SetValue("StartMenuInternet", AppName);
                }
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                k.SetValue(AppName, ClientKey + @"\Capabilities");
        }

        // PDF i filmy: Velivo na liscie "Otworz za pomoca" (Applications\Velivo.exe + OpenWithProgids). Wywolywane przy kazdym
        // starcie - dziala tez bez instalatora i po przeniesieniu programu; Windows odswieza liste tylko, gdy cos sie zmienilo.
        // Zwraca true, gdy wpisy byly nieaktualne.
        static bool RegisterFileTypes(string exe)
        {
            string open = "\"" + exe + "\" \"%1\"";
            string appKey = @"Software\Classes\Applications\" + Path.GetFileName(exe);
            bool changed;
            using (var c = Registry.CurrentUser.OpenSubKey(appKey + @"\shell\open\command"))
                changed = c == null || (c.GetValue("") as string) != open;
            foreach (var pid in new[] { new[] { PdfProgId, AppName + " PDF Document" }, new[] { VideoProgId, AppName + " Video" } })
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + pid[0]))
                {
                    k.SetValue("", pid[1]);
                    using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", exe + ",0");
                    using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
                }
            var types = new[] { ".pdf" }.Concat(VideoExts).ToArray();
            using (var k = Registry.CurrentUser.CreateSubKey(appKey))
            {
                k.SetValue("FriendlyAppName", AppName);
                using (var i = k.CreateSubKey("DefaultIcon")) i.SetValue("", exe + ",0");
                using (var c = k.CreateSubKey(@"shell\open\command")) c.SetValue("", open);
                using (var t = k.CreateSubKey("SupportedTypes")) foreach (var ext in types) t.SetValue(ext, "");
            }
            foreach (var ext in types)
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids"))
                    k.SetValue(ext == ".pdf" ? PdfProgId : VideoProgId, "");
            return changed;
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

        static void EnsureFileTypes()
        {
            Task.Run(() =>
            {
                try { if (RegisterFileTypes(Process.GetCurrentProcess().MainModule.FileName)) SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); }   // SHCNE_ASSOCCHANGED
                catch (Exception ex) { App.LogError(ex); }
            });
        }

        // Ustawienia Windows na stronie Velivo w "Aplikacjach domyslnych" (filmy, PDF, strony)
        void OpenVelivoDefaultApps()
        {
            try { if (!IsRegistered()) RegisterBrowser(); } catch (Exception ex) { App.LogError(ex); }
            try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + AppName) { UseShellExecute = true }); }
            catch (Exception) { try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); } catch (Exception) { } }
        }

        // Wybor uzytkownika dla https (nowsze Windows 11 trzymaja go w UserChoiceLatest).
        static string CurrentBrowserProgId()
        {
            foreach (var leaf in new[] { "UserChoiceLatest", "UserChoice" })
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\" + leaf))
                {
                    var v = k == null ? null : k.GetValue("ProgId") as string;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            return null;
        }

        static bool IsDefaultBrowser() { return CurrentBrowserProgId() == ProgId; }

        // Pierwsze uruchomienie (takze po instalacji): jedno pytanie o domyslna przegladarke; potem tylko w Ustawieniach.
        void AskDefaultBrowserOnce()
        {
            if (_settings.AskedDefaultBrowser) return;
            _settings.AskedDefaultBrowser = true;
            try { _settings.Save(DataDir); } catch (Exception) { }
            if (IsDefaultBrowser()) return;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                if (MessageBox.Show(this, L.T("Ustawić Velivo jako domyślną przeglądarkę?\n\nLinki z innych programów i pliki PDF będą otwierać się w Velivo. Zmienisz to później w Ustawieniach."),
                        AppName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    MakeDefaultBrowser(this);
            }));
        }

        void MakeDefaultBrowser(Window owner)
        {
            try { if (!IsRegistered()) RegisterBrowser(); }
            catch (Exception ex) { MessageBox.Show(owner, L.T("Nie udało się zarejestrować Velivo w Windows:\n") + ex.Message, AppName); return; }
            try
            {
                // Windows 11: strona Velivo w "Aplikacje domyslne" z przyciskiem "Ustaw domyslne"
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=" + AppName) { UseShellExecute = true });
            }
            catch (Exception)
            {
                Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
            }
            MessageBox.Show(owner,
                L.T("Otworzyły się Ustawienia Windows.\n\nKliknij „Ustaw domyślne” przy Velivo (Windows 11)\nalbo wybierz Velivo jako „Przeglądarka sieci Web” (Windows 10).\n\nWindows nie pozwala programom zrobić tego samodzielnie."),
                AppName);
        }
    }
}
