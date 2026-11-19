// Gemeinsame Leistungs-Sensoren für PowerTray und das FanControl-Plugin.
//   CPU: Windows "Energy Meter" (RAPL) über PDH - keine Adminrechte nötig.
//   GPU: AMD ADL (atiadlxx.dll) PMLog oder NVIDIA NVML (nvml.dll).
using System;
using System.Runtime.InteropServices;

namespace PowerTray.Sensors
{
    public class Pdh : IDisposable
    {
        [StructLayout(LayoutKind.Explicit)]
        struct PDH_FMT_COUNTERVALUE { [FieldOffset(0)] public uint CStatus; [FieldOffset(8)] public double doubleValue; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string src, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint fmt, IntPtr type, out PDH_FMT_COUNTERVALUE value);
        [DllImport("pdh.dll")] static extern uint PdhCloseQuery(IntPtr query);
        const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;

        IntPtr query;
        public Pdh() { if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) query = IntPtr.Zero; }

        public IntPtr Add(string path)
        {
            IntPtr c;
            if (query == IntPtr.Zero || PdhAddEnglishCounterW(query, path, IntPtr.Zero, out c) != 0) return IntPtr.Zero;
            return c;
        }

        public bool Collect() { return query != IntPtr.Zero && PdhCollectQueryData(query) == 0; }

        public static double? Read(IntPtr counter)
        {
            if (counter == IntPtr.Zero) return null;
            PDH_FMT_COUNTERVALUE v;
            if (PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, IntPtr.Zero, out v) != 0 || v.CStatus > 1) return null;
            return v.doubleValue;
        }

        public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = IntPtr.Zero; }
    }

    public class CpuPower : IDisposable
    {
        readonly Pdh pdh = new Pdh();
        readonly IntPtr pkg, cores, load;
        public bool Ok;

        public CpuPower()
        {
            pkg = pdh.Add(@"\Energy Meter(rapl_package0_pkg)\Power");
            cores = pdh.Add(@"\Energy Meter(rapl_package0_pp0)\Power");
            load = pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
            pdh.Collect();
            Ok = pkg != IntPtr.Zero;
        }

        public void Sample(out float? package, out float? coreW, out float? loadPct)
        {
            package = coreW = loadPct = null;
            if (!pdh.Collect()) return;
            double? p = Pdh.Read(pkg), k = Pdh.Read(cores), l = Pdh.Read(load);
            if (p.HasValue) package = (float)(p.Value / 1000.0);   // mW -> W
            if (k.HasValue) coreW = (float)(k.Value / 1000.0);
            if (l.HasValue) loadPct = (float)Math.Min(100.0, l.Value);
        }

        public void Dispose() { pdh.Dispose(); Ok = false; }
    }

    public class AmdPower : IDisposable
    {
        delegate IntPtr MallocCb(int size);
        [DllImport("atiadlxx.dll")] static extern int ADL2_Main_Control_Create(MallocCb cb, int connected, out IntPtr ctx);
        [DllImport("atiadlxx.dll")] static extern int ADL2_Main_Control_Destroy(IntPtr ctx);
        [DllImport("atiadlxx.dll")] static extern int ADL2_Adapter_NumberOfAdapters_Get(IntPtr ctx, out int n);
        [DllImport("atiadlxx.dll")] static extern int ADL2_Adapter_Active_Get(IntPtr ctx, int idx, out int active);
        [DllImport("atiadlxx.dll")] static extern int ADL2_New_QueryPMLogData_Get(IntPtr ctx, int idx, IntPtr data);

        const int Size = 4 + 256 * 8;
        static readonly MallocCb malloc = AllocMem;
        static IntPtr AllocMem(int s) { return Marshal.AllocHGlobal(s); }

        IntPtr ctx, buf;
        int adapter = -1;
        public bool Ok { get { return adapter >= 0; } }

        public AmdPower()
        {
            try
            {
                if (ADL2_Main_Control_Create(malloc, 1, out ctx) != 0) return;
                buf = Marshal.AllocHGlobal(Size);
                int n;
                if (ADL2_Adapter_NumberOfAdapters_Get(ctx, out n) != 0) return;
                for (int i = 0; i < n; i++)
                {
                    int act;
                    if (ADL2_Adapter_Active_Get(ctx, i, out act) == 0 && act != 0 && Query(i)) { adapter = i; break; }
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
        }

        bool Query(int idx)
        {
            for (int k = 0; k < Size; k += 4) Marshal.WriteInt32(buf, k, 0);
            Marshal.WriteInt32(buf, 0, Size);
            return ADL2_New_QueryPMLogData_Get(ctx, idx, buf) == 0;
        }

        public float? Sensor(int index)
        {
            if (!Ok || index < 0 || index > 255 || !Query(adapter)) return null;
            if (Marshal.ReadInt32(buf, 4 + index * 8) == 0) return null;
            return Marshal.ReadInt32(buf, 8 + index * 8);
        }

        public void Dispose()
        {
            if (ctx != IntPtr.Zero) ADL2_Main_Control_Destroy(ctx);
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            ctx = buf = IntPtr.Zero;
            adapter = -1;
        }
    }

    public class NvidiaPower : IDisposable
    {
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlShutdown();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint pct);
        [StructLayout(LayoutKind.Sequential)] struct Util { public uint gpu, memory; }
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Util u);

        public float? Temp() { uint t; return Ok && nvmlDeviceGetTemperature(dev, 0, out t) == 0 ? (float?)t : null; }
        public float? FanPct() { uint t; return Ok && nvmlDeviceGetFanSpeed(dev, out t) == 0 ? (float?)t : null; }
        public float? Load() { Util u; return Ok && nvmlDeviceGetUtilizationRates(dev, out u) == 0 ? (float?)u.gpu : null; }

        IntPtr dev;
        bool init;
        public bool Ok { get { return dev != IntPtr.Zero; } }

        public NvidiaPower()
        {
            try
            {
                if (nvmlInit_v2() != 0) return;
                init = true;
                IntPtr d;
                uint mw;
                if (nvmlDeviceGetHandleByIndex_v2(0, out d) == 0 && nvmlDeviceGetPowerUsage(d, out mw) == 0) dev = d;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (BadImageFormatException) { }
        }

        public float? Watts()
        {
            uint mw;
            if (!Ok || nvmlDeviceGetPowerUsage(dev, out mw) != 0) return null;
            return mw / 1000f;
        }

        public void Dispose()
        {
            if (init) { try { nvmlShutdown(); } catch { } }
            init = false; dev = IntPtr.Zero;
        }
    }

    /// <summary>GPU-Leistung, egal ob AMD oder NVIDIA.</summary>
    public class GpuPower : IDisposable
    {
        AmdPower amd;
        NvidiaPower nv;
        public int AmdSensorIndex = 73;

        public GpuPower()
        {
            amd = new AmdPower();
            if (!amd.Ok) nv = new NvidiaPower();
        }

        public bool Ok { get { return amd.Ok || (nv != null && nv.Ok); } }
        public string Source { get { return amd.Ok ? "AMD ADL" : nv != null && nv.Ok ? "NVIDIA NVML" : "n/a"; } }
        public float? Watts() { return amd.Ok ? amd.Sensor(AmdSensorIndex) : nv != null && nv.Ok ? nv.Watts() : null; }

        // ADL-PMLog-Indizes: 8 Edge, 27 Hotspot, 14 Lüfter-RPM, 15 Lüfter-%, 19 GFX-Auslastung
        public float? EdgeTemp() { return amd.Ok ? amd.Sensor(8) : nv != null ? nv.Temp() : null; }
        public float? HotspotTemp() { return amd.Ok ? amd.Sensor(27) : null; }
        public float? FanRpm() { return amd.Ok ? amd.Sensor(14) : null; }
        public float? FanPct() { return amd.Ok ? amd.Sensor(15) : nv != null ? nv.FanPct() : null; }
        public float? Load() { return amd.Ok ? amd.Sensor(19) : nv != null ? nv.Load() : null; }

        public void Dispose()
        {
            if (amd != null) amd.Dispose();
            if (nv != null) nv.Dispose();
        }
    }
}
