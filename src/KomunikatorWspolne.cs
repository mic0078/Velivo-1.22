using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Velivo
{
    // Wspólne dla Velivo i osobnej aplikacji „Velivo Komunikator” (Windows): klucze sprzętowe (WebAuthn), odczyt strumienia,
    // pliki po kawałkach, bezpieczna nazwa pliku, wykrywanie podrobionych adresów w linkach. Kod przeniesiony bez zmian.
    public partial class MainWindow
    {
        const string BankRpId = "velivo.local";

        sealed class BankKey
        {
            public string Id { get; set; } public string X { get; set; } public string Y { get; set; } public string Name { get; set; }
            public string WrapK { get; set; }   // czesc klucza kart zaszyfrowana sekretem z tego klucza sprzetowego (null = klucz tylko otwiera tryb)
            public string WrapKp { get; set; }  // czesc z hasla (Kp) zaszyfrowana tym samym sekretem - sam klucz wystarcza do otwarcia
        }

        // ======================================================================
        //  Windows WebAuthn (webauthn.dll) - rejestracja klucza i sprawdzenie podpisu
        // ======================================================================
        static class WebAuthn
        {
            [StructLayout(LayoutKind.Sequential)] struct RpInfo { public uint Ver; public IntPtr Id; public IntPtr Name; public IntPtr Icon; }
            [StructLayout(LayoutKind.Sequential)] struct UserInfo { public uint Ver; public uint CbId; public IntPtr PbId; public IntPtr Name; public IntPtr Icon; public IntPtr DisplayName; }
            [StructLayout(LayoutKind.Sequential)] struct CoseParam { public uint Ver; public IntPtr Type; public int Alg; }
            [StructLayout(LayoutKind.Sequential)] struct CoseParams { public uint Count; public IntPtr Items; }
            [StructLayout(LayoutKind.Sequential)] struct ClientData { public uint Ver; public uint Cb; public IntPtr Pb; public IntPtr HashAlg; }
            [StructLayout(LayoutKind.Sequential)] struct Credential { public uint Ver; public uint CbId; public IntPtr PbId; public IntPtr Type; }
            [StructLayout(LayoutKind.Sequential)] struct Credentials { public uint Count; public IntPtr Items; }
            [StructLayout(LayoutKind.Sequential)] struct Extensions { public uint Count; public IntPtr Items; }
            [StructLayout(LayoutKind.Sequential)]
            struct MakeOpts { public uint Ver; public uint Timeout; public Credentials Exclude; public Extensions Ext; public uint Attachment; public int RequireResident; public uint UserVerification; public uint Attestation; public uint Flags; }
            // wersja 6 opcji: dochodza m.in. sole hmac-secret (sekret z klucza sprzetowego)
            [StructLayout(LayoutKind.Sequential)]
            struct GetOpts
            {
                public uint Ver; public uint Timeout; public Credentials Allow; public Extensions Ext; public uint Attachment; public uint UserVerification; public uint Flags;
                public IntPtr U2fAppId; public IntPtr U2fAppIdUsed; public IntPtr CancellationId; public IntPtr AllowList;
                public uint LargeBlobOp; public uint CbLargeBlob; public IntPtr PbLargeBlob; public IntPtr HmacSaltValues; public int InPrivate;
            }
            [StructLayout(LayoutKind.Sequential)] struct Extension { public IntPtr Id; public uint Cb; public IntPtr Pv; }
            [StructLayout(LayoutKind.Sequential)] struct HmacSalt { public uint CbFirst; public IntPtr PbFirst; public uint CbSecond; public IntPtr PbSecond; }
            [StructLayout(LayoutKind.Sequential)] struct HmacSaltValues { public IntPtr Global; public uint CredCount; public IntPtr CredList; }
            [StructLayout(LayoutKind.Sequential)]
            struct Attestation { public uint Ver; public IntPtr Format; public uint CbAuthData; public IntPtr PbAuthData; public uint CbAtt; public IntPtr PbAtt; public uint DecodeType; public IntPtr Decode; public uint CbAttObj; public IntPtr PbAttObj; public uint CbCredId; public IntPtr PbCredId; }
            [StructLayout(LayoutKind.Sequential)]
            struct Assertion
            {
                public uint Ver; public uint CbAuthData; public IntPtr PbAuthData; public uint CbSig; public IntPtr PbSig; public Credential Cred;
                public uint CbUserId; public IntPtr PbUserId;
                public Extensions Ext; public uint CbLargeBlob; public IntPtr PbLargeBlob; public uint LargeBlobStatus;   // od wersji 2
                public IntPtr HmacSecret;                                                                                 // od wersji 3
            }

            [DllImport("webauthn.dll")] static extern int WebAuthNAuthenticatorMakeCredential(IntPtr hwnd, ref RpInfo rp, ref UserInfo user, ref CoseParams pubKeyParams, ref ClientData cd, ref MakeOpts opts, out IntPtr attestation);
            [DllImport("webauthn.dll")] static extern int WebAuthNAuthenticatorGetAssertion(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string rpId, ref ClientData cd, ref GetOpts opts, out IntPtr assertion);
            [DllImport("webauthn.dll")] static extern void WebAuthNFreeCredentialAttestation(IntPtr p);
            [DllImport("webauthn.dll")] static extern void WebAuthNFreeAssertion(IntPtr p);
            [DllImport("webauthn.dll")] static extern IntPtr WebAuthNGetErrorName(int hr);

            const uint CrossPlatform = 2, UvDiscouraged = 3, AttestationNone = 1;

            sealed class Mem : IDisposable
            {
                readonly List<IntPtr> _h = new List<IntPtr>();
                public IntPtr Str(string s) { var p = Marshal.StringToHGlobalUni(s); _h.Add(p); return p; }
                public IntPtr Bytes(byte[] b) { var p = Marshal.AllocHGlobal(Math.Max(1, b.Length)); Marshal.Copy(b, 0, p, b.Length); _h.Add(p); return p; }
                public IntPtr Array<T>(IList<T> items) where T : struct
                {
                    int sz = Marshal.SizeOf<T>();
                    var p = Marshal.AllocHGlobal(Math.Max(1, sz * items.Count)); _h.Add(p);
                    for (int i = 0; i < items.Count; i++) Marshal.StructureToPtr(items[i], p + i * sz, false);
                    return p;
                }
                public void Dispose() { foreach (var p in _h) Marshal.FreeHGlobal(p); }
            }

            static Exception Fail(int hr)
            {
                string name = "";
                try { name = Marshal.PtrToStringUni(WebAuthNGetErrorName(hr)); } catch (Exception) { }
                if (name == "NotAllowedError" || hr == unchecked((int)0x800704C7)) return new OperationCanceledException(L.T("Anulowano albo nie dotknięto klucza na czas."));
                if (name == "InvalidStateError") return new InvalidOperationException(L.T("Ten klucz jest już dodany."));
                return new InvalidOperationException(L.T("Klucz sprzętowy: błąd ") + (string.IsNullOrEmpty(name) ? "0x" + hr.ToString("X8") : name));
            }

            static byte[] ClientJson(string type, byte[] challenge)
            {
                string ch = Convert.ToBase64String(challenge).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                return Encoding.UTF8.GetBytes("{\"type\":\"" + type + "\",\"challenge\":\"" + ch + "\",\"origin\":\"https://" + BankRpId + "\",\"crossOrigin\":false}");
            }

            static byte[] Read(IntPtr p, uint n) { var b = new byte[n]; if (n > 0) Marshal.Copy(p, b, 0, (int)n); return b; }

            public static BankKey Register(IntPtr hwnd, string rpId, List<byte[]> exclude)
            {
                using (var m = new Mem())
                {
                    var rp = new RpInfo { Ver = 1, Id = m.Str(rpId), Name = m.Str("Velivo") };
                    var uid = RandomNumberGenerator.GetBytes(16);
                    var user = new UserInfo { Ver = 1, CbId = (uint)uid.Length, PbId = m.Bytes(uid), Name = m.Str("velivo-bank"), DisplayName = m.Str(L.T("Tryb bankowy Velivo")) };
                    var cose = new[] { new CoseParam { Ver = 1, Type = m.Str("public-key"), Alg = -7 } };
                    var cp = new CoseParams { Count = 1, Items = m.Array(cose) };
                    var json = ClientJson("webauthn.create", RandomNumberGenerator.GetBytes(32));
                    var cd = new ClientData { Ver = 1, Cb = (uint)json.Length, Pb = m.Bytes(json), HashAlg = m.Str("SHA-256") };
                    var ex = exclude.Select(id => new Credential { Ver = 1, CbId = (uint)id.Length, PbId = m.Bytes(id), Type = m.Str("public-key") }).ToList();
                    // rozszerzenie hmac-secret: klucz bedzie umial oddac sekret do szyfrowania kart
                    var on = m.Bytes(BitConverter.GetBytes(1));
                    var ext = new[] { new Extension { Id = m.Str("hmac-secret"), Cb = 4, Pv = on } };
                    var opts = new MakeOpts
                    {
                        Ver = 1, Timeout = 60000, Exclude = new Credentials { Count = (uint)ex.Count, Items = ex.Count > 0 ? m.Array(ex) : IntPtr.Zero },
                        Ext = new Extensions { Count = 1, Items = m.Array(ext) },
                        Attachment = CrossPlatform, UserVerification = UvDiscouraged, Attestation = AttestationNone
                    };
                    IntPtr res;
                    int hr = WebAuthNAuthenticatorMakeCredential(hwnd, ref rp, ref user, ref cp, ref cd, ref opts, out res);
                    if (hr == unchecked((int)0x80070057)) { opts.Ext = new Extensions(); hr = WebAuthNAuthenticatorMakeCredential(hwnd, ref rp, ref user, ref cp, ref cd, ref opts, out res); }
                    if (hr != 0) throw Fail(hr);
                    try
                    {
                        var a = Marshal.PtrToStructure<Attestation>(res);
                        var authData = Read(a.PbAuthData, a.CbAuthData);
                        var credId = Read(a.PbCredId, a.CbCredId);
                        byte[] x, y;
                        if (!ParseEs256(authData, out x, out y)) throw new InvalidOperationException(L.T("Ten klucz nie obsługuje wymaganego podpisu (ES256)."));
                        return new BankKey { Id = Convert.ToBase64String(credId), X = Convert.ToBase64String(x), Y = Convert.ToBase64String(y) };
                    }
                    finally { WebAuthNFreeCredentialAttestation(res); }
                }
            }

            // Klucz publiczny P-256 z danych rejestracji (authData -> attestedCredentialData -> COSE).
            static bool ParseEs256(byte[] ad, out byte[] x, out byte[] y)
            {
                x = y = null;
                if (ad.Length < 55 || (ad[32] & 0x40) == 0) return false;   // brak danych klucza
                int credLen = (ad[53] << 8) | ad[54];
                int start = 55 + credLen;
                for (int i = start; i + 35 <= ad.Length; i++)
                {
                    if (x == null && ad[i] == 0x21 && ad[i + 1] == 0x58 && ad[i + 2] == 0x20) { x = ad.Skip(i + 3).Take(32).ToArray(); i += 34; continue; }
                    if (y == null && ad[i] == 0x22 && ad[i + 1] == 0x58 && ad[i + 2] == 0x20) { y = ad.Skip(i + 3).Take(32).ToArray(); i += 34; continue; }
                }
                return x != null && y != null && x.Length == 32 && y.Length == 32;
            }

            public sealed class VerifyResult { public BankKey Key; public byte[] Hmac; }

            // Prosi o dotkniecie klucza i sprawdza podpis jednym z zarejestrowanych kluczy publicznych.
            // Z sola (hmacSalt) prosi tez o sekret hmac-secret - wtedy Hmac = 32 bajty (null, gdy klucz/Windows nie umie).
            public static VerifyResult Verify(IntPtr hwnd, string rpId, List<BankKey> keys, byte[] hmacSalt)
            {
                using (var m = new Mem())
                {
                    var challenge = RandomNumberGenerator.GetBytes(32);
                    var json = ClientJson("webauthn.get", challenge);
                    var cd = new ClientData { Ver = 1, Cb = (uint)json.Length, Pb = m.Bytes(json), HashAlg = m.Str("SHA-256") };
                    var allow = keys.Select(k => { var id = Convert.FromBase64String(k.Id); return new Credential { Ver = 1, CbId = (uint)id.Length, PbId = m.Bytes(id), Type = m.Str("public-key") }; }).ToList();
                    var opts = new GetOpts { Ver = 1, Timeout = 60000, Allow = new Credentials { Count = (uint)allow.Count, Items = m.Array(allow) }, Attachment = CrossPlatform, UserVerification = UvDiscouraged };
                    if (hmacSalt != null && hmacSalt.Length == 32)
                    {
                        var g = new[] { new HmacSalt { CbFirst = 32, PbFirst = m.Bytes(hmacSalt) } };
                        var vals = new[] { new HmacSaltValues { Global = m.Array(g) } };
                        opts.Ver = 6; opts.HmacSaltValues = m.Array(vals);
                    }
                    IntPtr res;
                    int hr = WebAuthNAuthenticatorGetAssertion(hwnd, rpId, ref cd, ref opts, out res);
                    if (hr == unchecked((int)0x80070057) && opts.Ver == 6)
                    {
                        // starszy Windows nie zna wersji 6 - samo sprawdzenie klucza, bez sekretu
                        opts.Ver = 1; opts.HmacSaltValues = IntPtr.Zero;
                        hr = WebAuthNAuthenticatorGetAssertion(hwnd, rpId, ref cd, ref opts, out res);
                    }
                    if (hr != 0) throw Fail(hr);
                    try
                    {
                        var a = Marshal.PtrToStructure<Assertion>(res);
                        var authData = Read(a.PbAuthData, a.CbAuthData);
                        var sig = Read(a.PbSig, a.CbSig);
                        var credId = Convert.ToBase64String(Read(a.Cred.PbId, a.Cred.CbId));
                        var key = keys.FirstOrDefault(k => k.Id == credId);
                        var none = new VerifyResult();
                        if (key == null || authData.Length < 37) return none;
                        // podpis musi dotyczyc tej aplikacji i potwierdzac obecnosc (dotkniecie)
                        if (!authData.Take(32).SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)))) return none;
                        if ((authData[32] & 0x01) == 0) return none;
                        var signed = authData.Concat(SHA256.HashData(json)).ToArray();
                        using (var ec = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = Convert.FromBase64String(key.X), Y = Convert.FromBase64String(key.Y) } }))
                            if (!ec.VerifyData(signed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) return none;
                        byte[] hmac = null;
                        if (opts.Ver == 6 && a.Ver >= 3 && a.HmacSecret != IntPtr.Zero)
                        {
                            var hs = Marshal.PtrToStructure<HmacSalt>(a.HmacSecret);
                            if (hs.CbFirst == 32 && hs.PbFirst != IntPtr.Zero) hmac = Read(hs.PbFirst, hs.CbFirst);
                        }
                        return new VerifyResult { Key = key, Hmac = hmac };
                    }
                    finally { WebAuthNFreeAssertion(res); }
                }
            }
        }

        static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
        {
            int offset = 0;
            while (offset < count)
            {
                int read = await stream.ReadAsync(buffer, offset, count - offset, token);
                if (read <= 0) return false;
                offset += read;
            }
            return true;
        }

        static string SafeFileName(string name)
        {
            var n = Path.GetFileName((name ?? "").Replace('\\', '/').Split('/').Last());
            n = new string(n.Where(c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray()).Trim().TrimStart('.');
            if (n.Length == 0) n = "plik";
            return n.Length > 120 ? n.Substring(n.Length - 120) : n;
        }

        static string FreeDownloadPath(string name)
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, Path.GetFileNameWithoutExtension(name) + " (" + i + ")" + Path.GetExtension(name));
            return path;
        }

        const int LanChunk = 1 << 20;
        const long LanFile2Limit = 1L << 40;   // bez praktycznego limitu (1 TB - ochrona przed blednym naglowkiem)

        static byte[] ChunkNonce(byte[] b, int i) { var n = (byte[])b.Clone(); for (int k = 0; k < 4; k++) n[8 + k] ^= (byte)(i >> (24 - 8 * k)); return n; }

        static byte[] ChunkAad(byte[] ad, int i, bool last) { return ad.Concat(Encoding.UTF8.GetBytes("\n" + i.ToString(CultureInfo.InvariantCulture) + "\n" + (last ? "1" : "0"))).ToArray(); }

        static int ChunkCount(long size) { return (int)Math.Max(1, (size + LanChunk - 1) / LanChunk); }

        // marka -> prawdziwe domeny (domena rejestrowana)
        static readonly Dictionary<string, string[]> ProtectedBrands = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "paypal", new[] { "paypal.com", "paypal.me", "paypalobjects.com" } },
            { "ebay", new[] { "ebay.com", "ebay.co.uk", "ebay.pl", "ebay.de", "ebay.fr", "ebay.it", "ebay.es", "ebayimg.com" } },
            { "amazon", new[] { "amazon.com", "amazon.co.uk", "amazon.pl", "amazon.de", "amazon.fr", "amazon.it", "amazon.es", "amazonpay.com", "media-amazon.com" } },
            { "facebook", new[] { "facebook.com", "fb.com", "fbcdn.net", "facebook.net" } },
            { "instagram", new[] { "instagram.com" } },
            { "whatsapp", new[] { "whatsapp.com", "whatsapp.net" } },
            { "google", new[] { "google.com", "google.co.uk", "google.pl", "googleusercontent.com", "googleapis.com", "youtube.com", "gmail.com" } },
            { "gmail", new[] { "gmail.com", "google.com" } },
            { "microsoft", new[] { "microsoft.com", "microsoftonline.com", "live.com", "outlook.com", "office.com", "office365.com", "msn.com", "bing.com", "skype.com", "sharepoint.com", "onedrive.com", "xbox.com" } },
            { "outlook", new[] { "outlook.com", "live.com", "office.com", "microsoft.com" } },
            { "apple", new[] { "apple.com", "icloud.com" } },
            { "icloud", new[] { "icloud.com", "apple.com" } },
            { "twitter", new[] { "twitter.com", "x.com", "twimg.com" } },
            { "netflix", new[] { "netflix.com" } },
            { "spotify", new[] { "spotify.com" } },
            { "allegro", new[] { "allegro.pl", "allegrolokalnie.pl" } },
            { "olx", new[] { "olx.pl", "olx.ua", "olx.ro", "olx.pt", "olx.bg" } },
            { "vinted", new[] { "vinted.pl", "vinted.co.uk", "vinted.com", "vinted.fr", "vinted.de" } },
            { "inpost", new[] { "inpost.pl", "inpost.co.uk", "inpost.eu" } },
            { "pkobp", new[] { "pkobp.pl", "ipko.pl" } },
            { "ipko", new[] { "ipko.pl", "pkobp.pl" } },
            { "mbank", new[] { "mbank.pl" } },
            { "santander", new[] { "santander.pl", "santander.co.uk", "santander.com" } },
            { "pekao", new[] { "pekao.com.pl", "pekao24.pl" } },
            { "millennium", new[] { "bankmillennium.pl" } },
            { "revolut", new[] { "revolut.com", "revolut.me" } },
            { "monzo", new[] { "monzo.com", "monzo.me" } },
            { "starling", new[] { "starlingbank.com" } },
            { "barclays", new[] { "barclays.co.uk", "barclays.com", "barclaycard.co.uk" } },
            { "hsbc", new[] { "hsbc.co.uk", "hsbc.com" } },
            { "lloyds", new[] { "lloydsbank.com", "lloydsbank.co.uk", "lloydsbankinggroup.com" } },
            { "natwest", new[] { "natwest.com" } },
            { "halifax", new[] { "halifax.co.uk", "halifax-online.co.uk" } },
            { "nationwide", new[] { "nationwide.co.uk" } },
            { "royalmail", new[] { "royalmail.com" } },
            { "evri", new[] { "evri.com" } },
            { "hmrc", new[] { "gov.uk" } },
            { "dvla", new[] { "gov.uk" } },
            { "binance", new[] { "binance.com" } },
            { "coinbase", new[] { "coinbase.com" } },
            { "bitdefender", new[] { "bitdefender.com", "bitdefender.co.uk" } },
        };

        // banki i platnosci: tu wlacza sie tryb "bezpieczne platnosci"
        static readonly string[] PaymentDomains =
        {
            "paypal.com", "pkobp.pl", "ipko.pl", "mbank.pl", "santander.pl", "santander.co.uk", "pekao.com.pl", "pekao24.pl", "bankmillennium.pl",
            "ing.pl", "aliorbank.pl", "credit-agricole.pl", "revolut.com", "monzo.com", "starlingbank.com", "barclays.co.uk", "barclaycard.co.uk",
            "hsbc.co.uk", "lloydsbank.co.uk", "lloydsbank.com", "natwest.com", "halifax-online.co.uk", "halifax.co.uk", "nationwide.co.uk",
            "tsb.co.uk", "rbs.co.uk", "bankofscotland.co.uk", "virginmoney.com", "wise.com", "stripe.com", "przelewy24.pl", "payu.com",
            "tpay.com", "blik.com", "klarna.com", "checkout.com", "worldpay.com", "sumup.com", "squareup.com"
        };

        static readonly string[] TwoLevelSuffixes =
        {
            "co.uk", "org.uk", "gov.uk", "ac.uk", "me.uk", "ltd.uk", "plc.uk", "com.pl", "net.pl", "org.pl", "gov.pl", "edu.pl",
            "com.au", "net.au", "co.nz", "co.jp", "com.br", "co.in", "com.tr", "com.ua", "co.za", "com.mx", "com.ar", "com.cn"
        };

        static string ProtRegDomain(string host)
        {
            host = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
            var p = host.Split('.');
            if (p.Length <= 2) return host;
            var last2 = p[p.Length - 2] + "." + p[p.Length - 1];
            if (TwoLevelSuffixes.Contains(last2)) return p[p.Length - 3] + "." + last2;
            return last2;
        }

        static string SecondLevelLabel(string registrable)
        {
            var i = registrable.IndexOf('.');
            return i > 0 ? registrable.Substring(0, i) : registrable;
        }

        // 0->o, 1->l, rn->m, vv->w ... - typowe podmiany w podrobkach
        static string Deconfuse(string s)
        {
            s = s.ToLowerInvariant().Replace("rn", "m").Replace("vv", "w").Replace("cl", "d");
            var map = new Dictionary<char, char> { { '0', 'o' }, { '1', 'l' }, { '3', 'e' }, { '4', 'a' }, { '5', 's' }, { '7', 't' }, { '8', 'b' }, { '9', 'g' }, { 'i', 'l' } };
            return new string(s.Select(c => map.TryGetValue(c, out var r) ? r : c).ToArray());
        }

        static int EditDistance(string a, string b)
        {
            if (Math.Abs(a.Length - b.Length) > 2) return 99;
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }

        static readonly HashSet<string> OfficialDomains = new HashSet<string>(ProtectedBrands.Values.SelectMany(x => x), StringComparer.OrdinalIgnoreCase);

        // null = strona wyglada w porzadku; inaczej: co podrabia
        internal static string CheckLookalike(string host, IEnumerable<string> extraDomains)
        {
            host = (host ?? "").ToLowerInvariant().TrimEnd('.');
            if (host.Length == 0 || Uri.CheckHostName(host) != UriHostNameType.Dns) return null;
            var reg = ProtRegDomain(host);
            var official = OfficialDomains;
            var extra = (extraDomains ?? Enumerable.Empty<string>()).ToList();
            if (official.Contains(reg) || extra.Contains(reg, StringComparer.OrdinalIgnoreCase)) return null;

            // 1. adres z obcymi znakami udajacymi litery (xn--)
            if (host.Split('.').Any(l => l.StartsWith("xn--", StringComparison.Ordinal)))
                return "xn";

            // 2. marka w adresie, ale domena nie nalezy do tej firmy (paypal-secure-login.com, ebay.co.uk.konto-weryfikacja.top)
            var tokens = host.Split('.', '-').Where(t => t.Length > 0).ToList();
            foreach (var kv in ProtectedBrands)
            {
                var brand = kv.Key;
                bool hit = tokens.Any(t => t == brand || (brand.Length >= 6 && t.StartsWith(brand, StringComparison.Ordinal)));
                if (hit) return kv.Value[0];
            }

            // 3. literowka lub podmieniony znak w nazwie znanej strony (paypa1.com, rnbank.pl, arnazon.co.uk)
            var label = SecondLevelLabel(reg);
            if (label.Length >= 4)
            {
                foreach (var d in official.Concat(extra))
                {
                    var other = SecondLevelLabel(d);
                    if (other.Length < 4 || other == label) continue;
                    if (Deconfuse(label) == Deconfuse(other)) return d;
                    if (other.Length >= 6 && label.Length >= 5 && EditDistance(label, other) == 1) return d;
                }
            }
            return null;
        }
    }
}
