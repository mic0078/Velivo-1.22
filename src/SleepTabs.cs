using System;
using System.Linq;
using System.Windows.Threading;

namespace Przegladarka
{
    // Usypianie nieuzywanych kart (jak w Edge, Chrome i Safari): karta, ktorej dawno nie ogladasz, przestaje zajmowac
    // procesor i pamiec; po kliknieciu budzi sie od razu. Nie zasypia: ogladana, przypieta ani grajaca (muzyka, film,
    // czytanie na glos). Ustawienia -> Karty; domyslnie wylaczone.
    public partial class MainWindow
    {
        DispatcherTimer _sleepTimer;

        internal static bool ShouldSleepTab(int afterMin, double idleMin, bool current, bool pinned, bool playing)
        {
            return afterMin > 0 && idleMin >= afterMin && !current && !pinned && !playing;
        }

        void StartSleepTabs()
        {
            _sleepTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _sleepTimer.Tick += async (s, e) =>
            {
                foreach (var t in _tabs.ToList())
                {
                    var core = t.View.CoreWebView2;
                    if (core == null || core.IsSuspended) continue;
                    bool playing = core.IsDocumentPlayingAudio || t == _readTab;
                    if (!ShouldSleepTab(_settings.SleepTabsMin, (DateTime.UtcNow - t.LastShown).TotalMinutes, IsOnScreen(t), t.Pinned, playing)) continue;
                    try { await core.TrySuspendAsync(); } catch (Exception) { }   // silnik odmawia np. widocznej karcie - wtedy zostaje
                }
            };
            _sleepTimer.Start();
        }

        // wybrana karta: budzimy ja (silnik i tak budzi sie sam przy pokazaniu) i liczymy czas od nowa
        void WakeTab(BrowserTab tab)
        {
            tab.LastShown = DateTime.UtcNow;
            try { if (tab.View.CoreWebView2 != null && tab.View.CoreWebView2.IsSuspended) tab.View.CoreWebView2.Resume(); } catch (Exception) { }
        }
    }
}
