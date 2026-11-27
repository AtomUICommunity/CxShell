using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace CxShell.Services;

/// <summary>
/// Decrypts the values mRemoteNG writes into confCons.xml. Two engines exist
/// on disk: the current authenticated encryption (AES with GCM or CCM, key
/// derived through PBKDF2/HMAC-SHA1) and the legacy unauthenticated AES-CBC
/// scheme that older files still carry. Passwords are optional in mRemoteNG:
/// without a master password the file is keyed with a built-in default, which
/// is why <see cref="DefaultPassword"/> is not a secret.
/// </summary>
internal static class MRemoteNgCrypto
{
    /// <summary>Value mRemoteNG uses when the user did not set a master password.</summary>
    public const string DefaultPassword = "mR3m";

    private const int SaltSize = 16;
    private const int TagSize = 16;
    private const int GcmNonceSize = 16;
    private const int CcmNonceSize = 11;

    public static bool IsSupportedEngine(string? engine) =>
        string.IsNullOrWhiteSpace(engine) ||
        engine.Equals("AES", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedMode(string? mode) =>
        string.IsNullOrWhiteSpace(mode) ||
        mode.Equals("GCM", StringComparison.OrdinalIgnoreCase) ||
        mode.Equals("CCM", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decrypts one base64 value. The authenticated layout is
    /// <c>[salt][nonce][ciphertext+tag]</c> with the salt also bound as
    /// associated data; the legacy layout is <c>[iv][ciphertext]</c>.
    /// </summary>
    public static string Decrypt(
        string base64Value,
        string password,
        string? engine,
        string? mode,
        int iterations)
    {
        if (string.IsNullOrWhiteSpace(base64Value))
            return string.Empty;

        var payload = Convert.FromBase64String(base64Value.Trim());
        return IsSupportedEngine(engine)
            ? DecryptAuthenticated(payload, password, mode, iterations)
            : DecryptLegacy(payload, password);
    }

    private static string DecryptAuthenticated(byte[] payload, string password, string? mode, int iterations)
    {
        var nonceSize = string.Equals(mode, "CCM", StringComparison.OrdinalIgnoreCase)
            ? CcmNonceSize
            : GcmNonceSize;

        if (payload.Length < SaltSize + nonceSize + TagSize)
            throw new CryptographicException("The encrypted value is too short.");

        var salt = payload.AsSpan(0, SaltSize).ToArray();
        var nonce = payload.AsSpan(SaltSize, nonceSize).ToArray();
        var cipherOffset = SaltSize + nonceSize;
        var cipherLength = payload.Length - cipherOffset;

        // mRemoteNG refuses anything below 1000; clamp the same way so a
        // hand-edited file cannot turn into a CPU burn.
        var effectiveIterations = Math.Max(iterations, 1000);
        var key = DeriveKey(password, salt, effectiveIterations);

        // mRemoteNG uses a 128-bit GCM nonce, which System.Security.Cryptography
        // rejects, so the ciphertext is unwrapped with BouncyCastle - the same
        // library mRemoteNG itself calls.
        IAeadBlockCipher cipher = nonceSize == CcmNonceSize
            ? new CcmBlockCipher(new AesEngine())
            : new GcmBlockCipher(new AesEngine());

        cipher.Init(false, new AeadParameters(new KeyParameter(key), TagSize * 8, nonce, salt));

        var plainText = new byte[cipher.GetOutputSize(cipherLength)];
        var written = cipher.ProcessBytes(payload, cipherOffset, cipherLength, plainText, 0);
        try
        {
            written += cipher.DoFinal(plainText, written);
        }
        catch (InvalidCipherTextException exception)
        {
            // Wrong key: the auth tag does not verify. Surface it as the same
            // exception type the framework ciphers use so callers have one
            // failure branch for "this password does not open the file".
            throw new CryptographicException("The encrypted value failed authentication.", exception);
        }

        return Encoding.UTF8.GetString(plainText, 0, written);
    }

    private static string DecryptLegacy(byte[] payload, string password)
    {
        if (payload.Length <= SaltSize)
            throw new CryptographicException("The encrypted value is too short.");

        var key = MD5.HashData(Encoding.UTF8.GetBytes(password));
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = payload.AsSpan(0, SaltSize).ToArray();

        var plainText = aes.DecryptCbc(
            payload.AsSpan(SaltSize).ToArray(),
            aes.IV,
            PaddingMode.PKCS7);
        return Encoding.UTF8.GetString(plainText);
    }

    private static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        // Pkcs5PasswordToBytes in BouncyCastle (what mRemoteNG calls) casts
        // each char to a byte rather than encoding the string.
        var passwordBytes = new byte[password.Length];
        for (var index = 0; index < password.Length; index++)
            passwordBytes[index] = (byte)password[index];

        return Rfc2898DeriveBytes.Pbkdf2(
            passwordBytes,
            salt,
            iterations,
            HashAlgorithmName.SHA1,
            32);
    }
}
