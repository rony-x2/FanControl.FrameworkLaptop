using System;

namespace FanControl.FrameworkLaptop
{
    /// <summary>Zugriffsweg auf den Embedded Controller (EC) des Framework-Geräts.</summary>
    public interface IEcBackend : IDisposable
    {
        /// <summary>Kurzbeschreibung für das Protokoll, z. B. "CrosEC-Treiber".</summary>
        string Description { get; }

        /// <summary>Temperaturen und Lüfterdrehzahlen lesen. Fan.Index ist der Lüfter-Index des EC.</summary>
        ThermalSnapshot ReadThermal();

        /// <summary>Duty-Wert (0-100 %) eines Lüfters setzen.</summary>
        void SetFanDuty(int fanIndex, int percent);

        /// <summary>Einen Lüfter (oder mit null alle) an die automatische Regelung des EC zurückgeben.</summary>
        void AutoFanControl(int? fanIndex);
    }
}
