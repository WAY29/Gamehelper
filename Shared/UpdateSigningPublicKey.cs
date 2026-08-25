namespace Shared.UpdateSecurity
{
    /// <summary>
    ///     RSA public key for manifest.sig verification.
    ///     Regenerate via scripts/ensure-update-signing-key.ps1
    /// </summary>
    internal static class UpdateSigningPublicKey
    {
        internal const string Pem = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAwd0F8sjTSm3fiOHQe/xE5Z9zfybXiQx8
1Fhweqejd8dWX17Tt8oMmoJ0K6Ll7AgnqJhCNY1kFzL6fGcsLbw+wxvFtcPWeIUBrvo2fl5omb/0
POdjabLJy1pyUFWHh6/1g01RjNlSt+zgZYDSSF3YQ4S6w+E27epVKpAF7CWiZJEUB7eCSNogtLOv
DZe/r5N74yPLjq80366D6Dlpq+ioZ7YuRDDkhxsZaFHEJzo+2VWe2omPn6mtbR4EIpLFVHSzATE0
JtRFIPnOM3dnUfbUHim7jDm2N0HDAy+4IjQyNRQIJ/GAVccZJeirNXz5WfI6qUbkE6sQ/BR18K6l
J7I2vQIDAQAB
-----END PUBLIC KEY-----";
    }
}