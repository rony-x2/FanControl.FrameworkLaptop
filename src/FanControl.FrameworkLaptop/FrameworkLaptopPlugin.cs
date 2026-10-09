using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using FanControl.Plugins;

namespace FanControl.FrameworkLaptop
{
    /// <summary>
    /// FanControl-Plugin für Framework-Laptops (13, 16, 12) und den Framework Desktop.
    ///
    /// Liefert:
    ///  - alle Temperaturfühler des EC als Temperatursensoren
    ///  - die Drehzahl jedes Lüfters
    ///  - eine Steuerung je Lüfter (Duty 0-100 %), automatisch mit dem passenden Drehzahlsensor gekoppelt
    ///
    /// Zugriff auf den EC:
    ///  1. direkt über den CrosEC-Treiber von Framework (DeviceIoControl, kein externer Prozess)
    ///  2. falls der Treiber nicht erreichbar ist: über das offizielle "framework_tool"
    ///
    /// Ein eigener Hintergrund-Thread liest die Werte einmal pro Sekunde und schickt geänderte
    /// Duty-Werte an den EC, damit FanControl selbst nie auf den EC warten muss.
    /// </summary>
    public sealed class FrameworkLaptopPlugin : IPlugin3
    {
        private const string IdPrefix = "FrameworkLaptop";
        private const int ReadIntervalMs = 1000;
        private const int RetryIntervalMs = 10000;
        private const int ReapplyIntervalMs = 30000; // EC fällt z. B. nach Standby auf Auto zurück
        private const int FailuresBeforeBlank = 3;

        private static bool _dialogShown; // Dialog höchstens einmal pro FanControl-Sitzung

        private readonly IPluginLogger _logger;
        private readonly IPluginDialog _dialog;

        private readonly object _lock = new object();
        private readonly Dictionary<int, int?> _desired = new Dictionary<int, int?>();      // null = Auto
        private readonly Dictionary<int, int?> _applied = new Dictionary<int, int?>();      // null = Auto
        private readonly Dictionary<int, long> _appliedAt = new Dictionary<int, long>();
        private readonly HashSet<int> _touched = new HashSet<int>();                         // Lüfter, die wir je gesteuert haben
        private readonly Dictionary<int, long> _retryAfter = new Dictionary<int, long>();   // nach Fehler kurz warten

        private IEcBackend _tool;
        private string _model = "";
        private ThermalSnapshot _initial;
        private readonly Dictionary<string, FrameworkSensor> _tempSensors = new Dictionary<string, FrameworkSensor>();
        private readonly Dictionary<int, FrameworkSensor> _fanSensors = new Dictionary<int, FrameworkSensor>();
        private string _loadedFanKey = "";

        private Thread _worker;
        private CancellationTokenSource _cts;
        private AutoResetEvent _wake;
        private readonly Stopwatch _clock = new Stopwatch();
        private string _lastError;
        private int _consecutiveFailures;
        private bool _refreshRequested;

        public FrameworkLaptopPlugin(IPluginLogger logger, IPluginDialog dialog)
        {
            _logger = logger;
            _dialog = dialog;
        }

        public string Name => "Framework Laptop";

        public event Action RefreshRequested;

        // ------------------------------------------------------------------ Lebenszyklus

        public void Initialize()
        {
            ResetState();
            _clock.Restart();

            _model = FanNames.DetectModel();
            if (_model.Length > 0)
                Log("Gerät: " + _model);

            _tool = SelectBackend(out var problems);
            if (_tool == null)
            {
                Fail("Kein Zugriff auf den Embedded Controller.\n\n" + string.Join("\n\n", problems) +
                     "\n\nBitte das Treiberpaket und das aktuelle BIOS von Framework installieren " +
                     "und FanControl als Administrator starten.", showDialog: true);
                return;
            }

            try
            {
                _initial = _tool.ReadThermal();
                Log($"Erkannt: {_initial.Temps.Count} Temperaturfühler, {_initial.Fans.Count} Lüfter.");
                if (_initial.Fans.Count == 0)
                    Log("Hinweis: Der EC meldet keine Lüfter. Für den Framework 16 ist mindestens BIOS 3.07 nötig.");
            }
            catch (Exception ex)
            {
                _initial = null;
                Fail("Lesen der Sensoren ist fehlgeschlagen: " + ex.Message, showDialog: true);
            }

            StartWorker();
        }

        public void Load(IPluginSensorsContainer container)
        {
            if (_tool == null || _initial == null)
                return; // Worker versucht es weiter und fordert bei Erfolg eine Neuladung an

            foreach (var t in _initial.Temps)
            {
                var s = new FrameworkSensor($"{IdPrefix}/Temp/{t.Name}", "Framework " + t.Name);
                s.Publish(t.Temperature);
                s.Update();
                _tempSensors[t.Name] = s;
                container.TempSensors.Add(s);
            }

            foreach (var f in _initial.Fans)
            {
                var label = FanNames.Label(f.Index, _initial.Fans.Count, _model);
                var fanSensor = new FrameworkSensor($"{IdPrefix}/Fan/{f.Index}", label);
                fanSensor.Publish(f.Rpm);
                fanSensor.Update();
                _fanSensors[f.Index] = fanSensor;
                container.FanSensors.Add(fanSensor);

                var control = new FrameworkControl(this, f.Index, $"{IdPrefix}/Control/{f.Index}", label, fanSensor.Id);
                container.ControlSensors.Add(control);
            }

            _loadedFanKey = FanKey(_initial);
        }

        public void Update()
        {
            // Nur zwischengespeicherte Werte übernehmen; das eigentliche Lesen macht der Worker.
            foreach (var s in _tempSensors.Values) s.Update();
            foreach (var s in _fanSensors.Values) s.Update();
        }

        public void Close()
        {
            StopWorker();

            // Alle Lüfter, die wir gesteuert haben, an den EC zurückgeben
            List<int> touched;
            lock (_lock) touched = _touched.ToList();

            if (_tool != null && touched.Count > 0)
            {
                try
                {
                    _tool.AutoFanControl(null);
                    Log("Lüfter an die automatische Regelung des EC zurückgegeben.");
                }
                catch (Exception ex)
                {
                    Log("Rückgabe an die automatische Regelung fehlgeschlagen: " + ex.Message);
                }
            }

            ResetState();
        }

        // ------------------------------------------------------------------ Aufrufe der Steuerungen

        internal void RequestDuty(int fanIndex, float percent)
        {
            int duty = (int)Math.Round(percent, MidpointRounding.AwayFromZero);
            lock (_lock)
            {
                if (_desired.TryGetValue(fanIndex, out var cur) && cur == duty)
                    return;
                _desired[fanIndex] = duty;
            }
            _wake?.Set();
        }

        internal void RequestAuto(int fanIndex)
        {
            lock (_lock)
            {
                if (_desired.TryGetValue(fanIndex, out var cur) && cur == null)
                    return;
                _desired[fanIndex] = null;
            }
            _wake?.Set();
        }

        // ------------------------------------------------------------------ Hintergrund-Thread

        private void StartWorker()
        {
            _cts = new CancellationTokenSource();
            _wake = new AutoResetEvent(false);
            var token = _cts.Token;
            _worker = new Thread(() => WorkerLoop(token))
            {
                IsBackground = true,
                Name = "FanControl.FrameworkLaptop"
            };
            _worker.Start();
        }

        private void StopWorker()
        {
            if (_worker == null) return;
            try
            {
                _cts.Cancel();
                _wake.Set();
                if (!_worker.Join(TimeSpan.FromSeconds(12)))
                    Log("Hintergrund-Thread hat sich nicht rechtzeitig beendet.");
            }
            catch { }
            finally
            {
                _worker = null;
                _cts.Dispose();
                _cts = null;
                _wake.Dispose();
                _wake = null;
            }
        }

        private void WorkerLoop(CancellationToken token)
        {
            long lastRead = _initial != null ? _clock.ElapsedMilliseconds : -RetryIntervalMs;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_tool != null)
                    {
                        ApplyPending(token);

                        long now = _clock.ElapsedMilliseconds;
                        int interval = _initial == null ? RetryIntervalMs : ReadIntervalMs;
                        if (now - lastRead >= interval)
                        {
                            lastRead = now;
                            ReadOnce();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Fail("Unerwarteter Fehler: " + ex.Message, showDialog: false);
                }

                try { _wake.WaitOne(250); } catch (ObjectDisposedException) { return; }
            }
        }

        private void ReadOnce()
        {
            ThermalSnapshot snap;
            try
            {
                snap = _tool.ReadThermal();
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;
                Fail("Lesen fehlgeschlagen: " + ex.Message, showDialog: false);
                if (_consecutiveFailures >= FailuresBeforeBlank)
                {
                    foreach (var s in _tempSensors.Values) s.Publish(null);
                    foreach (var s in _fanSensors.Values) s.Publish(null);
                }
                return;
            }

            if (_consecutiveFailures > 0 || _lastError != null)
            {
                Log("EC antwortet wieder.");
                _lastError = null;
            }
            _consecutiveFailures = 0;

            // Erststart war fehlgeschlagen oder Lüfteranzahl hat sich geändert: FanControl neu laden lassen
            if (_initial == null || FanKey(snap) != _loadedFanKey || snap.Temps.Any(t => !_tempSensors.ContainsKey(t.Name)))
            {
                if (!_refreshRequested && (snap.Fans.Count > 0 || snap.Temps.Count > 0))
                {
                    _refreshRequested = true;
                    Log("Sensoren haben sich geändert, FanControl lädt das Plugin neu.");
                    try { RefreshRequested?.Invoke(); } catch { }
                }
                if (_initial == null) return;
            }

            foreach (var t in snap.Temps)
                if (_tempSensors.TryGetValue(t.Name, out var s))
                    s.Publish(t.Temperature);

            foreach (var f in snap.Fans)
                if (_fanSensors.TryGetValue(f.Index, out var fs))
                    fs.Publish(f.Rpm);
        }

        private void ApplyPending(CancellationToken token)
        {
            List<KeyValuePair<int, int?>> work;
            long now = _clock.ElapsedMilliseconds;

            lock (_lock)
            {
                work = _desired
                    .Where(kv =>
                    {
                        if (_retryAfter.TryGetValue(kv.Key, out var retry) && now < retry) return false;
                        bool known = _applied.TryGetValue(kv.Key, out var applied);
                        if (!known || applied != kv.Value) return true;
                        // Duty regelmäßig erneut senden, Auto nur einmal
                        return kv.Value.HasValue && now - _appliedAt[kv.Key] >= ReapplyIntervalMs;
                    })
                    .ToList();
            }

            foreach (var kv in work)
            {
                if (token.IsCancellationRequested) return;
                int fan = kv.Key;
                try
                {
                    if (kv.Value.HasValue)
                    {
                        _tool.SetFanDuty(fan, kv.Value.Value);
                        lock (_lock) _touched.Add(fan);
                    }
                    else
                    {
                        try
                        {
                            _tool.AutoFanControl(fan);
                        }
                        catch (AllFansResetException)
                        {
                            // EC/framework_tool kennt keinen Lüfter-Index: alle wurden zurückgesetzt,
                            // die übrigen Lüfter beim nächsten Durchlauf neu setzen.
                            lock (_lock)
                                foreach (var k in _applied.Keys.ToList())
                                    if (k != fan) _applied.Remove(k);
                        }
                    }

                    lock (_lock)
                    {
                        _applied[fan] = kv.Value;
                        _appliedAt[fan] = _clock.ElapsedMilliseconds;
                        _retryAfter.Remove(fan);
                    }
                }
                catch (Exception ex)
                {
                    Fail($"Lüfter {fan + 1}: Setzen fehlgeschlagen: {ex.Message}", showDialog: false);
                    lock (_lock) _retryAfter[fan] = _clock.ElapsedMilliseconds + 5000; // nicht im Takt wiederholen
                }
            }
        }

        // ------------------------------------------------------------------ Hilfsfunktionen

        /// <summary>Treiber zuerst, framework_tool als Rückfall. Ein Weg gilt erst als gefunden, wenn er Sensoren lesen kann.</summary>
        private IEcBackend SelectBackend(out List<string> problems)
        {
            problems = new List<string>();

            if (BackendFactoryForTests != null)
                return BackendFactoryForTests();

            var transport = WindowsCrosEcTransport.TryOpen(out var openError);
            if (transport != null)
            {
                var backend = new CrosEcBackend(transport);
                try
                {
                    backend.ReadThermal();
                    Log("Zugriff über " + backend.Description + ".");
                    return backend;
                }
                catch (Exception ex)
                {
                    backend.Dispose();
                    problems.Add("CrosEC-Treiber geöffnet, aber Lesen fehlgeschlagen: " + ex.Message);
                }
            }
            else
            {
                problems.Add($"CrosEC-Treiber ({WindowsCrosEcTransport.DevicePath}) nicht verfügbar: {openError}");
            }

            var path = FrameworkTool.Locate(out var searched);
            if (path == null)
            {
                problems.Add("framework_tool.exe als Rückfall ebenfalls nicht gefunden.");
                return null;
            }

            var tool = new FrameworkTool(path);
            try
            {
                tool.ReadThermal();
                Log(problems[0]);
                Log("Rückfall: Zugriff über " + tool.Description + ".");
                return tool;
            }
            catch (Exception ex)
            {
                problems.Add("framework_tool gefunden (" + path + "), aber --thermal fehlgeschlagen: " + ex.Message);
                return null;
            }
        }

        /// <summary>Nur für Tests: ersetzt die Auswahl des Zugriffswegs.</summary>
        internal static Func<IEcBackend> BackendFactoryForTests;

        private static string FanKey(ThermalSnapshot snap)
            => string.Join(",", snap.Fans.Select(f => f.Index));

        private void ResetState()
        {
            lock (_lock)
            {
                _desired.Clear();
                _applied.Clear();
                _appliedAt.Clear();
                _touched.Clear();
                _retryAfter.Clear();
            }
            _tempSensors.Clear();
            _fanSensors.Clear();
            _loadedFanKey = "";
            _initial = null;
            try { _tool?.Dispose(); } catch { }
            _tool = null;
            _lastError = null;
            _consecutiveFailures = 0;
            _refreshRequested = false;
        }

        private void Log(string message)
        {
            try { _logger?.Log("[Framework Laptop] " + message); } catch { }
        }

        private void Fail(string message, bool showDialog)
        {
            // Gleiche Fehlermeldung nicht jede Sekunde ins Log schreiben
            if (message == _lastError) return;
            _lastError = message;
            Log(message);

            if (showDialog && !_dialogShown && _dialog != null)
            {
                _dialogShown = true;
                try { _dialog.ShowMessageDialog("Plugin „Framework Laptop“\n\n" + message); } catch { }
            }
        }
    }
}
