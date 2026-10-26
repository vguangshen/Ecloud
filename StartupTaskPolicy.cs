using System;
using System.Xml;

namespace EcloudKeepAlive
{
    internal static class StartupTaskPolicy
    {
        internal static void Configure(dynamic settings)
        {
            settings.Enabled = true;
            settings.StartWhenAvailable = true;
            settings.AllowDemandStart = true;
            // A continuously running tray app must not inherit Task Scheduler's 72-hour limit.
            settings.ExecutionTimeLimit = "PT0S";
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.RestartInterval = "PT1M";
            settings.RestartCount = 3;
            settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
        }

        internal static bool HasUnlimitedExecutionTime(string duration)
        {
            if (string.IsNullOrWhiteSpace(duration)) return false;
            try { return XmlConvert.ToTimeSpan(duration) == TimeSpan.Zero; }
            catch (FormatException) { return false; }
        }
    }
}
