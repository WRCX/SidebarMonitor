using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace HwiProbe;

/// <summary>
/// Layout of HWiNFO's shared-memory interface (HWiNFO_SENS_SM2).
///
/// Verified empirically against HWiNFO 7.72 by hex-dumping the region, not taken from the
/// SDK headers: the structs are packed (pack(1)), so poll_time sits at 12 with no alignment
/// padding, and current builds append UTF-8 twins of the user-facing strings that older
/// documentation omits. Those extras are what take the elements to 392 / 460 bytes.
///
/// Every hardcoded size is cross-checked at runtime against the sizes the header itself
/// reports, so a future layout change surfaces as a clear error instead of garbage readings.
/// </summary>
internal static class Shm
{
    public const string MapName = @"Global\HWiNFO_SENS_SM2";
    public const string MutexName = @"Global\HWiNFO_SM2_MUTEX";

    // "HWiS" as a little-endian DWORD.
    public const uint Signature = 0x53695748;

    // Header (packed)
    public const int HdrSignature = 0;
    public const int HdrVersion = 4;
    public const int HdrRevision = 8;
    public const int HdrPollTime = 12;   // __time64_t, unaligned
    public const int HdrSensorOffset = 20;
    public const int HdrSensorElemSize = 24;
    public const int HdrSensorCount = 28;
    public const int HdrReadingOffset = 32;
    public const int HdrReadingElemSize = 36;
    public const int HdrReadingCount = 40;
    public const int HdrPollingPeriod = 44;  // ms

    // Sensor element
    public const int SenId = 0;
    public const int SenInstance = 4;
    public const int SenNameOrig = 8;        // char[128]
    public const int SenNameUser = 136;      // char[128]  (localized)
    public const int SenNameUserUtf8 = 264;  // char[128]
    public const int SenElemSize = 392;

    // Reading element
    public const int RdType = 0;             // SENSOR_READING_TYPE enum
    public const int RdSensorIndex = 4;
    public const int RdId = 8;               // stable across restarts; use for matching
    public const int RdLabelOrig = 12;       // char[128]  (English, stable)
    public const int RdLabelUser = 140;      // char[128]  (localized, ANSI)
    public const int RdUnit = 268;           // char[16]
    public const int RdValue = 284;          // double, unaligned
    public const int RdValueMin = 292;
    public const int RdValueMax = 300;
    public const int RdValueAvg = 308;
    public const int RdLabelUserUtf8 = 316;  // char[128]
    public const int RdUnitUtf8 = 444;       // char[16]
    public const int RdElemSize = 460;

    public const int StringLen = 128;
    public const int UnitLen = 16;

    public static string TypeName(uint t) => t switch
    {
        0 => "None",
        1 => "Temp",
        2 => "Volt",
        3 => "Fan",
        4 => "Current",
        5 => "Power",
        6 => "Clock",
        7 => "Usage",
        8 => "Other",
        _ => $"?{t}",
    };
}

internal readonly record struct Reading(
    uint Type, uint SensorIndex, uint Id,
    string Label, string LabelOrig, string Unit,
    double Value, double Min, double Max, double Avg);

internal readonly record struct Sensor(uint Id, uint Instance, string Name);

internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        bool watch = args.Contains("--watch");
        bool bench = args.Contains("--bench");
        bool orig = args.Contains("--orig");
        string? filter = args.FirstOrDefault(a => a.StartsWith("--filter="))?["--filter=".Length..];

        MemoryMappedFile mmf;
        try
        {
            mmf = MemoryMappedFile.OpenExisting(Shm.MapName, MemoryMappedFileRights.Read);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"""
                No existe el objeto de memoria compartida "{Shm.MapName}".

                HWiNFO no esta corriendo, o "Shared Memory Support" esta desactivado.
                  1. Abre HWiNFO en modo "Sensors-only".
                  2. Boton Settings -> pestana Main Settings -> marca "Shared Memory Support".
                  3. Acepta y vuelve a lanzar esta herramienta.
                """);
            return 2;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Sin permiso para abrir la SHM: {ex.Message}");
            return 3;
        }

        using (mmf)
        using (var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            // The mutex is HWiNFO's own writer lock. It may not be openable for a
            // non-elevated reader; that is not fatal, so treat it as best-effort.
            Mutex? mutex = null;
            try { mutex = Mutex.OpenExisting(Shm.MutexName); }
            catch { /* best effort */ }

            uint sig = view.ReadUInt32(Shm.HdrSignature);
            if (sig != Shm.Signature)
            {
                Console.Error.WriteLine($"Firma inesperada: 0x{sig:X8} (esperada 0x{Shm.Signature:X8}). Layout desconocido, abortando.");
                return 4;
            }

            uint version = view.ReadUInt32(Shm.HdrVersion);
            uint revision = view.ReadUInt32(Shm.HdrRevision);
            long pollTime = view.ReadInt64(Shm.HdrPollTime);
            uint sensorOffset = view.ReadUInt32(Shm.HdrSensorOffset);
            uint sensorElemSize = view.ReadUInt32(Shm.HdrSensorElemSize);
            uint sensorCount = view.ReadUInt32(Shm.HdrSensorCount);
            uint readingOffset = view.ReadUInt32(Shm.HdrReadingOffset);
            uint readingElemSize = view.ReadUInt32(Shm.HdrReadingElemSize);
            uint readingCount = view.ReadUInt32(Shm.HdrReadingCount);
            uint pollingPeriod = view.ReadUInt32(Shm.HdrPollingPeriod);

            Console.WriteLine("=== HWiNFO shared memory ===");
            Console.WriteLine($"  version={version} revision={revision}  polling={pollingPeriod} ms");
            Console.WriteLine($"  poll_time={DateTimeOffset.FromUnixTimeSeconds(pollTime).LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"  sensores={sensorCount} (elem={sensorElemSize}B, esperado {Shm.SenElemSize}B) @0x{sensorOffset:X}");
            Console.WriteLine($"  lecturas={readingCount} (elem={readingElemSize}B, esperado {Shm.RdElemSize}B) @0x{readingOffset:X}");

            long mapped = readingOffset + (long)readingCount * readingElemSize;
            Console.WriteLine($"  region total ~{mapped / 1024.0:F1} KiB");
            Console.WriteLine();

            if (sensorElemSize != Shm.SenElemSize || readingElemSize != Shm.RdElemSize)
            {
                Console.Error.WriteLine("El layout declarado por HWiNFO no coincide con el que conocemos. Abortando por seguridad.");
                return 5;
            }

            var buf = new byte[Shm.RdElemSize];

            Sensor[] ReadSensors()
            {
                var list = new Sensor[sensorCount];
                for (uint i = 0; i < sensorCount; i++)
                {
                    long b = sensorOffset + (long)i * sensorElemSize;
                    view.ReadArray(b, buf, 0, Shm.SenElemSize);
                    uint id = BitConverter.ToUInt32(buf, Shm.SenId);
                    uint inst = BitConverter.ToUInt32(buf, Shm.SenInstance);
                    string user = Utf8(buf, Shm.SenNameUserUtf8, Shm.StringLen);
                    if (user.Length == 0) user = Ansi(buf, Shm.SenNameUser, Shm.StringLen);
                    string orig = Ansi(buf, Shm.SenNameOrig, Shm.StringLen);
                    list[i] = new Sensor(id, inst, user.Length > 0 ? user : orig);
                }
                return list;
            }

            Reading[] ReadReadings()
            {
                var list = new Reading[readingCount];
                for (uint i = 0; i < readingCount; i++)
                {
                    long b = readingOffset + (long)i * readingElemSize;
                    view.ReadArray(b, buf, 0, Shm.RdElemSize);
                    uint type = BitConverter.ToUInt32(buf, Shm.RdType);
                    uint sIdx = BitConverter.ToUInt32(buf, Shm.RdSensorIndex);
                    uint id = BitConverter.ToUInt32(buf, Shm.RdId);
                    string user = Utf8(buf, Shm.RdLabelUserUtf8, Shm.StringLen);
                    if (user.Length == 0) user = Ansi(buf, Shm.RdLabelUser, Shm.StringLen);
                    string orig = Ansi(buf, Shm.RdLabelOrig, Shm.StringLen);
                    string unit = Utf8(buf, Shm.RdUnitUtf8, Shm.UnitLen);
                    if (unit.Length == 0) unit = Ansi(buf, Shm.RdUnit, Shm.UnitLen);
                    list[i] = new Reading(
                        type, sIdx, id,
                        user.Length > 0 ? user : orig, orig, unit,
                        BitConverter.ToDouble(buf, Shm.RdValue),
                        BitConverter.ToDouble(buf, Shm.RdValueMin),
                        BitConverter.ToDouble(buf, Shm.RdValueMax),
                        BitConverter.ToDouble(buf, Shm.RdValueAvg));
                }
                return list;
            }

            // The hot path an agent actually runs: labels and units never change while HWiNFO
            // is up, so they get read once. Every tick only needs the doubles.
            var values = new double[readingCount];
            void ReadValuesOnly()
            {
                for (uint i = 0; i < readingCount; i++)
                    values[i] = view.ReadDouble(readingOffset + (long)i * readingElemSize + Shm.RdValue);
            }

            if (bench)
            {
                const int iters = 200;

                // Cold path: rebuild everything, strings included. This is what a naive
                // implementation would do on every tick.
                for (int i = 0; i < 20; i++) { ReadSensors(); ReadReadings(); }
                long before = GC.GetTotalAllocatedBytes(precise: true);
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iters; i++) { ReadSensors(); ReadReadings(); }
                sw.Stop();
                long after = GC.GetTotalAllocatedBytes(precise: true);
                double usFull = sw.Elapsed.TotalMicroseconds / iters;

                // Hot path: values only.
                for (int i = 0; i < 20; i++) ReadValuesOnly();
                long beforeV = GC.GetTotalAllocatedBytes(precise: true);
                var sw2 = Stopwatch.StartNew();
                for (int i = 0; i < iters; i++) ReadValuesOnly();
                sw2.Stop();
                long afterV = GC.GetTotalAllocatedBytes(precise: true);
                double usValues = sw2.Elapsed.TotalMicroseconds / iters;

                Console.WriteLine($"=== Benchmark ({sensorCount} sensores + {readingCount} lecturas) ===");
                Console.WriteLine();
                Console.WriteLine("  Snapshot completo (reconstruye strings cada vez):");
                Console.WriteLine($"    {usFull,8:F1} us   {(after - before) / (double)iters / 1024.0,7:F1} KiB asignados   {usFull / 10_000.0:F4} % de un core a 1 Hz");
                Console.WriteLine();
                Console.WriteLine("  Solo valores (lo que hara el agente; etiquetas cacheadas):");
                Console.WriteLine($"    {usValues,8:F1} us   {(afterV - beforeV) / (double)iters / 1024.0,7:F1} KiB asignados   {usValues / 10_000.0:F4} % de un core a 1 Hz");
                Console.WriteLine();
                Console.WriteLine($"  Mejora: {usFull / usValues:F1}x mas rapido, sin asignar nada");

                // Reported from inside the process: PeakWorkingSet64 reads back as 0 once it exits.
                using var self = Process.GetCurrentProcess();
                Console.WriteLine($"  pico de working set: {self.PeakWorkingSet64 / 1024.0 / 1024:F1} MiB");
                Console.WriteLine();
                return 0;
            }

            Regex? rx = filter is null ? null : new Regex(filter, RegexOptions.IgnoreCase);

            do
            {
                bool held = false;
                try
                {
                    try { held = mutex?.WaitOne(50) ?? false; } catch (AbandonedMutexException) { held = true; }

                    var sensors = ReadSensors();
                    var readings = ReadReadings();

                    if (watch) Console.Clear();

                    uint lastSensor = uint.MaxValue;
                    int shown = 0;
                    foreach (var r in readings)
                    {
                        if (rx is not null && !rx.IsMatch(r.Label) && !rx.IsMatch(r.LabelOrig) &&
                            !(r.SensorIndex < sensors.Length && rx.IsMatch(sensors[r.SensorIndex].Name)))
                            continue;

                        if (r.SensorIndex != lastSensor)
                        {
                            lastSensor = r.SensorIndex;
                            string sn = r.SensorIndex < sensors.Length ? sensors[r.SensorIndex].Name : "?";
                            Console.WriteLine();
                            Console.WriteLine($"--- [{r.SensorIndex}] {sn} ---");
                        }

                        // --orig shows the English label and the reading id: those are what an
                        // agent must match on, never the localized user label.
                        Console.WriteLine(orig
                            ? string.Create(CultureInfo.InvariantCulture,
                                $"  {Shm.TypeName(r.Type),-8} id=0x{r.Id:X8} {r.LabelOrig,-46} {r.Value,12:F3} {r.Unit}")
                            : string.Create(CultureInfo.InvariantCulture,
                                $"  {Shm.TypeName(r.Type),-8} {r.Label,-42} {r.Value,12:F3} {r.Unit,-6} (min {r.Min,10:F2}  max {r.Max,10:F2}  avg {r.Avg,10:F2})"));
                        shown++;
                    }

                    Console.WriteLine();
                    Console.WriteLine($"{shown} lecturas mostradas de {readingCount} totales, en {sensorCount} sensores.");
                }
                finally
                {
                    if (held) mutex!.ReleaseMutex();
                }

                if (watch) Thread.Sleep(1000);
            } while (watch && !(Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Escape));

            mutex?.Dispose();
        }

        return 0;
    }

    /// <summary>The legacy string fields: fixed-size, NUL-padded, ANSI codepage.</summary>
    private static string Ansi(byte[] buf, int offset, int maxLen) =>
        Encoding.Latin1.GetString(buf, offset, Len(buf, offset, maxLen)).Trim();

    /// <summary>The newer UTF-8 twins. Preferred: they survive accented labels intact.</summary>
    private static string Utf8(byte[] buf, int offset, int maxLen) =>
        Encoding.UTF8.GetString(buf, offset, Len(buf, offset, maxLen)).Trim();

    private static int Len(byte[] buf, int offset, int maxLen)
    {
        int end = offset, limit = offset + maxLen;
        while (end < limit && buf[end] != 0) end++;
        return end - offset;
    }
}
