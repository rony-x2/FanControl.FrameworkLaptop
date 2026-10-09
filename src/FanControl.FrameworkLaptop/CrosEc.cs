// Direkter Zugriff auf den Framework/ChromeOS-EC über den Windows-Treiber "CrosEC"
// (https://github.com/FrameworkComputer/crosecbus, https://github.com/DHowett/FrameworkWindowsUtils).
//
// Protokoll und Konstanten nach framework-system (framework_lib/src/chromium_ec/windows.rs, power.rs,
// commands.rs), Copyright (c) 2023, Framework Computer Inc, BSD-3-Clause-Lizenz.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace FanControl.FrameworkLaptop
{
    /// <summary>Transportschicht zum EC: Speicherbereich lesen und Host-Befehle senden.</summary>
    public interface ICrosEcTransport : IDisposable
    {
        /// <summary>Liest <paramref name="length"/> Bytes aus dem gemappten EC-Speicher (0x00-0xFE).</summary>
        byte[] ReadMemory(int offset, int length);

        /// <summary>Sendet einen Host-Befehl und liefert die Antwortdaten. Wirft <see cref="EcResultException"/>, wenn der EC einen Fehlercode meldet.</summary>
        byte[] Command(ushort command, byte version, byte[] request);
    }

    /// <summary>Fehlercode des EC (EC_RES_*).</summary>
    public sealed class EcResultException : Exception
    {
        private static readonly string[] Names =
        {
            "Success", "InvalidCommand", "Error", "InvalidParam", "AccessDenied", "InvalidResponse",
            "InvalidVersion", "InvalidChecksum", "InProgress", "Unavailable", "Timeout", "Overflow",
            "InvalidHeader", "RequestTruncated", "ResponseTooBig", "BusError", "Busy"
        };

        public const uint InvalidCommand = 1;
        public const uint InvalidParam = 3;
        public const uint InvalidVersion = 6;

        public uint Code { get; }

        public EcResultException(ushort command, uint code)
            : base($"EC-Befehl 0x{command:X4} abgelehnt: {(code < Names.Length ? Names[code] : "Code " + code)}")
        {
            Code = code;
        }
    }

    /// <summary>Pufferaufbau für die IOCTLs des CrosEC-Treibers (getrennt, damit er ohne Windows prüfbar ist).</summary>
    public static class CrosEcProtocol
    {
        public const uint FileDeviceCrosEmbeddedController = 0x80EC;
        public const uint MethodBuffered = 0;
        public const uint FileReadData = 1;
        public const uint FileWriteData = 2;

        public static readonly uint IoctlXcmd = CtlCode(FileDeviceCrosEmbeddedController, 0x801, MethodBuffered, FileReadData | FileWriteData);
        public static readonly uint IoctlRdmem = CtlCode(FileDeviceCrosEmbeddedController, 0x802, MethodBuffered, FileReadData);

        /// <summary>sizeof(CROSEC_COMMAND): version, command, outsize, insize, result (je ULONG).</summary>
        public const int CommandHeaderSize = 20;
        /// <summary>Größte Nutzlast: EC_LPC_HOST_PACKET_SIZE (0x100) minus Host-Header (8). Wie framework_tool.</summary>
        public const int MaxPayload = 0x100 - 8;
        public const int CommandBufferSize = CommandHeaderSize + MaxPayload;

        public const int MemmapSize = 0xFF;
        /// <summary>sizeof(CROSEC_READMEM): offset, bytes, buffer[0xFF], auf 4 Byte aufgerundet.</summary>
        public const int ReadMemBufferSize = 264;

        public static uint CtlCode(uint deviceType, uint function, uint method, uint access)
            => (deviceType << 16) | (access << 14) | (function << 2) | method;

        public static byte[] BuildCommand(ushort command, byte version, byte[] request)
        {
            request = request ?? new byte[0];
            if (request.Length > MaxPayload)
                throw new ArgumentException("Anfrage zu groß für den CrosEC-Treiber.", nameof(request));

            var buf = new byte[CommandBufferSize];
            WriteU32(buf, 0, version);
            WriteU32(buf, 4, command);
            WriteU32(buf, 8, (uint)request.Length);
            WriteU32(buf, 12, MaxPayload);
            WriteU32(buf, 16, 0xFF); // result, vom Treiber überschrieben
            Buffer.BlockCopy(request, 0, buf, CommandHeaderSize, request.Length);
            return buf;
        }

        /// <summary>Wertet die Antwort aus. <paramref name="returned"/> = von DeviceIoControl gemeldete Länge (inkl. Header).</summary>
        public static byte[] ParseCommandResponse(ushort command, byte[] buf, int returned)
        {
            uint result = ReadU32(buf, 16);
            if (result != 0)
                throw new EcResultException(command, result);

            int len = Math.Max(0, Math.Min(returned, buf.Length) - CommandHeaderSize);
            var data = new byte[len];
            Buffer.BlockCopy(buf, CommandHeaderSize, data, 0, len);
            return data;
        }

        public static byte[] BuildReadMem(int offset, int length)
        {
            if (offset < 0 || length <= 0 || offset + length > MemmapSize)
                throw new ArgumentOutOfRangeException(nameof(offset), "Bereich außerhalb des EC-Speichers.");
            var buf = new byte[ReadMemBufferSize];
            WriteU32(buf, 0, (uint)offset);
            WriteU32(buf, 4, (uint)length);
            return buf;
        }

        public static byte[] ParseReadMem(byte[] buf, int length)
        {
            var data = new byte[length];
            Buffer.BlockCopy(buf, 8, data, 0, length);
            return data;
        }

        internal static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        internal static uint ReadU32(byte[] b, int o)
            => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }

    /// <summary>Echter Transport über DeviceIoControl auf \\.\GLOBALROOT\Device\CrosEC (nur Windows).</summary>
    public sealed class WindowsCrosEcTransport : ICrosEcTransport
    {
        public const string DevicePath = @"\\.\GLOBALROOT\Device\CrosEC";

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 1;
        private const uint FileShareWrite = 2;
        private const uint OpenExisting = 3;
        private const int ErrorRetry = 1237;            // STATUS_RETRY -> ERROR_RETRY
        private const int ErrorIoDevice = 1117;

        private readonly SafeFileHandle _handle;
        private readonly object _lock = new object();

        private WindowsCrosEcTransport(SafeFileHandle handle)
        {
            _handle = handle;
        }

        /// <summary>Öffnet den Treiber. Liefert null und eine Fehlermeldung, wenn er nicht verfügbar ist.</summary>
        public static WindowsCrosEcTransport TryOpen(out string error)
        {
            error = null;
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                error = "kein Windows";
                return null;
            }

            var h = CreateFileW(DevicePath, GenericRead | GenericWrite, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int code = Marshal.GetLastWin32Error();
                h.Dispose();
                error = $"{new Win32Exception(code).Message} (Win32-Fehler {code})";
                return null;
            }
            return new WindowsCrosEcTransport(h);
        }

        public byte[] ReadMemory(int offset, int length)
        {
            var buf = CrosEcProtocol.BuildReadMem(offset, length);
            Ioctl(CrosEcProtocol.IoctlRdmem, buf, buf.Length, buf.Length);
            return CrosEcProtocol.ParseReadMem(buf, length);
        }

        public byte[] Command(ushort command, byte version, byte[] request)
        {
            var buf = CrosEcProtocol.BuildCommand(command, version, request);
            int returned = Ioctl(CrosEcProtocol.IoctlXcmd, buf, buf.Length, buf.Length);
            return CrosEcProtocol.ParseCommandResponse(command, buf, returned);
        }

        private int Ioctl(uint code, byte[] buf, int inLen, int outLen)
        {
            lock (_lock)
            {
                // Der Treiber meldet STATUS_RETRY, wenn gerade ein Kerneltreiber auf den EC zugreift
                for (int attempt = 0; ; attempt++)
                {
                    if (DeviceIoControl(_handle, code, buf, (uint)inLen, buf, (uint)outLen, out uint returned, IntPtr.Zero))
                        return (int)returned;

                    int err = Marshal.GetLastWin32Error();
                    if ((err == ErrorRetry || err == ErrorIoDevice) && attempt < 4)
                    {
                        Thread.Sleep(5 * (attempt + 1));
                        continue;
                    }
                    throw new Win32Exception(err, $"DeviceIoControl 0x{code:X8}: {new Win32Exception(err).Message}");
                }
            }
        }

        public void Dispose() => _handle.Dispose();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            byte[] lpInBuffer, uint nInBufferSize, byte[] lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);
    }

    /// <summary>EC-Zugriff über den CrosEC-Treiber: Temperaturen und Drehzahlen aus dem Speicherabbild, Lüfter per Host-Befehl.</summary>
    public sealed class CrosEcBackend : IEcBackend
    {
        // Speicherabbild (ec_commands.h)
        private const int MemmapTempSensor = 0x00; // 0x00-0x0E, ein Byte je Fühler
        private const int TempSensorCount = 0x0F;
        private const int MemmapFan = 0x10;        // 4 x u16 little endian
        private const int FanEntries = 4;

        private const byte TempNotPresent = 0xFF;
        private const byte TempError = 0xFE;
        private const byte TempNotPowered = 0xFD;
        private const byte TempNotCalibrated = 0xFC;
        private const int TempOffsetKelvin = 200; // Wert + 200 = Kelvin

        private const ushort FanNotPresent = 0xFFFF;
        private const ushort FanStalled = 0xFFFE;

        // Host-Befehle
        private const ushort CmdPwmSetFanDuty = 0x0024;
        private const ushort CmdAutoFanCtrl = 0x0052;
        private const ushort CmdTempSensorGetInfo = 0x0070;

        private readonly ICrosEcTransport _ec;
        private readonly Dictionary<int, string> _names = new Dictionary<int, string>();
        private bool _dutyV1Unsupported;
        private bool _autoV1Unsupported;

        public CrosEcBackend(ICrosEcTransport transport)
        {
            _ec = transport;
        }

        public string Description => "CrosEC-Treiber (direkt)";

        public ThermalSnapshot ReadThermal()
        {
            var temps = _ec.ReadMemory(MemmapTempSensor, TempSensorCount);
            var fans = _ec.ReadMemory(MemmapFan, FanEntries * 2);

            var snap = new ThermalSnapshot();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < temps.Length; i++)
            {
                byte raw = temps[i];
                if (raw == TempNotPresent) continue;

                float? value = raw == TempError || raw == TempNotPowered || raw == TempNotCalibrated
                    ? (float?)null
                    : raw + TempOffsetKelvin - 273;

                var name = SensorName(i);
                var unique = name;
                int n = 2;
                while (!seen.Add(unique)) unique = name + " #" + n++;

                snap.Temps.Add(new TempReading { Name = unique, Temperature = value });
            }

            for (int i = 0; i < FanEntries; i++)
            {
                ushort rpm = (ushort)(fans[i * 2] | (fans[i * 2 + 1] << 8));
                if (rpm == FanNotPresent) continue;
                snap.Fans.Add(new FanReading
                {
                    Index = i,
                    Name = "Fan " + i,
                    Rpm = rpm == FanStalled ? 0 : rpm
                });
            }

            return snap;
        }

        public void SetFanDuty(int fanIndex, int percent)
        {
            percent = Math.Max(0, Math.Min(100, percent));

            if (!_dutyV1Unsupported)
            {
                try
                {
                    // struct { u32 percent; u32 fan_idx; }
                    var req = new byte[8];
                    CrosEcProtocol.WriteU32(req, 0, (uint)percent);
                    CrosEcProtocol.WriteU32(req, 4, (uint)fanIndex);
                    _ec.Command(CmdPwmSetFanDuty, 1, req);
                    return;
                }
                catch (EcResultException ex) when (ex.Code == EcResultException.InvalidVersion && fanIndex == 0)
                {
                    _dutyV1Unsupported = true; // alte EC-Firmware: nur ein Lüfter, Version 0
                }
            }

            if (fanIndex != 0)
                throw new InvalidOperationException("EC-Firmware unterstützt keine Steuerung einzelner Lüfter.");

            var v0 = new byte[4];
            CrosEcProtocol.WriteU32(v0, 0, (uint)percent);
            _ec.Command(CmdPwmSetFanDuty, 0, v0);
        }

        public void AutoFanControl(int? fanIndex)
        {
            if (fanIndex.HasValue && !_autoV1Unsupported)
            {
                try
                {
                    _ec.Command(CmdAutoFanCtrl, 1, new[] { (byte)fanIndex.Value });
                    return;
                }
                catch (EcResultException ex) when (ex.Code == EcResultException.InvalidVersion)
                {
                    _autoV1Unsupported = true;
                    // Aufrufer erkennt an der Ausnahme, dass alle Lüfter zurückgesetzt wurden
                    _ec.Command(CmdAutoFanCtrl, 0, new byte[0]);
                    throw new AllFansResetException();
                }
            }

            if (fanIndex.HasValue)
            {
                _ec.Command(CmdAutoFanCtrl, 0, new byte[0]);
                throw new AllFansResetException();
            }

            _ec.Command(CmdAutoFanCtrl, 0, new byte[0]);
        }

        private string SensorName(int index)
        {
            if (_names.TryGetValue(index, out var cached))
                return cached;

            string name;
            try
            {
                // Antwort: char sensor_name[32]; u8 sensor_type
                var res = _ec.Command(CmdTempSensorGetInfo, 0, new[] { (byte)index });
                int len = Array.IndexOf(res, (byte)0);
                if (len < 0 || len > 32) len = Math.Min(32, res.Length);
                name = Encoding.ASCII.GetString(res, 0, len).Trim();
                if (name.Length == 0) name = "Temp " + index;
            }
            catch
            {
                name = "Temp " + index;
            }

            _names[index] = name;
            return name;
        }

        public void Dispose() => _ec.Dispose();
    }

    /// <summary>Signalisiert, dass statt eines einzelnen Lüfters alle an die Automatik zurückgegeben wurden.</summary>
    public sealed class AllFansResetException : Exception
    {
        public AllFansResetException() : base("Alle Lüfter an die Automatik zurückgegeben (EC kennt keinen Lüfter-Index).") { }
    }
}
