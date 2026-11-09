using System;
using System.Windows.Threading;

namespace Crest.Services
{
    public enum TimerMode
    {
        Timer,
        Stopwatch,
        /// <summary>Focus sessions: work, short break, work… (Pomodoro).</summary>
        Focus
    }

    /// <summary>A countdown timer and a stopwatch shared by every notch window.</summary>
    public sealed class TimerService
    {
        private readonly DispatcherTimer _tick;
        private DateTime _startedAt;
        private TimeSpan _accumulated;   // stopwatch: elapsed before the current run; timer: remaining before the current run

        public event EventHandler? Changed;
        public event EventHandler? Finished;
        /// <summary>Focus mode switched between work and break (UI thread).</summary>
        public event EventHandler? FocusPhaseChanged;

        public TimeSpan FocusWork { get; private set; } = TimeSpan.FromMinutes(25);
        public TimeSpan FocusBreak { get; private set; } = TimeSpan.FromMinutes(5);
        /// <summary>Focus mode: true during a break.</summary>
        public bool IsBreak { get; private set; }
        /// <summary>Focus mode: the current work round (1, 2, 3…).</summary>
        public int Round { get; private set; } = 1;
        /// <summary>A focus work block is running right now.</summary>
        public bool InFocusWork => Mode == TimerMode.Focus && Running && !IsBreak;

        public TimerMode Mode { get; private set; } = TimerMode.Timer;
        public bool Running { get; private set; }
        public TimeSpan Duration { get; private set; } = TimeSpan.FromMinutes(5);

        /// <summary>True while there's something to show (running, or paused part-way).</summary>
        public bool IsActive => Running || (Mode == TimerMode.Stopwatch ? _accumulated > TimeSpan.Zero : _accumulated > TimeSpan.Zero && _accumulated < Duration);

        public TimerService()
        {
            _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
            _tick.Tick += (_, _) => OnTick();
            _accumulated = Duration;
        }

        /// <summary>Timer: time left. Stopwatch: time elapsed.</summary>
        public TimeSpan Value
        {
            get
            {
                if (Mode == TimerMode.Stopwatch)
                    return Running ? _accumulated + (DateTime.Now - _startedAt) : _accumulated;
                var left = Running ? _accumulated - (DateTime.Now - _startedAt) : _accumulated;
                return left < TimeSpan.Zero ? TimeSpan.Zero : left;
            }
        }

        public void SetMode(TimerMode mode)
        {
            if (mode == Mode) return;
            Reset();
            Mode = mode;
            if (mode == TimerMode.Focus)
            {
                IsBreak = false;
                Round = 1;
                Duration = FocusWork;
            }
            else if (mode == TimerMode.Timer && Duration == FocusWork)
            {
                Duration = TimeSpan.FromMinutes(5);
            }
            _accumulated = mode == TimerMode.Stopwatch ? TimeSpan.Zero : Duration;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Lengths of focus blocks and breaks (from the settings).</summary>
        public void ConfigureFocus(int workMinutes, int breakMinutes)
        {
            FocusWork = TimeSpan.FromMinutes(Math.Clamp(workMinutes, 1, 180));
            FocusBreak = TimeSpan.FromMinutes(Math.Clamp(breakMinutes, 1, 60));
            if (Mode == TimerMode.Focus && !Running && _accumulated == Duration)
            {
                Duration = IsBreak ? FocusBreak : FocusWork;
                _accumulated = Duration;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Starts a fresh focus session right away.</summary>
        public void StartFocus()
        {
            SetMode(TimerMode.Focus);
            Reset();
            Start();
        }

        /// <summary>Starts a countdown of <paramref name="duration"/> right away.</summary>
        public void StartTimer(TimeSpan duration)
        {
            Mode = TimerMode.Timer;
            Duration = duration;
            _accumulated = duration;
            Start();
        }

        public void AddMinute()
        {
            if (Mode == TimerMode.Stopwatch) return;
            if (Running)
            {
                _accumulated = Value + TimeSpan.FromMinutes(1);
                _startedAt = DateTime.Now;
            }
            else
            {
                _accumulated += TimeSpan.FromMinutes(1);
            }
            if (_accumulated > Duration) Duration = _accumulated;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Toggle()
        {
            if (Running) Pause();
            else Start();
        }

        public void Start()
        {
            if (Running) return;
            if (Mode != TimerMode.Stopwatch && _accumulated <= TimeSpan.Zero) _accumulated = Duration;
            _startedAt = DateTime.Now;
            Running = true;
            _tick.Start();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Pause()
        {
            if (!Running) return;
            _accumulated = Value;
            Running = false;
            _tick.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public void Reset()
        {
            Running = false;
            _tick.Stop();
            if (Mode == TimerMode.Focus)
            {
                IsBreak = false;
                Round = 1;
                Duration = FocusWork;
            }
            _accumulated = Mode == TimerMode.Stopwatch ? TimeSpan.Zero : Duration;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void OnTick()
        {
            if (Mode == TimerMode.Focus && Value <= TimeSpan.Zero)
            {
                // Work → break → work… and keep running.
                IsBreak = !IsBreak;
                if (!IsBreak) Round++;
                Duration = IsBreak ? FocusBreak : FocusWork;
                _accumulated = Duration;
                _startedAt = DateTime.Now;
                Changed?.Invoke(this, EventArgs.Empty);
                FocusPhaseChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            if (Mode == TimerMode.Timer && Value <= TimeSpan.Zero)
            {
                Running = false;
                _tick.Stop();
                _accumulated = TimeSpan.Zero;
                Changed?.Invoke(this, EventArgs.Empty);
                Finished?.Invoke(this, EventArgs.Empty);
                _accumulated = Duration;   // ready for the next run
                return;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public static string Format(TimeSpan t)
            => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}
