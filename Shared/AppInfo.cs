using Lumigram.Mtproto;

namespace Lumigram.Phone.Services
{
    /// <summary>
    /// What the client tells Telegram about itself.
    ///
    /// Lives here so the credentials stay internal to one assembly: the agent
    /// needs a ClientInfo, not the api_id and api_hash themselves, and there is no
    /// reason to widen access to Secrets just to build one.
    /// </summary>
    public static class AppInfo
    {
        public static ClientInfo Create()
        {
            var info = ClientInfo.Default;
            info.ApiId = Secrets.ApiId;
            info.ApiHash = Secrets.ApiHash;
            info.DeviceModel = "Windows Phone";
            info.SystemVersion = "8.1";
            info.AppVersion = "Lumigram+";
            return info;
        }
    }
}
