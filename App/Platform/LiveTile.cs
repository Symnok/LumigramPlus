using System;
using System.Collections.Generic;
using System.Text;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>
    /// The start screen tile.
    ///
    /// Shows how many chats are waiting and who they are from - chats, not
    /// messages. Ten messages in one conversation is one thing to go and read, and a
    /// tile reading "10" for it says something the user has to go and disprove.
    ///
    /// Muted chats are left out, so the tile agrees with the notifications: a chat
    /// muted precisely so it stops asking for attention should not then ask for it
    /// from the start screen.
    ///
    /// The count is the system badge - the black disc the shell draws in the corner
    /// of the tile. There was briefly a second one painted onto the logo, on the
    /// assumption that the badge could not be styled; it can't, but it already looks
    /// the way it was wanted, so the tile ended up with two. The badge is the one to
    /// keep regardless: it is also what the lock screen reads.
    ///
    /// Updated by the app while it is open and by the background task while it is
    /// not, so what the tile says is as recent as the last check either of them
    /// made - a quarter of an hour at worst.
    /// </summary>
    internal static class LiveTile
    {
        /// <summary>How many chats to name. Four is what the wide tile fits.</summary>
        private const int Names = 4;

        /// <summary>
        /// Writes the tile from a chat list.
        ///
        /// Both tile sizes are written in one notification: the user chooses the
        /// size on the start screen, and a notification that only fills the one we
        /// guessed leaves the other blank.
        /// </summary>
        public static void Update(List<DialogEntry> dialogs, int nowUtc)
        {
            if (dialogs == null) return;

            var waiting = new List<string>();
            int chats = 0;

            foreach (DialogEntry d in dialogs)
            {
                if (d.UnreadCount <= 0) continue;
                if (d.IsMuted(nowUtc)) continue;

                chats++;

                if (waiting.Count < Names)
                {
                    waiting.Add(string.IsNullOrEmpty(d.Title) ? "chat" : d.Title);
                }
            }

            if (chats == 0)
            {
                Clear();
                return;
            }

            Write(chats, waiting);
        }

        /// <summary>Puts the tile back to its plain state.</summary>
        public static void Clear()
        {
            try
            {
                TileUpdateManager.CreateTileUpdaterForApplication().Clear();
                BadgeUpdateManager.CreateBadgeUpdaterForApplication().Clear();
            }
            catch (Exception)
            {
                // A tile that will not clear is not worth failing over.
            }
        }

        private static void Write(int chats, List<string> names)
        {
            try
            {
                string headline = chats == 1 ? "1 chat waiting"
                                             : chats + " chats waiting";

                TileUpdateManager.CreateTileUpdaterForApplication()
                    .Update(new TileNotification(Xml(headline, names)));

                // The badge is the part that reads at a glance, and it is the only
                // part the small tile has room for.
                XmlDocument badge = BadgeUpdateManager.GetTemplateContent(
                    BadgeTemplateType.BadgeNumber);

                ((XmlElement)badge.SelectSingleNode("/badge"))
                    .SetAttribute("value", chats.ToString());

                BadgeUpdateManager.CreateBadgeUpdaterForApplication()
                    .Update(new BadgeNotification(badge));
            }
            catch (Exception)
            {
                // Same: the tile is the last thing that should take a refresh down.
            }
        }

        /// <summary>
        /// Builds the tile payload by hand.
        ///
        /// The template helpers return one binding at a time and merging two
        /// documents is more code than writing the four lines they would have
        /// produced. branding="name" keeps the app name on the tile once a
        /// notification replaces the default one - without it the name the manifest
        /// asks for disappears the moment the tile goes live.
        /// </summary>
        private static XmlDocument Xml(string headline, List<string> names)
        {
            var text = new StringBuilder();
            text.Append("<tile><visual version='2' branding='name'>");

            // Peek templates alternate: the logo, then the names. Without the image
            // the tile goes plain while it has something to say, which is the one
            // time anybody looks at it.
            Peek(text, "TileSquare150x150PeekImageAndText02",
                 "TileSquarePeekImageAndText02",
                 "ms-appx:///Assets/Square150x150Logo.png", headline, names, 3);

            Peek(text, "TileWide310x150PeekImageAndText02",
                 "TileWidePeekImageAndText02",
                 "ms-appx:///Assets/Wide310x150Logo.png", headline, names, 4);

            text.Append("</visual></tile>");

            var xml = new XmlDocument();
            xml.LoadXml(text.ToString());
            return xml;
        }

        /// <summary>
        /// A binding that carries a picture as well as the text.
        ///
        /// The image is the first child; the template expects it there, and an
        /// image element after the text is ignored rather than rejected - which
        /// looks exactly like the picture having failed to render.
        /// </summary>
        private static void Peek(StringBuilder text, string template, string fallback,
                                 string picture, string headline, List<string> names,
                                 int lines)
        {
            text.Append("<binding template='").Append(template)
                .Append("' fallback='").Append(fallback).Append("'>");

            text.Append("<image id='1' src='").Append(Escape(picture)).Append("'/>");
            text.Append("<text id='1'>").Append(Escape(headline)).Append("</text>");

            for (int i = 0; i < lines && i < names.Count; i++)
            {
                text.Append("<text id='").Append(i + 2).Append("'>")
                    .Append(Escape(names[i])).Append("</text>");
            }

            text.Append("</binding>");
        }

        private static void Binding(StringBuilder text, string template, string fallback,
                                    string headline, List<string> names, int lines)
        {
            text.Append("<binding template='").Append(template)
                .Append("' fallback='").Append(fallback).Append("'>");

            text.Append("<text id='1'>").Append(Escape(headline)).Append("</text>");

            for (int i = 0; i < lines && i < names.Count; i++)
            {
                text.Append("<text id='").Append(i + 2).Append("'>")
                    .Append(Escape(names[i])).Append("</text>");
            }

            text.Append("</binding>");
        }

        /// <summary>
        /// Chat titles are arbitrary text and go straight into markup, so an
        /// ampersand in someone's name would otherwise fail the whole document.
        /// </summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            return value.Replace("&", "&amp;")
                        .Replace("<", "&lt;")
                        .Replace(">", "&gt;")
                        .Replace("'", "&apos;")
                        .Replace("\"", "&quot;");
        }
    }
}
