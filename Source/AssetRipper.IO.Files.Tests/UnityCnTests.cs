using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.BundleFiles.FileStream.UnityCn;
using AssetRipper.IO.Files.Streams.Smart;
using K4os.Compression.LZ4;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AssetRipper.IO.Files.Tests;

[NotInParallel(nameof(UnityCnTests))]
public class UnityCnTests
{
	private const string Key24 = "Tuanjie-Key-24-Chars-OK!";
	private const string Key32 = "UnityChina-AssetBundleKey-32Char";
	private const string Key16 = "MyUnityCnKey1234";
	private const string Hex32 = "4D79556E697479436E4B657931323334"; // hex of Key16

	[Test]
	public async Task RawKeyOf24CharactersGivesAes192Key()
	{
		await Assert.That(Key24.Length).IsEqualTo(24);
		await Assert.That(UnityCnKey.TryParse(Key24, out IReadOnlyList<byte[]> candidates, out _)).IsTrue();
		await Assert.That(candidates.Any(c => c.Length == 24 && c.SequenceEqual(Encoding.UTF8.GetBytes(Key24)))).IsTrue();
	}

	[Test]
	public async Task RawKeyOf32CharactersGivesAes256Key()
	{
		await Assert.That(Key32.Length).IsEqualTo(32);
		await Assert.That(UnityCnKey.TryParse(Key32, out IReadOnlyList<byte[]> candidates, out _)).IsTrue();
		await Assert.That(candidates.Count).IsEqualTo(1);
		await Assert.That(candidates[0].SequenceEqual(Encoding.UTF8.GetBytes(Key32))).IsTrue();
	}

	[Test]
	public async Task HexKeyOf32CharactersIsTriedFirst()
	{
		await Assert.That(UnityCnKey.TryParse(Hex32, out IReadOnlyList<byte[]> candidates, out _)).IsTrue();
		await Assert.That(candidates[0].SequenceEqual(Encoding.ASCII.GetBytes(Key16))).IsTrue();
		// Also kept as a raw 32 character key
		await Assert.That(candidates.Any(c => c.Length == 32)).IsTrue();
	}

	[Test]
	public async Task Base64KeyOf24CharactersGivesAes128Key()
	{
		string base64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(Key16));
		await Assert.That(base64.Length).IsEqualTo(24);
		await Assert.That(UnityCnKey.TryParse(base64, out IReadOnlyList<byte[]> candidates, out _)).IsTrue();
		await Assert.That(candidates[0].SequenceEqual(Encoding.ASCII.GetBytes(Key16))).IsTrue();
		await Assert.That(candidates.Any(c => c.Length == 24)).IsTrue();
	}

	[Test]
	[Arguments("hex:4D79556E697479436E4B657931323334", 16)]
	[Arguments("4d 79 55 6e 69 74 79 43 6e 4b 65 79 31 32 33 34", 16)]
	[Arguments("raw:4D79556E697479436E4B657931323334", 32)]
	[Arguments("MyUnityCnKey1234", 16)]
	public async Task PrefixesAndSeparators(string key, int expectedLength)
	{
		await Assert.That(UnityCnKey.TryParse(key, out IReadOnlyList<byte[]> candidates, out _)).IsTrue();
		await Assert.That(candidates[0].Length).IsEqualTo(expectedLength);
	}

	[Test]
	[Arguments("")]
	[Arguments("short")]
	[Arguments("this key has twenty-five!")]
	public async Task InvalidKeysAreRejected(string key)
	{
		await Assert.That(UnityCnKey.TryParse(key, out _, out string? error)).IsFalse();
		await Assert.That(error).IsNotNull();
	}

	[Test]
	[Arguments("2019.4.40f1", true)]
	[Arguments("2020.3.33f1c1", true)]
	[Arguments("2020.3.34f1", false)]
	[Arguments("2021.3.1f1", true)]
	[Arguments("2021.3.2f1", false)]
	[Arguments("2022.1.0f1", true)]
	[Arguments("2022.3.2t3", false)]
	[Arguments("", false)]
	public async Task OldEncryptionFlagVersions(string version, bool expected)
	{
		await Assert.That(UnityCnDecryptor.IsBeforePaddingFlag(version)).IsEqualTo(expected);
	}

	[Test]
	[Arguments(Key24, Key24)]
	[Arguments(Key32, Key32)]
	[Arguments(Key16, Hex32)]
	[Arguments(Key16, "TXlVbml0eUNuS2V5MTIzNA==")]
	public async Task EncryptedBundleIsReadWithKey(string bundleKey, string userKey)
	{
		byte[] payload = MakePayload();
		byte[] bundleBytes = MakeEncryptedBundle(Encoding.UTF8.GetBytes(bundleKey), payload, "2022.3.15f1c1");

		try
		{
			UnityCnKey.Set(userKey);
			using SmartStream stream = SmartStream.CreateMemory(bundleBytes);
			FileStreamBundleFile bundle = new FileStreamBundleScheme().Read(stream, "test.bundle", "test.bundle");

			await Assert.That(bundle.FailedFiles.Count).IsEqualTo(0);
			await Assert.That(bundle.UnityCn).IsNotNull();
			await Assert.That(bundle.ResourceFiles.Count).IsEqualTo(1);
			await Assert.That(bundle.ResourceFiles[0].ToByteArray().SequenceEqual(payload)).IsTrue();
		}
		finally
		{
			UnityCnKey.Set(null);
		}
	}

	[Test]
	public async Task WrongKeyIsRejected()
	{
		byte[] bundleBytes = MakeEncryptedBundle(Encoding.UTF8.GetBytes(Key24), MakePayload(), "2022.3.15f1c1");
		try
		{
			UnityCnKey.Set(Key32);
			using SmartStream stream = SmartStream.CreateMemory(bundleBytes);
			await Assert.That(() => new FileStreamBundleScheme().Read(stream, "test.bundle", "test.bundle")).Throws<UnityCnKeyException>();
		}
		finally
		{
			UnityCnKey.Set(null);
		}
	}

	private static byte[] MakePayload()
	{
		StringBuilder sb = new();
		for (int i = 0; i < 400; i++)
		{
			sb.Append("UnityCN payload line ").Append(i).Append(' ').Append(i * 31 % 17).Append('\n');
		}
		return Encoding.ASCII.GetBytes(sb.ToString());
	}

	/// <summary>
	/// Builds a minimal UnityFS bundle (version 7, new 0x400 encryption flag) with one encrypted LZ4 block containing one file.
	/// </summary>
	private static byte[] MakeEncryptedBundle(byte[] key, byte[] payload, string unityVersion)
	{
		// Encryption tables: index must be a permutation of 0-15 so that a test encryptor exists.
		byte[] index = new byte[16];
		for (int i = 0; i < 16; i++)
		{
			index[i] = (byte)((i * 7 + 3) & 0xF);
		}
		byte[] subRaw = [1, 9, 4, 12, 7, 0, 15, 3, 8, 2, 11, 6, 13, 5, 10, 14];
		byte[] sub = new byte[16];
		for (int i = 0; i < 16; i++)
		{
			sub[(i % 4 * 4) + (i / 4)] = subRaw[i];
		}
		byte[] info = new byte[16];
		for (int i = 0; i < 8; i++)
		{
			info[i] = (byte)((index[i * 2] << 4) | index[i * 2 + 1]);
			info[i + 8] = (byte)((subRaw[i * 2] << 4) | subRaw[i * 2 + 1]);
		}

		using Aes aes = Aes.Create();
		aes.Key = key;
		byte[] infoKey = RandomNumberGenerator.GetBytes(16);
		byte[] signatureKey = RandomNumberGenerator.GetBytes(16);
		byte[] encryptedInfoKey = aes.EncryptEcb(infoKey, PaddingMode.None);
		byte[] encryptedSignatureKey = aes.EncryptEcb(signatureKey, PaddingMode.None);
		byte[] signature = Encoding.ASCII.GetBytes(UnityCnDecryptor.Signature);

		MemoryStream cnData = new();
		cnData.Write(new byte[4]);
		cnData.Write(Xor(info, encryptedInfoKey));
		cnData.Write(infoKey);
		cnData.WriteByte(0);
		cnData.Write(Xor(signature, encryptedSignatureKey));
		cnData.Write(signatureKey);
		cnData.WriteByte(0);

		// Block data
		byte[] compressed = new byte[LZ4Codec.MaximumOutputSize(payload.Length)];
		int compressedLength = LZ4Codec.Encode(payload, compressed, LZ4Level.L00_FAST);
		compressed = compressed[..compressedLength];
		EncryptBlock(compressed, 0, index, sub);

		// Blocks info + directory (uncompressed, combined)
		MemoryStream meta = new();
		meta.Write(new byte[16]); // hash
		WriteInt32(meta, 1);
		WriteInt32(meta, payload.Length);
		WriteInt32(meta, compressed.Length);
		WriteInt16(meta, (short)(0x100 | 2)); // encrypted + LZ4
		WriteInt32(meta, 1);
		WriteInt64(meta, 0);
		WriteInt64(meta, payload.Length);
		WriteInt32(meta, 4);
		meta.Write(Encoding.ASCII.GetBytes("CAB-test\0"));

		const int flags = 0x40 | 0x200 | 0x400; // combined, padding, UnityCN
		MemoryStream bundle = new();
		bundle.Write(Encoding.ASCII.GetBytes("UnityFS\0"));
		WriteInt32(bundle, 7);
		bundle.Write(Encoding.ASCII.GetBytes("5.x.x\0"));
		bundle.Write(Encoding.ASCII.GetBytes(unityVersion + "\0"));
		long sizePosition = bundle.Position;
		WriteInt64(bundle, 0);
		WriteInt32(bundle, (int)meta.Length);
		WriteInt32(bundle, (int)meta.Length);
		WriteInt32(bundle, flags);
		cnData.WriteTo(bundle);
		Align16(bundle);
		meta.WriteTo(bundle);
		Align16(bundle);
		bundle.Write(compressed);

		long size = bundle.Length;
		bundle.Position = sizePosition;
		WriteInt64(bundle, size);
		return bundle.ToArray();
	}

	private static byte[] Xor(byte[] a, byte[] b)
	{
		byte[] result = new byte[a.Length];
		for (int i = 0; i < a.Length; i++)
		{
			result[i] = (byte)(a[i] ^ b[i]);
		}
		return result;
	}

	/// <summary>
	/// Inverse of <see cref="UnityCnDecryptor.DecryptBlock(Span{byte}, int)"/>, following the same LZ4 token walk.
	/// </summary>
	private static void EncryptBlock(Span<byte> bytes, int blockIndex, byte[] index, byte[] sub)
	{
		byte[] inverse = new byte[16];
		for (int i = 0; i < 16; i++)
		{
			inverse[index[i]] = (byte)i;
		}

		int EncryptByte(Span<byte> data, ref int offset, ref int idx)
		{
			int b = unchecked(sub[((idx >> 2) & 3) + 4] + sub[idx & 3] + sub[((idx >> 4) & 3) + 8] + sub[((byte)idx >> 6) + 12]);
			int plain = data[offset];
			int low = inverse[((plain & 0xF) + b) & 0xF];
			int high = inverse[((plain >> 4) + b) & 0xF];
			data[offset] = (byte)((high << 4) | low);
			offset++;
			idx++;
			return plain;
		}

		int offsetTotal = 0;
		int sequence = blockIndex;
		while (offsetTotal < bytes.Length)
		{
			Span<byte> span = bytes[offsetTotal..];
			int remaining = span.Length;
			int offset = 0;
			int idx = sequence++;
			int current = EncryptByte(span, ref offset, ref idx);
			int high = current >> 4;
			int low = current & 0xF;
			if (high == 0xF)
			{
				int b;
				do
				{
					b = EncryptByte(span, ref offset, ref idx);
					high += b;
				}
				while (b == 0xFF);
			}
			offset += high;
			if (offset < remaining)
			{
				EncryptByte(span, ref offset, ref idx);
				EncryptByte(span, ref offset, ref idx);
				if (low == 0xF)
				{
					int b;
					do
					{
						b = EncryptByte(span, ref offset, ref idx);
					}
					while (b == 0xFF);
				}
			}
			offsetTotal += offset;
		}
	}

	private static void Align16(Stream stream)
	{
		while (stream.Position % 16 != 0)
		{
			stream.WriteByte(0);
		}
	}

	private static void WriteInt16(Stream stream, short value)
	{
		Span<byte> buffer = stackalloc byte[2];
		BinaryPrimitives.WriteInt16BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteInt32(Stream stream, int value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteInt64(Stream stream, long value)
	{
		Span<byte> buffer = stackalloc byte[8];
		BinaryPrimitives.WriteInt64BigEndian(buffer, value);
		stream.Write(buffer);
	}
}
