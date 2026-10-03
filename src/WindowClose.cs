using System;
using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Przegladarka
{
    public partial class MainWindow
    {
        internal static string DataFolder { get { return DataDir; } }

        // Pewniejsza ochrona: jesli zamkniecie calego okna przyszlo z wewnetrznej obslugi kontrolki WebView2
        // (prosba strony o zamkniecie), anulujemy je - karte zamyka nasza obsluga WindowCloseRequested.
        void GuardAgainstWebViewClosingWindow(object sender, System.ComponentModel.CancelEventArgs e)
        {
            var frames = new System.Diagnostics.StackTrace().GetFrames();
            foreach (var f in frames)
            {
                var m = f.GetMethod();
                if (m != null && m.Name == "CoreWebView2_WindowCloseRequested") { e.Cancel = true; return; }
            }
        }

        // Kontrolka WPF WebView2 sama podpina sie pod CoreWebView2.WindowCloseRequested i zamyka okno,
        // w ktorym lezy - czyli cale Velivo. Odpinamy jej (prywatna) obsluge; karte zamykamy sami.
        static void DetachDefaultWindowClose(WebView2 view)
        {
            try
            {
                var core = view.CoreWebView2;
                for (var t = view.GetType(); t != null; t = t.BaseType)
                {
                    var mi = t.GetMethod("CoreWebView2_WindowCloseRequested", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (mi == null) continue;
                    var handler = (EventHandler<object>)Delegate.CreateDelegate(typeof(EventHandler<object>), view, mi);
                    core.WindowCloseRequested -= handler;
                    return;
                }
            }
            catch (Exception) { }
        }
    }
}
