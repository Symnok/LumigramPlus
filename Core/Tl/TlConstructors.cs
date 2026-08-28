namespace Lumigram.Tl
{
    /// <summary>
    /// TL constructor ids.
    ///
    /// A constructor id is the CRC32 of the type's schema declaration, so these are
    /// fixed forever for a given definition - but a single wrong digit produces a
    /// message the server discards without explanation, which is close to
    /// undebuggable from the client side.
    ///
    /// Every handshake value below was cross-checked against the shipped Unigram
    /// implementation (Telegram.Api.Native, TLTypes.h / TLMethods.h / Datacenter.cpp)
    /// rather than transcribed from memory.
    /// </summary>
    public static class TlConstructors
    {
        // Generic
        public const uint Vector = 0x1cb5c415;
        public const uint BoolTrue = 0x997275b5;
        public const uint BoolFalse = 0xbc799737;

        // --- Auth key generation ---------------------------------------------
        // req_pq#60469778 nonce:int128 = ResPQ
        //
        // req_pq rather than the newer req_pq_multi#be7e8ef1: this is the value
        // confirmed present in a client known to have worked against these servers.
        // Both remain supported; if the server ever objects, req_pq_multi is the
        // drop-in replacement.
        public const uint ReqPQ = 0x60469778;

        // resPQ#05162463 nonce:int128 server_nonce:int128 pq:bytes
        //                server_public_key_fingerprints:Vector<long> = ResPQ
        public const uint ResPQ = 0x05162463;

        // p_q_inner_data#83c95aec pq:bytes p:bytes q:bytes nonce:int128
        //                         server_nonce:int128 new_nonce:int256 = P_Q_inner_data
        public const uint PQInnerData = 0x83c95aec;

        // req_DH_params#d712e4be nonce:int128 server_nonce:int128 p:bytes q:bytes
        //                        public_key_fingerprint:long encrypted_data:bytes = Server_DH_Params
        public const uint ReqDHParams = 0xd712e4be;

        // server_DH_params_ok#d0e8075c nonce:int128 server_nonce:int128 encrypted_answer:bytes
        public const uint ServerDHParamsOk = 0xd0e8075c;
        public const uint ServerDHParamsFail = 0x79cb045d;

        // server_DH_inner_data#b5890dba nonce:int128 server_nonce:int128 g:int
        //                               dh_prime:bytes g_a:bytes server_time:int
        public const uint ServerDHInnerData = 0xb5890dba;

        // client_DH_inner_data#6643b654 nonce:int128 server_nonce:int128
        //                               retry_id:long g_b:bytes
        public const uint ClientDHInnerData = 0x6643b654;

        // set_client_DH_params#f5045f1f nonce:int128 server_nonce:int128 encrypted_data:bytes
        public const uint SetClientDHParams = 0xf5045f1f;

        // dh_gen_ok#3bcbf734 nonce:int128 server_nonce:int128 new_nonce_hash1:int128
        public const uint DhGenOk = 0x3bcbf734;
        public const uint DhGenRetry = 0x46dc1fb9;
        public const uint DhGenFail = 0xa69dae02;

        // --- Service messages -------------------------------------------------
        public const uint RpcResult = 0xf35c6d01;
        public const uint RpcError = 0x2144ca19;
        public const uint MsgContainer = 0x73f1f8dc;
        public const uint BadServerSalt = 0xedab447b;
        public const uint BadMsgNotification = 0xa7eff811;
        public const uint NewSessionCreated = 0x9ec20908;
        public const uint Pong = 0x347773c5;

        // ping_delay_disconnect#f3427b8c ping_id:long disconnect_delay:int = Pong
        //
        // Keepalive. Telegram drops an idle connection, and a dropped connection
        // stops delivering updates silently - the socket looks fine until something
        // is written to it. The disconnect_delay also tells the server to hang up if
        // *we* go quiet, which is what makes the failure detectable instead of a
        // connection that is open but dead.
        public const uint PingDelayDisconnect = 0xf3427b8c;
        public const uint MsgsAck = 0x62d6b459;
        public const uint GzipPacked = 0x3072cfa1;

        // --- API layer 228 ----------------------------------------------------
        // Layer 73 (what Unigram shipped in 2018) reaches the server and is accepted
        // for unauthenticated calls, but auth.sendCode is refused outright with
        // "406 UPDATE_APP_TO_LOGIN" - Telegram enforces a modern layer for login
        // specifically. So the client speaks the current layer instead.
        //
        // Definitions below come from the TDLib schema at
        // C:\projects\td\td\generate\scheme\telegram_api.tl, with the layer number
        // from td/telegram/Version.h. Field layouts were read from that schema, not
        // assumed: several of these changed shape since layer 73 even where the
        // constructor id did not.
        public const int Layer = 228;

        // invokeWithLayer#da9b0d0d layer:int query:!X   (unchanged since layer 73)
        public const uint InvokeWithLayer = 0xda9b0d0d;

        // initConnection#c1cd5ea9 flags:# api_id:int device_model:string
        //   system_version:string app_version:string system_lang_code:string
        //   lang_pack:string lang_code:string proxy:flags.0?InputClientProxy
        //   params:flags.1?JSONValue query:!X
        // Gained a flags field since layer 73, where it had none.
        public const uint InitConnection = 0xc1cd5ea9;

        // auth.sendCode#a677244f phone_number:string api_id:int api_hash:string
        //   settings:CodeSettings
        // The old form inlined its options; they now live in a CodeSettings object.
        public const uint AuthSendCode = 0xa677244f;

        // codeSettings#ad253d78 flags:# ... - every field is optional, so an empty
        // flags word is a complete, valid value.
        public const uint CodeSettings = 0xad253d78;

        // auth.signIn#8d52a951 flags:# phone_number:string phone_code_hash:string
        //   phone_code:flags.0?string email_verification:flags.1?EmailVerification
        // phone_code became optional when e-mail sign-in was added.
        public const uint AuthSignIn = 0x8d52a951;

        // auth.sentCode#5e002502 flags:# type:auth.SentCodeType
        //   phone_code_hash:string next_type:flags.1?auth.CodeType timeout:flags.2?int
        // Same id as layer 73, but the phone_registered flag is gone.
        public const uint AuthSentCode = 0x5e002502;

        // auth.authorization#2ea2c0d4 flags:# setup_password_required:flags.1?true
        //   otherwise_relogin_days:flags.1?int tmp_sessions:flags.0?int
        //   future_auth_token:flags.2?bytes user:User
        public const uint AuthAuthorization = 0x2ea2c0d4;
        public const uint AuthSentCodeTypeApp = 0x3dbb5986;
        public const uint AuthSentCodeTypeSms = 0xc000bba2;

        // --- two-step verification (SRP) --------------------------------------
        // account.getPassword#548a30f5 = account.Password
        public const uint AccountGetPassword = 0x548a30f5;

        // account.password#957b50fb flags:# has_recovery:flags.0?true
        //   has_secure_values:flags.1?true has_password:flags.2?true
        //   current_algo:flags.2?PasswordKdfAlgo srp_B:flags.2?bytes
        //   srp_id:flags.2?long hint:flags.3?string ...
        public const uint AccountPassword = 0x957b50fb;

        // passwordKdfAlgoSHA256SHA256PBKDF2HMACSHA512iter100000SHA256ModPow#3a912d4a
        //   salt1:bytes salt2:bytes g:int p:bytes
        public const uint PasswordKdfAlgoSha256Pbkdf2 = 0x3a912d4a;
        public const uint PasswordKdfAlgoUnknown = 0xd45ab096;

        // inputCheckPasswordSRP#d27ff082 srp_id:long A:bytes M1:bytes
        public const uint InputCheckPasswordSrp = 0xd27ff082;

        // auth.checkPassword#d18b4d16 password:InputCheckPasswordSRP = auth.Authorization
        public const uint AuthCheckPassword = 0xd18b4d16;

        // --- messaging --------------------------------------------------------
        public const uint InputPeerEmpty = 0x7f3b18ea;
        public const uint InputPeerSelf = 0x7da07ec9;
        public const uint InputPeerUser = 0xdde8a54c;
        public const uint InputPeerChat = 0x35a95cb9;
        public const uint InputPeerChannel = 0x27bcbbfc;
        public const uint InputChannel = 0xf35aec28;
        public const uint ChannelsReadHistory = 0xcc104937;
        public const uint MessagesDeleteMessages = 0xe58e95d2;
        public const uint AccountUpdateNotifySettings = 0x84be5b93;
        public const uint InputNotifyPeer = 0xb8bc5b0c;
        public const uint InputPeerNotifySettings = 0xcacb6ae2;
        public const uint ChannelsDeleteMessages = 0x84c1fd4e;

        public const uint PeerUser = 0x59511722;
        public const uint PeerChat = 0x36c6019a;
        public const uint PeerChannel = 0xa2a5371e;

        // messages.getHistory#4423e6c5 peer:InputPeer offset_id:int offset_date:int
        //   add_offset:int limit:int max_id:int min_id:int hash:long
        public const uint MessagesGetHistory = 0x4423e6c5;

        // messages.sendMessage#fef48f62 flags:# ... peer:InputPeer
        //   reply_to:flags.0?InputReplyTo message:string random_id:long ...
        public const uint MessagesSendMessage = 0xfef48f62;

        // ---- voice calls -------------------------------------------------

        // messages.getDhConfig#26cf8950 version:int random_length:int
        //   = messages.DhConfig
        public const uint MessagesGetDhConfig = 0x26cf8950;
        public const uint MessagesDhConfig = 0x2c221edd;
        public const uint MessagesDhConfigNotModified = 0xc0e24635;

        // account.updateStatus#6628562c offline:Bool = Bool
        //
        // How a client says it is at the keyboard. The server rings online
        // sessions; one that has never said so is not obviously a phone anybody
        // is holding.
        public const uint AccountUpdateStatus = 0x6628562c;

        // inputUser#f21158c6 user_id:long access_hash:long = InputUser
        public const uint InputUser = 0xf21158c6;

        // inputPhoneCall#1e36fded id:long access_hash:long = InputPhoneCall
        public const uint InputPhoneCall = 0x1e36fded;

        // phoneCallProtocol#fc878fc8 flags:# udp_p2p:flags.0?true
        //   udp_reflector:flags.1?true min_layer:int max_layer:int
        //   library_versions:Vector<string> = PhoneCallProtocol
        public const uint PhoneCallProtocol = 0xfc878fc8;

        // The call, at each point it can be in.
        public const uint PhoneCallEmpty = 0x5366c915;
        public const uint PhoneCallWaiting = 0xc5226f17;
        public const uint PhoneCallRequested = 0x14b0ed0c;
        public const uint PhoneCallAccepted = 0x3660c311;
        public const uint PhoneCall = 0x30535af5;
        public const uint PhoneCallDiscarded = 0x50ca4de1;

        // phoneConnection#9cc123c7 - a reflector. The webrtc shape alongside it is
        // for a transport this client does not speak.
        public const uint PhoneConnection = 0x9cc123c7;
        public const uint PhoneConnectionWebrtc = 0x635fe375;

        public const uint PhoneCallDiscardReasonMissed = 0x85e42301;
        public const uint PhoneCallDiscardReasonDisconnect = 0xe095c1a0;
        public const uint PhoneCallDiscardReasonHangup = 0x57adc690;
        public const uint PhoneCallDiscardReasonBusy = 0xfaf7e8c9;

        public const uint PhoneRequestCall = 0x42ff96ed;
        public const uint PhoneAcceptCall = 0x3bd2b4a0;
        public const uint PhoneConfirmCall = 0x2efe1722;
        public const uint PhoneReceivedCall = 0x17d54f61;
        public const uint PhoneDiscardCall = 0xb2cbc1c0;
        public const uint PhoneGetCallConfig = 0x55451fa9;
        public const uint PhonePhoneCall = 0xec82e140;

        // updatePhoneCall#ab0f6b1e phone_call:PhoneCall = Update
        public const uint UpdatePhoneCall = 0xab0f6b1e;

        // inputReplyToMessage#3bd4b7c2 flags:# reply_to_msg_id:int
        //   top_msg_id:flags.0?int reply_to_peer_id:flags.1?InputPeer ...
        public const uint InputReplyToMessage = 0x3bd4b7c2;

        // messages.forwardMessages#13704a7c flags:# ... from_peer:InputPeer
        //   id:Vector<int> random_id:Vector<long> to_peer:InputPeer ...
        public const uint MessagesForwardMessages = 0x13704a7c;

        // messages.getDialogs#a0f4cb4f flags:# exclude_pinned:flags.0?true
        //   folder_id:flags.1?int offset_date:int offset_id:int
        //   offset_peer:InputPeer limit:int hash:long
        public const uint MessagesGetDialogs = 0xa0f4cb4f;

        public const uint MessagesMessages = 0x1d73e7ea;
        public const uint MessagesMessagesSlice = 0x5f206716;
        public const uint MessagesChannelMessages = 0xc776ba4e;

        // message#7600b9d3 - two flags words and ~35 optional fields; see Messages.cs
        public const uint Message = 0x7600b9d3;
        public const uint MessageEmpty = 0x90a6ca84;
        public const uint MessageService = 0x7a800e0a;

        // --- QR login ---------------------------------------------------------
        // auth.exportLoginToken#b7e085fe api_id:int api_hash:string
        //   except_ids:Vector<long> = auth.LoginToken
        public const uint AuthExportLoginToken = 0xb7e085fe;
        public const uint AuthImportLoginToken = 0x95ac5ce4;

        public const uint AuthLoginToken = 0x629f1980;          // expires:int token:bytes
        public const uint AuthLoginTokenMigrateTo = 0x068e9916; // dc_id:int token:bytes
        public const uint AuthLoginTokenSuccess = 0x390d5c5e;   // authorization:auth.Authorization

        // Pushed when another device accepts the token; the client then re-exports
        // to collect the authorisation.
        public const uint UpdateLoginToken = 0x564fe691;

        // auth.logOut#3e72ba19 = auth.LoggedOut
        public const uint AuthLogOut = 0x3e72ba19;
        public const uint AuthLoggedOut = 0xc3a2835f;

        // users.getUsers#d91a548 id:Vector<InputUser> = Vector<User>
        public const uint UsersGetUsers = 0x0d91a548;
        public const uint InputUserSelf = 0xf7c1b13f;

        // --- reading and clearing history ------------------------------------
        // messages.readHistory#0e306d3a peer:InputPeer max_id:int = messages.AffectedMessages
        //
        // Marks everything up to max_id as read, on the server. Without this the
        // unread count comes straight back on the next getDialogs, because the
        // server - not the client - owns it.
        public const uint MessagesReadHistory = 0x0e306d3a;

        // messages.deleteHistory#b08f922a flags:# just_clear:flags.0?true
        //   revoke:flags.1?true peer:InputPeer max_id:int ...
        //
        // just_clear empties the chat but keeps it in the list; without it the
        // dialog itself goes away. revoke additionally deletes for the other side.
        public const uint MessagesDeleteHistory = 0xb08f922a;

        public const uint MessagesAffectedMessages = 0x84d19185;
        public const uint MessagesAffectedHistory = 0xb45c69d1;

        public const uint UpdateReadHistoryInbox = 0x9e84bc99;
        public const uint UpdateReadHistoryOutbox = 0x2f2f21bf;

        // --- finding people ---------------------------------------------------
        // contacts.resolveUsername#725afbbc flags:# username:string
        //   referer:flags.0?string = contacts.ResolvedPeer
        public const uint ContactsResolveUsername = 0x725afbbc;

        // contacts.resolvePhone#8af94344 phone:string = contacts.ResolvedPeer
        //
        // Preferred over contacts.importContacts for a lookup: importing would add
        // the person to the account's contact list as a side effect, which is not
        // what "start a chat with this number" should do.
        public const uint ContactsResolvePhone = 0x8af94344;

        // contacts.resolvedPeer#7f077ad9 peer:Peer chats:Vector<Chat> users:Vector<User>
        public const uint ContactsResolvedPeer = 0x7f077ad9;

        // --- media -----------------------------------------------------------
        // upload.getFile#be5335be flags:# precise:flags.0?true cdn_supported:flags.1?true
        //   location:InputFileLocation offset:long limit:int = upload.File
        public const uint UploadGetFile = 0xbe5335be;
        public const uint UploadFile = 0x096a18d5;
        public const uint UploadFileCdnRedirect = 0xf18cda44;

        // inputPhotoFileLocation#40181ffe id:long access_hash:long
        //   file_reference:bytes thumb_size:string
        public const uint InputPhotoFileLocation = 0x40181ffe;
        public const uint InputDocumentFileLocation = 0xbad07584;

        // photo#fb197a65 flags:# ... id:long access_hash:long file_reference:bytes
        //   date:int sizes:Vector<PhotoSize> ... dc_id:int
        public const uint Photo = 0xfb197a65;
        public const uint PhotoEmpty = 0x2331b22d;

        // document#8fd4c4d8 flags:# id:long access_hash:long file_reference:bytes
        //   date:int mime_type:string size:long ... dc_id:int attributes:...
        public const uint Document = 0x8fd4c4d8;
        public const uint DocumentEmpty = 0x36f8c871;

        public const uint PhotoSize = 0x75c78e60;             // type w h size
        public const uint PhotoCachedSize = 0x021e1ad6;       // type w h bytes
        public const uint PhotoStrippedSize = 0xe0b0bc2e;     // type bytes
        public const uint PhotoSizeProgressive = 0xfa3efb95;  // type w h sizes:Vector<int>

        public const uint MessageMediaPhoto = 0xe216eb63;
        public const uint MessageMediaDocument = 0x52d8ccd9;
        public const uint MessageMediaGeo = 0x56e0d474;

        // Note the field order: inputGeoPoint is lat then long, geoPoint is long
        // then lat. Reversed between the two, and transposing them puts the message
        // in the wrong hemisphere without failing.
        public const uint InputMediaGeoPoint = 0xf9c44144;
        public const uint InputGeoPoint = 0x48222faf;
        public const uint GeoPoint = 0xb2a2f663;
        public const uint GeoPointEmpty = 0x1117dd5f;

        public const uint DocumentAttributeImageSize = 0x6c37c15c;
        public const uint DocumentAttributeVideo = 0x43c57c48;
        public const uint DocumentAttributeFilename = 0x15590068;
        public const uint DocumentAttributeAudio = 0x9852f9c6;

        /// <summary>documentAttributeAudio: voice:flags.10?true.</summary>
        public const int DocumentAttributeAudioVoiceFlag = 1 << 10;

        // --- uploading --------------------------------------------------------
        // upload.saveFilePart#b304a621 file_id:long file_part:int bytes:bytes = Bool
        public const uint UploadSaveFilePart = 0xb304a621;

        // upload.saveBigFilePart#de7b673d file_id:long file_part:int
        //   file_total_parts:int bytes:bytes = Bool
        public const uint UploadSaveBigFilePart = 0xde7b673d;

        // inputFile#f52ff27f id:long parts:int name:string md5_checksum:string
        public const uint InputFile = 0xf52ff27f;
        public const uint InputFileBig = 0xfa4f0bb5;

        // inputMediaUploadedPhoto#7d8375da flags:# ... file:InputFile ...
        public const uint InputMediaUploadedPhoto = 0x7d8375da;

        // inputMediaUploadedDocument#037c9330 flags:# ... file:InputFile
        //   thumb:flags.2?InputFile mime_type:string attributes:Vector<DocumentAttribute> ...
        public const uint InputMediaUploadedDocument = 0x037c9330;

        // messages.sendMedia#0330e77f flags:# ... peer:InputPeer
        //   reply_to:flags.0?InputReplyTo media:InputMedia message:string random_id:long ...
        public const uint MessagesSendMedia = 0x0330e77f;

        // --- updates ----------------------------------------------------------
        // updates.getState#edd4882a = updates.State
        public const uint UpdatesGetState = 0xedd4882a;
        public const uint UpdatesState = 0xa56c2a3e;

        // updates.getDifference#19c2f763 flags:# pts:int pts_limit:flags.1?int
        //   pts_total_limit:flags.0?int date:int qts:int qts_limit:flags.2?int
        public const uint UpdatesGetDifference = 0x19c2f763;
        public const uint UpdatesDifference = 0x00f49ca0;
        public const uint UpdatesDifferenceEmpty = 0x5d75a138;
        public const uint UpdatesDifferenceSlice = 0xa8fb1981;
        public const uint UpdatesDifferenceTooLong = 0x4afe8f6d;

        // Individual Update variants that carry a new message.
        public const uint UpdateNewMessage = 0x1f2b0afd;
        public const uint UpdateNewChannelMessage = 0x62ba04d9;
        public const uint UpdateShortMessage = 0x313bc7f8;
        public const uint UpdateShortChatMessage = 0x4d6deea5;
        public const uint UpdatesCombined = 0x725b04c3;

        public const uint Updates = 0x74ae4240;
        public const uint UpdateShort = 0x78d4dec1;
        public const uint UpdatesTooLong = 0xe317af7e;
        public const uint UpdateShortSentMessage = 0x9015e101;
        public const uint UpdateMessageID = 0x4e90bfd6;

        public const uint MessagesDialogsSlice = 0x71e094f3;

        public const uint MessagesGetDialogFilters = 0xefd48c89;
        public const uint DialogFilter = 0xaa472651;
        public const uint DialogFilterChatlist = 0x96537bd7;
        public const uint DialogFilterDefault = 0x363293ae;
        public const uint MessagesUpdateDialogFilter = 0x1ad4a04a;
        public const uint FoldersEditPeerFolders = 0x6847d0ab;
        public const uint InputFolderPeer = 0xfbd2c296;
        public const uint TextWithEntities = 0x751f3146;

        public const uint UserProfilePhoto = 0x82d1f706;
        public const uint ChatPhoto = 0x1c6e1c11;
        public const uint InputPeerPhotoFileLocation = 0x37257e99;

        // help.getNearestDc#1fb33026 = NearestDc
        // Needs no authorisation, so it is the cheapest way to prove the encrypted
        // layer works before attempting a login.
        public const uint HelpGetNearestDc = 0x1fb33026;
        public const uint NearestDc = 0x8e1a1775;
    }
}
