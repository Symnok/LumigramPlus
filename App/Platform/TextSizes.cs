using System;
using Windows.UI.Xaml;

namespace LumigramPlus.App
{
    /// <summary>
    /// Puts the chosen text size where XAML can find it.
    ///
    /// The sizes live in Application.Current.Resources and are read by name with
    /// StaticResource, which is resolved once, when a page is parsed. That is the
    /// whole mechanism and it decides two things: the values have to be in place
    /// before the first page is built, and a change takes hold on each screen as
    /// that screen is next opened rather than on the one being looked at.
    ///
    /// The alternative - binding every FontSize to a property and raising changes -
    /// costs a binding on every line of every message for a setting that is chosen
    /// roughly once. This is four dictionary writes.
    ///
    /// Four kinds of text rather than one scale factor: text has jobs, and a
    /// timestamp growing in step with the message it belongs to would crowd out the
    /// message. Small is the app as it was, so nobody who liked it loses it.
    /// </summary>
    internal static class TextSizes
    {
        // body, list title, subtle, caption - one row per size, indexed by the
        // TextSize value rather than by position on screen. That is why the rows are
        // not in size order: the values are stored on the phone and must not be
        // renumbered. Order below is what the settings list follows.
        private static readonly double[][] Table =
        {
            new double[] { 15, 20, 14, 11 },   // 0 Small - the original app
            new double[] { 19, 23, 16, 13 },   // 1 Medium
            new double[] { 23, 27, 19, 15 },   // 2 Large
            new double[] { 13, 18, 12, 10 },   // 3 ExtraSmall
            new double[] { 27, 31, 22, 17 },   // 4 ExtraLarge
        };

        /// <summary>
        /// The sizes as they are offered, smallest first.
        ///
        /// The settings list is built from this, so the two cannot drift apart the
        /// way a hand-written list of items and a switch over indexes would.
        /// </summary>
        public static readonly TextSize[] Order =
        {
            TextSize.ExtraSmall,
            TextSize.Small,
            TextSize.Medium,
            TextSize.Large,
            TextSize.ExtraLarge,
        };

        /// <summary>Where a size sits in that list, or the place Medium sits.</summary>
        public static int IndexOf(TextSize size)
        {
            for (int i = 0; i < Order.Length; i++)
                if (Order[i] == size) return i;

            // Not one this build offers - a value written by a later version, say.
            for (int i = 0; i < Order.Length; i++)
                if (Order[i] == TextSize.Medium) return i;

            return 0;
        }

        /// <summary>The size at a position in that list, Medium if there is none.</summary>
        public static TextSize At(int index)
        {
            return index >= 0 && index < Order.Length ? Order[index] : TextSize.Medium;
        }

        private static readonly string[] Keys =
        {
            "AppBodyFontSize",
            "AppListTitleFontSize",
            "AppSubtleFontSize",
            "AppCaptionFontSize",
        };

        /// <summary>
        /// Writes the stored choice into the application resources.
        ///
        /// Called before the first page exists, and again whenever the setting
        /// changes. Safe to call more than once - it only overwrites.
        /// </summary>
        public static void Apply()
        {
            Apply(AppSettings.TextSize);
        }

        public static void Apply(TextSize size)
        {
            int row = (int)size;
            if (row < 0 || row >= Table.Length) row = (int)TextSize.Medium;

            try
            {
                ResourceDictionary resources = Application.Current.Resources;

                for (int i = 0; i < Keys.Length; i++)
                    resources[Keys[i]] = Table[row][i];
            }
            catch (Exception)
            {
                // Nothing is written and the sizes declared in App.xaml stand, which
                // is Medium - a readable app in the wrong size beats no app.
            }
        }
    }
}
