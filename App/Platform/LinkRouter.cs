using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using Lumigram.Mtproto;
using Lumigram.Tl;

namespace LumigramPlus.App
{
    /// <summary>
    /// Following a link that points back into Telegram.
    ///
    /// A t.me address and an @mention are the same request wearing different
    /// clothes: open this peer, possibly at this message. What differs is only how
    /// the peer is named, and that decides how it is found - a username is asked of
    /// the server, where an internal id can only be matched against the chats this
    /// account is already in.
    ///
    /// Sending one of these to the browser instead, which is what happens without
    /// this, lands the user on a page telling them to install Telegram.
    /// </summary>
    internal static class LinkRouter
    {
        /// <summary>How many chats to look through for a t.me/c/ link.</summary>
        private const int SearchPages = 5;
        private const int PageSize = 100;

        /// <summary>
        /// Opens what the link points at, or returns why it could not.
        ///
        /// Null means it worked. The caller gets a sentence rather than an exception
        /// because every reason this fails is something the user can understand and
        /// nothing they can be asked to retry.
        /// </summary>
        public static async Task<string> OpenAsync(Frame frame, TelegramLink link)
        {
            if (frame == null || link == null) return "That link could not be read.";

            MtprotoClient client = await TelegramService.ConnectAsync();

            DialogItem peer = link.IsPrivate
                ? await FindByIdAsync(client, link.ChannelId)
                : await ResolveAsync(client, link.Username);

            if (peer == null)
            {
                return link.IsPrivate
                    ? "That link points inside a chat this account is not in."
                    : "No account or chat found for @" + link.Username + ".";
            }

            frame.Navigate(typeof(ConversationPage), new ConversationRequest
            {
                Peer = peer,
                TopicId = link.TopicId,
                FocusMessageId = link.MessageId,

                // Where to land when the link named no message.
                //
                // A chat found in the chat list brings its real read marker, so it
                // opens where the reading stopped, as tapping it in the list would.
                // A chat resolved from a username does not - it may not even be one
                // this account is in - and int.MaxValue is how that is said: nothing
                // here is known to be unread, so open at the newest rather than
                // drawing an unread line in an arbitrary place.
                ReadInboxMaxId = peer.ReadInboxMaxId != 0 ? peer.ReadInboxMaxId : int.MaxValue,
            });

            return null;
        }

        private static async Task<DialogItem> ResolveAsync(MtprotoClient client, string username)
        {
            try
            {
                ResolvedPeer found = await Contacts.ResolveAsync(
                    client, username, TelegramService.Info);

                if (found == null) return null;

                return new DialogItem
                {
                    PeerId = found.PeerId,
                    AccessHash = found.AccessHash,
                    Kind = found.Kind,
                    Title = found.Title,
                    Username = found.Username,
                };
            }
            catch (RpcException)
            {
                // USERNAME_NOT_OCCUPIED and USERNAME_INVALID both mean the same
                // thing to the person who tapped it: there is nobody there.
                return null;
            }
        }

        /// <summary>
        /// Finds a chat by its own id, among the ones this account is in.
        ///
        /// There is no lookup for this. An id addresses nothing on its own - every
        /// request needs the access hash that comes with it - and the only place
        /// this client holds hashes is the chat list. So a t.me/c/ link works for a
        /// member of that chat and for nobody else, which is also exactly what the
        /// link means: it is the form Telegram gives out for chats that have no
        /// public address.
        ///
        /// The archive is searched too. A link to a chat is no less valid for the
        /// chat having been archived, and that is an easy place to look and a
        /// baffling exception to hit.
        /// </summary>
        private static async Task<DialogItem> FindByIdAsync(MtprotoClient client, long channelId)
        {
            DialogItem found = await SearchFolderAsync(client, channelId, 0);
            if (found != null) return found;

            return await SearchFolderAsync(client, channelId, Folders.ArchiveFolderId);
        }

        private static async Task<DialogItem> SearchFolderAsync(MtprotoClient client,
                                                                long channelId, int folderId)
        {
            int offsetDate = 0;
            int offsetId = 0;
            byte[] offsetPeer = null;

            for (int page = 0; page < SearchPages; page++)
            {
                Messages.DialogPage list = await Messages.GetDialogPageAsync(
                    client, PageSize, offsetDate, offsetId, offsetPeer,
                    TelegramService.Info, folderId);

                foreach (DialogEntry d in list.Entries)
                {
                    if (d.Kind != "channel" || d.PeerId != channelId) continue;

                    return new DialogItem
                    {
                        PeerId = d.PeerId,
                        AccessHash = d.AccessHash,
                        Kind = d.Kind,
                        Title = d.Title,
                        Username = d.Username,
                        ReadInboxMaxId = d.ReadInboxMaxId,
                        IsForum = d.IsForum,
                        Entry = d,
                    };
                }

                if (!list.HasMore || list.Entries.Count == 0) break;

                DialogEntry last = list.Entries[list.Entries.Count - 1];
                offsetDate = last.TopMessageDate;
                offsetId = last.TopMessageId;
                offsetPeer = Messages.InputPeerFor(last);
            }

            return null;
        }
    }
}
