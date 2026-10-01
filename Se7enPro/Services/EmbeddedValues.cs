namespace Se7enPro.Services;

internal static class EmbeddedValues
{
#if SE7EN_SECRETS

    private static readonly byte[] _e_PropagationChannelId = BuildSecrets.PropagationChannelId;
    private static readonly byte[] _e_SponsorId = BuildSecrets.SponsorId;
    private static readonly byte[] _e_ClientVersion = BuildSecrets.ClientVersion;
    private static readonly byte[] _e_ClientPlatform = BuildSecrets.ClientPlatform;
    private static readonly byte[] _e_RemoteServerListSignaturePublicKey = BuildSecrets.RemoteServerListSignaturePublicKey;
    private static readonly byte[] _e_ServerEntrySignaturePublicKey = BuildSecrets.ServerEntrySignaturePublicKey;
    private static readonly byte[] _e_FeedbackEncryptionPublicKey = BuildSecrets.FeedbackEncryptionPublicKey;
    private static readonly byte[] _e_RemoteServerListUrlsJson = BuildSecrets.RemoteServerListUrlsJson;
    private static readonly byte[] _e_ObfuscatedServerListRootUrlsJson = BuildSecrets.ObfuscatedServerListRootUrlsJson;
    private static readonly byte[] _e_FeedbackUploadUrlsJson = BuildSecrets.FeedbackUploadUrlsJson;

    public static string PropagationChannelId => SecretStore.DecryptString(_e_PropagationChannelId);
    public static string SponsorId => SecretStore.DecryptString(_e_SponsorId);
    public static string ClientVersion => SecretStore.DecryptString(_e_ClientVersion);
    public static string ClientPlatform => SecretStore.DecryptString(_e_ClientPlatform);
    public static string RemoteServerListSignaturePublicKey => SecretStore.DecryptString(_e_RemoteServerListSignaturePublicKey);
    public static string ServerEntrySignaturePublicKey => SecretStore.DecryptString(_e_ServerEntrySignaturePublicKey);
    public static string FeedbackEncryptionPublicKey => SecretStore.DecryptString(_e_FeedbackEncryptionPublicKey);
    public static string RemoteServerListUrlsJson => SecretStore.DecryptString(_e_RemoteServerListUrlsJson);
    public static string ObfuscatedServerListRootUrlsJson => SecretStore.DecryptString(_e_ObfuscatedServerListRootUrlsJson);
    public static string FeedbackUploadUrlsJson => SecretStore.DecryptString(_e_FeedbackUploadUrlsJson);

#else

    public const string PropagationChannelId = "PROPAGATION_CHANNEL_ID";
    public const string SponsorId = "SPONSOR_ID";
    public const string ClientVersion = "1";
    public const string ClientPlatform = "Windows";

    public const string RemoteServerListSignaturePublicKey =
        "REMOTE_SERVER_LIST_SIGNATURE_PUBLIC_KEY";

    public const string ServerEntrySignaturePublicKey =
        "SERVER_ENTRY_SIGNATURE_PUBLIC_KEY";

    public const string FeedbackEncryptionPublicKey =
        "FEEDBACK_ENCRYPTION_PUBLIC_KEY";

    public const string RemoteServerListUrlsJson =
        """[{"URL": "REMOTE_SERVER_LIST_URL_BASE64", "OnlyAfterAttempts": 0, "SkipVerify": false}]""";

    public const string ObfuscatedServerListRootUrlsJson =
        """[{"URL": "OBFUSCATED_SERVER_LIST_ROOT_URL_BASE64", "OnlyAfterAttempts": 0, "SkipVerify": false}]""";

    public const string FeedbackUploadUrlsJson =
        """[{"URL": "FEEDBACK_UPLOAD_URL_BASE64", "RequestHeaders": {"x-amz-acl": "bucket-owner-full-control"}, "OnlyAfterAttempts": 0, "SkipVerify": false}]""";

#endif
}