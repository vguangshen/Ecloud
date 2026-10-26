using System;
using System.Runtime.InteropServices;
using System.Xml;
using EcloudKeepAlive;

internal static class RegressionTests
{
    private const long Minute = 60000L;
    private static int checks;

    [STAThread]
    private static void Main()
    {
        var schedule = new KeepAliveSchedule();
        var now = 0L;
        schedule.Start(now, 360);
        Check(schedule.IsDue(now), "The first attempt must run immediately.");
        schedule.CompleteAttempt(now, true);
        Check(!schedule.IsDue(360 * Minute - 1), "A successful launch must respect its interval.");
        Check(schedule.IsDue(360 * Minute), "The scheduled attempt must become due.");

        // Advance the real production scheduler through 100 days without sleeping.
        // This crosses both the old 72-hour task limit and 32-bit millisecond rollover.
        for (var attempt = 0; attempt < 400; attempt++)
        {
            now += 360 * Minute;
            Check(schedule.IsDue(now), "Long-running schedule lost an attempt.");
            schedule.CompleteAttempt(now, true);
            Check(schedule.Remaining(now).Value == TimeSpan.FromHours(6), "Long-running deadline drifted.");
        }

        // An extended outage must keep retrying and then restore the normal interval.
        schedule.CompleteAttempt(now, false);
        for (var attempt = 0; attempt < 576; attempt++)
        {
            Check(!schedule.IsDue(now + 5 * Minute - 1), "A retry fired too early.");
            now += 5 * Minute;
            Check(schedule.IsDue(now), "Retries stopped during an outage.");
            schedule.CompleteAttempt(now, false);
        }
        schedule.CompleteAttempt(now, true);
        Check(schedule.Remaining(now).Value == TimeSpan.FromHours(6), "Recovery did not restore the normal interval.");

        // Manual attempts and live edits must reset the actual due time, not just the label.
        now += 60 * Minute;
        schedule.CompleteAttempt(now, true);
        Check(!schedule.IsDue(now + 359 * Minute), "A manual attempt did not reset the deadline.");
        Check(schedule.IsDue(now + 360 * Minute), "The reset deadline is incorrect.");
        schedule.ChangeInterval(now, 60);
        Check(!schedule.IsDue(now + 60 * Minute - 1), "A live interval edit fired too early.");
        Check(schedule.IsDue(now + 60 * Minute), "A live interval edit did not take effect.");
        Check(schedule.IsDue(now + 48 * 60 * Minute), "A missed deadline must catch up after a long pause.");
        schedule.Stop();
        schedule.CompleteAttempt(now, false);
        Check(!schedule.Enabled && !schedule.IsDue(long.MaxValue), "Stopping during an attempt must not re-arm it.");
        Check(!schedule.Remaining(now).HasValue, "A stopped schedule must have no next deadline.");
        RejectInterval(0);
        RejectInterval(1440);
        Check(StartupTaskPolicy.HasUnlimitedExecutionTime("PT0S"), "Unlimited duration was not recognized.");
        Check(StartupTaskPolicy.HasUnlimitedExecutionTime("P0D"), "Equivalent zero duration was not recognized.");
        Check(!StartupTaskPolicy.HasUnlimitedExecutionTime("PT72H"), "An old limited task must require migration.");
        Check(!StartupTaskPolicy.HasUnlimitedExecutionTime(null), "An unknown limit must require verification.");
        Check(!StartupTaskPolicy.HasUnlimitedExecutionTime("invalid"), "A malformed limit must require verification.");

        if (OperatingSystem.IsWindows()) CheckWindowsTaskSettings();
        else Console.WriteLine("Windows Task Scheduler COM verification skipped on this OS.");
        Console.WriteLine("PASS: " + checks + " assertions; 100-day schedule and 48-hour retry simulation.");
    }

    private static void CheckWindowsTaskSettings()
    {
        object serviceObject = null;
        object definitionObject = null;
        object settingsObject = null;
        try
        {
            serviceObject = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
            dynamic service = serviceObject;
            service.Connect();
            definitionObject = service.NewTask(0);
            dynamic definition = definitionObject;
            settingsObject = definition.Settings;
            dynamic settings = settingsObject;
            StartupTaskPolicy.Configure(settings);
            Check(StartupTaskPolicy.HasUnlimitedExecutionTime((string)settings.ExecutionTimeLimit), "Windows retained a task runtime limit.");
            Check(!(bool)settings.DisallowStartIfOnBatteries && !(bool)settings.StopIfGoingOnBatteries, "Power restrictions still stop the task.");
            Check((int)settings.RestartCount == 3, "Failure restart count is missing.");
            Check(XmlConvert.ToTimeSpan((string)settings.RestartInterval) == TimeSpan.FromMinutes(1), "Failure restart interval is incorrect.");
            Check((int)settings.MultipleInstances == 2, "Duplicate task instances must be ignored.");
            var xml = new XmlDocument();
            xml.LoadXml((string)definition.XmlText);
            var namespaces = new XmlNamespaceManager(xml.NameTable);
            namespaces.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            var limit = xml.SelectSingleNode("/t:Task/t:Settings/t:ExecutionTimeLimit", namespaces);
            Check(limit != null && StartupTaskPolicy.HasUnlimitedExecutionTime(limit.InnerText), "Serialized Windows task is still time-limited.");
            // Deliberately never register or run a task on the CI host.
        }
        finally
        {
            if (settingsObject != null) Marshal.FinalReleaseComObject(settingsObject);
            if (definitionObject != null) Marshal.FinalReleaseComObject(definitionObject);
            if (serviceObject != null) Marshal.FinalReleaseComObject(serviceObject);
        }
    }

    private static void RejectInterval(int minutes)
    {
        try { new KeepAliveSchedule().Start(0, minutes); }
        catch (ArgumentOutOfRangeException) { checks++; return; }
        throw new Exception("Unsafe interval accepted: " + minutes);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }
}
