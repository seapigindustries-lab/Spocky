using System.Runtime.InteropServices;

namespace Spocky.Services.GpuTelemetry.Providers;

internal sealed class NvidiaGpuTelemetryProvider : IGpuTelemetryProvider
{
    private readonly List<IntPtr> _devices = new();
    private bool _initialized;

    public NvidiaGpuTelemetryProvider()
    {
        try
        {
            var initResult = NvmlNative.nvmlInit_v2();
            if (initResult != NvmlReturn.Success)
            {
                return;
            }

            _initialized = true;

            uint count = 0;
            if (NvmlNative.nvmlDeviceGetCount_v2(ref count) != NvmlReturn.Success || count == 0)
            {
                return;
            }

            for (uint i = 0; i < count; i++)
            {
                var handle = IntPtr.Zero;
                if (NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, ref handle) == NvmlReturn.Success && handle != IntPtr.Zero)
                {
                    _devices.Add(handle);
                }
            }
        }
        catch (DllNotFoundException)
        {
            throw;
        }
        catch
        {
            // Ignore initialization failures.
        }
    }

    public bool IsAvailable => _initialized && _devices.Count > 0;

    public double? ReadCoreTemperatureC()
    {
        if (!IsAvailable)
        {
            return null;
        }

        foreach (var device in _devices)
        {
            uint value = 0;
            if (NvmlNative.nvmlDeviceGetTemperature(device, NvmlTemperatureSensor.Gpu, ref value) == NvmlReturn.Success && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try
            {
                NvmlNative.nvmlShutdown();
            }
            catch
            {
                // Ignore shutdown failures
            }
            finally
            {
                _initialized = false;
            }
        }
    }

    private static class NvmlNative
    {
        private const string DllName = "nvml.dll";

        [DllImport(DllName)]
        public static extern NvmlReturn nvmlInit_v2();

        [DllImport(DllName)]
        public static extern NvmlReturn nvmlShutdown();

        [DllImport(DllName)]
        public static extern NvmlReturn nvmlDeviceGetCount_v2(ref uint deviceCount);

        [DllImport(DllName)]
        public static extern NvmlReturn nvmlDeviceGetHandleByIndex_v2(uint index, ref IntPtr device);

        [DllImport(DllName)]
        public static extern NvmlReturn nvmlDeviceGetTemperature(IntPtr device, NvmlTemperatureSensor sensorType, ref uint temp);
    }
}

internal enum NvmlReturn
{
    Success = 0
}

internal enum NvmlTemperatureSensor
{
    Gpu = 0
}
