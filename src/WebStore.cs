using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    // Instalacja dodatkow z Chrome Web Store. WebView2 nie ma API sklepu (chrome.webstorePrivate),
    // wiec pobieramy paczke .crx z serwera aktualizacji Google, zdejmujemy naglowek CRX i rozpakowujemy ZIP.
    public partial class MainWindow
    {
        static readonly Regex StoreId = new Regex(@"(?:chromewebstore\.google\.com/detail/(?:[^/?#]+/)?|chrome\.google\.com/webstore/detail/(?:[^/?#]+/)?|^)([a-p]{32})(?:[/?#]|$)", RegexOptions.IgnoreCase);
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        static string ExtensionsDir { get { return Path.Combine(DataDir, "Dodatki"); } }
        static string BundledQuickAccessDir { get { return Path.Combine(AppContext.BaseDirectory, "Dodatki", "Szybki Dostęp"); } }

        async Task EnsureBundledQuickAccessAsync()
        {
            if (Core == null || !File.Exists(Path.Combine(BundledQuickAccessDir, "manifest.json")))
                return;
            if (_settings != null && !_settings.QuickAccessNewTab)
                return;
            try
            {
                var exts = await Core.Profile.GetBrowserExtensionsAsync();
                CoreWebView2BrowserExtension installed = null;
                var paths = LoadExtPaths();
                foreach (var ext in exts)
                {
                    string extPath;
                    bool pathMatch = paths.TryGetValue(ext.Id, out extPath) &&
                        string.Equals(Path.GetFullPath(extPath).TrimEnd(Path.DirectorySeparatorChar),
                            Path.GetFullPath(BundledQuickAccessDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
                    if (IsQuickAccessExtensionId(ext.Id) || pathMatch)
                    {
                        installed = ext;
                        break;
                    }
                }

                string installedPath;
                if (installed != null && paths.TryGetValue(installed.Id, out installedPath) &&
                    string.Equals(Path.GetFullPath(installedPath).TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(BundledQuickAccessDir).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                {
                    Directory.CreateDirectory(ExtensionsDir);
                    File.WriteAllText(Path.Combine(ExtensionsDir, QuickAccessExtensionId + ".id"), installed.Id);
                    EnsureSejfMostAllowedOrigins(new[] { installed.Id });
                    return;
                }

                // Szybki Dostep juz jest w profilu (rozpoznany po ID) - NIE usuwamy go. Usuniecie dodatku kasuje jego
                // dane (skroty, grupy, ustawienia), przez co po aktualizacji Velivo wygladalo jak zainstalowane od zera.
                if (installed != null)
                {
                    if (!installed.IsEnabled) await installed.EnableAsync(true);
                    if (!paths.ContainsKey(installed.Id)) SaveExtPath(installed.Id, BundledQuickAccessDir);
                    Directory.CreateDirectory(ExtensionsDir);
                    File.WriteAllText(Path.Combine(ExtensionsDir, QuickAccessExtensionId + ".id"), installed.Id);
                    EnsureSejfMostAllowedOrigins(new[] { installed.Id });
                    return;
                }

                var added = await Core.Profile.AddBrowserExtensionAsync(BundledQuickAccessDir);
                if (!added.IsEnabled && (_settings == null || _settings.QuickAccessNewTab))
                    await added.EnableAsync(true);
                SaveExtPath(added.Id, BundledQuickAccessDir);
                Directory.CreateDirectory(ExtensionsDir);
                File.WriteAllText(Path.Combine(ExtensionsDir, QuickAccessExtensionId + ".id"), added.Id);
                EnsureSejfMostAllowedOrigins(new[] { added.Id });
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // ID dodatku z adresu strony sklepu albo z samego ID; null gdy to nie dodatek.
        static string ParseStoreId(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = StoreId.Match(text.Trim());
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
        }

        void UpdateStoreButton()
        {
            var url = CurrentUrl;
            bool isStore = url != null && (url.Contains("chromewebstore.google.com/detail/") || url.Contains("chrome.google.com/webstore/detail/"))
                           && ParseStoreId(url) != null;
            StoreBtn.Visibility = isStore ? Visibility.Visible : Visibility.Collapsed;
        }

        async void StoreInstall_Click(object sender, RoutedEventArgs e)
        {
            var id = ParseStoreId(CurrentUrl);
            if (id != null) await InstallFromStore(id, this, false);
        }

        // Zwraca true po udanej instalacji.
        async Task<bool> InstallFromStore(string id, Window owner, bool silent)
        {
            if (Core == null || !Regex.IsMatch(id ?? "", "^[a-p]{32}$")) return false;
            var button = StoreBtn;
            object oldContent = null;
            if (button != null)
            {
                button.IsEnabled = false;
                oldContent = button.Content;
                button.Content = "⏳ Instaluję…";
            }
            string tmp = null;
            try
            {
                string url = "https://clients2.google.com/service/update2/crx?response=redirect&prodversion=" + BrowserMajorVersion() +
                             ".0.0.0&acceptformat=crx2,crx3&x=id%3D" + id + "%26uc";
                byte[] crx = await Http.GetByteArrayAsync(url);
                byte[] zip = StripCrxHeader(crx);

                string target = Path.Combine(ExtensionsDir, id);
                tmp = target + ".nowy";
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                using (var ms = new MemoryStream(zip))
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Read))
                    archive.ExtractToDirectory(tmp);

                // folder _metadata (podpis sklepu) blokuje wczytanie rozpakowanego dodatku
                var meta = Path.Combine(tmp, "_metadata");
                if (Directory.Exists(meta)) Directory.Delete(meta, true);
                if (!File.Exists(Path.Combine(tmp, "manifest.json")))
                    throw new InvalidDataException("Paczka nie zawiera pliku manifest.json.");

                // usun poprzednia wersje: dodatek z folderu ma w WebView2 inne ID niz w sklepie,
                // wiec szukamy po ID zapamietanym przy instalacji (plik <id-sklepu>.id)
                string idFile = Path.Combine(ExtensionsDir, id + ".id");
                string installedId = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : null;
                foreach (var ext in await Core.Profile.GetBrowserExtensionsAsync())
                    if (string.Equals(ext.Id, installedId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ext.Id, id, StringComparison.OrdinalIgnoreCase))
                        await ext.RemoveAsync();
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(tmp, target);
                tmp = null;

                var added = await Core.Profile.AddBrowserExtensionAsync(target);
                File.WriteAllText(idFile, added.Id);
                SaveExtPath(added.Id, target);
                await SaveExtensionsSyncListAsync();
                NotifyLanStateChanged();
                await RefreshExtensions();
                if (!silent)
                    MessageBox.Show(owner, "Zainstalowano dodatek „" + added.Name + "”.\nOdśwież strony, na których ma działać.\nZarządzasz nim w oknie 🧩.", "Dodatki");
                return true;
            }
            catch (HttpRequestException ex)
            {
                if (!silent)
                    MessageBox.Show(owner, "Nie udało się pobrać dodatku ze sklepu.\nSprawdź połączenie z internetem – albo dodatek został wycofany.\n\n" + ex.Message, "Dodatki");
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show(owner, "Nie udało się zainstalować dodatku:\n" + ex.Message +
                        "\n\nNiektóre dodatki wymagają funkcji pełnego Chrome, których silnik WebView2 nie ma.", "Dodatki");
                else App.LogError(ex);
            }
            finally
            {
                if (tmp != null) try { Directory.Delete(tmp, true); } catch (IOException) { }
                if (button != null)
                {
                    button.Content = oldContent;
                    button.IsEnabled = true;
                }
            }
            return false;
        }

        // Przycisk sklepu „Dodaj do Chrome” pobiera plik .crx - przechwytujemy to i instalujemy sami.
        bool TryHandleCrxDownload(CoreWebView2DownloadStartingEventArgs e)
        {
            string uri = e.DownloadOperation.Uri ?? "";
            string name = Path.GetFileName(e.ResultFilePath ?? "");
            bool isCrx = name.EndsWith(".crx", StringComparison.OrdinalIgnoreCase) ||
                         uri.Contains("/service/update2/crx") || uri.Contains("/crx/blobs/");
            if (!isCrx) return false;
            var m = Regex.Match(uri + " " + name, "(?:id%3D|id=|^|[^a-p])([a-p]{32})(?:[^a-p]|$)");
            if (!m.Success) return false;
            e.Cancel = true;
            string id = m.Groups[1].Value;
            Dispatcher.BeginInvoke(new Action(async () => await InstallFromStore(id, this, false)));
            return true;
        }

        string BrowserMajorVersion()
        {
            try
            {
                var v = _env.BrowserVersionString; // np. "154.0.4258.48"
                int dot = v.IndexOf('.');
                return dot > 0 ? v.Substring(0, dot) : "140";
            }
            catch (Exception) { return "140"; }
        }

        // CRX2: "Cr24" ver=2, len(klucz), len(podpis), klucz, podpis, ZIP
        // CRX3: "Cr24" ver=3, len(naglowek), naglowek (protobuf), ZIP
        static byte[] StripCrxHeader(byte[] crx)
        {
            if (crx.Length > 4 && crx[0] == 'P' && crx[1] == 'K') return crx; // juz ZIP
            if (crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
                throw new InvalidDataException("Serwer nie zwrócił paczki dodatku (CRX).");
            uint version = BitConverter.ToUInt32(crx, 4);
            long start;
            if (version == 3) start = 12L + BitConverter.ToUInt32(crx, 8);
            else if (version == 2) start = 16L + BitConverter.ToUInt32(crx, 8) + BitConverter.ToUInt32(crx, 12);
            else throw new InvalidDataException("Nieznana wersja paczki CRX: " + version);
            if (start >= crx.Length) throw new InvalidDataException("Uszkodzona paczka CRX.");
            var zip = new byte[crx.Length - start];
            Array.Copy(crx, start, zip, 0, zip.Length);
            return zip;
        }
    }
}



