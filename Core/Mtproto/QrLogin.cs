using System;
using System.Threading.Tasks;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>What a QR login step produced.</summary>
    public enum QrLoginStatus
    {
        /// <summary>A token to display. Scan it from another signed-in Telegram.</summary>
        ShowToken,

        /// <summary>The account lives elsewhere; reconnect to <see cref="QrLoginStep.DcId"/>.</summary>
        Migrate,

        /// <summary>Signed in.</summary>
        Success,

        /// <summary>Signed in as far as the code goes, but two-step verification is required.</summary>
        PasswordNeeded,
    }

    public sealed class QrLoginStep
    {
        public QrLoginStatus Status;
        public byte[] Token;
        public int Expires;          // unix time
        public int DcId;

        /// <summary>The URL to render as a QR code.</summary>
        public string Url
        {
            get { return Token == null ? null : "tg://login?token=" + Base64Url(Token); }
        }

        public int SecondsRemaining
        {
            get
            {
                int now = (int)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                          .TotalSeconds;
                return Math.Max(0, Expires - now);
            }
        }

        /// <summary>
        /// Base64 with the URL-safe alphabet and no padding, which is what the
        /// tg://login scheme expects. Convert.ToBase64String gives the standard
        /// alphabet, so the three substitutions are applied here.
        /// </summary>
        public static string Base64Url(byte[] data)
        {
            return Convert.ToBase64String(data)
                          .Replace('+', '-')
                          .Replace('/', '_')
                          .TrimEnd('=');
        }
    }

    /// <summary>
    /// Signing in by QR code, for when a login code never arrives.
    ///
    /// The exchange is: ask the server for a token, render it as a QR code, and
    /// wait. Another device that is already signed in scans it and accepts it on
    /// the user's behalf; the server then pushes updateLoginToken to this session,
    /// and re-exporting the token returns the authorisation instead of a new token.
    ///
    /// No code is ever sent anywhere, which is the point: it sidesteps SMS and app
    /// delivery entirely. It does require another signed-in Telegram to scan with.
    ///
    /// Tokens expire - typically in under a minute - so the display has to refresh
    /// rather than show one code indefinitely.
    /// </summary>
    public static class QrLogin
    {
        /// <summary>
        /// Requests a login token, or collects the authorisation if one is ready.
        ///
        /// The same call does both jobs: before the token is accepted it returns a
        /// token to display, and afterwards it returns success. That is why polling
        /// it is a valid fallback when the push does not arrive.
        /// </summary>
        public static async Task<QrLoginStep> ExportTokenAsync(MtprotoClient client,
                                                               int apiId, string apiHash,
                                                               ClientInfo info = null)
        {
            var q = new TlWriter(64);
            q.WriteConstructor(TlConstructors.AuthExportLoginToken)
             .WriteInt(apiId)
             .WriteString(apiHash)
             .WriteConstructor(TlConstructors.Vector)
             .WriteInt(0);                         // except_ids: none

            try
            {
                TlReader r = await client.InvokeAsync(q.ToArray(), info);
                return Interpret(r);
            }
            catch (RpcException ex)
            {
                // Two-step verification is reported as an error rather than as one
                // of the auth.LoginToken results, so without this the status exists
                // in the enum and can never be returned - and every caller has to
                // remember the same catch. Getting it wrong is expensive: the token
                // has already been accepted at this point, so treating it as a
                // failure abandons a login that had all but succeeded.
                if ((ex.ErrorType ?? "").Contains("SESSION_PASSWORD_NEEDED"))
                    return new QrLoginStep { Status = QrLoginStatus.PasswordNeeded };

                throw;
            }
        }

        /// <summary>
        /// Presents a token to a different datacenter after a migrate response.
        /// The token from the first datacenter is not usable there directly - it has
        /// to be imported.
        /// </summary>
        public static async Task<QrLoginStep> ImportTokenAsync(MtprotoClient client, byte[] token,
                                                               ClientInfo info = null)
        {
            // Only steps that carry a token can be imported. Without this the
            // failure is a null reference from inside a TlWriter, which says nothing
            // about the caller having polled with a result that had already finished
            // the login.
            if (token == null)
                throw new ArgumentNullException("token", "this login step has no token to import");

            var q = new TlWriter(token.Length + 16);
            q.WriteConstructor(TlConstructors.AuthImportLoginToken)
             .WriteBytes(token);

            TlReader r = await client.InvokeAsync(q.ToArray(), info);
            return Interpret(r);
        }

        private static QrLoginStep Interpret(TlReader r)
        {
            TlObject o = TlSchema.ReadObject(r);

            if (o.Ctor == TlConstructors.AuthLoginToken)
            {
                return new QrLoginStep
                {
                    Status = QrLoginStatus.ShowToken,
                    Token = o.Bytes("token"),
                    Expires = o.IntOr("expires", 0),
                };
            }

            if (o.Ctor == TlConstructors.AuthLoginTokenMigrateTo)
            {
                return new QrLoginStep
                {
                    Status = QrLoginStatus.Migrate,
                    DcId = o.IntOr("dc_id", 0),
                    Token = o.Bytes("token"),
                };
            }

            if (o.Ctor == TlConstructors.AuthLoginTokenSuccess)
                return new QrLoginStep { Status = QrLoginStatus.Success };

            throw new MtprotoException("unexpected auth.LoginToken 0x" + o.Ctor.ToString("x8"));
        }

        /// <summary>True if this pushed update means the token was accepted.</summary>
        public static bool IsTokenAccepted(TlObject pushed)
        {
            if (pushed == null) return false;
            if (pushed.Ctor == TlConstructors.UpdateLoginToken) return true;

            // It usually arrives wrapped in updateShort.
            if (pushed.Ctor == TlConstructors.UpdateShort && pushed.Has("update"))
                return pushed.Obj("update").Ctor == TlConstructors.UpdateLoginToken;

            if (pushed.Ctor == TlConstructors.Updates || pushed.Ctor == TlConstructors.UpdatesCombined)
            {
                foreach (object o in pushed.Vec("updates"))
                    if (((TlObject)o).Ctor == TlConstructors.UpdateLoginToken) return true;
            }
            return false;
        }
    }
}
