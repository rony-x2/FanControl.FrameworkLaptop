using System;

namespace FanControl.FrameworkLaptop
{
    /// <summary>Lesbare Lüfternamen je Framework-Modell (Zuordnung wie in framework-system).</summary>
    public static class FanNames
    {
        /// <summary>SMBIOS-Familie/Produktname, z. B. "Laptop 16 (AMD Ryzen 7040 Series)". Leer, wenn nicht ermittelbar.</summary>
        public static string DetectModel()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
                {
                    if (key == null) return "";
                    var family = key.GetValue("SystemFamily") as string ?? "";
                    var product = key.GetValue("SystemProductName") as string ?? "";
                    return (family + " " + product).Trim();
                }
            }
            catch
            {
                return "";
            }
        }

        public static string Label(int index, int fanCount, string model)
        {
            model = model ?? "";
            bool Has(string s) => model.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0;

            string name = null;
            if (Has("Desktop"))
                name = index == 0 ? "APU-Lüfter" : index == 1 ? "Front-Lüfter" : index == 2 ? "Dritter Lüfter" : null;
            else if (Has("Laptop 16"))
                name = index == 0 ? "Lüfter links" : index == 1 ? "Lüfter rechts" : null;
            else if (Has("Laptop 13") || Has("Laptop 12"))
                name = index == 0 ? "APU-Lüfter" : null;

            if (name == null)
                name = fanCount > 1 ? $"Lüfter {index + 1}" : "Lüfter";

            return "Framework " + name;
        }
    }
}
