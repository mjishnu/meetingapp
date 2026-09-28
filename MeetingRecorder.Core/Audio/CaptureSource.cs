using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MeetingRecorder.Core.Audio;

internal sealed class CaptureSource
{
    private readonly BufferedWaveProvider _buffer;
    private readonly PrimingGate _gate;
    private readonly PeakMeter _meter;

    public CaptureSource(string name, WaveFormat deviceFormat, WaveFormat mixFormat, TimeSpan cushion, TimeSpan maxBuffered)
    {
        Name = name;
        _buffer = new BufferedWaveProvider(deviceFormat, maxBuffered)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        _gate = new PrimingGate(_buffer, cushion);

        ISampleProvider chain = _gate.ToSampleProvider();
        chain = chain.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(chain),
            2 => chain,
            _ => new DownmixToStereoSampleProvider(chain),
        };
        if (chain.WaveFormat.SampleRate != mixFormat.SampleRate)
            chain = new WdlResamplingSampleProvider(chain, mixFormat.SampleRate);
        _meter = new PeakMeter(chain);
        Output = _meter;
    }

    public string Name { get; }
    public ISampleProvider Output { get; }

    public TimeSpan Buffered => _buffer.BufferedDuration;

    public int Underruns => _gate.Underruns;

    public void Add(ReadOnlySpan<byte> data) => _buffer.AddSamples(data);

    public void AddSilence(int byteCount)
    {
        Span<byte> zeros = stackalloc byte[Math.Min(byteCount, 4096)];
        zeros.Clear();
        while (byteCount > 0)
        {
            int n = Math.Min(byteCount, zeros.Length);
            _buffer.AddSamples(zeros[..n]);
            byteCount -= n;
        }
    }

    public void TrimTo(TimeSpan keep)
    {
        var excess = _buffer.BufferedDuration - keep;
        if (excess <= TimeSpan.Zero) return;
        var fmt = _buffer.WaveFormat;
        int bytes = (int)(excess.TotalSeconds * fmt.AverageBytesPerSecond);
        bytes -= bytes % fmt.BlockAlign;
        var scratch = new byte[Math.Min(bytes, 64 * 1024)];
        while (bytes > 0)
        {
            int read = _buffer.Read(scratch.AsSpan(0, Math.Min(bytes, scratch.Length)));
            if (read <= 0) break;
            bytes -= read;
        }
    }
    public float TakePeak() => _meter.TakePeak();

    private sealed class PrimingGate(BufferedWaveProvider inner, TimeSpan cushion) : IWaveProvider
    {
        private bool _running;

        public WaveFormat WaveFormat => inner.WaveFormat;

        public int Underruns { get; private set; }

        public int Read(Span<byte> buffer)
        {
            if (!_running)
            {
                if (inner.BufferedDuration < cushion)
                {
                    buffer.Clear();
                    return buffer.Length;
                }
                _running = true;
            }
            if (inner.BufferedBytes < buffer.Length)
            {
                _running = false;
                Underruns++;
            }
            return inner.Read(buffer);
        }
    }

    private sealed class PeakMeter(ISampleProvider source) : ISampleProvider
    {
        private float _peak;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            int read = source.Read(buffer);
            float peak = 0;
            foreach (var s in buffer[..read])
            {
                float a = Math.Abs(s);
                if (a > peak) peak = a;
            }
            if (peak > Volatile.Read(ref _peak)) Volatile.Write(ref _peak, peak);
            return read;
        }

        public float TakePeak() => Interlocked.Exchange(ref _peak, 0f);
    }

    private sealed class DownmixToStereoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _scratch = [];

        public DownmixToStereoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            int frames = buffer.Length / 2;
            int needed = frames * _channels;
            if (_scratch.Length < needed) _scratch = new float[needed];
            int read = _source.Read(_scratch.AsSpan(0, needed));
            int framesRead = read / _channels;
            for (int f = 0; f < framesRead; f++)
            {
                int i = f * _channels;
                float centre = _channels >= 3 ? _scratch[i + 2] * 0.7071f : 0f;
                buffer[f * 2] = _scratch[i] + centre;
                buffer[f * 2 + 1] = _scratch[i + 1] + centre;
            }
            return framesRead * 2;
        }
    }
}
