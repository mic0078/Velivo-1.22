using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Velivo
{
    // 📁 opis i plakat obok filmu (jak Jellyfin / Kodi): „Film.nfo” i „Film-poster.jpg” w folderze filmu.
    // Folder z filmem można przekopiować na inny dysk / komputer - Velivo (i Jellyfin) od razu zna opis i plakat.
    public partial class MainWindow
    {
        static readonly ConcurrentDictionary<string, MovieInfo> _nfoCache = new ConcurrentDictionary<string, MovieInfo>(StringComparer.OrdinalIgnoreCase);
        static bool _nfoErrLogged;

        static bool PlikLokalny(string path) => !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && !path.Contains("://");
        static string NfoPlik(string path) => Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + ".nfo");
        static string PlakatObok(string path) => Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + "-poster.jpg");

        // zapis obok filmu: nfo i plakat; force = nadpisz (dane poprawione ręcznie)
        void MetaObokFilmu(string path, bool force = false)
        {
            if (!PlikLokalny(path)) return;
            var m = LookupMeta(Meta, path);
            if (m == null || string.IsNullOrWhiteSpace(m.Name)) return;
            Task.Run(() => MetaObokZapisz(path, m, force));
        }

        void MetaObokWszystkich()
        {
            var meta = Meta;
            var lib = LoadLibrary().Where(v => PlikLokalny(v.Path)).Select(v => (v.Path, M: LookupMeta(meta, v.Path))).Where(x => x.M != null && !string.IsNullOrWhiteSpace(x.M.Name)).ToList();
            Task.Run(() => { foreach (var x in lib) MetaObokZapisz(x.Path, x.M, false); });
        }

        static void MetaObokZapisz(string path, MovieInfo m, bool force)
        {
            try
            {
                if (!File.Exists(path)) return;
                var nfo = NfoPlik(path);
                if (force || !File.Exists(nfo))
                {
                    var x = new XElement("movie",
                        new XElement("title", m.Name ?? ""),
                        string.IsNullOrWhiteSpace(m.Original) ? null : new XElement("originaltitle", m.Original),
                        m.Year > 0 ? new XElement("year", m.Year) : null,
                        m.Rating > 0 ? new XElement("rating", m.Rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)) : null,
                        string.IsNullOrWhiteSpace(m.Overview) ? null : new XElement("plot", m.Overview.Trim()),
                        string.IsNullOrWhiteSpace(m.Age) ? null : new XElement("mpaa", m.Age),
                        string.IsNullOrWhiteSpace(m.Director) ? null : new XElement("director", m.Director),
                        (m.Genres ?? new System.Collections.Generic.List<string>()).Select(g => new XElement("genre", g)),
                        (m.People ?? new System.Collections.Generic.List<string>()).Select(p => new XElement("actor", new XElement("name", p))),
                        string.IsNullOrEmpty(m.Id) ? null : new XElement("uniqueid", new XAttribute("type", "jellyfin"), m.Id));
                    File.WriteAllText(nfo, new XDocument(new XDeclaration("1.0", "utf-8", "yes"), x).ToString(), new UTF8Encoding(false));
                }
                var src = string.IsNullOrEmpty(m.Poster) ? null : Path.Combine(PosterDir, m.Poster);
                var dst = PlakatObok(path);
                if (src != null && File.Exists(src) && !string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase) && (force || !File.Exists(dst)))
                    File.Copy(src, dst, true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (!_nfoErrLogged) { _nfoErrLogged = true; App.LogError(ex); }   // folder tylko do odczytu (płyta, NAS) - raz w dzienniku
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        // odczyt: film przekopiowany z innego komputera - opis i plakat z jego folderu (bez Jellyfin i bez bazy)
        static MovieInfo MetaZFolderu(string path)
        {
            if (!PlikLokalny(path)) return null;
            if (_nfoCache.TryGetValue(path, out var c)) return c;
            MovieInfo m = null;
            try
            {
                var nfo = NfoPlik(path);
                if (File.Exists(nfo))
                {
                    var r = XDocument.Load(nfo).Root;
                    string V(string n) => r?.Element(n)?.Value?.Trim();
                    if (r != null && !string.IsNullOrEmpty(V("title")))
                    {
                        m = new MovieInfo { Name = V("title"), Original = V("originaltitle"), Overview = V("plot"), Age = V("mpaa"), Director = V("director"),
                            Genres = r.Elements("genre").Select(e => e.Value.Trim()).Where(s => s.Length > 0).ToList(),
                            People = r.Elements("actor").Select(e => (e.Element("name")?.Value ?? "").Trim()).Where(s => s.Length > 0).ToList() };
                        int.TryParse(V("year"), out var y); m.Year = y;
                        double.TryParse((V("rating") ?? "").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rt); m.Rating = Math.Max(0, Math.Min(10, rt));
                        var dir = Path.GetDirectoryName(path) ?? "";
                        var pf = new[] { PlakatObok(path), Path.Combine(dir, "poster.jpg"), Path.Combine(dir, "folder.jpg") }.FirstOrDefault(File.Exists);
                        if (pf != null) m.Poster = pf;   // pełna ścieżka - Path.Combine(PosterDir, …) zwraca ją bez zmian
                    }
                }
            }
            catch (Exception ex) { App.LogError(ex); }
            _nfoCache[path] = m;
            return m;
        }
    }
}
