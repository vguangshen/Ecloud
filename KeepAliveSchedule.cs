using System;

namespace EcloudKeepAlive
{
    // Elapsed time is supplied by the caller so system clock changes cannot defer a connection.
    internal sealed class KeepAliveSchedule
    {
        internal const int DefaultIntervalMinutes = 360;
        internal const int MaximumIntervalMinutes = 720;
        internal const int RetryIntervalMinutes = 5;
        private long? dueAtMilliseconds;
        private int intervalMinutes;

        internal bool Enabled { get { return dueAtMilliseconds.HasValue; } }

        internal void Start(long nowMilliseconds, int minutes)
        {
            intervalMinutes = ValidateInterval(minutes);
            dueAtMilliseconds = nowMilliseconds;
        }

        internal void ChangeInterval(long nowMilliseconds, int minutes)
        {
            intervalMinutes = ValidateInterval(minutes);
            if (Enabled) dueAtMilliseconds = nowMilliseconds + intervalMinutes * 60000L;
        }

        internal bool IsDue(long nowMilliseconds)
        {
            return dueAtMilliseconds.HasValue && nowMilliseconds >= dueAtMilliseconds.Value;
        }

        internal void CompleteAttempt(long nowMilliseconds, bool clientStarted)
        {
            if (!Enabled) return;
            var minutes = clientStarted ? intervalMinutes : RetryIntervalMinutes;
            dueAtMilliseconds = nowMilliseconds + minutes * 60000L;
        }

        internal TimeSpan? Remaining(long nowMilliseconds)
        {
            return dueAtMilliseconds.HasValue
                ? TimeSpan.FromMilliseconds(Math.Max(0, dueAtMilliseconds.Value - nowMilliseconds))
                : (TimeSpan?)null;
        }

        internal void Stop()
        {
            dueAtMilliseconds = null;
        }

        private static int ValidateInterval(int minutes)
        {
            if (minutes < 1 || minutes > MaximumIntervalMinutes)
                throw new ArgumentOutOfRangeException(nameof(minutes));
            return minutes;
        }
    }
}
