using System.IO.MemoryMappedFiles;
using System.Text;

namespace SidebarMonitor.Agent;

/// <summary>
/// Reads HWiNFO's shared memory. Labels and units never change while HWiNFO is up, so the
/// indices of the readings we care about are resolved once; every tick then only reads doubles.
/// That is the difference between ~116 us and ~4 us per tick.
///
/// Matching is done on szLabelOrig (English, stable), never on the user label, which HWiNFO
/// localizes. See the README for the full struct layout and how it was recovered.
/// </summary>
internal sealed class HwiSensors : IDisposable
{
    private const string MapName = @"Global\HWiNFO_SENS_SM2";
    private const uint Signature = 0x53695748;   // "HWiS"

    private const int HdrPollTime = 12;
    private const int HdrSensorOffset = 20, HdrSensorElemSize = 24, HdrSensorCount = 28;
    private const int HdrReadingOffset = 32, HdrReadingElemSize = 36, HdrReadingCount = 40;
    private const int SenNameOrig = 8, SenElemSize = 392;
    private const int RdSensorIndex = 4, RdLabelOrig = 12, RdValue = 284, RdElemSize = 460;
    private const int StringLen = 128;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _readingOffset;
    private readonly uint _readingElemSize;

    private readonly int _idxPackagePower;
    private readonly int _idxCpuTemp;

    /// <summary>Sensor name of each S.M.A.R.T. group -> index of its "Drive Temperature" reading.</summary>
    private readonly List<(string SensorName, int Index)> _driveTemps = [];

    public string CpuName { get; } = "CPU";

    public double PackagePowerW => Read(_idxPackagePower);
    public double CpuTempC => Read(_idxCpuTemp);

    /// <summary>HWiNFO stamps this each poll. If it stops advancing, the SHM is frozen (the free
    /// build disables it after 12 h) and every reading here is stale.</summary>
    public long PollTime => _view.ReadInt64(HdrPollTime);

    /// <summary>
    /// HWiNFO names its S.M.A.R.T. sensors "S.M.A.R.T.: &lt;model&gt; (&lt;serial&gt;)", so the model
    /// reported by IOCTL_STORAGE_QUERY_PROPERTY is a substring of it. That is the only join we have.
    /// </summary>
    public double DriveTempC(string model)
    {
        if (string.IsNullOrEmpty(model)) return double.NaN;
        foreach (var (sensorName, index) in _driveTemps)
            if (sensorName.Contains(model, StringComparison.OrdinalIgnoreCase))
                return Read(index);
        return double.NaN;
    }

    private HwiSensors(MemoryMappedFile mmf, MemoryMappedViewAccessor view)
    {
        _mmf = mmf;
        _view = view;

        uint sensorOffset = view.ReadUInt32(HdrSensorOffset);
        uint sensorElemSize = view.ReadUInt32(HdrSensorElemSize);
        uint sensorCount = view.ReadUInt32(HdrSensorCount);
        _readingOffset = view.ReadUInt32(HdrReadingOffset);
        _readingElemSize = view.ReadUInt32(HdrReadingElemSize);
        uint readingCount = view.ReadUInt32(HdrReadingCount);

        if (sensorElemSize != SenElemSize || _readingElemSize != RdElemSize)
            throw new InvalidDataException(
                $"layout de HWiNFO desconocido: sensor={sensorElemSize}B (esperado {SenElemSize}), " +
                $"reading={_readingElemSize}B (esperado {RdElemSize})");

        var buf = new byte[Math.Max(SenElemSize, RdElemSize)];

        var sensorNames = new string[sensorCount];
        for (uint i = 0; i < sensorCount; i++)
        {
            view.ReadArray(sensorOffset + (long)i * sensorElemSize, buf, 0, SenElemSize);
            string name = Ansi(buf, SenNameOrig, StringLen);
            sensorNames[i] = name;

            if (CpuName == "CPU" && name.StartsWith("CPU [#0]", StringComparison.Ordinal))
            {
                int colon = name.IndexOf(':');
                if (colon >= 0 && colon + 2 < name.Length) CpuName = name[(colon + 2)..];
            }
        }

        for (uint i = 0; i < readingCount; i++)
        {
            view.ReadArray(_readingOffset + (long)i * _readingElemSize, buf, 0, RdElemSize);
            string labelOrig = Ansi(buf, RdLabelOrig, StringLen);

            // The first "Drive Temperature" of each S.M.A.R.T. sensor. NVMe drives expose
            // several (composite, sensor 2, sensor 3); the first one is the one to show.
            if (labelOrig == "Drive Temperature")
            {
                uint sensorIdx = BitConverter.ToUInt32(buf, RdSensorIndex);
                if (sensorIdx < sensorCount) _driveTemps.Add((sensorNames[sensorIdx], (int)i));
            }

            switch (labelOrig)
            {
                case "CPU Package Power" when _idxPackagePower == 0: _idxPackagePower = (int)i + 1; break;
                case "CPU (Tctl/Tdie)" when _idxCpuTemp == 0: _idxCpuTemp = (int)i + 1; break;
            }
        }

        // Stored +1 so that 0 means "not found".
        _idxPackagePower--;
        _idxCpuTemp--;
    }

    public static HwiSensors? TryOpen(out string? error)
    {
        error = null;
        try
        {
            var mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            if (view.ReadUInt32(0) != Signature)
            {
                view.Dispose(); mmf.Dispose();
                error = "firma incorrecta en la SHM de HWiNFO";
                return null;
            }
            return new HwiSensors(mmf, view);
        }
        catch (FileNotFoundException)
        {
            error = "HWiNFO no esta corriendo, o Shared Memory Support esta desactivado";
            return null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private double Read(int index)
    {
        if (index < 0) return double.NaN;
        try { return _view.ReadDouble(_readingOffset + (long)index * _readingElemSize + RdValue); }
        catch { return double.NaN; }
    }

    private static string Ansi(byte[] buf, int offset, int maxLen)
    {
        int end = offset, limit = offset + maxLen;
        while (end < limit && buf[end] != 0) end++;
        return Encoding.Latin1.GetString(buf, offset, end - offset).Trim();
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
