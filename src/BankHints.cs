using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Przegladarka
{
    // Tryb bankowy: propozycja pod klikniętym polem. Klikasz w login, haslo, wybrane znaki, numer karty albo numer konta
    // odbiorcy - jesli baza trybu ma pasujace dane dla tej strony, tuz pod polem pojawia sie wybor (jedno klikniecie wypelnia);
    // nic nie pasuje - nic sie nie pojawia. Strona przysyla tylko rodzaj pola i jego polozenie (bez wartosci); propozycja to
    // okienko Velivo, wiec strona nie widzi nazw Twoich kont.
    public partial class MainWindow
    {
        const string BankFieldHintScript = @"(function(){try{if(window.top!==window||!window.chrome||!chrome.webview)return;
var pm=chrome.webview.postMessage.bind(chrome.webview),last=null;
function kindOf(el){if(!el||!/^(INPUT|SELECT)$/.test(el.tagName)||el.disabled||el.readOnly)return null;var t=(el.type||'').toLowerCase();
if(el.tagName==='INPUT'&&!/^(text|email|tel|number|password|search|)$/.test(t))return null;
var a=((el.getAttribute('autocomplete')||'')+' '+(el.name||'')+' '+(el.id||'')+' '+(el.getAttribute('placeholder')||'')+' '+(el.getAttribute('aria-label')||'')).toLowerCase();
if(el.id){var l=document.querySelector('label[for='+JSON.stringify(el.id)+']');if(l)a+=' '+(l.textContent||'').toLowerCase();}
if(/one-time-code|\botp\b|sms/.test(a))return null;
if(/cc-|card|karty|cvv|cvc|expir/.test(a))return 'card';
if(el.tagName==='SELECT'||(el.maxLength>0&&el.maxLength<=2))return 'partial';
if(/sort.?code/.test(a))return null;
if(/iban|nrb|account.?(no|num)|accountnumber|numer.?(rachunku|konta)|rachun/.test(a)&&!/name|nazwa/.test(a))return 'transfer';
if(t==='password')return 'login';
if(/user|login|email|mail|klient|customer|ident|nik/.test(a))return 'login';
return null;}
document.addEventListener('focusin',function(e){var el=e.target,k=kindOf(el);if(!k||el===last)return;last=el;var r=el.getBoundingClientRect();
pm('velivo:__VT__:bfocus:'+JSON.stringify({k:k,x:r.left,y:r.top,w:r.width,h:r.height,filled:!!(el.value&&el.value.length)}));},true);
document.addEventListener('focusout',function(e){if(e.target===last){last=null;pm('velivo:__VT__:bblur:');}},true);
}catch(x){}})();";

        sealed class BankFocusMessage { public string k { get; set; } public double x { get; set; } public double y { get; set; } public double w { get; set; } public double h { get; set; } public bool filled { get; set; } }

        Popup _bankHint;
        DispatcherTimer _bankHintTimer;

        void HandleBankFocus(BrowserTab tab, string json)
        {
            try
            {
                CloseBankHint();
                if (tab == null || tab != _current || !tab.Bank || !_bankUnlocked || _bankKey == null) return;
                var core = tab.View.CoreWebView2; if (core == null) return;
                var m = JsonSerializer.Deserialize<BankFocusMessage>(json); if (m == null) return;
                var host = HostOf(core.Source); var c = LoadBank();
                if (host == null || c == null) return;
                var acts = new List<KeyValuePair<string, Action>>();
                switch (m.k)
                {
                    case "login":
                        if (m.filled) break;   // pole juz wypelnione (np. samo przy wejsciu) - bez propozycji
                        foreach (var l in LoginsFor(c, host).Take(5)) { var acc = l; acts.Add(new KeyValuePair<string, Action>("🔑 " + acc.Label, () => BankFillLogin(acc, host))); }
                        break;
                    case "partial":
                        string pin, pwd, mem; List<(string Label, string User, string Pass)> choices;
                        if (!PartialDataFor(c, host, out pin, out pwd, out mem, out choices)) break;
                        if (choices.Count == 0) acts.Add(new KeyValuePair<string, Action>(L.T("🔢 Wpisz wybrane znaki"), () => { _ = RunPartialFill(pin, pwd, mem, host); }));
                        else foreach (var a in choices.Take(5)) { var acc = a; acts.Add(new KeyValuePair<string, Action>("🔢 " + acc.Label, () => { _ = RunPartialFill(pin, acc.Pass, mem, host); })); }
                        break;
                    case "card":
                        foreach (var k in LoadCards(c).Take(5)) { var card = k; acts.Add(new KeyValuePair<string, Action>("💳 " + CardLine(card), () => { _ = BankFillCard(card, host); })); }
                        break;
                    case "transfer":
                        foreach (var a in LoadSealed<BankItem>(c.Accounts).Where(x => x.Get("number").Length > 0).Take(5))
                        { var acc = a; acts.Add(new KeyValuePair<string, Action>(L.T("💸 Przelew do: ") + acc.Get("name"), () => { _ = BankFillTransfer(acc, host); })); }
                        break;
                }
                if (acts.Count == 0) return;   // brak danych dla tej strony i pola - nic nie proponujemy
                ShowBankHint(tab, m, acts);
            }
            catch (Exception ex) { App.LogError(ex); }
        }

        void ShowBankHint(BrowserTab tab, BankFocusMessage m, List<KeyValuePair<string, Action>> acts)
        {
            var panel = new StackPanel();
            foreach (var a in acts)
            {
                var act = a.Value;
                var b = new Button { Content = a.Key, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(2), MinWidth = 200 };
                b.Click += (s, e) => { CloseBankHint(); if (_current == tab && _tabs.Contains(tab)) act(); };
                panel.Children.Add(b);
            }
            double zoom = tab.View.ZoomFactor > 0 ? tab.View.ZoomFactor : 1;
            var p = tab.View.PointToScreen(new Point(Math.Max(0, m.x) * zoom, (Math.Max(0, m.y) + Math.Max(0, m.h)) * zoom + 2));
            var src = PresentationSource.FromVisual(this);
            if (src != null && src.CompositionTarget != null) p = src.CompositionTarget.TransformFromDevice.Transform(p);
            _bankHint = new Popup
            {
                Placement = PlacementMode.Absolute, HorizontalOffset = p.X, VerticalOffset = p.Y, StaysOpen = true, AllowsTransparency = true,
                Child = new Border
                {
                    Child = panel, Padding = new Thickness(4), CornerRadius = new CornerRadius(6), Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), BorderThickness = new Thickness(1)
                }
            };
            _bankHint.IsOpen = true;
            if (_bankHintTimer == null)
            {
                _bankHintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                _bankHintTimer.Tick += (s, e) => CloseBankHint();
            }
            _bankHintTimer.Stop(); _bankHintTimer.Start();
        }

        // pole stracilo fokus - chwila zwloki, zeby klikniecie w propozycje zdazylo zadzialac
        void BankFieldBlur()
        {
            var hint = _bankHint; if (hint == null) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            t.Tick += (s, e) => { t.Stop(); if (_bankHint == hint && !(hint.Child != null && hint.Child.IsMouseOver)) CloseBankHint(); };
            t.Start();
        }

        void CloseBankHint()
        {
            if (_bankHintTimer != null) _bankHintTimer.Stop();
            if (_bankHint != null) { _bankHint.IsOpen = false; _bankHint = null; }
        }
    }
}
