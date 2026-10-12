using System;
using System.IO;
using System.Threading;
using LibVLCSharp.Shared;
using Xunit;

// Dodatek "odtwarzacz filmow": biblioteka VLC pobrana i rozpakowana jak w instalatorze (testy/odtwarzacz-vlc/przygotuj.py)
// naprawde odtwarza film AVI (bez okna i dzwieku). Uruchamiany tylko w CI, osobnym krokiem: --filter Category=Vlc.
public class VlcPlaybackTests
{
    [Fact, Trait("Category", "Vlc")]
    public void BibliotekaZInstalatoraOdtwarzaAvi()
    {
        var dir = Environment.GetEnvironmentVariable("VELIVO_VLC_DIR");
        var avi = Environment.GetEnvironmentVariable("VELIVO_VLC_AVI");
        Assert.True(File.Exists(Path.Combine(dir ?? "", "libvlc.dll")), "brak libvlc: " + dir);
        Core.Initialize(dir);
        using var vlc = new LibVLC("--vout=dummy", "--aout=dummy", "--no-video-title-show", "--no-lua", "--no-stats");
        using var mp = new MediaPlayer(vlc);
        using var media = new Media(vlc, avi, FromType.FromPath);
        var ended = new ManualResetEventSlim();
        bool error = false; long maxTime = 0;
        mp.EndReached += (s, e) => ended.Set();
        mp.EncounteredError += (s, e) => { error = true; ended.Set(); };
        mp.TimeChanged += (s, e) => maxTime = Math.Max(maxTime, e.Time);
        Assert.True(mp.Play(media));
        Assert.True(ended.Wait(TimeSpan.FromSeconds(30)), "film nie dobiegl konca w 30 s");
        Assert.False(error, "VLC zglosil blad odtwarzania");
        Assert.True(maxTime >= 1000, "odtworzono tylko " + maxTime + " ms z 2000");
    }
}
