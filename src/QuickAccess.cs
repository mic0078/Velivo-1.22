using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace Przegladarka
{
    public partial class MainWindow
    {
        const int QuickAccessArchiveLimitBytes = 24 * 1024 * 1024;
        static string QuickAccessDataDir
        {
            get
            {
                var overrideDir = Environment.GetEnvironmentVariable("PRZEGLADARKA_DANE");
                var root = string.IsNullOrWhiteSpace(overrideDir) ? AppContext.BaseDirectory : overrideDir;
                return Path.Combine(root, "Szybki Dostęp");
            }
        }

        static byte[] CreateQuickAccessArchive()
        {
            if (!Directory.Exists(QuickAccessDataDir)) return Array.Empty<byte>();
            using (var output = new MemoryStream())
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                {
                    var files = Directory.EnumerateFiles(QuickAccessDataDir, "*", SearchOption.AllDirectories)
                        .Where(file => (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                        .Select(file => new { File = file, Relative = Path.GetRelativePath(QuickAccessDataDir, file).Replace('\\', '/') })
                        .Where(item => !string.Equals(Path.GetFileName(item.Relative), "z-sieci.json", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    foreach (var item in files)
                    {
                        var file = item.File;
                        var relative = item.Relative;
                        if (!IsSafeQuickAccessArchivePath(relative)) throw new InvalidDataException("Nieprawidłowa ścieżka pliku Szybkiego Dostępu.");
                        var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using (var source = File.OpenRead(file))
                        using (var destination = entry.Open()) source.CopyTo(destination);
                        if (output.Length > QuickAccessArchiveLimitBytes) throw new InvalidDataException("Dane Szybkiego Dostępu przekraczają limit synchronizacji 24 MB.");
                    }
                }
                if (output.Length > QuickAccessArchiveLimitBytes) throw new InvalidDataException("Dane Szybkiego Dostępu przekraczają limit synchronizacji 24 MB.");
                return output.ToArray();
            }
        }

        static bool IsSafeQuickAccessArchivePath(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("/", StringComparison.Ordinal) || relative.Contains(":")) return false;
            return relative.Replace('\\', '/').Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        }

        static void ImportQuickAccessArchive(byte[] data)
        {
            if (data == null || data.Length == 0) return;
            if (data.Length > QuickAccessArchiveLimitBytes) throw new InvalidDataException("Dane Szybkiego Dostępu przekraczają limit 24 MB.");

            var parent = Path.GetDirectoryName(QuickAccessDataDir);
            Directory.CreateDirectory(parent);
            var temp = QuickAccessDataDir + ".import-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(temp);
                using (var input = new MemoryStream(data, false))
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
                {
                    if (archive.Entries.Count > 5000) throw new InvalidDataException("Za dużo plików w magazynie Szybkiego Dostępu.");
                    long expanded = 0;
                    foreach (var entry in archive.Entries)
                    {
                        var relative = entry.FullName.Replace('\\', '/');
                        if (!IsSafeQuickAccessArchivePath(relative)) throw new InvalidDataException("Nieprawidłowa ścieżka w magazynie Szybkiego Dostępu.");
                        expanded += entry.Length;
                        if (expanded > QuickAccessArchiveLimitBytes * 4L) throw new InvalidDataException("Rozpakowane dane Szybkiego Dostępu przekraczają limit.");
                        var target = Path.GetFullPath(Path.Combine(temp, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!target.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Nieprawidłowa ścieżka w magazynie Szybkiego Dostępu.");
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var source = entry.Open())
                        using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(destination);
                    }
                }

                Directory.CreateDirectory(QuickAccessDataDir);
                foreach (var stagedFile in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(temp, stagedFile).Replace('\\', '/');
                    if (string.Equals(Path.GetFileName(relative), "z-sieci.json", StringComparison.OrdinalIgnoreCase)) continue;
                    var targetRelative = string.Equals(Path.GetFileName(relative), "kopia.json", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Path.GetDirectoryName(relative) ?? "", "z-sieci.json")
                        : relative;
                    var target = Path.GetFullPath(Path.Combine(QuickAccessDataDir, targetRelative));
                    if (!target.StartsWith(QuickAccessDataDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Nieprawidłowa ścieżka w magazynie Szybkiego Dostępu.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(stagedFile, target, true);
                }
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
        }

        static string ExportQuickAccessBase64()
        {
            if (!Directory.Exists(QuickAccessDataDir)) return null;
            var archive = CreateQuickAccessArchive();
            try { return Convert.ToBase64String(archive); }
            finally { Array.Clear(archive, 0, archive.Length); }
        }

        static void ImportQuickAccessBase64(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded)) return;
            if (encoded.Length > ((QuickAccessArchiveLimitBytes + 2) / 3 * 4) + 8)
                throw new InvalidDataException("Dane Szybkiego Dostępu przekraczają limit 24 MB.");
            var archive = Convert.FromBase64String(encoded);
            try { ImportQuickAccessArchive(archive); }
            finally { Array.Clear(archive, 0, archive.Length); }
        }

        static bool IsQuickAccessUrl(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return false;
            Uri parsed;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed)) return false;
            if (!string.Equals(parsed.Scheme, "chrome-extension", StringComparison.OrdinalIgnoreCase)) return false;
            return IsQuickAccessExtensionId(parsed.Host);
        }

        static bool IsQuickAccessDataPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(":")) return false;
            var normalized = path.Replace('\\', '/');
            if (normalized != "dane" && !normalized.StartsWith("dane/", StringComparison.OrdinalIgnoreCase)) return false;
            return normalized.Split('/').All(part => part.Length > 0 && part != "." && part != "..");
        }

        async Task HandleQuickAccessWebMessageAsync(CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!IsQuickAccessUrl(e.Source)) return;
            try
            {
                using (var doc = JsonDocument.Parse(e.WebMessageAsJson))
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("typ", out var type) || type.GetString() != "velivo-quick-access" ||
                        !root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out int id) ||
                        !root.TryGetProperty("zadanie", out var request) || request.ValueKind != JsonValueKind.Object ||
                        !request.TryGetProperty("c", out var commandElement) || commandElement.ValueKind != JsonValueKind.String) return;

                    string command = commandElement.GetString();
                    string path = request.TryGetProperty("p", out var pathElement) && pathElement.ValueKind == JsonValueKind.String ? pathElement.GetString() : null;
                    string b64 = request.TryGetProperty("b64", out var dataElement) && dataElement.ValueKind == JsonValueKind.String ? dataElement.GetString() : null;
                    bool allowed = command == "ping" || ((command == "read" || command == "write" || command == "list" || command == "del") && IsQuickAccessDataPath(path));
                    if (!allowed || (b64 != null && b64.Length > 16 * 1024 * 1024))
                    {
                        ReplyQuickAccess(core, id, new { ok = false, id });
                        return;
                    }

                    var response = await Task.Run(() => ExecuteQuickAccessCommand(command, path, b64, id));
                    ReplyQuickAccess(core, id, response);
                }
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        static object ExecuteQuickAccessCommand(string command, string relativePath, string b64, int id)
        {
            if (command == "ping") return new { ok = true, baza = QuickAccessDataDir, id };

            var root = Path.GetFullPath(QuickAccessDataDir);
            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new { ok = false, id };

            try
            {
                if (command == "read")
                {
                    if (!File.Exists(path)) return new { ok = false, id };
                    return new { ok = true, b64 = Convert.ToBase64String(File.ReadAllBytes(path)), id };
                }
                if (command == "write")
                {
                    var bytes = Convert.FromBase64String(b64 ?? "");
                    if (bytes.Length > 12 * 1024 * 1024) return new { ok = false, id };
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, bytes);
                    return new { ok = true, id };
                }
                if (command == "list")
                {
                    var directory = Directory.Exists(path) ? path : null;
                    var files = directory == null ? Array.Empty<string>() : Directory.GetFiles(directory).Select(Path.GetFileName).ToArray();
                    return new { ok = true, pliki = files, id };
                }
                if (command == "del")
                {
                    if (File.Exists(path)) File.Delete(path);
                    return new { ok = true, id };
                }
            }
            catch (Exception ex) { App.LogError(ex); }
            return new { ok = false, id };
        }

        static void ReplyQuickAccess(CoreWebView2 core, int id, object response)
        {
            try
            {
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new
                {
                    typ = "velivo-quick-access-odpowiedz",
                    id,
                    odpowiedz = response
                }));
            }
            catch (Exception) { }
        }
    }
}