using System;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace Island
{
    // Слушает то, что играет в колонках (WASAPI loopback, без микрофона), и раскладывает звук на полосы:
    // от баса к верхам. Значения 0..1 читает интерфейс и рисует столбики.
    sealed class AudioSpectrum : IDisposable
    {
        public const int Bands = 5;
        const int FftM = 10;                 // 2^10 = 1024 отсчёта
        const int FftN = 1 << FftM;

        // границы полос, Гц
        static readonly double[] Edges = { 40, 140, 400, 1100, 3000, 9000 };
        // музыка к верхам тише — подтягиваем высокие полосы, чтобы столбики были живее
        static readonly double[] Tilt = { 1.0, 1.4, 2.4, 4.0, 7.0 };

        public readonly float[] Levels = new float[Bands];   // 0..1
        public bool Failed { get; private set; }             // не удалось подключиться к звуку — интерфейс рисует запасной вариант

        WasapiLoopbackCapture _cap;
        readonly float[] _ring = new float[FftN];
        readonly Complex[] _fft = new Complex[FftN];
        readonly double[] _raw = new double[Bands];
        int _pos;
        int _filled;
        double _agc = 0.01;                  // «автогромкость»: подстраиваемся под текущий уровень звука
        DateTime _lastData = DateTime.MinValue;
        DateTime _lastFft = DateTime.MinValue;

        // loopback молчит, когда ничего не играет, поэтому «давно не было данных» = тишина
        public bool HasRecentData => (DateTime.UtcNow - _lastData).TotalMilliseconds < 400;

        public void Start()
        {
            if (_cap != null) return;
            try
            {
                _cap = new WasapiLoopbackCapture();
                _cap.DataAvailable += OnData;
                _cap.StartRecording();
                Failed = false;
            }
            catch
            {
                Failed = true;
                Stop();
            }
        }

        public void Stop()
        {
            var c = _cap;
            _cap = null;
            if (c != null)
            {
                try { c.DataAvailable -= OnData; c.StopRecording(); } catch { }
                try { c.Dispose(); } catch { }
            }
            Array.Clear(Levels, 0, Levels.Length);
        }

        public void Dispose() => Stop();

        void OnData(object sender, WaveInEventArgs e)
        {
            var cap = _cap;
            if (cap == null) return;
            var wf = cap.WaveFormat;
            int ch = Math.Max(1, wf.Channels);
            int bytes = wf.BitsPerSample / 8;
            if (bytes != 4 && bytes != 2) return;   // в режиме общего доступа Windows отдаёт float32, 16 бит — на всякий случай

            int frame = ch * bytes;
            int frames = e.BytesRecorded / frame;
            for (int i = 0; i < frames; i++)
            {
                int o = i * frame;
                float s = 0;
                for (int c = 0; c < ch; c++)
                    s += bytes == 4 ? BitConverter.ToSingle(e.Buffer, o + c * 4)
                                    : BitConverter.ToInt16(e.Buffer, o + c * 2) / 32768f;
                _ring[_pos] = s / ch;
                _pos = (_pos + 1) % FftN;
                if (_filled < FftN) _filled++;
            }
            _lastData = DateTime.UtcNow;

            // считаем спектр не чаще ~30 раз в секунду
            if (_filled < FftN || (_lastData - _lastFft).TotalMilliseconds < 30) return;
            _lastFft = _lastData;
            Analyze(wf.SampleRate);
        }

        void Analyze(int rate)
        {
            for (int i = 0; i < FftN; i++)
            {
                float v = _ring[(_pos + i) % FftN];
                double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftN - 1));   // окно Хэннинга
                _fft[i].X = (float)(v * w);
                _fft[i].Y = 0;
            }
            FastFourierTransform.FFT(true, FftM, _fft);

            double binHz = (double)rate / FftN;
            double peak = 0;
            for (int b = 0; b < Bands; b++)
            {
                int lo = Math.Max(1, (int)(Edges[b] / binHz));
                int hi = Math.Min(FftN / 2 - 1, (int)(Edges[b + 1] / binHz));
                if (hi < lo) hi = lo;
                double sum = 0;
                for (int k = lo; k <= hi; k++)
                    sum += Math.Sqrt(_fft[k].X * _fft[k].X + _fft[k].Y * _fft[k].Y);
                // с окном Хэннинга синус амплитуды A даёт пик около A·N/4 — приводим к «амплитуде»
                double m = sum / (hi - lo + 1) / (FftN / 4.0) * Tilt[b];
                _raw[b] = m;
                if (m > peak) peak = m;
            }

            _agc = Math.Max(peak, _agc * 0.985);   // уровень «медленно остывает», пока звук тише
            if (_agc < 0.0008)                     // почти тишина
            {
                Array.Clear(Levels, 0, Levels.Length);
                return;
            }
            for (int b = 0; b < Bands; b++)
                Levels[b] = (float)Math.Min(1.0, Math.Pow(_raw[b] / _agc, 1.3));
        }
    }
}
