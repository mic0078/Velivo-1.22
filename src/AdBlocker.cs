using System;
using System.Collections.Generic;
using System.IO;
using System.Net;

namespace Przegladarka
{
    // Lekki filtr: domeny w HashSet (sprawdzanie O(liczba czlonow hosta)), fragmenty URL jako lista.
    // Czyta tez listy w formacie EasyList - z nich bierze tylko reguly blokujace cale domeny (||domena^),
    // bo reguly zalezne od strony ($domain=), kosmetyczne (##) i inne specjalne wymagalyby pelnego silnika
    // i przy uproszczonej obsludze moglyby psuc strony.
    public sealed class AdBlocker
    {
        readonly HashSet<string> _blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _fragments = new List<string>();

        public bool Enabled = true;
        public int RuleCount { get { return _blocked.Count + _fragments.Count; } }

        public static bool IsLocalNetworkUri(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;

            var host = uri.Host.TrimEnd('.');
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".home", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase) ||
                host.IndexOf('.') < 0) return true;

            IPAddress address;
            if (!IPAddress.TryParse(host, out address)) return false;
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return true;

            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
            {
                return bytes[0] == 10 ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 169 && bytes[1] == 254) ||
                       (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) ||
                       bytes[0] == 0;
            }

            return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
        }

        static readonly string[] UnsupportedOptions = { "domain=", "~", "redirect", "csp", "removeparam", "badfilter", "rewrite", "header=", "permissions", "urltransform", "replace=", "denyallow", "to=", "from=", "method=" };

        // domainOnly: z list zewnetrznych bierzemy tylko reguly domenowe (szybkie i bezpieczne)
        public void Load(string path, bool domainOnly = false)
        {
            if (!File.Exists(path)) return;
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '!' || line[0] == '[' || (line[0] == '#' && !line.StartsWith("##"))) continue;
                if (line.Contains("##") || line.Contains("#@#") || line.Contains("#?#") || line.Contains("#$#") || line.Contains("#%#")) continue; // kosmetyczne
                bool allow = line.StartsWith("@@");
                if (allow) line = line.Substring(2);
                int opt = line.IndexOf('$');
                if (opt >= 0)
                {
                    var options = line.Substring(opt + 1).ToLowerInvariant();
                    bool skip = false;
                    foreach (var u in UnsupportedOptions) if (options.Contains(u)) { skip = true; break; }
                    if (skip) continue;
                    line = line.Substring(0, opt);
                }
                bool anchored = line.StartsWith("||");
                if (anchored) line = line.Substring(2);
                line = line.TrimEnd('^', '*');
                if (line.Length == 0) continue;

                bool isDomain = line.IndexOf('/') < 0 && line.IndexOf('.') > 0 && line.IndexOf('*') < 0 && line.IndexOf('^') < 0 && line.IndexOf(':') < 0;
                if (isDomain && (anchored || !domainOnly)) (allow ? _allowed : _blocked).Add(line);
                else if (!allow && !domainOnly) _fragments.Add(line.ToLowerInvariant());
            }
        }

        bool Matches(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != "http" && u.Scheme != "https") return false;
            if (IsLocalNetworkUri(url)) return false;

            // host i wszystkie domeny nadrzedne: a.b.example.com -> b.example.com -> example.com
            string host = u.Host;
            while (true)
            {
                if (_allowed.Contains(host)) return false;
                if (_blocked.Contains(host)) return true;
                int dot = host.IndexOf('.');
                if (dot < 0) break;
                host = host.Substring(dot + 1);
            }

            if (_fragments.Count > 0)
            {
                string low = url.ToLowerInvariant();
                foreach (var f in _fragments)
                    if (low.Contains(f)) return true;
            }
            return false;
        }

        public bool ShouldBlock(string url) { return Enabled && Matches(url); }

        public bool ShouldBlockForced(string url) { return Matches(url); }
    }
}
