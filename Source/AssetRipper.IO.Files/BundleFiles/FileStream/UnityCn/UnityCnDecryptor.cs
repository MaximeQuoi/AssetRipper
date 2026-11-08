using System.Security.Cryptography;

namespace AssetRipper.IO.Files.BundleFiles.FileStream.UnityCn;

/// <summary>
/// Decryption of UnityCN (Unity China / Tuanjie) encrypted AssetBundle blocks.
/// </summary>
/// <remarks>
/// The encryption data is stored directly after the UnityFS header:
/// <code>
/// uint32   unknown
/// byte[16] info bytes      (xor AES(key, info key))
/// byte[16] info key
/// byte     separator
/// byte[16] signature bytes (xor AES(key, signature key)) == "#$unity3dchina!@"
/// byte[16] signature key
/// byte     separator
/// </code>
/// Each storage block flagged with <see cref="EncryptedBlockFlag"/> is LZ4 compressed and then encrypted.
/// </remarks>
public sealed class UnityCnDecryptor
{
	public const string Signature = "#$unity3dchina!@";
	/// <summary>
	/// Size of the encryption data following the header.
	/// </summary>
	public const int HeaderDataSize = 4 + 16 + 16 + 1 + 16 + 16 + 1;
	/// <summary>
	/// Storage block flag set on encrypted blocks.
	/// </summary>
	public const StorageBlockFlags EncryptedBlockFlag = (StorageBlockFlags)0x100;

	private readonly byte[] m_index = new byte[0x10];
	private readonly byte[] m_sub = new byte[0x10];

	/// <summary>
	/// The key which successfully decrypted the signature.
	/// </summary>
	public UnityCnKeyCandidate Key { get; }

	private UnityCnDecryptor(UnityCnKeyCandidate key, ReadOnlySpan<byte> infoBytes)
	{
		Key = key;
		Span<byte> nibbles = stackalloc byte[0x20];
		for (int i = 0; i < 0x10; i++)
		{
			nibbles[i * 2] = (byte)(infoBytes[i] >> 4);
			nibbles[i * 2 + 1] = (byte)(infoBytes[i] & 0xF);
		}
		nibbles[..0x10].CopyTo(m_index);
		ReadOnlySpan<byte> subBytes = nibbles.Slice(0x10, 0x10);
		for (int i = 0; i < subBytes.Length; i++)
		{
			int idx = (i % 4 * 4) + (i / 4);
			m_sub[idx] = subBytes[i];
		}
	}

	/// <summary>
	/// Reads the encryption data at the current position of <paramref name="stream"/> and validates it
	/// against the keys configured in <see cref="UnityCnKey"/>.
	/// </summary>
	/// <exception cref="UnityCnKeyException">No configured key is valid for this bundle.</exception>
	public static UnityCnDecryptor Read(Stream stream, string bundleName)
	{
		Span<byte> data = stackalloc byte[HeaderDataSize];
		stream.ReadExactly(data);
		return Create(data, UnityCnKey.GetCandidates(), bundleName, UnityCnKey.ReportFound);
	}

	/// <summary>
	/// Validates the encryption data against each candidate key, in order.
	/// </summary>
	/// <param name="onFound">Called with the candidate that decrypted the signature.</param>
	/// <exception cref="UnityCnKeyException">No candidate key is valid for this bundle.</exception>
	public static UnityCnDecryptor Create(ReadOnlySpan<byte> data, IEnumerable<UnityCnKeyCandidate> candidates, string bundleName, Action<UnityCnKeyCandidate>? onFound = null)
	{
		if (data.Length < HeaderDataSize)
		{
			throw new ArgumentException("Not enough UnityCN header data.", nameof(data));
		}

		ReadOnlySpan<byte> infoBytes = data.Slice(4, 16);
		ReadOnlySpan<byte> infoKey = data.Slice(20, 16);
		ReadOnlySpan<byte> signatureBytes = data.Slice(37, 16);
		ReadOnlySpan<byte> signatureKey = data.Slice(53, 16);

		Span<byte> decrypted = stackalloc byte[16];
		using Aes aes = Aes.Create();
		int tried = 0;
		foreach (UnityCnKeyCandidate candidate in candidates)
		{
			tried++;
			aes.Key = candidate.Key;

			XorWithEncryptedKey(aes, signatureKey, signatureBytes, decrypted);
			if (!decrypted.SequenceEqual(SignatureBytes))
			{
				continue;
			}

			XorWithEncryptedKey(aes, infoKey, infoBytes, decrypted);
			onFound?.Invoke(candidate);
			return new UnityCnDecryptor(candidate, decrypted);
		}

		if (tried == 0)
		{
			throw new UnityCnKeyException($"'{bundleName}' is UnityCN encrypted, but no decryption key was provided. " +
				$"Set the UnityCN key or a key file (Il2CppDumper dump.cs / stringliteral.json) in the settings, " +
				$"or the {UnityCnKey.EnvironmentVariable} / {UnityCnKey.KeyFileEnvironmentVariable} environment variables.");
		}
		throw new UnityCnKeyException($"None of the {tried} UnityCN key candidate(s) is valid for '{bundleName}' (signature mismatch).");
	}

	private static ReadOnlySpan<byte> SignatureBytes => "#$unity3dchina!@"u8;

	private static void XorWithEncryptedKey(Aes aes, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> destination)
	{
		Span<byte> encryptedKey = stackalloc byte[16];
		aes.EncryptEcb(key, encryptedKey, PaddingMode.None);
		for (int i = 0; i < 16; i++)
		{
			destination[i] = (byte)(data[i] ^ encryptedKey[i]);
		}
	}

	/// <summary>
	/// Decrypts a block in place.
	/// </summary>
	/// <param name="bytes">The compressed (LZ4) block data.</param>
	/// <param name="blockIndex">The index of the block within the bundle.</param>
	public void DecryptBlock(Span<byte> bytes, int blockIndex)
	{
		int offset = 0;
		int index = blockIndex;
		while (offset < bytes.Length)
		{
			offset += Decrypt(bytes[offset..], index++, bytes.Length - offset);
		}
	}

	private int DecryptByte(Span<byte> bytes, ref int offset, ref int index)
	{
		int b;
		unchecked
		{
			b = m_sub[((index >> 2) & 3) + 4] + m_sub[index & 3] + m_sub[((index >> 4) & 3) + 8] + m_sub[((byte)index >> 6) + 12];
			bytes[offset] = (byte)(((m_index[bytes[offset] & 0xF] - b) & 0xF) | (0x10 * (m_index[bytes[offset] >> 4] - b)));
		}
		b = bytes[offset];
		offset++;
		index++;
		return b;
	}

	private int Decrypt(Span<byte> bytes, int index, int remaining)
	{
		int offset = 0;

		int currentByte = DecryptByte(bytes, ref offset, ref index);
		int byteHigh = currentByte >> 4;
		int byteLow = currentByte & 0xF;

		if (byteHigh == 0xF)
		{
			int b;
			do
			{
				b = DecryptByte(bytes, ref offset, ref index);
				byteHigh += b;
			}
			while (b == 0xFF);
		}

		offset += byteHigh;

		if (offset < remaining)
		{
			DecryptByte(bytes, ref offset, ref index);
			DecryptByte(bytes, ref offset, ref index);
			if (byteLow == 0xF)
			{
				int b;
				do
				{
					b = DecryptByte(bytes, ref offset, ref index);
				}
				while (b == 0xFF);
			}
		}

		return offset;
	}

	/// <summary>
	/// Whether the bundle is UnityCN encrypted, and whether 0x200 means encryption (old scheme) rather than padding.
	/// </summary>
	public static bool IsEncrypted(FileStreamBundleHeader header, out bool usesOldFlag)
	{
		usesOldFlag = false;
		if ((header.Flags & BundleFlags.EncryptionNew) != 0)
		{
			return true;
		}
		if ((header.Flags & BundleFlags.EncryptionOld) != 0 && IsBeforePaddingFlag(header.UnityWebMinimumRevision.String))
		{
			usesOldFlag = true;
			return true;
		}
		return false;
	}

	/// <summary>
	/// <see cref="BundleFlags.BlockInfoNeedPaddingAtStart"/> was introduced in 2020.3.34f1, 2021.3.2f1 and 2022.1.1f1.
	/// Before that, 0x200 was the UnityCN encryption flag.
	/// </summary>
	public static bool IsBeforePaddingFlag(string? version)
	{
		if (!TryParseVersion(version, out int major, out int minor, out int build))
		{
			return false;
		}
		return major switch
		{
			< 2020 => true,
			2020 => minor < 3 || (minor == 3 && build < 34),
			2021 => minor < 3 || (minor == 3 && build < 2),
			2022 => minor < 1 || (minor == 1 && build < 1),
			_ => false,
		};
	}

	private static bool TryParseVersion(string? version, out int major, out int minor, out int build)
	{
		major = minor = build = 0;
		if (string.IsNullOrEmpty(version))
		{
			return false;
		}
		Span<int> parts = stackalloc int[3];
		int part = 0;
		int value = 0;
		bool hasDigit = false;
		foreach (char c in version)
		{
			if (char.IsAsciiDigit(c))
			{
				value = value * 10 + (c - '0');
				hasDigit = true;
			}
			else
			{
				if (!hasDigit)
				{
					break;
				}
				parts[part++] = value;
				value = 0;
				hasDigit = false;
				if (part == 3 || c != '.')
				{
					break;
				}
			}
		}
		if (hasDigit && part < 3)
		{
			parts[part++] = value;
		}
		if (part < 3)
		{
			return false;
		}
		(major, minor, build) = (parts[0], parts[1], parts[2]);
		return true;
	}
}

public sealed class UnityCnKeyException(string message) : Exception(message);
