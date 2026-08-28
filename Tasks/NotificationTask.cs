using System;
using Windows.ApplicationModel.Background;
using LumigramPlus.App;

namespace LumigramPlus.Tasks
{
    /// <summary>
    /// The timer's wake.
    ///
    /// Activated into the system's own host process, which is why this class lives
    /// in a Windows Runtime Component: the platform resolves the manifest's entry
    /// point as an activatable class, and only a component's classes are registered
    /// as such.
    ///
    /// Everything is guarded. A background task that throws is killed by the
    /// platform and can stop being scheduled, so a failed wake has to end quietly
    /// and leave the next one to try again.
    /// </summary>
    public sealed class NotificationTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            // Taken first. Without it the process is free to be torn down at the
            // first await, which is before anything useful has happened.
            BackgroundTaskDeferral deferral = taskInstance.GetDeferral();

            // Written before the work, not after: a wake that starts and never
            // comes back is a different problem from one that never started, and
            // only a line written now can tell them apart.
            BackgroundLog.Record("woken");

            try
            {
                if (AppSettings.BackgroundMode == BackgroundMode.Off)
                {
                    BackgroundLog.Record("woken, but background is off");
                }
                else
                {
                    BackgroundLog.Record(await BackgroundCheck.RunAsync());
                }
            }
            catch (Exception ex)
            {
                // Not rethrown. A background task that throws is killed by the
                // platform and can stop being scheduled - but it can still say why
                // before it goes quiet.
                BackgroundLog.Record("failed: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
