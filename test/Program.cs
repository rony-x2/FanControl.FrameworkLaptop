using FanControl.Plugins;
using FanControl.FrameworkLaptop;

int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FEHLER ") + what); if (!ok) fails++; }

// 1. Parser gegen bekannte Ausgaben (aus den Tests von framework-control)
var p = ThermalParser.Parse("  F75303_Local: 40 C\n  APU:          39 C\n  dGPU temp:    NotPowered\n  Fan Speed:  2165 RPM\n  Fan Speed:  2035 RPM\n  AP Throttle Status\n    Soft (AMD SPPT):  false\n");
Check(p.Temps.Count == 3 && p.Temps[2].Temperature == null, "Parser: 3 Fühler, NotPowered = null");
Check(p.Fans.Count == 2 && p.Fans[0].Rpm == 2165 && p.Fans[1].Index == 1, "Parser: 2 Lüfter mit Index");
var st = ThermalParser.Parse("  Left Fan:  65534 RPM (Stalled)\n  Right Fan:  1300 RPM\r\n");
Check(st.Fans.Count == 2 && st.Fans[0].Rpm == 0 && st.Fans[1].Rpm == 1300, "Parser: Stalled = 0, CRLF");
var one = ThermalParser.Parse("  APU:            62 C\n  Fan Speed:    3171 RPM\n");
Check(one.Fans.Count == 1 && one.Temps[0].Temperature == 62, "Parser: Laptop 13 mit einem Lüfter");

// 2. Plugin-Lebenszyklus mit simuliertem framework_tool
var dir = AppContext.GetData("APP_CONTEXT_BASE_DIRECTORY") as string;
var fake = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "fake_framework_tool.sh"));
File.Delete("calls.log"); File.Delete("state");
Environment.SetEnvironmentVariable("FRAMEWORK_TOOL_PATH", fake);

var log = new Logger(); var dlg = new Dialog();
var plugin = new FrameworkLaptopPlugin(log, dlg);
var c = new Container();
plugin.Initialize(); plugin.Load(c);
Check(c.TempSensors.Count == 6 && c.FanSensors.Count == 2 && c.ControlSensors.Count == 2, $"Load: {c.TempSensors.Count} Temp, {c.FanSensors.Count} Fan, {c.ControlSensors.Count} Control");
plugin.Update();
Check(c.TempSensors.First(s => s.Name == "Framework APU").Value == 61, "Wert APU = 61");
Check(c.FanSensors[0].Value == 2100, "Drehzahl Lüfter 1 = 2100 (Auto)");
var ctl = (IPluginControlSensor2)c.ControlSensors[1];
Check(ctl.PairedFanSensorId == c.FanSensors[1].Id && ctl.Name == "Framework Lüfter 2", "Steuerung gekoppelt: " + ctl.PairedFanSensorId);

// Set wiederholt mit gleichem Wert -> nur ein Aufruf
for (int i = 0; i < 5; i++) { c.ControlSensors[0].Set(40.4f); c.ControlSensors[1].Set(70f); Thread.Sleep(50); }
Thread.Sleep(1600); plugin.Update();
var calls = File.ReadAllLines("calls.log").Select(l => l.Substring(l.IndexOf(' ') + 1)).ToList();
Check(calls.Count(x => x == "--fansetduty 0 40") == 1 && calls.Count(x => x == "--fansetduty 1 70") == 1, "Duty genau einmal gesendet (Lüfter 1: 40 %, Lüfter 2: 70 %)");
Check(c.FanSensors[0].Value == 2200 && c.FanSensors[1].Value == 3850, $"Drehzahl folgt: {c.FanSensors[0].Value} / {c.FanSensors[1].Value}");

c.ControlSensors[1].Reset(); Thread.Sleep(600);
calls = File.ReadAllLines("calls.log").Select(l => l.Substring(l.IndexOf(' ') + 1)).ToList();
Check(calls.Contains("--autofanctrl 1"), "Reset -> --autofanctrl 1");

var sw = System.Diagnostics.Stopwatch.StartNew();
for (int i = 0; i < 100; i++) plugin.Update();
Check(sw.ElapsedMilliseconds < 50, $"Update() blockiert nicht ({sw.ElapsedMilliseconds} ms für 100 Aufrufe)");

plugin.Close();
calls = File.ReadAllLines("calls.log").Select(l => l.Substring(l.IndexOf(' ') + 1)).ToList();
Check(calls.Last() == "--autofanctrl", "Close -> --autofanctrl (alle Lüfter zurück an EC)");

// 3. Neustart nach Close (Initialize/Load wiederverwendbar)
var c2 = new Container(); plugin.Initialize(); plugin.Load(c2);
Check(c2.ControlSensors.Count == 2, "Erneutes Initialize/Load funktioniert"); plugin.Close();

// 4. framework_tool fehlt
Environment.SetEnvironmentVariable("FRAMEWORK_TOOL_PATH", "/nicht/vorhanden/framework_tool.exe");
var c3 = new Container(); var p3 = new FrameworkLaptopPlugin(log, dlg); p3.Initialize(); p3.Load(c3); p3.Update(); p3.Close();
Check(c3.TempSensors.Count == 0 && dlg.Messages.Count == 1, "Ohne framework_tool: keine Sensoren, ein Hinweisdialog");


Check(log.Lines.Any(l => l.Contains("Rückfall: Zugriff über framework_tool")), "Ohne Treiber: Rückfall auf framework_tool");

// 5. Protokoll des CrosEC-Treibers
Check(CrosEcProtocol.IoctlXcmd == 0x80ECE004 && CrosEcProtocol.IoctlRdmem == 0x80EC6008, $"IOCTL-Codes 0x{CrosEcProtocol.IoctlXcmd:X8} / 0x{CrosEcProtocol.IoctlRdmem:X8}");
var cb = CrosEcProtocol.BuildCommand(0x24, 1, new byte[] { 40, 0, 0, 0, 1, 0, 0, 0 });
Check(cb.Length == 268 && BitConverter.ToUInt32(cb, 0) == 1 && BitConverter.ToUInt32(cb, 4) == 0x24 && BitConverter.ToUInt32(cb, 8) == 8 && BitConverter.ToUInt32(cb, 12) == 248 && cb[20] == 40 && cb[24] == 1,
      "XCMD-Puffer: 268 Byte, Header version/command/outsize/insize, Daten ab Byte 20 (wie framework_tool)");
var rb = CrosEcProtocol.BuildReadMem(0x10, 8);
Check(rb.Length == 264 && BitConverter.ToUInt32(rb, 0) == 0x10 && BitConverter.ToUInt32(rb, 4) == 8, "RDMEM-Puffer: 264 Byte, offset/bytes");

// 6. Plugin über simulierten Treiber
var drv = new FakeDriver();
FrameworkLaptopPlugin.BackendFactoryForTests = () => new CrosEcBackend(drv);
var log2 = new Logger(); var c4 = new Container();
var p4 = new FrameworkLaptopPlugin(log2, new Dialog());
p4.Initialize(); p4.Load(c4); p4.Update();
Check(string.Join("|", c4.TempSensors.Select(s => s.Name)) == "Framework F75303_Local|Framework F75303_CPU|Framework dGPU temp|Framework APU", "Treiber: Fühlernamen vom EC: " + string.Join(", ", c4.TempSensors.Select(s => s.Name)));
Check(c4.TempSensors[0].Value == 40 && c4.TempSensors[2].Value == null && c4.TempSensors[3].Value == 61, "Treiber: Temperaturen 40 / NotPowered / 61 °C");
Check(c4.FanSensors.Count == 2 && c4.FanSensors[0].Value == 2100 && c4.FanSensors[1].Value == 2000, "Treiber: 2 Lüfter, 2100 / 2000 rpm");
c4.ControlSensors[0].Set(30); c4.ControlSensors[1].Set(80); Thread.Sleep(1300); p4.Update();
Check(drv.Duty[0] == 30 && drv.Duty[1] == 80 && drv.Calls.Count(x => x == "duty v1 fan1 80") == 1, "Treiber: PwmSetFanDuty v1 je Lüfter, einmalig");
Check(c4.FanSensors[1].Value == 4400, "Treiber: Drehzahl folgt (" + c4.FanSensors[1].Value + ")");
c4.ControlSensors[0].Reset(); Thread.Sleep(500);
Check(drv.Calls.Contains("auto v1 fan0") && drv.Duty[0] == null && drv.Duty[1] == 80, "Treiber: Reset -> AutoFanCtrl v1 nur Lüfter 1");
p4.Close();
Check(drv.Calls.Last() == "auto v0" && drv.Disposed, "Treiber: Close -> AutoFanCtrl v0, Handle geschlossen");

// 7. Alte EC-Firmware ohne Lüfter-Index bei AutoFanCtrl
var old = new FakeDriver { NoAutoV1 = true };
FrameworkLaptopPlugin.BackendFactoryForTests = () => new CrosEcBackend(old);
var c5 = new Container(); var p5 = new FrameworkLaptopPlugin(new Logger(), new Dialog());
p5.Initialize(); p5.Load(c5);
c5.ControlSensors[0].Set(50); c5.ControlSensors[1].Set(60); Thread.Sleep(600);
c5.ControlSensors[0].Reset(); Thread.Sleep(800);
Check(old.Duty[0] == null && old.Duty[1] == 60, $"Alte Firmware: Lüfter 2 nach globalem Auto wieder auf 60 % (ist {old.Duty[1]})");
p5.Close();
FrameworkLaptopPlugin.BackendFactoryForTests = null;

// 8. Lüfternamen je Modell
Check(FanNames.Label(0, 2, "Laptop 16 (AMD Ryzen 7040 Series)") == "Framework Lüfter links" && FanNames.Label(1, 2, "Laptop 16") == "Framework Lüfter rechts"
      && FanNames.Label(0, 1, "Laptop 13 (AMD Ryzen AI 300 Series)") == "Framework APU-Lüfter" && FanNames.Label(1, 3, "Desktop (AMD Ryzen AI Max 300 Series)") == "Framework Front-Lüfter"
      && FanNames.Label(1, 2, "") == "Framework Lüfter 2", "Lüfternamen für 16 / 13 / Desktop / unbekannt");

Console.WriteLine("\n--- Log Treiber ---"); log2.Lines.ForEach(Console.WriteLine);
Console.WriteLine("\n--- Log ---"); log.Lines.ForEach(Console.WriteLine);
Console.WriteLine("--- Dialog ---\n" + string.Join("\n", dlg.Messages));
Console.WriteLine(fails == 0 ? "\nALLE TESTS OK" : $"\n{fails} FEHLER");
return fails;

/// Simuliert den CrosEC-Treiber auf Pufferebene (wie DeviceIoControl mit METHOD_BUFFERED)
class FakeDriver : ICrosEcTransport {
  public int?[] Duty = new int?[4]; public List<string> Calls = new(); public bool Disposed; public bool NoAutoV1;
  string[] names = { "F75303_Local", "F75303_CPU", "dGPU temp", "APU" };
  byte[] Memmap() {
    var m = Enumerable.Repeat((byte)0xFF, 0xFF).ToArray();
    m[0] = 40 + 73; m[1] = 38 + 73; m[2] = 0xFD; m[3] = 61 + 73;
    for (int i = 0; i < 4; i++) { ushort r = i >= 2 ? (ushort)0xFFFF : (ushort)(Duty[i] is int d ? d * 55 : (i == 0 ? 2100 : 2000)); m[0x10 + i * 2] = (byte)r; m[0x11 + i * 2] = (byte)(r >> 8); }
    return m;
  }
  public byte[] ReadMemory(int offset, int length) {
    var buf = CrosEcProtocol.BuildReadMem(offset, length);
    int off = (int)BitConverter.ToUInt32(buf, 0), n = (int)BitConverter.ToUInt32(buf, 4);
    if (buf.Length < 263 || off + n > 0xFF) throw new Exception("STATUS_INVALID_ADDRESS");
    Buffer.BlockCopy(Memmap(), off, buf, 8, n);
    return CrosEcProtocol.ParseReadMem(buf, length);
  }
  public byte[] Command(ushort command, byte version, byte[] request) {
    var buf = CrosEcProtocol.BuildCommand(command, version, request);
    uint ver = BitConverter.ToUInt32(buf, 0), cmd = BitConverter.ToUInt32(buf, 4), outsize = BitConverter.ToUInt32(buf, 8), insize = BitConverter.ToUInt32(buf, 12);
    if (buf.Length > 20 + 248 || buf.Length < 20 + outsize || buf.Length < 20 + insize) throw new Exception("STATUS_BUFFER_OVERFLOW/TOO_SMALL");
    var data = buf.Skip(20).Take((int)outsize).ToArray();
    int resLen = 0; uint result = 0;
    switch (cmd) {
      case 0x70: { var nm = System.Text.Encoding.ASCII.GetBytes(names[data[0]]); Array.Clear(buf, 20, 33); Buffer.BlockCopy(nm, 0, buf, 20, nm.Length); resLen = 33; break; }
      case 0x24 when ver == 1: { int pct = BitConverter.ToInt32(data, 0), fan = BitConverter.ToInt32(data, 4); Duty[fan] = pct; Calls.Add($"duty v1 fan{fan} {pct}"); break; }
      case 0x24: { int pct = BitConverter.ToInt32(data, 0); Duty[0] = Duty[1] = pct; Calls.Add($"duty v0 {pct}"); break; }
      case 0x52 when ver == 1: if (NoAutoV1) { result = 6; break; } Duty[data[0]] = null; Calls.Add($"auto v1 fan{data[0]}"); break;
      case 0x52: Duty[0] = Duty[1] = null; Calls.Add("auto v0"); break;
      default: result = 1; break;
    }
    BitConverter.GetBytes(result).CopyTo(buf, 16);
    return CrosEcProtocol.ParseCommandResponse(command, buf, 20 + resLen);
  }
  public void Dispose() => Disposed = true;
}

class Logger : IPluginLogger { public List<string> Lines = new(); public void Log(string m) { lock (Lines) Lines.Add(m); } }
class Dialog : IPluginDialog { public List<string> Messages = new(); public Task ShowMessageDialog(string m) { Messages.Add(m); return Task.CompletedTask; } }
class Container : IPluginSensorsContainer {
  public List<IPluginControlSensor> ControlSensors { get; } = new();
  public List<IPluginSensor> FanSensors { get; } = new();
  public List<IPluginSensor> TempSensors { get; } = new();
}
