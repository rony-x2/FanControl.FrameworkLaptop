using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace FanControl.FrameworkLaptop
{
    /// <summary>
    /// Dünne Hülle um das offizielle Kommandozeilenwerkzeug "framework_tool" von Framework
    /// (https://github.com/FrameworkComputer/framework-system). Alle EC-Zugriffe laufen darüber.
    /// </summary>
    public sealed class FrameworkTool : IEcBackend
    {
        public const string EnvVar = "FRAMEWORK_TOOL_PATH";
        private const string ExeName = "framework_tool.exe";

        public string Path { get; }

        public int TimeoutMs { get; set; } = 10000;

        public string Description => "framework_tool (" + Path + ")";

        public void Dispose() { }

        public FrameworkTool(string path)
        {
            Path = path;
        }

        public ThermalSnapshot ReadThermal()
        {
            return ThermalParser.Parse(Run("--thermal"));
        }

        /// <summary>Setzt den Duty-Wert (0-100 %) eines Lüfters (0-basierter Index).</summary>
        public void SetFanDuty(int fanIndex, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));
            Run("--fansetduty",
                fanIndex.ToString(CultureInfo.InvariantCulture),
                percent.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Gibt einen Lüfter (oder mit null alle) an die automatische Regelung des EC zurück.</summary>
        public void AutoFanControl(int? fanIndex)
        {
            if (!fanIndex.HasValue)
            {
                Run("--autofanctrl");
                return;
            }

            try
            {
                Run("--autofanctrl", fanIndex.Value.ToString(CultureInfo.InvariantCulture));
            }
            catch (FrameworkToolException)
            {
                // Ältere framework_tool-Versionen kennen keinen Lüfter-Index
                Run("--autofanctrl");
                throw new AllFansResetException();
            }
        }

        public string Run(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Path,
                Arguments = string.Join(" ", args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = System.IO.Path.GetDirectoryName(Path) ?? Environment.CurrentDirectory
            };

            using (var p = new Process { StartInfo = psi })
            {
                p.Start();
                var stdoutTask = p.StandardOutput.ReadToEndAsync();
                var stderrTask = p.StandardError.ReadToEndAsync();

                if (!p.WaitForExit(TimeoutMs))
                {
                    try { p.Kill(); } catch { /* bereits beendet */ }
                    throw new FrameworkToolException($"framework_tool {psi.Arguments} hat nicht innerhalb von {TimeoutMs / 1000} s geantwortet.");
                }
                p.WaitForExit();

                var stdout = stdoutTask.Result ?? "";
                var stderr = stderrTask.Result ?? "";

                if (p.ExitCode != 0)
                {
                    var msg = (stderr.Trim().Length > 0 ? stderr : stdout).Trim();
                    throw new FrameworkToolException($"framework_tool {psi.Arguments} endete mit Code {p.ExitCode}: {msg}");
                }

                // framework_tool meldet manche Fehler nur als Text, z. B. "Failed to read thermal information"
                if (stdout.IndexOf("Failed to", StringComparison.OrdinalIgnoreCase) >= 0 && args.Length > 0 && args[0] == "--thermal")
                    throw new FrameworkToolException(stdout.Trim());

                return stdout;
            }
        }

        /// <summary>
        /// Sucht framework_tool.exe in dieser Reihenfolge:
        /// 1. Umgebungsvariable FRAMEWORK_TOOL_PATH
        /// 2. Datei FanControl.FrameworkLaptop.path.txt neben der Plugin-DLL (erste Zeile = Pfad)
        /// 3. Neben der Plugin-DLL, im FanControl-Ordner
        /// 4. PATH
        /// 5. Übliche winget-Ablagen und Programmordner
        /// </summary>
        public static string Locate(out List<string> searched)
        {
            searched = new List<string>();

            var env = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                env = env.Trim().Trim('"');
                searched.Add(env);
                if (File.Exists(env)) return env;
            }

            var pluginDir = GetPluginDirectory();
            if (pluginDir != null)
            {
                var cfg = System.IO.Path.Combine(pluginDir, "FanControl.FrameworkLaptop.path.txt");
                if (File.Exists(cfg))
                {
                    var first = File.ReadAllLines(cfg).Select(l => l.Trim().Trim('"')).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#"));
                    if (!string.IsNullOrEmpty(first))
                    {
                        searched.Add(first);
                        if (File.Exists(first)) return first;
                    }
                }
            }

            var candidates = new List<string>();
            if (pluginDir != null)
            {
                candidates.Add(System.IO.Path.Combine(pluginDir, ExeName));
                var parent = Directory.GetParent(pluginDir);
                if (parent != null) candidates.Add(System.IO.Path.Combine(parent.FullName, ExeName));
            }
            candidates.Add(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExeName));

            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { candidates.Add(System.IO.Path.Combine(dir.Trim().Trim('"'), ExeName)); } catch { }
            }

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
                candidates.Add(System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Links", ExeName));

            foreach (var c in candidates)
            {
                searched.Add(c);
                if (File.Exists(c)) return c;
            }

            // Tiefere Suche: winget-Paketordner und Programmordner (z. B. Framework Control)
            var roots = new List<string>();
            if (!string.IsNullOrEmpty(localAppData))
                roots.Add(System.IO.Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

            foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct())
            {
                IEnumerable<string> dirs;
                try
                {
                    dirs = Directory.GetDirectories(root)
                        .Where(d => System.IO.Path.GetFileName(d).IndexOf("framework", StringComparison.OrdinalIgnoreCase) >= 0);
                }
                catch { continue; }

                foreach (var d in dirs)
                {
                    searched.Add(System.IO.Path.Combine(d, "**", ExeName));
                    try
                    {
                        var hit = Directory.GetFiles(d, ExeName, SearchOption.AllDirectories).FirstOrDefault();
                        if (hit != null) return hit;
                    }
                    catch { /* kein Zugriff */ }
                }
            }

            return null;
        }

        private static string GetPluginDirectory()
        {
            try
            {
                var loc = typeof(FrameworkTool).GetTypeInfo().Assembly.Location;
                return string.IsNullOrEmpty(loc) ? null : System.IO.Path.GetDirectoryName(loc);
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class FrameworkToolException : Exception
    {
        public FrameworkToolException(string message) : base(message) { }
    }
}
