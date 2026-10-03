using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Przegladarka
{
    // Pelne listy filtrow AdBlocka: pobierane w tle, trzymane na dysku, odswiezane co 4 dni.
    public partial class MainWindow
    {
        static readonly (string File, string Url, string Name)[] FilterSources =
        {
            ("easylist.txt",    "https://easylist.to/easylist/easylist.txt", "EasyList (reklamy)"),
            ("easyprivacy.txt", "https://easylist.to/easylist/easyprivacy.txt", "EasyPrivacy (trackery)"),
            ("polska.txt",      "https://raw.githubusercontent.com/MajkiIT/polish-ads-filter/master/polish-adblock-filters/adblock.txt", "Polska lista (KAD)"),
        };
        static string FiltersDir { get { return Path.Combine(DataDir, "Filtry"); } }
        static readonly TimeSpan FilterMaxAge = TimeSpan.FromDays(4);
        static readonly HttpClient FilterHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        internal string FilterStatus = "";

        // Budowa filtra w tle (ok. 100 tys. regul - kilkaset ms) i podmiana w calosci.
        Task RebuildBlocker()
        {
            bool enabled = _blocker.Enabled;
            bool useLists = _settings.FullFilterLists;
            return Task.Run(() =>
            {
                var b = new AdBlocker { Enabled = enabled };
                b.Load(Path.Combine(AppContext.BaseDirectory, "filters.txt"));
                b.Load(Path.Combine(DataDir, "filters.txt"));
                if (useLists)
                    foreach (var s in FilterSources) b.Load(Path.Combine(FiltersDir, s.File), true);
                return b;
            }).ContinueWith(t =>
            {
                if (t.Status != TaskStatus.RanToCompletion) { App.LogError(t.Exception); return; }
                Dispatcher.Invoke(() => { t.Result.Enabled = _blocker.Enabled; _blocker = t.Result; UpdateCounter(); });
            });
        }

        void StartFilterLists()
        {
            Task.Run(async () =>
            {
                await RebuildBlocker();                     // najpierw to, co juz jest na dysku
                if (_settings.FullFilterLists && FiltersStale())
                {
                    await DownloadFilterLists();
                    await RebuildBlocker();
                }
            });
        }

        static bool FiltersStale()
        {
            return FilterSources.Any(s =>
            {
                var f = Path.Combine(FiltersDir, s.File);
                return !File.Exists(f) || DateTime.Now - File.GetLastWriteTime(f) > FilterMaxAge;
            });
        }

        // Pobiera listy; plik podmieniany dopiero po udanym pobraniu (nieudane pobranie zostawia stara liste).
        async Task<int> DownloadFilterLists()
        {
            int ok = 0;
            Directory.CreateDirectory(FiltersDir);
            foreach (var s in FilterSources)
            {
                try
                {
                    var text = await FilterHttp.GetStringAsync(s.Url);
                    if (text.Length < 1000 || !text.Contains("||")) continue; // to nie lista filtrow
                    var path = Path.Combine(FiltersDir, s.File);
                    File.WriteAllText(path + ".tmp", text);
                    File.Move(path + ".tmp", path, true);
                    ok++;
                }
                catch (Exception ex) { App.LogError(ex); }
            }
            FilterStatus = ok == FilterSources.Length ? "" : L.T("Nie udało się pobrać ") + (FilterSources.Length - ok) + L.T(" list(y) – używam poprzednich.");
            return ok;
        }

        static string FilterListsInfo()
        {
            var dates = FilterSources.Select(s => Path.Combine(FiltersDir, s.File)).Where(File.Exists).Select(File.GetLastWriteTime).ToList();
            if (dates.Count == 0) return L.T("jeszcze nie pobrane");
            return "zaktualizowane " + dates.Min().ToString("d.MM.yyyy HH:mm");
        }
    }
}
