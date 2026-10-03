using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace Przegladarka
{
    // Loginy z Sejfu bez otwierania okienka dodatku: gdy strona ma pole hasla, Velivo pyta Sejf
    // (przez ten sam SejfMost.exe co dodatek Szybki Dostep) o loginy dla TEJ domeny. Jesli sa,
    // na pasku pojawia sie kluczyk z liczba loginow; klikniecie wypelnia formularz.
    // Domena zawsze z prawdziwego adresu karty; przed wypelnieniem sprawdzamy, ze karta nadal jest na tej stronie.
    public partial class MainWindow
    {
        sealed class SejfLogin { public string Nazwa, Login, Haslo; }

        Process _most;
        Stream _mostIn, _mostOut;
        int _mostId;
        readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _mostWaiting = new ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>();
        readonly SemaphoreSlim _mostWrite = new SemaphoreSlim(1, 1);
        List<SejfLogin> _keyLogins = new List<SejfLogin>();
        string _keyHost;
        int _keyCheck; // numer biezacego sprawdzenia (starsze wyniki ignorujemy)

        // Sciezka do SejfMost.exe z rejestracji hosta (tak samo jak szuka jej przegladarka).
        static string FindSejfMost()
        {
            foreach (var manifest in FindSejfMostManifestPaths())
            {
                try
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(manifest)))
                    {
                        var path = doc.RootElement.GetProperty("path").GetString();
                        if (!Path.IsPathRooted(path)) path = Path.Combine(Path.GetDirectoryName(manifest), path);
                        if (File.Exists(path)) return path;
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        static IEnumerable<string> FindSejfMostManifestPaths()
        {
            foreach (var key in new[] { @"Software\Microsoft\Edge\NativeMessagingHosts\pl.szybkidostep.most", @"Software\Google\Chrome\NativeMessagingHosts\pl.szybkidostep.most" })
            {
                string manifest = null;
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(key))
                        manifest = k == null ? null : k.GetValue("") as string;
                }
                catch (Exception) { }
                if (!string.IsNullOrWhiteSpace(manifest) && File.Exists(manifest))
                    yield return manifest;
            }
        }

        static bool IsExtensionIdLike(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length != 32) return false;
            return id.All(ch => ch >= 'a' && ch <= 'p');
        }

        static void EnsureSejfMostAllowedOrigins(IEnumerable<string> extraIds = null)
        {
            try
            {
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in QuickAccessKnownIds())
                    if (IsExtensionIdLike(id)) ids.Add(id);
                if (extraIds != null)
                    foreach (var id in extraIds)
                        if (IsExtensionIdLike(id)) ids.Add(id);
                if (ids.Count == 0) return;

                foreach (var manifest in FindSejfMostManifestPaths().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var root = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject;
                        if (root == null) continue;
                        var allowed = root["allowed_origins"] as JsonArray;
                        if (allowed == null)
                        {
                            allowed = new JsonArray();
                            root["allowed_origins"] = allowed;
                        }

                        bool changed = false;
                        foreach (var id in ids)
                        {
                            var origin = "chrome-extension://" + id + "/";
                            bool exists = allowed.Any(n => string.Equals((string)n, origin, StringComparison.OrdinalIgnoreCase));
                            if (!exists)
                            {
                                allowed.Add(origin);
                                changed = true;
                            }
                        }

                        if (changed)
                            File.WriteAllText(manifest, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch (Exception ex)
                    {
                        App.LogError(ex);
                    }
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }

        bool EnsureMost()
        {
            if (_most != null && !_most.HasExited) return true;
            EnsureSejfMostAllowedOrigins(new[] { QuickAccessInstalledId });
            var exe = FindSejfMost();
            if (exe == null) return false;
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            psi.ArgumentList.Add("chrome-extension://" + QuickAccessInstalledId + "/"); // jak przy uruchomieniu przez przegladarke
            _most = Process.Start(psi);
            _mostIn = _most.StandardInput.BaseStream;
            _mostOut = _most.StandardOutput.BaseStream;
            var proc = _most; var output = _mostOut;
            Task.Run(() => ReadMost(proc, output));
            return true;
        }

        void ReadMost(Process proc, Stream output)
        {
            try
            {
                var head = new byte[4];
                while (true)
                {
                    if (!ReadExact(output, head, 4)) break;
                    int len = BitConverter.ToInt32(head, 0);
                    if (len <= 0 || len > 64 * 1024 * 1024) break;
                    var body = new byte[len];
                    if (!ReadExact(output, body, len)) break;
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement.Clone();
                        if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int id) && _mostWaiting.TryRemove(id, out var tcs))
                            tcs.TrySetResult(root);
                    }
                }
            }
            catch (Exception) { }
            foreach (var kv in _mostWaiting.ToArray()) if (_mostWaiting.TryRemove(kv.Key, out var t)) t.TrySetResult(default(JsonElement));
        }

        static bool ReadExact(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count) { int n = s.Read(buf, got, count - got); if (n <= 0) return false; got += n; }
            return true;
        }

        async Task<List<SejfLogin>> SejfSearch(string domain)
        {
            if (!EnsureMost()) return null;
            int id = Interlocked.Increment(ref _mostId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _mostWaiting[id] = tcs;
            var msg = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { c = "sejf-szukaj", domena = domain, id }));
            await _mostWrite.WaitAsync();
            try
            {
                await _mostIn.WriteAsync(BitConverter.GetBytes(msg.Length), 0, 4);
                await _mostIn.WriteAsync(msg, 0, msg.Length);
                await _mostIn.FlushAsync();
            }
            catch (Exception) { _mostWaiting.TryRemove(id, out _); return null; }
            finally { _mostWrite.Release(); }
            // Sejf moze zapytac o odblokowanie (okno Sejfu) - dajemy minute, potem odpuszczamy
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(60)));
            if (done != tcs.Task) { _mostWaiting.TryRemove(id, out _); return null; }
            var r = tcs.Task.Result;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True) return null;
            var list = new List<SejfLogin>();
            if (r.TryGetProperty("wpisy", out var wpisy) && wpisy.ValueKind == JsonValueKind.Array)
                foreach (var w in wpisy.EnumerateArray())
                    list.Add(new SejfLogin
                    {
                        Nazwa = w.TryGetProperty("Nazwa", out var n) ? n.GetString() : "",
                        Login = w.TryGetProperty("Login", out var l) ? l.GetString() : "",
                        Haslo = w.TryGetProperty("Haslo", out var h) ? h.GetString() : "",
                    });
            return list;
        }

        // Po zaladowaniu strony: ma pole hasla? -> zapytaj Sejf o loginy dla tej domeny.
        async void CheckSejfLogins(BrowserTab tab)
        {
            if (tab != _current) return;
            int check = ++_keyCheck;
            HideKey();
            if (!_settings.SejfLogins) return;
            var core = tab.View.CoreWebView2;
            if (core == null) return;
            var host = HostOf(core.Source);
            if (host == null) return;
            try
            {
                // pole hasla moze pojawic sie chwile po zaladowaniu (strony skryptowe) - kilka prob
                bool hasPassword = false;
                for (int i = 0; i < 4 && !hasPassword; i++)
                {
                    if (i > 0) await Task.Delay(1200);
                    if (check != _keyCheck || core != tab.View.CoreWebView2) return;
                    hasPassword = await core.ExecuteScriptAsync("!!document.querySelector('input[type=password]')") == "true";
                }
                if (!hasPassword) return;
                var logins = await Task.Run(() => SejfSearch(host));
                if (check != _keyCheck || tab != _current || HostOf(core.Source) != host) return; // w miedzyczasie zmieniono strone
                if (logins == null || logins.Count == 0) return;
                _keyLogins = logins; _keyHost = host;
                KeyBtn.Content = "" + (logins.Count > 1 ? " " + logins.Count : "");
                KeyBtn.ToolTip = "Sejf ma " + (logins.Count == 1 ? "login" : logins.Count + " loginy") + " dla " + host + "\nKliknij, aby wypełnić formularz";
                KeyBtn.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void HideKey() { KeyBtn.Visibility = Visibility.Collapsed; _keyLogins = new List<SejfLogin>(); _keyHost = null; }

        void KeyBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_keyLogins.Count == 1) { FillLogin(_keyLogins[0]); return; }
            var menu = new ContextMenu { PlacementTarget = KeyBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var l in _keyLogins)
            {
                var login = l;
                var item = new MenuItem { Header = (string.IsNullOrEmpty(login.Login) ? "(bez loginu)" : login.Login) + (string.IsNullOrEmpty(login.Nazwa) ? "" : "   —   " + login.Nazwa) };
                item.Click += (s, a) => FillLogin(login);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // Wypelnienie formularza (ta sama logika co w okienku Szybkiego Dostepu).
        async void FillLogin(SejfLogin login)
        {
            var core = Core;
            if (core == null || _keyHost == null || HostOf(core.Source) != _keyHost) { HideKey(); return; } // inna strona - nie wypelniamy
            const string fill = @"(function (login, haslo, host) {
  if (!['http:', 'https:'].includes(location.protocol) || location.hostname.toLowerCase() !== host) return false;
  const pola = [...document.querySelectorAll('input:not([type=""hidden""]), textarea')];
  const poleHasla = pola.find(p => p.type === 'password' || /current-password/i.test(p.autocomplete || ''));
  const poleLogin = pola.find(p => p !== poleHasla && /username|email|user|login/i.test(`${p.name} ${p.id} ${p.autocomplete} ${p.type}`)) ||
    pola.find(p => p !== poleHasla && p.type === 'text') || pola[0];
  function ustaw(pole, w) {
    if (!pole) return false;
    const proto = pole instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    const set = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    if (set) set.call(pole, w); else pole.value = w;
    pole.dispatchEvent(new Event('input', { bubbles: true, composed: true }));
    pole.dispatchEvent(new Event('change', { bubbles: true, composed: true }));
    return true;
  }
  const a = login ? ustaw(poleLogin, login) : true, b = ustaw(poleHasla, haslo);
  if (poleLogin) poleLogin.dispatchEvent(new Event('blur', { bubbles: true }));
  if (poleHasla) poleHasla.dispatchEvent(new Event('blur', { bubbles: true }));
  return a && b;
})";
            try
            {
                var args = JsonSerializer.Serialize(login.Login ?? "") + "," + JsonSerializer.Serialize(login.Haslo ?? "") + "," + JsonSerializer.Serialize(_keyHost.ToLowerInvariant());
                var ok = await core.ExecuteScriptAsync(fill + "(" + args + ")");
                if (ok != "true") ShowToast("🔑 Nie znaleziono pola loginu lub hasła na tej stronie.", null);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void StopMost()
        {
            try { if (_most != null && !_most.HasExited) { _mostIn.Close(); if (!_most.WaitForExit(1500)) _most.Kill(); } } catch (Exception) { }
        }
    }
}
