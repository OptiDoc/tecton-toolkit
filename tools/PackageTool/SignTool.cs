using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Pack;

/// <summary>Подпись архива Эдвардс-25519: сообщение «id|version|sha256», ключ base64 (32 байта).</summary>
internal static class SignTool
{
    public static (string PrivateKey, string PublicKey) Keygen()
    {
        var random = new SecureRandom();
        var seed = new byte[32];
        random.NextBytes(seed);
        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        return (Convert.ToBase64String(seed), Convert.ToBase64String(privateKey.GeneratePublicKey().GetEncoded()));
    }

    /// <summary>Публичный ключ из приватного (base64, 32 байта).</summary>
    public static string PublicFromPrivate(string privateKeyBase64)
    {
        var seed = DecodeKey(privateKeyBase64, 32, "приватный ключ");
        var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
        return Convert.ToBase64String(privateKey.GeneratePublicKey().GetEncoded());
    }

    public static string Sign(string privateKeyBase64, string message)
    {
        var seed = DecodeKey(privateKeyBase64, 32, "приватный ключ");
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed, 0));
        var body = System.Text.Encoding.UTF8.GetBytes(message);
        signer.BlockUpdate(body, 0, body.Length);
        return "ed25519:" + Convert.ToBase64String(signer.GenerateSignature());
    }

    public static bool Verify(string publicKeyBase64, string message, string signature)
    {
        var publicBytes = DecodeKey(publicKeyBase64, 32, "публичный ключ");
        var signatureBytes = DecodeSignature(signature);
        var body = System.Text.Encoding.UTF8.GetBytes(message);
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicBytes, 0));
        verifier.BlockUpdate(body, 0, body.Length);
        return verifier.VerifySignature(signatureBytes);
    }

    /// <summary>Ключ — base64 (32 байта) либо «@путь/к/файлу».</summary>
    public static string ResolveKey(string key)
    {
        if (key.StartsWith('@'))
        {
            var path = key[1..];
            if (!File.Exists(path))
            {
                throw new PackException($"файл ключа не найден: {path}");
            }

            return File.ReadAllText(path).Trim();
        }

        return key.Trim();
    }

    private static byte[] DecodeKey(string value, int expectedLength, string what)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(value.Trim());
        }
        catch (FormatException)
        {
            throw new PackException($"{what} не является base64");
        }

        if (bytes.Length != expectedLength)
        {
            throw new PackException($"{what} должен содержать {expectedLength} байт, получено {bytes.Length}");
        }

        return bytes;
    }

    private static byte[] DecodeSignature(string signature)
    {
        var text = signature.Trim();
        if (text.StartsWith("ed25519:", StringComparison.Ordinal))
        {
            text = text["ed25519:".Length..];
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new PackException("подпись не является base64");
        }

        if (bytes.Length != 64)
        {
            throw new PackException($"подпись должна содержать 64 байта, получено {bytes.Length}");
        }

        return bytes;
    }
}
