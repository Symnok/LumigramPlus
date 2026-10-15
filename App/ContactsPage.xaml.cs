using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.Contacts;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Lumigram.Mtproto;

namespace LumigramPlus.App
{
    /// <summary>One number of one person in the phone's address book.</summary>
    public sealed class ContactRow
    {
        public string Name { get; set; }
        public string Number { get; set; }

        /// <summary>"mobile", "work"... or empty.</summary>
        public string Kind { get; set; }

        public string Initial
        {
            get { return string.IsNullOrEmpty(Name) ? "#" : Name.Substring(0, 1).ToUpperInvariant(); }
        }

        public string Detail
        {
            get { return string.IsNullOrEmpty(Kind) ? Number : Kind + "   " + Number; }
        }
    }

    /// <summary>
    /// Writing to someone from the phone's address book.
    ///
    /// The address book is read on the phone and stays there. Nothing is uploaded:
    /// only the one number tapped is looked up, with contacts.resolvePhone, the
    /// same lookup as the find page - which reads, and so does not add the person
    /// to the account's contacts the way contacts.importContacts would.
    ///
    /// The one real difficulty is that address books hold numbers the way people
    /// dial them, mostly without a country code. PhoneNumbers turns each into the
    /// international forms worth trying, using the calling code of the user's own
    /// number; a number nobody on Telegram has is reported as such.
    /// </summary>
    public sealed partial class ContactsPage : Page
    {
        /// <summary>
        /// The user's own calling code, worked out once per run. Two requests'
        /// worth, and it does not change while the app is open.
        /// </summary>
        private static string _ownCode;
        private static bool _ownCodeKnown;

        private List<ContactRow> _rows = new List<ContactRow>();
        private bool _resolving;

        public ContactsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await LoadAsync();
        }

        private async void Reload_Click(object sender, RoutedEventArgs e)
        {
            await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SetBusy(true, "Reading the address book...");

            try
            {
                _rows = await ReadPhoneBookAsync();
                ApplySearch();

                SetBusy(false, _rows.Count == 0
                    ? "There are no phone numbers in the address book."
                    : "Tap a number to write to that person on Telegram.");
            }
            catch (UnauthorizedAccessException)
            {
                SetBusy(false, "Lumigram+ is not allowed to read the contacts. It can be " +
                               "allowed in the phone's settings, under applications.");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Could not read the contacts: " + ex.Message);
            }
        }

        /// <summary>Every number in the address book, sorted by name.</summary>
        private static async Task<List<ContactRow>> ReadPhoneBookAsync()
        {
            ContactStore store = await ContactManager.RequestStoreAsync();
            if (store == null) throw new UnauthorizedAccessException();

            IReadOnlyList<Contact> contacts = await store.FindContactsAsync();

            var rows = new List<ContactRow>();
            foreach (Contact contact in contacts)
            {
                if (contact.Phones == null) continue;

                string name = NameOf(contact);
                var seen = new HashSet<string>();

                foreach (ContactPhone phone in contact.Phones)
                {
                    if (phone == null || string.IsNullOrEmpty(phone.Number)) continue;

                    // The same number saved twice, as mobile and as home, is one
                    // row: tapping either would do exactly the same thing.
                    string digits = PhoneNumbers.Digits(phone.Number);
                    if (digits.Length == 0 || !seen.Add(digits)) continue;

                    rows.Add(new ContactRow
                    {
                        Name = string.IsNullOrEmpty(name) ? phone.Number : name,
                        Number = phone.Number.Trim(),
                        Kind = KindOf(phone.Kind),
                    });
                }
            }

            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return rows;
        }

        private static string NameOf(Contact contact)
        {
            if (!string.IsNullOrEmpty(contact.DisplayName)) return contact.DisplayName.Trim();

            string joined = ((contact.FirstName ?? "") + " " + (contact.LastName ?? "")).Trim();
            return joined;
        }

        private static string KindOf(ContactPhoneKind kind)
        {
            switch (kind)
            {
                case ContactPhoneKind.Mobile: return "mobile";
                case ContactPhoneKind.Home: return "home";
                case ContactPhoneKind.Work: return "work";
                default: return "";
            }
        }

        // ---- search ------------------------------------------------------------

        private void Search_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearch();
        }

        private void ApplySearch()
        {
            string text = (SearchBox.Text ?? "").Trim();
            string digits = PhoneNumbers.Digits(text);

            // With no letters in it, it is matched against the number's digits, so
            // "555 1234" finds "(617) 555-1234"; anything else against the name.
            bool byNumber = digits.Length > 0;
            foreach (char c in text)
                if (char.IsLetter(c)) byNumber = false;

            if (text.Length == 0)
            {
                ContactList.ItemsSource = _rows;
                return;
            }

            var shown = new List<ContactRow>();
            foreach (ContactRow row in _rows)
            {
                bool match = byNumber
                    ? PhoneNumbers.Digits(row.Number).Contains(digits)
                    : row.Name.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0;

                if (match) shown.Add(row);
            }

            ContactList.ItemsSource = shown;
        }

        // ---- opening a chat ----------------------------------------------------

        private async void Contact_Click(object sender, ItemClickEventArgs e)
        {
            var row = e.ClickedItem as ContactRow;
            if (row == null || _resolving) return;

            _resolving = true;
            SetBusy(true, "Looking for " + row.Name + " on Telegram...");

            try
            {
                MtprotoClient client = await TelegramService.ConnectAsync();

                string ownCode = await OwnCodeAsync(client);
                List<string> tries = PhoneNumbers.Candidates(row.Number, ownCode);

                if (tries.Count == 0)
                {
                    SetBusy(false, row.Number + " has no country code, and the one of your own " +
                                   "number could not be found to fill it in. Save it with a + " +
                                   "and the country code.");
                    return;
                }

                ResolvedPeer peer = await ResolveFirstAsync(client, tries);

                if (peer == null)
                {
                    SetBusy(false, row.Name + " (" + row.Number + ") is not on Telegram - or lets " +
                                   "only their own contacts find them by number.");
                    return;
                }

                SetBusy(false, "");

                var item = new DialogItem
                {
                    PeerId = peer.PeerId,
                    AccessHash = peer.AccessHash,
                    Kind = peer.Kind,
                    // The name the user saved, which is how they know this person;
                    // the Telegram profile name may be anything.
                    Title = string.IsNullOrEmpty(peer.Title) || peer.Title.StartsWith("id ")
                        ? row.Name : peer.Title,
                };

                Frame.Navigate(typeof(ConversationPage), item);
            }
            catch (Exception ex)
            {
                var rpc = ex as RpcException;
                string error = rpc != null ? rpc.ErrorType : ex.Message;

                SetBusy(false, error != null && error.StartsWith("FLOOD_WAIT")
                    ? "Telegram limits how many numbers can be looked up. Try again in a while."
                    : "Could not look that up: " + error);
            }
            finally
            {
                _resolving = false;
            }
        }

        /// <summary>
        /// The first candidate Telegram knows, or null if none.
        ///
        /// Tried one at a time and in order, and stopping at the first hit: each
        /// lookup counts against a rate limit, and a list of two is the most it
        /// ever is.
        /// </summary>
        private static async Task<ResolvedPeer> ResolveFirstAsync(MtprotoClient client, List<string> tries)
        {
            foreach (string number in tries)
            {
                try
                {
                    return await Contacts.ResolveAsync(client, "+" + number, TelegramService.Info);
                }
                catch (RpcException ex)
                {
                    // Nobody has it, or it is not a number at all in this reading:
                    // either way the next reading may still be right.
                    if (ex.ErrorType != "PHONE_NOT_OCCUPIED" &&
                        ex.ErrorType != "PHONE_NUMBER_INVALID") throw;
                }
            }

            return null;
        }

        /// <summary>
        /// The calling code of the user's own number, or null if it cannot be
        /// found - then only numbers written with a country code can be looked up.
        /// </summary>
        private static async Task<string> OwnCodeAsync(MtprotoClient client)
        {
            if (_ownCodeKnown) return _ownCode;

            try
            {
                string self = await PhoneNumbers.GetSelfPhoneAsync(client, TelegramService.Info);
                if (self == null) return null;

                List<string> codes = await PhoneNumbers.GetCallingCodesAsync(client, TelegramService.Info);
                _ownCode = PhoneNumbers.OwnCode(self, codes);
                _ownCodeKnown = _ownCode != null;
                return _ownCode;
            }
            catch (RpcException)
            {
                // Not fatal: numbers with a + still work, and the next tap tries again.
                return null;
            }
        }

        private void SetBusy(bool busy, string status)
        {
            Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = status ?? "";
        }
    }
}
