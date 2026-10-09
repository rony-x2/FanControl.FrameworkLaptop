using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace FanControl.FrameworkLaptop
{
    /// <summary>Ein Temperaturfühler aus "framework_tool --thermal". Temperature ist null, wenn der Fühler gerade keinen Wert liefert (z. B. NotPowered).</summary>
    public sealed class TempReading
    {
        public string Name { get; set; }
        public float? Temperature { get; set; }
    }

    /// <summary>Ein Lüfter aus "framework_tool --thermal". Index ist die Position (0-basiert) = Lüfter-Index für --fansetduty.</summary>
    public sealed class FanReading
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public int Rpm { get; set; }
    }

    public sealed class ThermalSnapshot
    {
        public List<TempReading> Temps { get; } = new List<TempReading>();
        public List<FanReading> Fans { get; } = new List<FanReading>();
    }

    /// <summary>
    /// Liest die Textausgabe von "framework_tool --thermal".
    /// Beispiel (Framework 16):
    ///   F75303_Local: 40 C
    ///   APU:          39 C
    ///   dGPU temp:    NotPowered
    ///   Fan Speed:  2165 RPM
    ///   Fan Speed:  2035 RPM
    ///   AP Throttle Status
    /// </summary>
    public static class ThermalParser
    {
        // EC meldet einen stehenden Lüfter als 0xFFFE (65534)
        private const int StalledSentinel = 0xFFFE;

        private static readonly Regex TempLine =
            new Regex(@"^\s*(?<name>[^:]+?)\s*:\s+(?<val>-?\d+)\s*C\s*$", RegexOptions.Compiled);

        private static readonly Regex TempStatusLine =
            new Regex(@"^\s*(?<name>[^:]+?)\s*:\s+(?<status>NotPowered|NotCalibrated|Error)\s*$", RegexOptions.Compiled);

        private static readonly Regex FanLine =
            new Regex(@"^\s*(?<name>[^:]+?)\s*:\s+(?<rpm>\d+)\s+RPM(?<rest>.*)$", RegexOptions.Compiled);

        public static ThermalSnapshot Parse(string output)
        {
            var snap = new ThermalSnapshot();
            if (string.IsNullOrEmpty(output))
                return snap;

            var seenTemps = new HashSet<string>(StringComparer.Ordinal);
            var lines = output.Replace("\r", "").Split('\n');

            foreach (var raw in lines)
            {
                var line = raw.TrimEnd();
                if (line.Trim().Length == 0)
                    continue;

                // Alles danach (Throttle-Status) interessiert hier nicht
                if (line.TrimStart().StartsWith("AP Throttle Status", StringComparison.OrdinalIgnoreCase))
                    break;

                var fan = FanLine.Match(line);
                if (fan.Success)
                {
                    int rpm = int.Parse(fan.Groups["rpm"].Value, CultureInfo.InvariantCulture);
                    if (rpm >= StalledSentinel || fan.Groups["rest"].Value.IndexOf("Stalled", StringComparison.OrdinalIgnoreCase) >= 0)
                        rpm = 0;

                    snap.Fans.Add(new FanReading
                    {
                        Index = snap.Fans.Count,
                        Name = fan.Groups["name"].Value.Trim(),
                        Rpm = rpm
                    });
                    continue;
                }

                var temp = TempLine.Match(line);
                if (temp.Success)
                {
                    AddTemp(snap, seenTemps, temp.Groups["name"].Value.Trim(),
                        float.Parse(temp.Groups["val"].Value, CultureInfo.InvariantCulture));
                    continue;
                }

                var status = TempStatusLine.Match(line);
                if (status.Success)
                {
                    AddTemp(snap, seenTemps, status.Groups["name"].Value.Trim(), null);
                }
            }

            return snap;
        }

        private static void AddTemp(ThermalSnapshot snap, HashSet<string> seen, string name, float? value)
        {
            // Doppelte Namen eindeutig machen, damit die Sensor-IDs stabil bleiben
            var unique = name;
            int n = 2;
            while (!seen.Add(unique))
                unique = name + " #" + n++;

            snap.Temps.Add(new TempReading { Name = unique, Temperature = value });
        }
    }
}
