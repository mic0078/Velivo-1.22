using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace VelivoProcesor
{
    // 🤖 Głosowanie (Ensemble) i przewidywanie (Prediction) - wnioski z CPU Manager v40, w najlżejszej postaci:
    // kilka „głosów” proponuje tryb, każdy ma trafność liczoną ze skutków (co naprawdę było potrzebne chwilę później),
    // wygrywa suma trafności; przy remisie cichszy, Turbo tylko przy wyraźnej przewadze. Same tablice liczb - bez kosztu.
    sealed partial class Silnik
    {
        const int GQ = 0, GProg = 1, GTrend = 2, GPora = 3, GLiczba = 4;
        static readonly string[] GNazwy = { "nauka skutków", "profil programu", "przewidywanie", "pora dnia" };
        readonly double[] trafnosc = { 0.6, 0.6, 0.5, 0.4 };
        readonly int[] ostGlos = { -1, -1, -1, -1 };
        // przewidywanie: ostatnie 30 s obciążenia + średnia dla godziny i dnia tygodnia
        readonly double[] bufor = new double[30]; int bufPos, bufN;
        readonly double[,] tydzien = new double[7, 24]; readonly int[,] tydzienN = new int[7, 24];
        static string GlosyFile => Path.Combine(Dir, "glosy.txt");
        bool glosyWczytane;

        double Przewiduj(double cpu)
        {   // za ok. 5 s: średnia z ostatnich 10 s + trend (nachylenie z 30 s)
            bufor[bufPos] = cpu; bufPos = (bufPos + 1) % bufor.Length; if (bufN < bufor.Length) bufN++;
            var d = DateTime.Now; int dt = (int)d.DayOfWeek, h = d.Hour; int n = Math.Min(++tydzienN[dt, h], 3600); tydzien[dt, h] += (cpu - tydzien[dt, h]) / n;
            if (bufN < 10) return cpu;
            double s10 = 0; for (int i = 1; i <= 10; i++) s10 += bufor[(bufPos - i + bufor.Length) % bufor.Length];
            double stare = bufor[(bufPos - bufN + bufor.Length) % bufor.Length];
            double nach = (bufor[(bufPos - 1 + bufor.Length) % bufor.Length] - stare) / Math.Max(1, bufN - 1);
            return Math.Max(0, Math.Min(100, s10 / 10 + nach * 5));
        }
        readonly double[] wynik = new double[3]; double ostPrz;   // bez nowych obiektów co sekundę
        // opis słowami - tylko gdy głosowanie decyduje
        string GlosOpis(string prog, Tryb w)
        {
            var za = "";
            for (int i = 0; i < GLiczba; i++) if (ostGlos[i] == (int)w) za += (za.Length > 0 ? ", " : "") + GNazwy[i] + " " + (trafnosc[i] * 100).ToString("0") + "%";
            return (string.IsNullOrEmpty(prog) ? "" : prog + " – ") + "głosowanie: " + Nazwa(w) + " (" + za + "; przewiduję " + ostPrz.ToString("0") + " %).";
        }
        int Prog(double cpu) => cpu > ProgWysoki ? 2 : cpu < ProgNiski ? 0 : 1;

        // głosowanie; null = żaden głos nie ma zdania
        Tryb? Glosuj(Pomiar p)
        {
            if (!glosyWczytane) { glosyWczytane = true; WczytajGlosy(); }
            // ⚖ trafność poprzednich głosów ze skutku: przycięcie = trzeba było Turbo, spokój = wystarczał Cichy, inaczej Zrównoważony
            bool lag = p.Cpu > 85 || p.ProgCpu > 90; int idealny = lag ? 2 : p.Cpu < ProgNiski ? 0 : 1;
            for (int i = 0; i < GLiczba; i++) if (ostGlos[i] >= 0) trafnosc[i] += ((ostGlos[i] == idealny ? 1 : 0) - trafnosc[i]) * 0.01;
            double prz = Przewiduj(p.Cpu);
            var d = DateTime.Now;
            ostGlos[GQ] = Oceniony(p.Program, out var q) ? (int)q : -1;
            ostGlos[GProg] = ZnanyProgram(p.Program, out var v) && v.N >= 30 ? (int)Wyuczony(v) : -1;
            ostGlos[GTrend] = bufN >= 10 ? Prog(prz) : -1;
            ostGlos[GPora] = tydzienN[(int)d.DayOfWeek, d.Hour] > 600 ? Prog(tydzien[(int)d.DayOfWeek, d.Hour]) : -1;
            Array.Clear(wynik, 0, 3); int glosow = 0, naTurbo = 0;
            for (int i = 0; i < GLiczba; i++) if (ostGlos[i] >= 0) { wynik[ostGlos[i]] += trafnosc[i]; glosow++; if (ostGlos[i] == 2) naTurbo++; }
            if (glosow < 2) return null;   // za mało wiedzy - decydują zwykłe zasady
            int w = wynik[0] >= wynik[1] && wynik[0] >= wynik[2] * 0.9 ? 0 : wynik[2] > wynik[1] && naTurbo >= 2 ? 2 : 1;   // przy remisie cichszy, Turbo co najmniej 2 głosy
            ostPrz = prz; return (Tryb)w;
        }

        void WczytajGlosy()
        {
            try
            {
                if (!File.Exists(GlosyFile)) return;
                foreach (var l in File.ReadAllLines(GlosyFile))
                {
                    var a = l.Split('\t');
                    if (a[0] == "W" && a.Length == GLiczba + 1) for (int i = 0; i < GLiczba; i++) trafnosc[i] = double.Parse(a[i + 1], Inv);
                    else if (a[0] == "D" && a.Length == 5) { int dt = int.Parse(a[1]), h = int.Parse(a[2]); tydzien[dt, h] = double.Parse(a[3], Inv); tydzienN[dt, h] = int.Parse(a[4]); }
                }
            }
            catch (Exception) { }
        }
        public void ZapiszGlosy()
        {
            try
            {
                var l = new System.Collections.Generic.List<string> { "W\t" + string.Join("\t", Array.ConvertAll(trafnosc, x => x.ToString("0.000", Inv))) };
                for (int dt = 0; dt < 7; dt++) for (int h = 0; h < 24; h++) if (tydzienN[dt, h] > 0) l.Add("D\t" + dt + "\t" + h + "\t" + tydzien[dt, h].ToString("0.0", Inv) + "\t" + tydzienN[dt, h]);
                File.WriteAllLines(GlosyFile, l);
            }
            catch (Exception) { }
        }

        // 🤖 dla okna „Jak myśli AI” - kopia stanu (czytana tylko, gdy okno otwarte)
        public sealed class AiStan { public string[] Nazwy; public double[] Traf; public int[] Glos; public double Teraz, Przew, Pora; public double[,] Tydz; public List<(string Prog, double[] Q, int N)> Programy; }
        public AiStan StanAi()
        {
            var d = DateTime.Now; var st = new AiStan { Nazwy = GNazwy, Traf = (double[])trafnosc.Clone(), Glos = (int[])ostGlos.Clone(), Przew = ostPrz, Tydz = (double[,])tydzien.Clone(),
                Teraz = bufN > 0 ? bufor[(bufPos - 1 + bufor.Length) % bufor.Length] : 0, Pora = tydzienN[(int)d.DayOfWeek, d.Hour] > 0 ? tydzien[(int)d.DayOfWeek, d.Hour] : -1 };
            lock (programy) st.Programy = ocena.Select(o => (o.Key, (double[])o.Value.Q.Clone(), o.Value.N)).OrderByDescending(x => x.N).ToList();
            return st;
        }
        public void ZapomnijOcene(string prog) { lock (programy) ocena.Remove(prog); ZapiszNauke(); }
        public void ZerujTrafnosc() { trafnosc[0] = trafnosc[1] = 0.6; trafnosc[2] = 0.5; trafnosc[3] = 0.4; ZapiszGlosy(); }
    }
}
