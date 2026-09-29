using System;
using System.Threading.Tasks;
using Lumigram.Tl;

namespace Lumigram.Mtproto
{
    /// <summary>
    /// One account's authorisation, in the form it can be handed to another
    /// datacenter.
    /// </summary>
    public sealed class ExportedAuth
    {
        public long Id;
        public byte[] Bytes;
    }

    /// <summary>
    /// Lending the signed-in authorisation to a second datacenter.
    ///
    /// Telegram does not keep an account's files in one place. A photo lives on the
    /// datacenter nearest the phone that sent it, so a chat list of people in
    /// several countries routinely refers to files on three or four of them, and
    /// asking the wrong one answers FILE_MIGRATE_n rather than any kind of failure.
    ///
    /// Reaching n means a second connection, and a connection is a datacenter of its
    /// own with its own auth key - so the key negotiated there is a stranger's key
    /// until the account is imported onto it. That is the whole of this file: export
    /// on the datacenter already signed in, import on the new one.
    ///
    /// The exported credential is short-lived and single-use, so it is fetched for
    /// each datacenter as that datacenter is first needed rather than kept.
    /// </summary>
    public static class CrossDc
    {
        /// <summary>
        /// Asks the signed-in connection for a credential the given datacenter will
        /// accept.
        /// </summary>
        public static async Task<ExportedAuth> ExportAsync(MtprotoClient home, int dcId,
                                                           ClientInfo info = null)
        {
            var q = new TlWriter(16);
            q.WriteConstructor(TlConstructors.AuthExportAuthorization)
             .WriteInt(dcId);

            TlReader r = await home.InvokeAsync(q.ToArray(), info);
            TlObject o = TlSchema.ReadObject(r);

            if (o.Ctor != TlConstructors.AuthExportedAuthorization)
                throw new MtprotoException(
                    "unexpected auth.ExportedAuthorization 0x" + o.Ctor.ToString("x8"));

            return new ExportedAuth { Id = o.Long("id"), Bytes = o.Bytes("bytes") };
        }

        /// <summary>
        /// Signs a freshly built connection in as the same account.
        ///
        /// The first call on a new connection carries initConnection, which
        /// InvokeAsync arranges - so this is sent through the ordinary path rather
        /// than wrapped by hand, exactly as the login calls are.
        /// </summary>
        public static async Task ImportAsync(MtprotoClient target, ExportedAuth auth,
                                             ClientInfo info = null)
        {
            if (auth == null) throw new ArgumentNullException("auth");

            var q = new TlWriter(auth.Bytes.Length + 32);
            q.WriteConstructor(TlConstructors.AuthImportAuthorization)
             .WriteLong(auth.Id)
             .WriteBytes(auth.Bytes);

            TlReader r = await target.InvokeAsync(q.ToArray(), info);
            TlObject o = TlSchema.ReadObject(r);

            if (o.Ctor != TlConstructors.AuthAuthorization)
                throw new MtprotoException(
                    "unexpected auth.Authorization 0x" + o.Ctor.ToString("x8"));
        }
    }
}
