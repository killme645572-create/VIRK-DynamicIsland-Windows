using System;
using System.Windows.Threading;

namespace Island
{
    public class IslandTimer
    {
        public static readonly IslandTimer Instance = new IslandTimer();

        public TimeSpan Total { get; private set; }
        public TimeSpan Remaining { get; private set; }
        public bool Active { get; private set; }   // таймер запущен или стоит на паузе
        public bool Running { get; private set; }  // идёт отсчёт

        public event Action Changed;
        public event Action Finished;

        readonly DispatcherTimer _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        DateTime _end;

        IslandTimer() { _t.Tick += (s, e) => Tick(); }

        public void Start(TimeSpan d)
        {
            Total = d; Remaining = d; _end = DateTime.UtcNow + d;
            Active = true; Running = true;
            _t.Start();
            Changed?.Invoke();
        }

        public void Pause()
        {
            if (!Running) return;
            Remaining = _end - DateTime.UtcNow;
            Running = false;
            _t.Stop();
            Changed?.Invoke();
        }

        public void Resume()
        {
            if (Running || !Active) return;
            _end = DateTime.UtcNow + Remaining;
            Running = true;
            _t.Start();
            Changed?.Invoke();
        }

        public void Cancel()
        {
            Active = false; Running = false; Remaining = TimeSpan.Zero;
            _t.Stop();
            Changed?.Invoke();
        }

        void Tick()
        {
            Remaining = _end - DateTime.UtcNow;
            if (Remaining <= TimeSpan.Zero)
            {
                Remaining = TimeSpan.Zero; Active = false; Running = false;
                _t.Stop();
                Changed?.Invoke();
                Finished?.Invoke();
            }
            else Changed?.Invoke();
        }

        public static string Format(TimeSpan t)
        {
            int secs = (int)Math.Ceiling(t.TotalSeconds);
            if (secs < 0) secs = 0;
            int h = secs / 3600, m = secs % 3600 / 60, s = secs % 60;
            return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
        }
    }
}
