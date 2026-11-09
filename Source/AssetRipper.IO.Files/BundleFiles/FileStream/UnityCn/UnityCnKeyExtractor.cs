using System.Text;

namespace AssetRipper.IO.Files.BundleFiles.FileStream.UnityCn;

/// <summary>
/// Extracts UnityCN key candidates from text files, typically the output of Il2CppDumper:
/// <list type="bullet">
/// <item><c>dump.cs</c>: constant fields, eg <c>private const string Key = "...";</c></item>
/// <item><c>stringliteral.json</c>: every string literal of the code, eg the argument of <c>AssetBundle.SetAssetBundleDecryptKey</c>.</item>
/// </list>
/// Any other text file works too: every quoted string, every line that is itself a possible key,
/// and every 32 digit hexadecimal key anywhere in the text, are considered.
/// </summary>
/// <remarks>
/// Only strings with a possible key length are kept. Strings found next to key related words
/// (key, secret, encrypt, aes...) come first. The real keys are then identified by decrypting the bundle signatures.
/// </remarks>
public static class UnityCnKeyExtractor
{
	private static readonly string[] KeyWords =
	[
		"key", "secret", "crypt", "aes", "cipher", "password", "passwd", "pwd", "unitycn", "unity3dchina", "tuanjie", "assetbundle", "bundle",
	];

	public static IReadOnlyList<UnityCnKeyCandidate> ExtractFromFile(string path)
	{
		using StreamReader reader = new(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return Extract(reader, Path.GetFileName(path));
	}

	public static IReadOnlyList<UnityCnKeyCandidate> ExtractFromText(string text, string source = "text")
	{
		using StringReader reader = new(text);
		return Extract(reader, source);
	}

	public static IReadOnlyList<UnityCnKeyCandidate> Extract(TextReader reader, string source)
	{
		// Buckets, most likely first.
		List<UnityCnKeyCandidate> nearKeyWord = [];
		List<UnityCnKeyCandidate> hex128 = [];
		List<UnityCnKeyCandidate> keyShaped = [];
		List<UnityCnKeyCandidate> other = [];
		HashSet<UnityCnKeyCandidate> seen = [];

		List<string> strings = [];
		List<string> hexRuns = [];
		string? line;
		int lineNumber = 0;
		while ((line = reader.ReadLine()) is not null)
		{
			lineNumber++;
			strings.Clear();
			hexRuns.Clear();
			ReadQuotedStrings(line, strings);
			ReadHexKeys(line, hexRuns);
			if (strings.Count == 0)
			{
				// Plain text file with one key per line
				string trimmed = line.Trim();
				if (UnityCnKey.HasPossibleKeyLength(trimmed.Length) && !trimmed.Contains(' '))
				{
					strings.Add(trimmed);
				}
			}
			if (strings.Count == 0 && hexRuns.Count == 0)
			{
				continue;
			}

			bool hasKeyWord = ContainsKeyWord(line);
			string lineSource = $"{source}:{lineNumber}";

			foreach (string value in strings)
			{
				if (IsPlausible(value) && UnityCnKey.TryParse(value, lineSource, out IReadOnlyList<UnityCnKeyCandidate> parsed, out _))
				{
					AddAll(parsed, value.Contains(' '));
				}
			}
			foreach (string hex in hexRuns)
			{
				// Hex keys found anywhere: in comments, inside longer strings, as 0x... values, in GUIDs.
				if (UnityCnKey.TryParse("hex:" + hex, lineSource, out IReadOnlyList<UnityCnKeyCandidate> parsed, out _))
				{
					AddAll(parsed, false);
				}
			}

			void AddAll(IReadOnlyList<UnityCnKeyCandidate> parsed, bool hasSpace)
			{
				foreach (UnityCnKeyCandidate candidate in parsed)
				{
					if (!seen.Add(candidate))
					{
						continue;
					}
					if (hasKeyWord)
					{
						nearKeyWord.Add(candidate);
					}
					else if (candidate.Format == "hex" && candidate.Key.Length == 16)
					{
						hex128.Add(candidate);
					}
					else if (candidate.Format is "hex" or "base64" || !hasSpace)
					{
						keyShaped.Add(candidate);
					}
					else
					{
						other.Add(candidate);
					}
				}
			}
		}

		return [.. nearKeyWord, .. hex128, .. keyShaped, .. other];
	}

	/// <summary>
	/// Maximum length of a hexadecimal run split into 32 digit chunks.
	/// </summary>
	private const int MaxHexRunLength = 256;

	/// <summary>
	/// Finds every hexadecimal key of a line, wherever it is: each run of 32, 48 or 64 hex digits,
	/// every 32 digit window of runs up to 47 digits, the aligned 32 digit chunks of longer runs,
	/// and GUIDs (8-4-4-4-12) without their dashes.
	/// </summary>
	public static void ReadHexKeys(string line, List<string> result)
	{
		int i = 0;
		while (i < line.Length)
		{
			if (!char.IsAsciiHexDigit(line[i]))
			{
				i++;
				continue;
			}
			int start = i;
			while (i < line.Length && char.IsAsciiHexDigit(line[i]))
			{
				i++;
			}
			int length = i - start;
			if (length < 32)
			{
				continue;
			}
			string run = line.Substring(start, length);
			if (length is 32 or 48 or 64)
			{
				result.Add(run);
			}
			if (length > 32 && length < 48)
			{
				// A key glued to a few other hex digits: every window
				for (int offset = 0; offset + 32 <= length; offset++)
				{
					result.Add(run.Substring(offset, 32));
				}
			}
			else if (length > 32 && length <= MaxHexRunLength)
			{
				// Long runs (hashes, concatenated keys): aligned 32 digit chunks, from both ends
				for (int offset = 0; offset + 32 <= length; offset += 32)
				{
					result.Add(run.Substring(offset, 32));
				}
				if (length % 32 != 0)
				{
					for (int offset = length - 32; offset >= 0; offset -= 32)
					{
						result.Add(run.Substring(offset, 32));
					}
				}
			}
		}

		// GUIDs
		for (int index = line.IndexOf('-'); index >= 8 && index + 28 <= line.Length; index = line.IndexOf('-', index + 1))
		{
			int start = index - 8;
			if (IsGuidAt(line, start))
			{
				result.Add(line.Substring(start, 36).Replace("-", ""));
			}
		}

		static bool IsGuidAt(string text, int start)
		{
			if (start < 0 || start + 36 > text.Length)
			{
				return false;
			}
			if (start > 0 && char.IsAsciiHexDigit(text[start - 1]))
			{
				return false;
			}
			if (start + 36 < text.Length && char.IsAsciiHexDigit(text[start + 36]))
			{
				return false;
			}
			for (int k = 0; k < 36; k++)
			{
				char c = text[start + k];
				bool dash = k is 8 or 13 or 18 or 23;
				if (dash ? c != '-' : !char.IsAsciiHexDigit(c))
				{
					return false;
				}
			}
			return true;
		}
	}

	private static bool ContainsKeyWord(string line)
	{
		foreach (string word in KeyWords)
		{
			if (line.Contains(word, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	private static bool IsPlausible(string value)
	{
		if (!UnityCnKey.HasPossibleKeyLength(value.Length))
		{
			return false;
		}
		foreach (char c in value)
		{
			if (char.IsControl(c))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// Reads the C# / JSON string literals of a line, with their escape sequences decoded.
	/// </summary>
	public static void ReadQuotedStrings(string line, List<string> result)
	{
		StringBuilder sb = new();
		int i = 0;
		while (i < line.Length)
		{
			int start = line.IndexOf('"', i);
			if (start < 0)
			{
				return;
			}
			// Skip char literals like '"'
			if (start > 0 && line[start - 1] == '\'' && start + 1 < line.Length && line[start + 1] == '\'')
			{
				i = start + 2;
				continue;
			}

			sb.Clear();
			int j = start + 1;
			bool closed = false;
			while (j < line.Length)
			{
				char c = line[j];
				if (c == '"')
				{
					closed = true;
					j++;
					break;
				}
				if (c == '\\' && j + 1 < line.Length)
				{
					char e = line[j + 1];
					j += 2;
					switch (e)
					{
						case 'n': sb.Append('\n'); break;
						case 'r': sb.Append('\r'); break;
						case 't': sb.Append('\t'); break;
						case '0': sb.Append('\0'); break;
						case 'a': sb.Append('\a'); break;
						case 'b': sb.Append('\b'); break;
						case 'f': sb.Append('\f'); break;
						case 'v': sb.Append('\v'); break;
						case 'u' when j + 4 <= line.Length && TryParseHex(line.AsSpan(j, 4), out int u):
							sb.Append((char)u);
							j += 4;
							break;
						case 'x':
							{
								int length = 0;
								while (length < 4 && j + length < line.Length && char.IsAsciiHexDigit(line[j + length]))
								{
									length++;
								}
								if (length > 0 && TryParseHex(line.AsSpan(j, length), out int x))
								{
									sb.Append((char)x);
									j += length;
								}
								else
								{
									sb.Append('x');
								}
							}
							break;
						default: sb.Append(e); break;
					}
					continue;
				}
				sb.Append(c);
				j++;
			}

			if (!closed)
			{
				return;
			}
			result.Add(sb.ToString());
			i = j;
		}
	}

	private static bool TryParseHex(ReadOnlySpan<char> span, out int value)
	{
		return int.TryParse(span, System.Globalization.NumberStyles.AllowHexSpecifier, null, out value);
	}
}
