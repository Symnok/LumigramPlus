using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;

namespace LumigramPlus.App
{
    /// <summary>
    /// Registering and unregistering what runs in the background.
    ///
    /// One mechanism: a TimeTrigger, at the platform's fifteen-minute floor, held
    /// until there is a network to use.
    ///
    /// There was a second. A ControlChannelTrigger hands the socket to the system so
    /// it can wake the process the moment bytes arrive, and it is what WinRT offers
    /// in place of the Silverlight client's location trick - which has no equivalent
    /// here, because a WinRT app cannot ask to keep running at all. Declaring a
    /// controlChannel task stopped the package installing on the phone, in three
    /// shapes: the class in the app assembly, the class in the component, and both
    /// task types under one declaration. The error never says more than "could not
    /// be registered", so what the system objected to is not established - only that
    /// it objects.
    ///
    /// So the honest summary of what this does is: a quarter-hourly check, and no
    /// real time.
    /// </summary>
    internal static class BackgroundNotifications
    {
        private const string PeriodicTask = "LumigramPlusPeriodic";

        /// <summary>
        /// The entry point, spelled exactly as the manifest declares it.
        ///
        /// A string rather than typeof().FullName because the platform matches it
        /// against the manifest as text: a mismatch registers happily and then never
        /// fires, which is the hardest kind of failure to see.
        /// </summary>
        private const string PeriodicEntry = "LumigramPlus.Tasks.NotificationTask";

        /// <summary>
        /// How often the timer may fire, in minutes.
        ///
        /// Fifteen is the platform floor - TimeTrigger refuses anything smaller, so
        /// this is as close to prompt as a timer gets. The system still decides when
        /// within the window the wake happens, and coalesces it with every other
        /// app's, so the interval is a floor rather than a schedule.
        /// </summary>
        private const uint Minutes = 15;

        /// <summary>
        /// Puts the registrations in line with the setting.
        ///
        /// Called on launch as well as on change: background registrations survive
        /// reinstalls and updates, so a setting turned off in one build must not
        /// leave a task from an older one still running.
        /// </summary>
        public static async Task<string> ApplyAsync(BackgroundMode mode)
        {
            Unregister(PeriodicTask);

            if (mode == BackgroundMode.Off) return null;

            // Nothing may be registered before this is granted, and it is the user's
            // choice to give - so a refusal is reported rather than swallowed.
            BackgroundAccessStatus access;

            try
            {
                access = await BackgroundExecutionManager.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            if (access == BackgroundAccessStatus.Denied ||
                access == BackgroundAccessStatus.Unspecified)
            {
                return "background access was refused - check Settings, "
                     + "battery saver, then this app";
            }

            try
            {
                var builder = new BackgroundTaskBuilder();
                builder.Name = PeriodicTask;
                builder.TaskEntryPoint = PeriodicEntry;

                // false: fire on the schedule from now on, not once when the
                // condition first happens to be true.
                builder.SetTrigger(new TimeTrigger(Minutes, false));

                // Held back until there is a network rather than fired into one that
                // is not there. Without this a wake in a tunnel is a wake spent
                // failing to connect, and the next one is fifteen minutes away.
                // The condition also means the wake lands when connectivity returns,
                // which is exactly when something is usually waiting.
                builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));

                builder.Register();
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            return null;
        }

        /// <summary>Whether the periodic task is currently registered.</summary>
        public static bool PeriodicRegistered
        {
            get { return Find(PeriodicTask) != null; }
        }

        private static IBackgroundTaskRegistration Find(string name)
        {
            try
            {
                foreach (var entry in BackgroundTaskRegistration.AllTasks)
                    if (entry.Value.Name == name) return entry.Value;
            }
            catch (Exception)
            {
                // Enumeration fails on a platform that will not have us; the caller
                // reads that as "not registered", which is true.
            }

            return null;
        }

        private static void Unregister(string name)
        {
            IBackgroundTaskRegistration existing = Find(name);
            if (existing == null) return;

            // true: if a wake is in flight, stop it. The next one will see the same
            // state, so there is nothing to finish.
            try { existing.Unregister(true); }
            catch (Exception) { }
        }
    }
}
