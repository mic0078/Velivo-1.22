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

        void OpenFromOutside(string[] urls)
        {
            if (urls.Length == 0) AddTab(NewTabUrl);
            foreach (var u in urls) AddTab(u);
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
                        foreach (var ext in new[] { ".htm", ".html", ".shtml", ".xhtml", ".svg" }) f.SetValue(ext, ProgId);
                    using (var s = cap.CreateSubKey("StartMenu")) s.SetValue("StartMenuInternet", AppName);
                }
            }
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                k.SetValue(AppName, ClientKey + @"\Capabilities");
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
