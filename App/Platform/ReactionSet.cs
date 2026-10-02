using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml.Media;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// Which reactions this phone offers, and how to draw one.
    ///
    /// Only the old emoji. The phone's emoji font predates most of what Telegram
    /// now offers - 🤔 🤩 🥰 and the rest arrived later - and a picker full of
    /// empty boxes is worse than a short one. Everything here comes from the first
    /// emoji sets, which the font was built for.
    ///
    /// What is offered is this list narrowed by what the server accepts, so a
    /// reaction Telegram retires, or turns Premium-only, drops out on its own.
    /// Reactions other people put on messages are drawn whatever they are: this
    /// list decides what can be chosen, not what can be seen.
    /// </summary>
    internal static class ReactionSet
    {
        /// <summary>
        /// The old emoji, in the order they are offered. The set and its order are
        /// the user's own choice, 2026-10-02.
        ///
        /// Spelled as Telegram spells them, which is what is sent: the heart in
        /// particular is the bare U+2764, see Reactions.Heart.
        /// </summary>
        public static readonly string[] Old =
        {
            Reactions.ThumbsUp,     // 👍
            "\U0001F44E",           // 👎
            Reactions.Heart,        // ❤
            "\U0001F525",           // 🔥
            "\U0001F44F",           // 👏
            "\U0001F601",           // 😁
            "\U0001F631",           // 😱
            "\U0001F622",           // 😢
            "\U0001F389",           // 🎉
            "\U0001F64F",           // 🙏
            "\U0001F44C",           // 👌
            "\U0001F60D",           // 😍
            "\U0001F4AF",           // 💯
            "\u26A1",               // ⚡
            "\U0001F3C6",           // 🏆
            "\U0001F494",           // 💔
            "\U0001F62D",           // 😭
            "\U0001F440",           // 👀
            "\U0001F607",           // 😇
            "\U0001F60E",           // 😎
            "\U0001F621",           // 😡
            "\U0001F4A9",           // 💩
        };

        /// <summary>
        /// What the picker shows: Old, less whatever the server will not accept.
        ///
        /// Null until it has been asked once, and then kept for as long as the app
        /// runs - the list changes on Telegram's schedule, not the user's.
        /// </summary>
        private static List<string> _offered;

        public static async Task<List<string>> OfferedAsync(MtprotoClient client)
        {
            if (_offered != null) return _offered;

            var offered = new List<string>();

            try
            {
                List<AvailableReaction> available =
                    await Reactions.GetAvailableAsync(client, TelegramService.Info);

                var accepted = new HashSet<string>();
                foreach (AvailableReaction a in available)
                    if (!a.Premium && !a.Inactive && a.Emoticon != null) accepted.Add(a.Emoticon);

                foreach (string emoji in Old)
                    if (accepted.Contains(emoji)) offered.Add(emoji);
            }
            catch (Exception)
            {
                // Not reachable just now. The old list is still the right answer to
                // offer - the worst case is one refusal for a retired reaction -
                // and it is not cached, so the next open asks again.
                return new List<string>(Old);
            }

            // An empty intersection means the server spelled everything differently
            // from this list, which would leave nothing to pick. Offer the list as
            // it is rather than an empty picker.
            if (offered.Count == 0) offered.AddRange(Old);

            _offered = offered;
            return _offered;
        }

        /// <summary>
        /// The text to draw for a reaction.
        ///
        /// The heart is sent bare, as Telegram requires, but bare U+2764 is the
        /// *text* heart, which fonts draw black. The emoji variation selector after
        /// it asks for the coloured one - added for drawing only, never for sending.
        /// </summary>
        public static string Display(string emoticon)
        {
            if (emoticon == Reactions.Heart) return Reactions.Heart + "\uFE0F";
            return emoticon ?? "";
        }

        /// <summary>
        /// The colour to draw a reaction in.
        ///
        /// Red for the heart, and the belt to the selector's braces: a colour emoji
        /// ignores the text colour, so this only shows if the font draws the plain
        /// heart anyway - and then it draws it red instead of black.
        /// </summary>
        public static Brush BrushFor(string emoticon)
        {
            return emoticon == Reactions.Heart ? HeartBrush : PlainBrush;
        }

        private static readonly Brush HeartBrush =
            new SolidColorBrush(Color.FromArgb(255, 232, 36, 52));

        private static readonly Brush PlainBrush = new SolidColorBrush(Colors.White);
    }
}
