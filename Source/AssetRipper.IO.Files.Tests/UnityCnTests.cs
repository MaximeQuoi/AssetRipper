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
			UnityCnKey.Configure(null, null);
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
			UnityCnKey.Configure(null, null);
		}
	}

	private const string SampleDump = """
		// Namespace: Game.Loading
		public class BundleLoader : MonoBehaviour // TypeDefIndex: 4242
		{
			// Fields
			private const string BundleKey = "Tuanjie-Key-24-Chars-OK!"; // 0x0
			public const string Version = "1.0.0";
			private const string Unrelated = "abcdefghijklmnop";
			private const string EscapedQuote = "\"quoted\" value here!";
			private const char Quote = '"';

			// Methods
			// RVA: 0x1A2B3C Offset: 0x1A2B3C VA: 0x1A2B3C
			public void .ctor() { }
		}
		""";

	private const string SampleStringLiteral = """
		[
		  {
		    "value": "Loading...",
		    "address": "0x2C1E8A0"
		  },
		  {
		    "value": "UnityChina-AssetBundleKey-32Char",
		    "address": "0x2C1E8A8"
		  }
		]
		""";

	[Test]
	public async Task KeysAreExtractedFromDumpCs()
	{
		IReadOnlyList<UnityCnKeyCandidate> candidates = UnityCnKeyExtractor.ExtractFromText(SampleDump, "dump.cs");
		await Assert.That(candidates.Count).IsGreaterThanOrEqualTo(1);
		// Found next to "Key", so it comes first
		await Assert.That(candidates[0].Text).IsEqualTo(Key24);
		await Assert.That(candidates.Any(c => c.Text == "1.0.0")).IsFalse();
		await Assert.That(candidates.Any(c => c.Text == "abcdefghijklmnop")).IsTrue();
		await Assert.That(candidates[0].Source).IsEqualTo("dump.cs:5");
	}

	[Test]
	public async Task KeysAreExtractedFromStringLiteralJson()
	{
		IReadOnlyList<UnityCnKeyCandidate> candidates = UnityCnKeyExtractor.ExtractFromText(SampleStringLiteral, "stringliteral.json");
		await Assert.That(candidates.Count).IsEqualTo(1);
		await Assert.That(candidates[0].Text).IsEqualTo(Key32);
	}

	[Test]
	public async Task EscapesAreDecoded()
	{
		List<string> strings = [];
		UnityCnKeyExtractor.ReadQuotedStrings("x = \"a\\\"b\\u0041\\x42\"; y = \"c\"", strings);
		await Assert.That(strings.Count).IsEqualTo(2);
		await Assert.That(strings[0]).IsEqualTo("a\"bAB");
		await Assert.That(strings[1]).IsEqualTo("c");
	}

	[Test]
	public async Task KeyFileFindsTheRightKeysForEachBundle()
	{
		string directory = Path.Combine(Path.GetTempPath(), "AssetRipperUnityCnTests_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string dumpPath = Path.Combine(directory, "dump.cs");
		// Noise first, then the two real keys (one in dump.cs style, one as a raw line)
		StringBuilder dump = new();
		for (int i = 0; i < 500; i++)
		{
			dump.AppendLine($"\tprivate const string Noise{i} = \"{i:D4}-noise-noise-xyz1234\";");
		}
		dump.AppendLine(SampleDump);
		dump.AppendLine($"\tpublic const string Other = \"{Key32}\";");
		File.WriteAllText(dumpPath, dump.ToString());

		byte[] payload = MakePayload();
		byte[] bundle24 = MakeEncryptedBundle(Encoding.UTF8.GetBytes(Key24), payload, "2022.3.15f1c1");
		byte[] bundle32 = MakeEncryptedBundle(Encoding.UTF8.GetBytes(Key32), payload, "2022.3.15f1c1");

		try
		{
			int count = UnityCnKey.Configure(null, dumpPath);
			await Assert.That(count).IsGreaterThan(500);

			foreach (byte[] bytes in (byte[][])[bundle24, bundle32, bundle24])
			{
				using SmartStream stream = SmartStream.CreateMemory(bytes);
				FileStreamBundleFile bundle = new FileStreamBundleScheme().Read(stream, "test.bundle", "test.bundle");
				await Assert.That(bundle.FailedFiles.Count).IsEqualTo(0);
				await Assert.That(bundle.ResourceFiles[0].ToByteArray().SequenceEqual(payload)).IsTrue();
			}

			IReadOnlyList<UnityCnKeyCandidate> found = UnityCnKey.FoundKeys;
			await Assert.That(found.Count).IsEqualTo(2);
			await Assert.That(found[0].Text).IsEqualTo(Key24);
			await Assert.That(found[1].Text).IsEqualTo(Key32);
			// Found keys are tried first
			await Assert.That(UnityCnKey.GetCandidates().First().Text).IsEqualTo(Key24);
		}
		finally
		{
			UnityCnKey.Configure(null, null);
			Directory.Delete(directory, true);
		}
	}

	[Test]
	public async Task SeveralManualKeysCanBeGiven()
	{
		try
		{
			UnityCnKey.Set($"{Key24}; {Hex32}\n{Key32}");
			await Assert.That(UnityCnKey.Candidates.Any(c => c.Text == Key24)).IsTrue();
			await Assert.That(UnityCnKey.Candidates.Any(c => c.Text == Hex32 && c.Format == "hex")).IsTrue();
			await Assert.That(UnityCnKey.Candidates.Any(c => c.Text == Key32)).IsTrue();
		}
		finally
		{
			UnityCnKey.Configure(null, null);
		}
	}

	private static FileStreamBundleFile ReadBundle(byte[] bytes)
	{
		SmartStream stream = SmartStream.CreateMemory(bytes);
		return new FileStreamBundleScheme().Read(stream, "test.bundle", "test.bundle");
	}

	private static async Task AssertPayload(FileStreamBundleFile bundle, byte[] payload)
	{
		await Assert.That(bundle.FailedFiles.Count).IsEqualTo(0);
		await Assert.That(bundle.ResourceFiles.Count).IsEqualTo(1);
		await Assert.That(bundle.ResourceFiles[0].ToByteArray().SequenceEqual(payload)).IsTrue();
	}

	[Test]
	public async Task HexKeysAreFoundAnywhere()
	{
		List<string> found = [];
		UnityCnKeyExtractor.ReadHexKeys($"// RVA: 0x1A2B3C Offset: 0x{Hex32} VA: 0x1A2B3C", found);
		await Assert.That(found).Contains(Hex32);

		found.Clear();
		UnityCnKeyExtractor.ReadHexKeys($"private const string Url = \"https://cdn.example.com/{Hex32}/bundles\";", found);
		await Assert.That(found).Contains(Hex32);

		found.Clear();
		UnityCnKeyExtractor.ReadHexKeys($"x = \"abc{Hex32}def0\"", found); // inside a longer hex run
		await Assert.That(found).Contains(Hex32);

		found.Clear();
		UnityCnKeyExtractor.ReadHexKeys("[Guid(\"4d79556e-6974-7943-6e4b-657931323334\")]", found);
		await Assert.That(found).Contains("4d79556e697479436e4b657931323334");

		found.Clear();
		UnityCnKeyExtractor.ReadHexKeys("// RVA: 0x1A2B3C Offset: 0x1A2B3C", found);
		await Assert.That(found.Count).IsEqualTo(0);
	}

	[Test]
	public async Task HexKeyOnlyInACommentDecryptsTheBundle()
	{
		string dump = $$"""
			// Namespace:
			public class Boot // TypeDefIndex: 12
			{
				// RVA: 0x5F3C10 Offset: 0x5F2C10 VA: 0x5F3C10 Slot: 4 {{Hex32}}
				public void Start() { }
			}
			""";
		byte[] payload = MakePayload();
		byte[] bytes = MakeEncryptedBundle(Encoding.ASCII.GetBytes(Key16), payload, "2022.3.15f1c1");
		string directory = Path.Combine(Path.GetTempPath(), "AssetRipperUnityCnTests_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(directory);
		string dumpPath = Path.Combine(directory, "dump.cs");
		File.WriteAllText(dumpPath, dump);
		try
		{
			UnityCnKey.Configure(null, dumpPath);
			await AssertPayload(ReadBundle(bytes), payload);
			await Assert.That(UnityCnKey.FoundKeys[0].Text).IsEqualTo(Hex32);
			await Assert.That(UnityCnKey.FoundKeys[0].Source).IsEqualTo("dump.cs:4");
		}
		finally
		{
			UnityCnKey.Configure(null, null);
			Directory.Delete(directory, true);
		}
	}

	[Test]
	public async Task KnownKeysAreTriedWithoutConfiguration()
	{
		byte[] payload = MakePayload();
		byte[] key = Convert.FromHexString("6B75726F6B75726F6B75726F6B75726F");
		UnityCnKey.Configure(null, null);
		try
		{
			await AssertPayload(ReadBundle(MakeEncryptedBundle(key, payload, "2019.4.40f1", flags: 0x40 | 0x200, padding: false)), payload);
			await Assert.That(UnityCnKey.FoundKeys[0].Source).Contains("Punishing Gray Raven");
		}
		finally
		{
			UnityCnKey.Configure(null, null);
		}
	}

	[Test]
	public async Task ManjuuSignatureIsAccepted()
	{
		byte[] payload = MakePayload();
		try
		{
			UnityCnKey.Set(Key24);
			byte[] bytes = MakeEncryptedBundle(Encoding.UTF8.GetBytes(Key24), payload, "2022.3.15f1c1", signatureText: UnityCnDecryptor.ManjuuSignature);
			await AssertPayload(ReadBundle(bytes), payload);
		}
		finally
		{
			UnityCnKey.Configure(null, null);
		}
	}

	[Test]
	[Arguments("2019.4.40f1")] // 0x200 = encryption according to everyone
	[Arguments("2021.3.2f1")] // boundary: AnimeStudio says encryption, the padding fix says padding
	[Arguments("2022.3.0f1c1")] // same
	public async Task OldEncryptionFlagIsDetected(string version)
	{
		byte[] payload = MakePayload();
		try
		{
			UnityCnKey.Set(Key32);
			byte[] bytes = MakeEncryptedBundle(Encoding.UTF8.GetBytes(Key32), payload, version, flags: 0x40 | 0x200, padding: false);
			FileStreamBundleFile bundle = ReadBundle(bytes);
			await Assert.That(bundle.UnityCn).IsNotNull();
			await AssertPayload(bundle, payload);
		}
		finally
		{
			UnityCnKey.Configure(null, null);
		}
	}

	[Test]
	[Arguments("2021.3.2f1")]
	[Arguments("2022.3.10f1")]
	[Arguments("2020.1.0f1")]
	public async Task PaddingFlagIsNotMistakenForEncryption(string version)
	{
		byte[] payload = MakePayload();
		try
		{
			UnityCnKey.Set(Key32); // a key is configured, but the bundle is not encrypted
			byte[] bytes = MakeEncryptedBundle(Encoding.UTF8.GetBytes(Key32), payload, version, flags: 0x40 | 0x200, encrypt: false, padding: true);
			FileStreamBundleFile bundle = ReadBundle(bytes);
			await Assert.That(bundle.UnityCn).IsNull();
			await AssertPayload(bundle, payload);
		}
		finally
		{
			UnityCnKey.Configure(null, null);
		}
	}

	[Test]
	public async Task Flag0x1000IsHandled()
	{
		byte[] payload = MakePayload();
		try
		{
			UnityCnKey.Set(Key24);
			byte[] encrypted = MakeEncryptedBundle(Encoding.UTF8.GetBytes(Key24), payload, "2022.3.15f1c1", flags: 0x40 | 0x200 | 0x1000);
			await AssertPayload(ReadBundle(encrypted), payload);
			byte[] plain = MakeEncryptedBundle(Encoding.UTF8.GetBytes(Key24), payload, "2022.3.15f1c1", flags: 0x40 | 0x200 | 0x1000, encrypt: false);
			FileStreamBundleFile bundle = ReadBundle(plain);
			await Assert.That(bundle.UnityCn).IsNull();
			await AssertPayload(bundle, payload);
		}
		finally
		{
			UnityCnKey.Configure(null, null);
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
	private static byte[] MakeEncryptedBundle(byte[] key, byte[] payload, string unityVersion,
		int flags = 0x40 | 0x200 | 0x400, string signatureText = UnityCnDecryptor.Signature, bool encrypt = true, bool padding = true)
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
		byte[] signature = Encoding.ASCII.GetBytes(signatureText);

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
		if (encrypt)
		{
			EncryptBlock(compressed, 0, index, sub);
		}

		// Blocks info + directory (uncompressed, combined)
		MemoryStream meta = new();
		meta.Write(new byte[16]); // hash
		WriteInt32(meta, 1);
		WriteInt32(meta, payload.Length);
		WriteInt32(meta, compressed.Length);
		WriteInt16(meta, (short)((encrypt ? 0x100 : 0) | 2)); // encrypted + LZ4
		WriteInt32(meta, 1);
		WriteInt64(meta, 0);
		WriteInt64(meta, payload.Length);
		WriteInt32(meta, 4);
		meta.Write(Encoding.ASCII.GetBytes("CAB-test\0"));

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
		if (encrypt)
		{
			cnData.WriteTo(bundle);
		}
		Align16(bundle);
		meta.WriteTo(bundle);
		if (padding)
		{
			Align16(bundle);
		}
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
