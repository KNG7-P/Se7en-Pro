namespace PsiphonUI.Services;

internal static class EmbeddedValues
{
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
        """[{"URL": "FEEDBACK_UPLOAD_URL_BASE64", "RequestHeaders": {"x-amz-acl": " bucket-owner-full-control"}, "OnlyAfterAttempts": 0, "SkipVerify": false}]""";
}
