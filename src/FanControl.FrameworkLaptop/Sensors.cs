using FanControl.Plugins;

namespace FanControl.FrameworkLaptop
{
    /// <summary>Lesender Sensor (Temperatur oder Drehzahl). Den Wert schreibt der Hintergrund-Thread, FanControl liest ihn in Update().</summary>
    internal sealed class FrameworkSensor : IPluginSensor
    {
        private volatile object _boxed; // float? atomar austauschen

        public FrameworkSensor(string id, string name)
        {
            Id = id;
            Name = name;
        }

        public string Id { get; }
        public string Name { get; }

        public float? Value { get; private set; }

        internal void Publish(float? value) => _boxed = value;

        public void Update() => Value = (float?)_boxed;
    }

    /// <summary>Steuerung eines Lüfters. Set()/Reset() werden nur vorgemerkt, die Ausführung übernimmt der Hintergrund-Thread.</summary>
    internal sealed class FrameworkControl : IPluginControlSensor2
    {
        private readonly FrameworkLaptopPlugin _owner;

        public FrameworkControl(FrameworkLaptopPlugin owner, int fanIndex, string id, string name, string pairedFanSensorId)
        {
            _owner = owner;
            FanIndex = fanIndex;
            Id = id;
            Name = name;
            PairedFanSensorId = pairedFanSensorId;
        }

        public int FanIndex { get; }
        public string Id { get; }
        public string Name { get; }
        public string PairedFanSensorId { get; }

        /// <summary>Zuletzt von FanControl gewünschter Wert in Prozent (null = automatische Regelung durch den EC).</summary>
        public float? Value { get; private set; }

        public void Set(float val)
        {
            if (val < 0) val = 0;
            if (val > 100) val = 100;
            Value = val;
            _owner.RequestDuty(FanIndex, val);
        }

        public void Reset()
        {
            Value = null;
            _owner.RequestAuto(FanIndex);
        }

        public void Update() { }
    }
}
