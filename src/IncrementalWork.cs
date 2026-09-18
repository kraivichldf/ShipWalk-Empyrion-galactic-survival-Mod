using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ShipWalk
{
    // The budget limits work between native operations; a single Unity call
    // cannot be interrupted. Record overruns instead of claiming a hard bound.
    internal sealed class IncrementalWork : IDisposable
    {
        public const int UnitsPerSlice = 128;
        public const double MillisecondsPerSlice = 4;
        private IEnumerator<bool> iterator;
        private readonly Func<double> milliseconds;
        public bool Completed { get; private set; }
        public int Slices { get; private set; }
        public double TotalMs { get; private set; }
        public double MaxSliceMs { get; private set; }

        public IncrementalWork(IEnumerator<bool> iterator, Func<double> milliseconds = null)
        {
            this.iterator = iterator ?? throw new ArgumentNullException(nameof(iterator));
            this.milliseconds = milliseconds ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        public bool Advance()
        {
            if (iterator == null) return Completed;
            double start = milliseconds();
            int units = 0;
            try
            {
                do
                {
                    if (!iterator.MoveNext()) { Completed = true; Dispose(); break; }
                    units++;
                }
                while (units < UnitsPerSlice && milliseconds() - start < MillisecondsPerSlice);
            }
            catch { Dispose(); throw; }
            finally
            {
                double elapsed = Math.Max(0, milliseconds() - start);
                Slices++; TotalMs += elapsed; MaxSliceMs = Math.Max(MaxSliceMs, elapsed);
            }
            return Completed;
        }

        public void Dispose()
        {
            IEnumerator<bool> previous = iterator;
            iterator = null;
            previous?.Dispose();
        }
    }
}
