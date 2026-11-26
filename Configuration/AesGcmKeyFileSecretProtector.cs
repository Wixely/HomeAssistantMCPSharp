using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HomeAssistantMCPSharp.Configuration;

/// <summary>
/// AES-256-GCM protection whose key-file permissions form the trust boundary.
/// Blob format: version byte, 12-byte nonce, ciphertext, then 16-byte tag.
/// </summary>
internal sealed class AesGcmKeyFileSecretProtector : ISecretProtector
{
    internal const int KeyLengthBytes = 32;
    private const int NonceLengthBytes = 12;
    private const int TagLengthBytes = 16;
    private const byte FormatVersion = 1;

    private readonly string _keyPath;
    private readonly Action<string>? _onKeyCreated;
    private readonly Lazy<byte[]> _key;

    public AesGcmKeyFileSecretProtector(string keyPath, Action<string>? onKeyCreated = null)
    {
        _keyPath = keyPath;
        _onKeyCreated = onKeyCreated;
        _key = new Lazy<byte[]>(LoadOrCreateKey);
    }

    public string Scheme => "aesgcm-keyfile";

    public string Protect(string plaintext)
    {
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLengthBytes);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[TagLengthBytes];

        using var aes = new AesGcm(_key.Value, TagLengthBytes);
        aes.Encrypt(nonce, plain, ciphertext, tag);

        var blob = new byte[1 + NonceLengthBytes + ciphertext.Length + TagLengthBytes];
        blob[0] = FormatVersion;
        Buffer.BlockCopy(nonce, 0, blob, 1, NonceLengthBytes);
        Buffer.BlockCopy(ciphertext, 0, blob, 1 + NonceLengthBytes, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, blob, 1 + NonceLengthBytes + ciphertext.Length, TagLengthBytes);
        return Convert.ToBase64String(blob);
    }

    public string Unprotect(string ciphertext)
    {
        var blob = Convert.FromBase64String(ciphertext);
        if (blob.Length < 1 + NonceLengthBytes + TagLengthBytes)
        {
            throw new CryptographicException("Protected blob is too short.");
        }

        if (blob[0] != FormatVersion)
        {
            throw new CryptographicException($"Unsupported protected-blob version: 0x{blob[0]:X2}.");
        }

        var ciphertextLength = blob.Length - 1 - NonceLengthBytes - TagLengthBytes;
        var nonce = blob.AsSpan(1, NonceLengthBytes);
        var encrypted = blob.AsSpan(1 + NonceLengthBytes, ciphertextLength);
        var tag = blob.AsSpan(1 + NonceLengthBytes + ciphertextLength, TagLengthBytes);
        var plain = new byte[ciphertextLength];

        using var aes = new AesGcm(_key.Value, TagLengthBytes);
        aes.Decrypt(nonce, encrypted, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private byte[] LoadOrCreateKey()
    {
        var directory = Path.GetDirectoryName(_keyPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(_keyPath))
        {
            var existing = File.ReadAllBytes(_keyPath);
            if (existing.Length != KeyLengthBytes)
            {
                throw new CryptographicException(
                    $"Key file '{_keyPath}' is {existing.Length} bytes; expected {KeyLengthBytes}.");
            }

            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(KeyLengthBytes);
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var keyFile = new FileStream(_keyPath, fileOptions))
        {
            keyFile.Write(key);
            keyFile.Flush(flushToDisk: true);
        }

        _onKeyCreated?.Invoke(_keyPath);
        return key;
    }
}
