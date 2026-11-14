using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace G435Monitor;

public sealed record BatteryReading(
    int? Millivolts,
    int? Percent,
    int? ModeBefore,
    int? ModeAfter,
    bool Restored,
    string? Error)
{
    public bool Success => Millivolts is int && Percent is int && Error is null;
}

internal sealed record PiFrame(byte Type, byte SeqFlags, byte[] Payload)
{
    public int Sequence => SeqFlags & 0x3F;
}

internal sealed class BatteryException : Exception
{
    public BatteryException(string message) : base(message) { }
}

/// <summary>
/// Leitura da bateria pela sequência validada no G4:
///   0x85 01 (ACK ignorado) → GET_MODE → SET_MODE 1 (se local) → GET_MODE
///   → GET_BATTERY → SET_MODE 0 (se alterou) → 0x85 02.
/// Um worker por leitura; sem retries; restauração no finally.
/// </summary>
public sealed class BatteryService
{
    private const int PiTimeoutMs = 3000;
    private const int MinPlausibleMv = 2500;
    private const int MaxPlausibleMv = 4600;

    public BatteryReading Read()
    {
        AppLog.Write("read: início");

        string? path = G435Hid.FindDevicePath(out string? findError);
        if (path is null)
            return Fail(findError ?? "Dongle não encontrado.");

        using SafeFileHandle handle = G435Hid.OpenForReadWrite(path);
        if (handle.IsInvalid)
            return Fail($"Não foi possível abrir a interface HID ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).");

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            bufferSize: 65, FileOptions.Asynchronous);
        using var reader = new PiReader(stream);
        reader.Start();

        bool modeChanged = false;
        bool restoreOk = true;
        byte seq = 1;
        BatteryReading result;

        try
        {
            // A — 0x85 01 (desligar power saving). ACK é ignorado.
            reader.DrainQueue();
            WriteAndAck(handle, BuildPowerSaving(0x01));

            // B — GET_MODE
            byte bSeq = seq++;
            reader.DrainQueue();
            if (!WriteAndAck(handle, BuildAw(0x0E, bSeq, new byte[] { 0xE0 })).Ok)
                throw new BatteryException("Falha ao enviar GET_MODE.");

            PiFrame? modeFrame = WaitMode(reader, bSeq);
            if (modeFrame is null)
                throw new BatteryException("Sem resposta do modo. O fone está ligado e pareado?");

            int modeBefore = modeFrame.Payload[1];
            int modeAfter = modeBefore;

            if (modeBefore == 0)
            {
                // D — SET_MODE 1
                byte dSeq = seq++;
                reader.DrainQueue();
                if (!WriteAndAck(handle, BuildAw(0x0E, dSeq, new byte[] { 0xE1, 0x01 })).Ok)
                    throw new BatteryException("Falha ao enviar SET_MODE 1.");

                modeChanged = true;
                Thread.Sleep(120);

                // D2 — confirma o modo remoto
                byte d2Seq = seq++;
                reader.DrainQueue();
                if (!WriteAndAck(handle, BuildAw(0x0E, d2Seq, new byte[] { 0xE0 })).Ok)
                    throw new BatteryException("Falha ao confirmar o modo.");

                PiFrame? confirm = WaitMode(reader, d2Seq);
                if (confirm is null)
                    throw new BatteryException("SET_MODE 1 não confirmado.");

                modeAfter = confirm.Payload[1];
                if (modeAfter != 1)
                    throw new BatteryException("O dongle não entrou em modo remoto.");
            }
            else if (modeBefore != 1)
            {
                throw new BatteryException($"Modo inesperado: {modeBefore}.");
            }

            // E — GET_BATTERY
            byte eSeq = seq++;
            reader.DrainQueue();
            if (!WriteAndAck(handle, BuildAw(0x06, eSeq, new byte[] { 0x00, 0x31 })).Ok)
                throw new BatteryException("Falha ao enviar GET_BATTERY.");

            PiFrame? battery = reader.WaitFor(
                f => IsOtaStatus(f, eSeq), PiTimeoutMs);

            if (battery is null)
                throw new BatteryException("Sem resposta de bateria.");

            int mv = battery.Payload[3] | (battery.Payload[4] << 8);
            if (mv < MinPlausibleMv || mv > MaxPlausibleMv)
                throw new BatteryException($"Tensão implausível: {mv} mV.");

            int pct = MillivoltsToPercent(mv);
            result = new BatteryReading(mv, pct, modeBefore, modeAfter, true, null);
            AppLog.Write($"read: ok mv={mv} pct={pct} modo={modeBefore}->{modeAfter}");
        }
        catch (Exception ex)
        {
            result = Fail(ex.Message);
            AppLog.Write($"read: erro {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try
            {
                if (modeChanged)
                {
                    byte fSeq = seq++;
                    reader.DrainQueue();
                    var r = WriteAndAck(handle, BuildAw(0x0E, fSeq, new byte[] { 0xE1, 0x00 }));
                    restoreOk &= r.Ok && r.Ack == 0x4F;
                }

                reader.DrainQueue();
                var g = WriteAndAck(handle, BuildPowerSaving(0x02));
                restoreOk &= g.Ok && g.Ack == 0x4F;
                AppLog.Write($"read: restauração {(restoreOk ? "ok" : "parcial")}");
            }
            catch (Exception ex)
            {
                restoreOk = false;
                AppLog.Write($"read: falha na restauração: {ex.Message}");
            }
        }

        return result with { Restored = restoreOk };
    }

    private static PiFrame? WaitMode(PiReader reader, byte seq)
    {
        PiFrame? status = reader.WaitFor(f => IsLqStatus(f, seq), PiTimeoutMs);
        if (status is null)
            return null;

        return reader.WaitFor(
            f => f.Type == 0x0E && f.Payload.Length >= 2 && f.Payload[0] == 0xE0,
            PiTimeoutMs);
    }

    private static bool IsLqStatus(PiFrame f, byte seq) =>
        f.Type == 0x01
        && f.Payload.Length >= 3
        && f.Payload[0] == 0x0E
        && (f.Payload[1] & 0x7F) == seq
        && f.Payload[2] == 0x00;

    private static bool IsOtaStatus(PiFrame f, byte seq) =>
        f.Type == 0x01
        && f.Payload.Length >= 5
        && f.Payload[0] == 0x06
        && (f.Payload[1] & 0x7F) == seq
        && f.Payload[2] == 0x00;

    private static (bool Ok, byte? Ack) WriteAndAck(SafeFileHandle handle, byte[] frame)
    {
        if (!G435Hid.WriteReport(handle, frame))
            return (false, null);

        Thread.Sleep(20);

        var ack = new byte[65];
        ack[0] = 0;
        bool got = G435Hid.GetInputReport(handle, ack);
        return (true, got ? ack[1] : null);
    }

    private static byte[] BuildPowerSaving(byte value)
    {
        var frame = new byte[65];
        frame[0] = 0x00;
        frame[1] = 0x85;
        frame[2] = 0x01;
        frame[5] = value;
        SetCrc(frame);
        return frame;
    }

    private static byte[] BuildAw(byte category, byte seq, byte[] payload)
    {
        int len = category == 0x06 ? payload.Length - 1 : payload.Length;

        var pa = new byte[5 + payload.Length];
        pa[0] = 0x50;
        pa[1] = 0x41;
        pa[2] = category;
        pa[3] = seq;
        pa[4] = (byte)len;
        Array.Copy(payload, 0, pa, 5, payload.Length);

        var frame = new byte[65];
        frame[0] = 0x00;
        frame[1] = 0x80;
        frame[2] = (byte)pa.Length;
        Array.Copy(pa, 0, frame, 5, pa.Length);
        SetCrc(frame);
        return frame;
    }

    private static void SetCrc(byte[] frame)
    {
        ushort crc = Crc16Xmodem(frame);
        frame[3] = (byte)(crc & 0xFF);
        frame[4] = (byte)(crc >> 8);
    }

    private static ushort Crc16Xmodem(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }

    private static readonly (int Mv, int Pct)[] Curve =
    {
        (4200, 100), (4150, 95), (4100, 90), (4050, 85),
        (4000, 80), (3950, 75), (3900, 70), (3850, 62),
        (3800, 55), (3750, 45), (3700, 35), (3650, 25),
        (3600, 15), (3550, 10), (3500, 5), (3400, 0)
    };

    internal static int MillivoltsToPercent(int mv)
    {
        if (mv >= Curve[0].Mv)
            return 100;

        for (int i = 0; i < Curve.Length - 1; i++)
        {
            (int hiMv, int hiPct) = Curve[i];
            (int loMv, int loPct) = Curve[i + 1];

            if (mv >= loMv)
            {
                double t = (double)(mv - loMv) / (hiMv - loMv);
                return (int)Math.Round(loPct + t * (hiPct - loPct));
            }
        }

        return 0;
    }

    private static BatteryReading Fail(string error) =>
        new(null, null, null, null, true, error);
}

/// <summary>
/// Lê relatórios de entrada, re-sincroniza em "PI" e expõe os quadros.
/// </summary>
internal sealed class PiReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly BlockingCollection<PiFrame> _frames = new();
    private readonly List<byte> _buffer = new();
    private volatile bool _stopped;
    private Task? _task;

    public PiReader(FileStream stream) => _stream = stream;

    public void Start() => _task = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var report = new byte[65];
        try
        {
            while (!_stopped)
            {
                int n = await _stream.ReadAsync(report.AsMemory(0, report.Length))
                    .ConfigureAwait(false);
                if (n > 0)
                    Feed(report.AsSpan(0, n));
            }
        }
        catch (Exception)
        {
            // stream fechado no dispose
        }
    }

    private void Feed(ReadOnlySpan<byte> data)
    {
        _buffer.AddRange(data.ToArray());

        while (true)
        {
            int pi = FindPi();
            if (pi < 0)
            {
                if (_buffer.Count > 4096)
                    _buffer.Clear();
                return;
            }

            if (pi > 0)
                _buffer.RemoveRange(0, pi);

            if (_buffer.Count < 10)
                return;

            int len = _buffer[8] | (_buffer[9] << 8);
            if (len > 1024)
            {
                _buffer.RemoveRange(0, 10);
                continue;
            }

            if (_buffer.Count < 10 + len)
                return;

            byte[] raw = _buffer.GetRange(0, 10 + len).ToArray();
            byte[] payload = _buffer.GetRange(10, len).ToArray();
            _buffer.RemoveRange(0, 10 + len);

            _frames.Add(new PiFrame(raw[2], raw[3], payload));
        }
    }

    private int FindPi()
    {
        for (int i = 0; i + 1 < _buffer.Count; i++)
        {
            if (_buffer[i] == 0x50 && _buffer[i + 1] == 0x49)
                return i;
        }
        return -1;
    }

    public PiFrame? WaitFor(Func<PiFrame, bool> predicate, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0)
                return null;

            if (!_frames.TryTake(out PiFrame? frame, remaining))
                return null;

            if (predicate(frame))
                return frame;
        }
    }

    public void DrainQueue()
    {
        while (_frames.TryTake(out _))
        {
        }
    }

    public void Dispose()
    {
        _stopped = true;
        try { _stream.Dispose(); } catch { }
        try { _task?.Wait(500); } catch { }
        try { _frames.CompleteAdding(); } catch { }
        _frames.Dispose();
    }
}

internal static class AppLog
{
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "G435-Monitor", "logs");
            Directory.CreateDirectory(dir);

            lock (Lock)
            {
                File.AppendAllText(
                    Path.Combine(dir, "app.log"),
                    $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // log é best-effort
        }
    }
}
