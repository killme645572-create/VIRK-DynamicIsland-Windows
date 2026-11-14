using System;
using System.Windows.Threading;

namespace Island
{
    public enum PomoPhase { Idle, Focus, Break, LongBreak }

    // Помодоро: фокус → перерыв → фокус → … → большой перерыв. Фазы идут сами, цикл заканчивается после большого перерыва.
    public class Pomodoro
    {
        public static readonly Pomodoro Instance = new Pomodoro();

        public PomoPhase Phase { get; private set; } = PomoPhase.Idle;
        public int Round { get; private set; }          // номер текущей фокус-сессии, с 1
        public int Rounds => Math.Max(1, Settings.Current.PomoRounds);
        public TimeSpan Total { get; private set; }
        public TimeSpan Remaining { get; private set; }
        public bool Active => Phase != PomoPhase.Idle;  // идёт или стоит на паузе
        public bool Running { get; private set; }       // идёт отсчёт
        public bool Resting => Phase == PomoPhase.Break || Phase == PomoPhase.LongBreak;

        public event Action Changed;
        public event Action<PomoPhase> PhaseStarted;    // началась новая фаза
        public event Action CycleFinished;              // отработан весь цикл

        readonly DispatcherTimer _t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        DateTime _end;

        Pomodoro() { _t.Tick += (s, e) => Tick(); }

        public void Start()
        {
            Round = 1;
            Begin(PomoPhase.Focus);
        }

        void Begin(PomoPhase p)
        {
            var s = Settings.Current;
            int min = p == PomoPhase.Focus ? s.PomoFocus : p == PomoPhase.Break ? s.PomoBreak : s.PomoLong;
            Phase = p;
            Total = TimeSpan.FromMinutes(Math.Max(1, min));
            Remaining = Total;
            _end = DateTime.UtcNow + Total;
            Running = true;
            _t.Start();
            Changed?.Invoke();
            PhaseStarted?.Invoke(p);
        }

        void Next()
        {
            switch (Phase)
            {
                case PomoPhase.Focus:
                    Begin(Round >= Rounds ? PomoPhase.LongBreak : PomoPhase.Break);
                    break;
                case PomoPhase.Break:
                    Round++;
                    Begin(PomoPhase.Focus);
                    break;
                default:   // большой перерыв закончился — цикл завершён
                    Stop();
                    CycleFinished?.Invoke();
                    break;
            }
        }

        public void Skip() { if (Active) Next(); }

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

        public void Stop()
        {
            Phase = PomoPhase.Idle;
            Running = false;
            Remaining = TimeSpan.Zero;
            _t.Stop();
            Changed?.Invoke();
        }

        void Tick()
        {
            if (!Running) return;
            Remaining = _end - DateTime.UtcNow;
            if (Remaining <= TimeSpan.Zero) { Remaining = TimeSpan.Zero; Next(); }
            else Changed?.Invoke();
        }
    }
}
