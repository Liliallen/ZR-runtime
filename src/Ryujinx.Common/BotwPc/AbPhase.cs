using System;
using System.Diagnostics;
using System.Globalization;

namespace Ryujinx.Common.BotwPc
{
    /// <summary>
    /// BOTW-PC diagnostics: A/B comparison inside one run. With BOTWPC_AB_PERIOD=&lt;seconds&gt;, switches that consult
    /// <see cref="Active"/> are on only in every second period of the Stopwatch clock (the clock of framelog.csv's
    /// time_s column), so botw/scripts/ab_phases.py can compare adjacent periods. Slow scene changes (weather, time of
    /// day, wind, camera drift) and background load affect both sides of a pair alike, which separate runs do not
    /// achieve in W1. Without BOTWPC_AB_PERIOD, <see cref="Active"/> is always true.
    /// </summary>
    public static class AbPhase
    {
        private static readonly long _periodTicks = Parse();

        private static long Parse()
        {
            string value = Environment.GetEnvironmentVariable("BOTWPC_AB_PERIOD");

            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
                ? (long)(seconds * Stopwatch.Frequency)
                : 0;
        }

        /// <summary>True in the "B" periods (odd period numbers), or always when no period is set.</summary>
        public static bool Active => _periodTicks == 0 || ((Stopwatch.GetTimestamp() / _periodTicks) & 1) == 1;

        private static volatile bool _on = true;

        /// <summary>
        /// <see cref="Active"/> as a plain field read, for switches checked per draw or per binding: a background
        /// thread flips it at each period boundary (within about a millisecond, inside the settle time that
        /// ab_phases.py drops).
        /// </summary>
        public static bool On => _on;

        /// <summary>
        /// A switch for an in-run comparison: off when the environment variable is "0"; when its name is listed in
        /// BOTWPC_AB_SWITCHES (comma-separated), on only in the "B" periods (<see cref="On"/>).
        /// </summary>
        public readonly struct Switch
        {
            private readonly bool _enabled;
            private readonly bool _phased;

            public Switch(string variable, bool experimental = false)
            {
                // Experimental switches (rejected components kept for re-measurement) are off unless set to "1" or
                // compared in-run through BOTWPC_AB_SWITCHES.
                string value = Environment.GetEnvironmentVariable(variable);
                _enabled = experimental ? value == "1" || Array.IndexOf((Environment.GetEnvironmentVariable("BOTWPC_AB_SWITCHES") ?? "").Split(','), variable) >= 0 : value != "0";
                _phased = _periodTicks != 0 && Array.IndexOf((Environment.GetEnvironmentVariable("BOTWPC_AB_SWITCHES") ?? "").Split(','), variable) >= 0;
            }

            public bool IsOn => _enabled && (!_phased || _on);
        }

        static AbPhase()
        {
            if (_periodTicks == 0)
            {
                return;
            }

            _on = Active;

            new System.Threading.Thread(() =>
            {
                while (true)
                {
                    long now = Stopwatch.GetTimestamp();
                    long next = (now / _periodTicks + 1) * _periodTicks;
                    System.Threading.Thread.Sleep((int)Math.Max(1, (next - now) * 1000 / Stopwatch.Frequency));

                    while (Stopwatch.GetTimestamp() < next)
                    {
                        System.Threading.Thread.Sleep(0);
                    }

                    _on = Active;
                }
            })
            { Name = "BOTW-PC A/B phase", IsBackground = true }.Start();
        }
    }
}
