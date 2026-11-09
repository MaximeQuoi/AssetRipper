using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace AssetRipper.IO.Files.BundleFiles.FileStream.NetEase;

/// <summary>
/// Decryption of the first block of asset bundles protected by NetEase (Yidun / libnesec).
/// </summary>
/// <remarks>
/// Port of <c>NetEaseUtils</c> from AnimeStudio (<see href="https://github.com/Escartem/AnimeStudio"/>, MIT License),
/// algorithm by LukeFZ.<br/>
/// The first compressed block starts with a header (signature <c>EE DD</c> followed by a version such as <c>0x2022</c>)
/// that replaces part of the serialized file header, then up to 0x1000 bytes are encrypted.
/// No key is needed: everything is derived from the block itself.
/// </remarks>
public static class NetEaseDecryptor
{
	private const int MaxEncryptedSize = 0x1000;

	/// <summary>
	/// Whether the block starts like a NetEase encrypted LZ4 block.
	/// </summary>
	public static bool HasHeader(ReadOnlySpan<byte> bytes)
	{
		int index = bytes.IndexOf(Signature);
		if (index < 0 || index >= 0x40 || index + 10 > bytes.Length)
		{
			return false;
		}
		ushort version = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(index + 2)..]);
		return version is >= 0x2017 and <= 0x2025;
	}

	/// <summary>
	/// Decrypts an LZ4 compressed first block, in place.
	/// </summary>
	/// <returns>False if the block has no NetEase header.</returns>
	public static bool TryDecryptWithHeader(Span<byte> bytes)
	{
		if (!HasHeader(bytes))
		{
			return false;
		}
		(int encryptedOffset, int encryptedSize) = ReadHeader(bytes);
		if (encryptedOffset < 0 || encryptedSize <= 0 || encryptedOffset + encryptedSize > bytes.Length)
		{
			return false;
		}
		Decrypt(bytes.Slice(encryptedOffset, encryptedSize));
		return true;
	}

	/// <summary>
	/// Decrypts an LZMA compressed first block, in place. Such blocks have no header.
	/// </summary>
	public static void DecryptWithoutHeader(Span<byte> bytes)
	{
		Decrypt(bytes[..Math.Min(bytes.Length, MaxEncryptedSize)]);
	}

	private static ReadOnlySpan<byte> Signature => [0xEE, 0xDD];

	private static void Decrypt(Span<byte> bytes)
	{
		unchecked
		{
			if (bytes.Length < 0x20)
			{
				return;
			}
			Span<int> encryptedInts = MemoryMarshal.Cast<byte, int>(bytes);

			int[] seedInts = [encryptedInts[3], encryptedInts[1], encryptedInts[4], bytes.Length, encryptedInts[2]];
			byte[] seedBytes = MemoryMarshal.AsBytes(seedInts.AsSpan()).ToArray();
			int seed = (int)Crc.CalculateDigest(seedBytes);

			int keyPart0 = seed ^ (encryptedInts[7] + 0x1981);
			int keyPart1 = seed ^ (bytes.Length + 0x2013);
			int keyPart2 = seed ^ (encryptedInts[5] + 0x1985);
			int keyPart3 = seed ^ (encryptedInts[6] + 0x2018);

			for (int i = 0; i < 0x20; i++)
			{
				bytes[i] ^= 0xA6;
			}

			Span<byte> block = bytes[0x20..];
			int[] keyVector = [keyPart2, keyPart0, keyPart1, keyPart3];
			int[] keysVector = [0x571, keyPart3, 0x892, 0x750, keyPart2, keyPart0, 0x746, keyPart1, 0x568];
			if (block.Length >= 0x80)
			{
				Span<byte> dataBlock = block[0x80..];
				byte[] keyBlock = block[..0x80].ToArray();
				Span<int> keyBlockInts = MemoryMarshal.Cast<byte, int>(keyBlock.AsSpan());

				Rc4(block[..0x80], seed);
				Rc4(keyBlock, keyPart1);

				int blockCount = dataBlock.Length / 0x80;
				for (int i = 0; i < blockCount; i++)
				{
					int blockOffset = i * 0x80;
					int type = (byte)keysVector[i % keysVector.Length] % keyVector.Length;
					Span<int> dataBlockInts = MemoryMarshal.Cast<byte, int>(dataBlock.Slice(blockOffset, 0x80));
					for (int j = 0; j < 0x20; j++)
					{
						dataBlockInts[j] ^= keyBlockInts[j] ^ type switch
						{
							0 => keysVector[j % keysVector.Length] ^ (0x20 - j),
							1 => keyVector[(byte)keyBlockInts[j] % keyVector.Length],
							2 => keyVector[(byte)keyBlockInts[j] % keyVector.Length] ^ j,
							_ => keyVector[(byte)keysVector[j % keysVector.Length] % keyVector.Length] ^ j,
						};
					}
				}

				int remainingCount = dataBlock.Length % 0x80;
				if (remainingCount > 0)
				{
					Span<byte> remaining = bytes[^remainingCount..];
					for (int i = 0; i < remainingCount; i++)
					{
						remaining[i] ^= (byte)(keyBlock[i] ^ ((uint)keysVector[(uint)keyVector[i % keyVector.Length] % keysVector.Length] % 0xFF) ^ i);
					}
				}
			}
			else
			{
				Rc4(block, seed);
			}
		}
	}

	private static (int Offset, int Size) ReadHeader(Span<byte> bytes)
	{
		int index = bytes.IndexOf(Signature);
		Span<byte> info = bytes[index..];

		// Restore the original bytes: the header replaces "2022.3.62f1"-like text of the serialized file header.
		ushort version = BinaryPrimitives.ReadUInt16LittleEndian(info[2..]);
		Encoding.UTF8.GetBytes(version.ToString("X4"), info);

		int encryptedSize = ReadEncryptedSize(info, bytes.Length);

		int headerOffset = 0;
		ReadHeaderOffset(info, 8, ref headerOffset);
		ReadHeaderOffset(info, 9, ref headerOffset);

		int headerSize = 0x30;
		int encryptedOffset = 0x30;
		if (headerOffset == index || headerOffset == 0)
		{
			if (index >= 0x20)
			{
				headerSize = 0x40;
				encryptedOffset = 0x40;
			}
		}
		else
		{
			if (headerOffset >= 0x20)
			{
				headerSize = 0x40;
				encryptedOffset = 0x40;
			}
			if (headerOffset > index)
			{
				encryptedOffset += index - headerOffset;
			}
		}

		encryptedSize -= headerSize;
		return (encryptedOffset, encryptedSize);
	}

	private static int ReadEncryptedSize(Span<byte> info, int size)
	{
		(byte vectorCount, byte bytesCount) = (info[4], info[6]);
		int encryptedSize = size > MaxEncryptedSize ? MaxEncryptedSize : size;
		if (vectorCount != 0x2E && bytesCount != 0x2E)
		{
			encryptedSize = bytesCount + 0x10 * vectorCount;
			if (vectorCount == 0xAA && bytesCount == 0xBB)
			{
				encryptedSize = MaxEncryptedSize;
			}
			info[4] = info[6] = 0x2E;
		}
		return encryptedSize;
	}

	private static void ReadHeaderOffset(Span<byte> info, int index, ref int headerOffset)
	{
		if (info[index + 1] == 0x31 && info[index] != 0x66)
		{
			headerOffset = info[index];
			info[index] = 0x66;
		}
	}

	private static void Rc4(Span<byte> data, int key)
	{
		Span<byte> keyBytes = stackalloc byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(keyBytes, key);

		Span<int> s = stackalloc int[0x100];
		for (int n = 0; n < 0x100; n++)
		{
			s[n] = n;
		}

		int j = 0;
		for (int i = 0; i < 0x100; i++)
		{
			j = (j + s[i] + keyBytes[i % 4]) % 0x100;
			(s[j], s[i]) = (s[i], s[j]);
		}

		int x = 0;
		j = 0;
		for (int iteration = 0; iteration < data.Length; iteration++)
		{
			x = (x + 1) % 0x100;
			j = (j + s[x]) % 0x100;
			(s[j], s[x]) = (s[x], s[j]);
			uint k = (uint)s[(s[j] + s[x]) % 0x100];
			uint rotated = unchecked((byte)(k << 6) | (k >> 2));
			data[iteration] ^= unchecked((byte)(rotated + 0x3A));
		}
	}

	private static class Crc
	{
		private static readonly uint[] Table = CreateTable();

		private static uint[] CreateTable()
		{
			const uint Polynomial = 0x9823D6E;
			uint[] table = new uint[256];
			for (uint i = 0; i < 256; i++)
			{
				uint r = i;
				for (int j = 0; j < 8; j++)
				{
					if ((r & 1) != 0)
					{
						r ^= Polynomial;
					}
					r >>= 1;
				}
				table[i] = r;
			}
			return table;
		}

		public static uint CalculateDigest(ReadOnlySpan<byte> data)
		{
			unchecked
			{
				uint value = 0xFFFFFFFF;
				foreach (byte b in data)
				{
					value = (Table[(byte)value ^ b] ^ (value >> 8)) + 0x10;
				}
				return ~value - 0x7D29C488;
			}
		}
	}
}
