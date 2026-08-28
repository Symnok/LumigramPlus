using System;
using Windows.Storage;

namespace LumigramPlus.App
{
    /// <summary>
    /// What the background task did, the last time it did anything.
    ///
    /// A background task is invisible by construction. It runs in another process,
    /// with no screen, and it is required to fail quietly - a task that throws gets
    /// killed and can stop being scheduled. So every way it can go wrong looks
    /// exactly like every other way, and like never having run at all:
    ///
    ///   the trigger never fired          nothing happens
    ///   it fired with no network         nothing happens
    ///   it connected and found nothing   nothing happens
    ///   it announced something           a toast, which may have been missed
    ///
    /// Those need telling apart before any of them can be fixed, and the only thing
    /// the two processes share is storage. This writes a line the app can show.
    /// </summary>
    internal static class BackgroundLog
    {
        private const string Key = "backgroundLastRun";

        /// <summary>
        /// Notes what happened, stamped with the time.
        ///
        /// Called on the way in as well as on the way out: a wake that starts and
        /// never finishes is its own diagnosis, and only a line written before the
        /// work begins can record one.
        /// </summary>
        public static void Record(string outcome)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[Key] =
                    DateTime.Now.ToString("MMM d HH:mm") + " - " + outcome;
            }
            catch (Exception)
            {
                // Losing the note is not worth failing the wake it describes.
            }
        }

        /// <summary>The last line written, or null if the task has never run.</summary>
        public static string Last
        {
            get
            {
                try
                {
                    return ApplicationData.Current.LocalSettings.Values[Key] as string;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }
    }
}
