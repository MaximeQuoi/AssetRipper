using AssetRipper.IO.Files.BundleFiles.FileStream;
using AssetRipper.IO.Files.BundleFiles.FileStream.NetEase;
using AssetRipper.IO.Files.BundleFiles.FileStream.UnityCn;
using AssetRipper.IO.Files.Streams.Smart;
using System.Text;

namespace AssetRipper.IO.Files.Tests;

[NotInParallel(nameof(UnityCnTests))]
public class NetEaseTests
{
	private const string UnityVersion = "2022.3.62f1";

	/// <summary>
	/// A payload starting like a serialized file header, so that the compressed block contains the Unity version as a literal.
	/// </summary>
	private static byte[] MakePayload(int lines)
	{
		StringBuilder sb = new();
		for (int i = 0; i < lines; i++)
		{
			sb.Append("NetEase payload ").Append(i).Append(' ').Append((i * 7919) % 104729).Append('\n');
		}
		return [.. new byte[20], .. Encoding.ASCII.GetBytes(UnityVersion), 0, 0x0D, 0, 0, 0, .. Encoding.ASCII.GetBytes(sb.ToString())];
	}

	/// <summary>
	/// Applies the NetEase transformation: header (EE DD + version) over the Unity version, then encryption from 0x30.
	/// </summary>
	/// <remarks>
	/// The cipher only XORs keystreams derived from the ciphertext's first bytes, so it can be inverted with the decryptor itself.
	/// </remarks>
	private static byte[] EncryptFirstBlock(byte[] plain)
	{
		byte[] result = (byte[])plain.Clone();
		int index = result.AsSpan().IndexOf(Encoding.ASCII.GetBytes(UnityVersion));
		if (index < 0 || index >= 0x20)
		{
			throw new InvalidOperationException($"Unity version literal at {index}");
		}
		result[index] = 0xEE;
		result[index + 1] = 0xDD;
		result[index + 2] = 0x22;
		result[index + 3] = 0x20;

		const int offset = 0x30;
		int size = Math.Min(result.Length, 0x1000) - offset;
		Span<byte> region = result.AsSpan(offset, size);
		byte[] p = region.ToArray();

		// 1. First 0x20 bytes: XOR 0xA6
		byte[] c = new byte[size];
		for (int i = 0; i < 0x20; i++)
		{
			c[i] = (byte)(p[i] ^ 0xA6);
		}
		// 2. Next 0x80 bytes: RC4 keyed by the first 0x20 ciphertext bytes
		byte[] probe = new byte[size];
		c.AsSpan(0, 0x20).CopyTo(probe);
		NetEaseDecryptor.DecryptWithoutHeader(probe);
		for (int i = 0x20; i < Math.Min(0xA0, size); i++)
		{
			c[i] = (byte)(p[i] ^ probe[i]);
		}
		// 3. The rest: keystream from the first 0xA0 ciphertext bytes
		probe = new byte[size];
		c.AsSpan(0, Math.Min(0xA0, size)).CopyTo(probe);
		NetEaseDecryptor.DecryptWithoutHeader(probe);
		for (int i = 0xA0; i < size; i++)
		{
			c[i] = (byte)(p[i] ^ probe[i]);
		}
		c.CopyTo(region);
		return result;
	}

	private static FileStreamBundleFile Read(byte[] bytes)
	{
		return new FileStreamBundleScheme().Read(SmartStream.CreateMemory(bytes), "test.bundle", "test.bundle");
	}

	[Test]
	[Arguments(300)]  // first block smaller than 0x1000 bytes
	[Arguments(6000)] // larger than 0x1000 bytes
	public async Task NetEaseFirstBlockIsDecrypted(int lines)
	{
		byte[] payload = MakePayload(lines);
		byte[]? encryptedBlock = null;
		byte[] bytes = UnityCnTestsAccess.MakeBundle(payload, block => encryptedBlock = EncryptFirstBlock(block));

		// Without the NetEase decryption, the block is not valid LZ4
		byte[] output = new byte[payload.Length];
		await Assert.That(K4os.Compression.LZ4.LZ4Codec.Decode(encryptedBlock!, output)).IsNotEqualTo(payload.Length);

		UnityCnKey.Configure(null, null);
		FileStreamBundleFile bundle = Read(bytes);
		await Assert.That(bundle.FailedFiles.Count).IsEqualTo(0);
		await Assert.That(bundle.ResourceFiles[0].ToByteArray().SequenceEqual(payload)).IsTrue();
	}

	[Test]
	public async Task PlainBundleWithNetEaseLookingBytesIsUntouched()
	{
		// Valid LZ4 that happens to contain EE DD 22 20 near the start
		byte[] payload = [.. new byte[8], 0xEE, 0xDD, 0x22, 0x20, .. MakePayload(300)];
		byte[] bytes = UnityCnTestsAccess.MakeBundle(payload, null);
		FileStreamBundleFile bundle = Read(bytes);
		await Assert.That(bundle.FailedFiles.Count).IsEqualTo(0);
		await Assert.That(bundle.ResourceFiles[0].ToByteArray().SequenceEqual(payload)).IsTrue();
	}

	[Test]
	public async Task HeaderDetection()
	{
		await Assert.That(NetEaseDecryptor.HasHeader([0x16, 0x00, 0xEE, 0xDD, 0x22, 0x20, 0x2E, 0x33, 0x2E, 0x36, 0x32, 0x66])).IsTrue();
		await Assert.That(NetEaseDecryptor.HasHeader([0x16, 0x00, 0xEE, 0xDD, 0x99, 0x19, 0x2E, 0x33, 0x2E, 0x36, 0x32, 0x66])).IsFalse();
		await Assert.That(NetEaseDecryptor.HasHeader(new byte[64])).IsFalse();
	}
}
