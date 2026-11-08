using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Windows.Threading;

namespace Velivo
{
    // 🎮 Velivo Pilot: telefon (sparowany przez synchronizację LAN – ten sam klucz, ta sama ochrona) steruje odtwarzaczem tego komputera.
    // Telefon wysyła „pilot” (polecenie albo „ping”), komputer odpowiada „pilot-st” (stan odtwarzacza) – wprost na adres telefonu,
    // zaszyfrowane kluczem grupy; dopóki telefon ma otwarty Pilot (ping co kilka sekund), stan idzie co sekundę i od razu po każdej zmianie.
    public partial class MainWindow
    {
        internal sealed class LanPilotCmd { public string to { get; set; } public string op { get; set; } public string v { get; set; } public double n { get; set; } }

        Func<object> _vlcPilotState;                    // stan odtwarzacza (null = odtwarzacz zamknięty) – ustawia VlcPlayer
        Action<string, string, double> _vlcPilotCmd;    // polecenie dla odtwarzacza – ustawia VlcPlayer
        readonly Dictionary<string, (IPEndPoint Ep, DateTime Seen)> _pilotClients = new Dictionary<string, (IPEndPoint, DateTime)>();
        DispatcherTimer _pilotTimer;

        void ReceivePilot(LanStatePacket pkt, LanPilotCmd cmd, IPEndPoint remote)
        {
            if (cmd == null || remote == null || (cmd.to != _lanId && cmd.to != "*")) return;
            _pilotClients[pkt.id] = (remote, DateTime.UtcNow);   // odpowiedź na ten sam adres i port, z którego telefon wysłał
            if (cmd.op != "ping" && cmd.to == _lanId)   // polecenie tylko do jednego, wybranego komputera
            {
                try { _vlcPilotCmd?.Invoke(cmd.op ?? "", cmd.v, cmd.n); } catch (Exception ex) { App.LogError(ex); }
                var t1 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                t1.Tick += (s, e) => { t1.Stop(); SendPilotState(pkt.id); };
                t1.Start();
            }
            SendPilotState(pkt.id);
            if (_pilotTimer == null)
            {
                _pilotTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _pilotTimer.Tick += (s, e) =>
                {
                    foreach (var k in _pilotClients.Where(c => DateTime.UtcNow - c.Value.Seen > TimeSpan.FromSeconds(30)).Select(c => c.Key).ToList()) _pilotClients.Remove(k);   // pilot zamknięty
                    if (_pilotClients.Count == 0) { _pilotTimer.Stop(); _pilotTimer = null; return; }
                    foreach (var k in _pilotClients.Keys.ToList()) SendPilotState(k);
                };
                _pilotTimer.Start();
            }
        }

        void SendPilotState(string phoneId)
        {
            if (_lanTx == null || _lanEncryptionKey == null || !_pilotClients.TryGetValue(phoneId, out var c)) return;
            object st = null;
            try { st = _vlcPilotState?.Invoke(); } catch (Exception) { }
            var payload = new Dictionary<string, object> { ["to"] = phoneId, ["st"] = st };
            var pkt = new LanStatePacket { t = "pilot-st", id = _lanId, device = _lanDeviceName, profile = SelectedProfileName, ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), hash = "" };
            try
            {
                EncryptLanBlob(pkt, JsonSerializer.SerializeToUtf8Bytes(payload));
                var bytes = JsonSerializer.SerializeToUtf8Bytes(pkt);
                _lanTx.Send(bytes, bytes.Length, c.Ep);
            }
            catch (Exception) { }
        }
    }
}
