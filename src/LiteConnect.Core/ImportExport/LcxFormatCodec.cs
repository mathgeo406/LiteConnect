using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LiteConnect.Core.Serialization;

namespace LiteConnect.Core.ImportExport;

/// <summary>
/// Implements the binary <c>.lcx</c> file format specification (§2.5):
/// <list type="bullet">
///   <item>0x00 (4B): Magic <c>"LCX\x01"</c></item>
///   <item>0x04 (2B): Format version (<c>uint16</c> = 1)</item>
///   <item>0x06 (1B): Flags (bit 0 = credentials, bit 1 = SSH keys, bit 2 = AES-256-GCM encrypted)</item>
///   <item>0x07 (32B): PBKDF2 salt</item>
///   <item>0x27 (12B): AES-GCM nonce/IV</item>
///   <item>0x33 (4B): Payload length (<c>uint32</c>)</item>
///   <item>0x37 (NB): Payload bytes</item>
///   <item>0x37+N (16B): AES-GCM authentication tag</item>
/// </list>
/// </summary>
public static class LcxFormatCodec
{
    public static ReadOnlySpan<byte> MagicBytes => [0x4C, 0x43, 0x58, 0x01]; // "LCX\x01"
    public const ushort CurrentVersion = 1;
    public const int HeaderSize = 0x37; // 55 bytes
    public const int SaltSize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    private const int Pbkdf2Iterations = 100_000;

    private const byte FlagIncludesCredentials = 0x01;
    private const byte FlagIncludesSshKeys     = 0x02;
    private const byte FlagIsEncrypted         = 0x04;

    public static bool TryInspectHeader(
        ReadOnlySpan<byte> data,
        out bool isEncrypted,
        out bool includesCredentials,
        out bool includesSshKeys)
    {
        isEncrypted         = false;
        includesCredentials = false;
        includesSshKeys     = false;

        if (data.Length < HeaderSize)
            return false;

        if (!data[..4].SequenceEqual(MagicBytes))
            return false;

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
        if (version != CurrentVersion)
            return false;

        byte flags = data[6];
        includesCredentials = (flags & FlagIncludesCredentials) != 0;
        includesSshKeys     = (flags & FlagIncludesSshKeys) != 0;
        isEncrypted         = (flags & FlagIsEncrypted) != 0;
        return true;
    }

    public static void WriteLcx(
        LcxBundlePayload payload,
        bool includeCredentials,
        bool includeSshKeys,
        bool encrypt,
        string? password,
        Stream output)
    {
        if (encrypt && string.IsNullOrEmpty(password))
            throw new ArgumentException("An export password is required for encrypted .lcx bundles.", nameof(password));

        string json = ConnectionSettingsSerializer.Serialize(payload) ?? "{}";
        byte[] plainBytes = Encoding.UTF8.GetBytes(json);

        byte flags = 0;
        if (includeCredentials) flags |= FlagIncludesCredentials;
        if (includeSshKeys)     flags |= FlagIncludesSshKeys;
        if (encrypt)            flags |= FlagIsEncrypted;

        byte[] salt  = new byte[SaltSize];
        byte[] nonce = new byte[NonceSize];
        byte[] tag   = new byte[TagSize];
        byte[] bodyBytes;

        if (encrypt)
        {
            RandomNumberGenerator.Fill(salt);
            RandomNumberGenerator.Fill(nonce);

            byte[] key = DeriveKey(password!, salt);
            try
            {
                bodyBytes = new byte[plainBytes.Length];
                using var aesGcm = new AesGcm(key, TagSize);
                aesGcm.Encrypt(nonce, plainBytes, bodyBytes, tag);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        else
        {
            bodyBytes = plainBytes;
            byte[] sha = SHA256.HashData(plainBytes);
            Array.Copy(sha, 0, tag, 0, TagSize);
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        MagicBytes.CopyTo(header[..4]);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(4, 2), CurrentVersion);
        header[6] = flags;
        salt.CopyTo(header.Slice(7, SaltSize));
        nonce.CopyTo(header.Slice(39, NonceSize));
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(51, 4), (uint)bodyBytes.Length);

        output.Write(header);
        output.Write(bodyBytes);
        output.Write(tag);
        output.Flush();
    }

    public static LcxBundlePayload ReadLcx(Stream input, string? password)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        ReadOnlySpan<byte> bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);

        if (!TryInspectHeader(bytes, out bool isEncrypted, out _, out _))
            throw new InvalidDataException("Invalid or unsupported .lcx file header.");

        ReadOnlySpan<byte> salt  = bytes.Slice(7, SaltSize);
        ReadOnlySpan<byte> nonce = bytes.Slice(39, NonceSize);
        uint payloadLen          = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(51, 4));

        if (bytes.Length < HeaderSize + (int)payloadLen + TagSize)
            throw new InvalidDataException("Truncated .lcx file payload.");

        ReadOnlySpan<byte> payloadSpan = bytes.Slice(HeaderSize, (int)payloadLen);
        ReadOnlySpan<byte> tagSpan     = bytes.Slice(HeaderSize + (int)payloadLen, TagSize);

        byte[] plainBytes;
        if (isEncrypted)
        {
            if (string.IsNullOrEmpty(password))
                throw new CryptographicException("A password is required to decrypt this .lcx bundle.");

            byte[] key = DeriveKey(password, salt);
            try
            {
                plainBytes = new byte[payloadLen];
                using var aesGcm = new AesGcm(key, TagSize);
                aesGcm.Decrypt(nonce, payloadSpan, tagSpan, plainBytes);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new CryptographicException("Incorrect export password or corrupted .lcx file.", ex);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        else
        {
            plainBytes = payloadSpan.ToArray();
        }

        string json = Encoding.UTF8.GetString(plainBytes);
        return ConnectionSettingsSerializer.Deserialize<LcxBundlePayload>(json)
               ?? throw new InvalidDataException("Failed to deserialize .lcx JSON payload.");
    }

    private static byte[] DeriveKey(string password, ReadOnlySpan<byte> salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            outputLength: 32);
    }
}
