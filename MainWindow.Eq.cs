using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Island
{
    // Эквалайзер в пилюле: пока играет музыка, справа пляшут столбики в такт (как в Dynamic Island на iPhone).
    // Звук слушаем через WASAPI loopback (NAudio), поэтому работает с любым плеером.
    public partial class MainWindow
    {
        const double EqExtra = 44;   // на столько пилюля шире, пока играет музыка

        AudioSpectrum _spectrum;
        readonly DispatcherTimer _eqCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly Rectangle[] _eqBars = new Rectangle[AudioSpectrum.Bands];
        readonly double[] _eqVals = new double[AudioSpectrum.Bands];
        bool _eqPlaying;   // плеер сейчас играет
        bool _eqShown;     // столбики включены (музыка играет и функция не выключена)

        void InitEq()
        {
            for (int i = 0; i < _eqBars.Length; i++)
            {
                var r = new Rectangle { Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = Brushes.White };
                Canvas.SetLeft(r, i * 4.75);
                Canvas.SetTop(r, 7.5);
                EqBars.Children.Add(r);
                _eqBars[i] = r;
            }
            _eqCheck.Tick += (s, e) => EqCheck();
            _eqCheck.Start();
        }

        // раз в секунду смотрим, играет ли плеер, и включаем / выключаем «слушалку» звука
        void EqCheck()
        {
            bool playing = false;
            try
            {
                playing = _session != null &&
                          _session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch { }
            _eqPlaying = playing;

            bool want = playing && Settings.Current.Equalizer;
            if (want && _spectrum == null)
            {
                _spectrum = new AudioSpectrum();
                _spectrum.Start();
            }
            else if (!want && _spectrum != null)
            {
                _spectrum.Dispose();
                _spectrum = null;
            }

            if (want != _eqShown)
            {
                _eqShown = want;
                UpdateIdleLayout();
                if (_mode == Mode.Idle) SetMode(Mode.Idle);   // перерисовать ширину пилюли
            }
        }

        // вызывается из FxTick (каждые 40 мс)
        void UpdateEq()
        {
            bool vis = _eqShown && _mode == Mode.Idle;
            EqBars.Opacity += ((vis ? 1 : 0) - EqBars.Opacity) * 0.25;
            if (!vis && EqBars.Opacity < 0.02) { EqBars.Opacity = 0; return; }

            var sp = _spectrum;
            bool real = sp != null && !sp.Failed;
            for (int i = 0; i < _eqBars.Length; i++)
            {
                double target;
                if (real) target = sp.HasRecentData ? sp.Levels[i] : 0;
                else target = _eqPlaying   // запасной вариант, если звук не удалось подключить: столбики просто пляшут
                    ? 0.25 + 0.75 * Math.Abs(Math.Sin(_phase * (0.9 + 0.31 * i) + i * 1.9)) * (0.6 + 0.4 * _rng.NextDouble())
                    : 0;

                // вверх — быстро, вниз — плавно
                _eqVals[i] += (target - _eqVals[i]) * (target > _eqVals[i] ? 0.6 : 0.2);
                double h = 3 + 15 * Math.Max(0, Math.Min(1, _eqVals[i]));
                _eqBars[i].Height = h;
                Canvas.SetTop(_eqBars[i], (18 - h) / 2);
            }
        }

        void EqToggle_Click(object s, RoutedEventArgs e)
        {
            Settings.Current.Equalizer = !Settings.Current.Equalizer;
            Settings.Current.Save();
            UpdateToggleLabels();
            EqCheck();
        }
    }
}
