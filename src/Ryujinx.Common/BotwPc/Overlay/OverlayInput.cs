using System;

namespace Ryujinx.Common.BotwPc.Overlay
{
    /// <summary>Physical controller buttons as printed on an Xbox pad (positional: A = bottom), independent of game mappings.</summary>
    [Flags]
    public enum PadButtons : uint
    {
        None = 0,
        Up = 1 << 0,
        Down = 1 << 1,
        Left = 1 << 2,
        Right = 1 << 3,
        A = 1 << 4,
        B = 1 << 5,
        X = 1 << 6,
        Y = 1 << 7,
        LB = 1 << 8,
        RB = 1 << 9,
        LS = 1 << 10,
        RS = 1 << 11,
        Menu = 1 << 12,
        View = 1 << 13,
    }

    /// <summary>
    /// Recognises the overlay shortcut L3 + R3 on the physical controller without stealing single stick clicks:
    /// a stick press is held back from the game for at most <see cref="WindowMs"/>; if the other stick follows within
    /// that window the chord fires (both presses are swallowed until both sticks are released), otherwise the press is
    /// passed on late, and a tap shorter than the window is replayed for <see cref="ReplayMs"/> so the game still sees it.
    /// </summary>
    public sealed class StickChord
    {
        public double WindowMs { get; set; } = 80;
        public const double ReplayMs = 60;

        private PadButtons _pending;
        private double _pendingSince;
        private bool _chorded;
        private PadButtons _replay;
        private double _replayUntil;
        private PadButtons _prev;

        /// <summary>
        /// Call once per input poll with the held physical buttons and a monotonic time in ms. Returns whether the chord
        /// fired this poll and which of LS / RS the game may see now (the rest of the buttons are untouched).
        /// </summary>
        public (bool Fired, PadButtons SticksForGame) Update(PadButtons held, double nowMs)
        {
            PadButtons sticks = held & (PadButtons.LS | PadButtons.RS);
            PadButtons pressed = sticks & ~_prev;
            _prev = sticks;
            bool fired = false;

            if (_chorded)
            {
                if (sticks == PadButtons.None)
                {
                    _chorded = false;
                }

                return (false, PadButtons.None);
            }

            if (_pending != PadButtons.None)
            {
                PadButtons other = (PadButtons.LS | PadButtons.RS) & ~_pending;

                if ((sticks & other) != 0)
                {
                    fired = _chorded = true;
                    _pending = PadButtons.None;

                    return (true, PadButtons.None);
                }

                if ((sticks & _pending) == 0)
                {
                    // Released inside the window: a normal tap, replayed so the game still gets the click.
                    _replay = _pending;
                    _replayUntil = nowMs + ReplayMs;
                    _pending = PadButtons.None;
                }
                else if (nowMs - _pendingSince >= WindowMs)
                {
                    _pending = PadButtons.None; // a normal hold: from now on the game sees it
                }
                else
                {
                    return (false, sticks & ~_pending);
                }
            }
            else if (pressed == (PadButtons.LS | PadButtons.RS) && sticks == pressed)
            {
                fired = _chorded = true;

                return (true, PadButtons.None);
            }
            else if (pressed != PadButtons.None && (sticks & ~pressed) == PadButtons.None)
            {
                // First stick down with the other one up: wait for a possible chord.
                _pending = pressed;
                _pendingSince = nowMs;

                return (false, PadButtons.None);
            }

            PadButtons replay = nowMs < _replayUntil ? _replay : PadButtons.None;

            return (fired, sticks | replay);
        }
    }

    /// <summary>Turns held navigation buttons into presses with real-time key repeat (independent of the frame rate).</summary>
    public sealed class NavRepeat
    {
        public double DelayMs { get; set; } = 380;
        public double IntervalMs { get; set; } = 90;

        private const PadButtons Repeating = PadButtons.Up | PadButtons.Down | PadButtons.Left | PadButtons.Right;

        private PadButtons _prev;
        private PadButtons _repeatButton;
        private double _nextRepeat;

        /// <summary>Buttons that count as pressed this poll (new presses, plus repeats of a held direction).</summary>
        public PadButtons Update(PadButtons held, double nowMs)
        {
            PadButtons pressed = held & ~_prev;
            _prev = held;

            if ((pressed & Repeating) != 0)
            {
                _repeatButton = pressed & Repeating;
                _nextRepeat = nowMs + DelayMs;
            }
            else if ((held & _repeatButton) == 0)
            {
                _repeatButton = PadButtons.None;
            }
            else if (_repeatButton != PadButtons.None && nowMs >= _nextRepeat)
            {
                pressed |= _repeatButton;
                _nextRepeat = nowMs + IntervalMs;
            }

            return pressed;
        }
    }
}
