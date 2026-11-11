using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using K4os.Compression.LZ4;
using System.Buffers.Binary;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Shaders.GlslTranslation;

/// <summary>
/// A compiled GPU program of a shader, as stored in its compressed blob.
/// </summary>
internal sealed record class BlobSubProgram(int ProgramType, IReadOnlyList<string> Keywords, byte[] Code)
{
	public string CodeText => Encoding.UTF8.GetString(Code).TrimEnd('\0');
}

/// <summary>
/// Reads the compressed program blob of a shader (Unity 5.5 and later).
/// </summary>
/// <remarks>
/// Format from AnimeStudio's ShaderConverter (MIT License, Copyright (c) 2016 Radu, 2016-2020 Perfare, 2022-2024 Razmoth).
/// </remarks>
internal static class ShaderBlob
{
	[ThreadStatic]
	public static string? LastError;

	/// <summary>
	/// The sub programs of one platform, by blob index. Empty if the platform is not in the shader or the blob cannot be read.
	/// </summary>
	/// <summary>
	/// The decompressed blob entries of one platform, by blob index: programs and, since Unity 2021.2, parameter tables.
	/// Empty if the platform is not in the shader or the blob cannot be read.
	/// </summary>
	public static Dictionary<int, byte[]> ReadEntries(IShader shader, GPUPlatform platform)
	{
		Dictionary<int, byte[]> result = [];
		int platformIndex = -1;
		for (int i = 0; i < shader.Platforms.Count; i++)
		{
			if (shader.Platforms[i] == (uint)platform)
			{
				platformIndex = i;
				break;
			}
		}
		if (platformIndex < 0 || shader.CompressedBlob.Length == 0)
		{
			return result;
		}

		try
		{
			List<(uint Offset, uint Compressed, uint Decompressed)> segments = [];
			bool hasSegments = shader.Has_Offsets_AssetList_AssetList_UInt32();
			if (hasSegments)
			{
				var offsets = shader.Offsets_AssetList_AssetList_UInt32[platformIndex];
				var compressed = shader.CompressedLengths_AssetList_AssetList_UInt32[platformIndex];
				var decompressed = shader.DecompressedLengths_AssetList_AssetList_UInt32[platformIndex];
				for (int j = 0; j < offsets.Count; j++)
				{
					segments.Add((offsets[j], compressed[j], decompressed[j]));
				}
			}
			else if (shader.Has_Offsets_AssetList_UInt32())
			{
				segments.Add((shader.Offsets_AssetList_UInt32[platformIndex], shader.CompressedLengths_AssetList_UInt32[platformIndex], shader.DecompressedLengths_AssetList_UInt32[platformIndex]));
			}

			List<(int Offset, int Length, int Segment)>? entries = null;
			for (int segment = 0; segment < segments.Count; segment++)
			{
				(uint offset, uint compressedLength, uint decompressedLength) = segments[segment];
				byte[] data = new byte[decompressedLength];
				int written = LZ4Codec.Decode(shader.CompressedBlob.AsSpan((int)offset, (int)compressedLength), data);
				if (written != decompressedLength)
				{
					LastError = $"LZ4: {written} bytes instead of {decompressedLength}";
					return result;
				}
				if (segment == 0)
				{
					entries = ReadEntries(data, hasSegments);
				}
				for (int i = 0; i < entries!.Count; i++)
				{
					if (entries[i].Segment == segment && entries[i].Offset >= 0 && entries[i].Offset + entries[i].Length <= data.Length)
					{
						result[i] = data.AsSpan(entries[i].Offset, entries[i].Length).ToArray();
					}
				}
			}
		}
		catch (Exception e)
		{
			LastError = e.ToString();
			result.Clear();
		}
		return result;
	}

	public static BlobSubProgram? TryReadProgram(byte[] entry)
	{
		try
		{
			return ReadSubProgram(entry);
		}
		catch (Exception e)
		{
			LastError = e.ToString();
			return null;
		}
	}

	private static List<(int, int, int)> ReadEntries(byte[] data, bool hasSegments)
	{
		int position = 0;
		int count = ReadInt32(data, ref position);
		List<(int, int, int)> entries = new(count);
		for (int i = 0; i < count; i++)
		{
			int offset = ReadInt32(data, ref position);
			int length = ReadInt32(data, ref position);
			int segment = hasSegments ? ReadInt32(data, ref position) : 0;
			entries.Add((offset, length, segment));
		}
		return entries;
	}

	[ThreadStatic]
	public static string? FirstProgramHex;

	private static BlobSubProgram? ReadSubProgram(ReadOnlySpan<byte> data)
	{
		FirstProgramHex ??= Convert.ToHexString(data[..Math.Min(200, data.Length)]) + " len " + data.Length;
		int position = 0;
		int version = ReadInt32(data, ref position);
		int programType = ReadInt32(data, ref position);
		position += 12;
		if (version >= 201608170)
		{
			position += 4;
		}
		int keywordCount = ReadInt32(data, ref position);
		if (keywordCount < 0 || keywordCount > 1024)
		{
			return null;
		}
		List<string> keywords = new(keywordCount);
		for (int i = 0; i < keywordCount; i++)
		{
			keywords.Add(ReadAlignedString(data, ref position));
		}
		if (version >= 201806140 && version < 202012090)
		{
			int localCount = ReadInt32(data, ref position);
			for (int i = 0; i < localCount; i++)
			{
				keywords.Add(ReadAlignedString(data, ref position));
			}
		}
		int codeLength = ReadInt32(data, ref position);
		if (codeLength < 0 || position + codeLength > data.Length)
		{
			return null;
		}
		byte[] code = data.Slice(position, codeLength).ToArray();
		return new BlobSubProgram(programType, keywords, code);
	}

	private static int ReadInt32(ReadOnlySpan<byte> data, ref int position)
	{
		int value = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
		position += 4;
		return value;
	}

	private static string ReadAlignedString(ReadOnlySpan<byte> data, ref int position)
	{
		int length = ReadInt32(data, ref position);
		string value = Encoding.UTF8.GetString(data.Slice(position, length));
		position += length;
		position = (position + 3) & ~3;
		return value;
	}
}
