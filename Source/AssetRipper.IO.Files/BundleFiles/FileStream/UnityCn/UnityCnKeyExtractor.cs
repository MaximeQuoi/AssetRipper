using System.Text;

namespace AssetRipper.IO.Files.BundleFiles.FileStream.UnityCn;

/// <summary>
/// Extracts UnityCN key candidates from text files, typically the output of Il2CppDumper:
/// <list type="bullet">
/// <item><c>dump.cs</c>: constant fields, eg <c>private const string Key = "...";</c></item>
/// <item><c>stringliteral.json</c>: every string literal of the code, eg the argument of <c>AssetBundle.SetAssetBundleDecryptKey</c>.</item>
/// </list>
/// Any other text file works too: every quoted string, and every line that is itself a possible key, is considered.
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
		List<UnityCnKeyCandidate> keyShaped = [];
		List<UnityCnKeyCandidate> other = [];
		HashSet<UnityCnKeyCandidate> seen = [];

		List<string> strings = [];
		string? line;
		int lineNumber = 0;
		while ((line = reader.ReadLine()) is not null)
		{
			lineNumber++;
			strings.Clear();
			ReadQuotedStrings(line, strings);
			if (strings.Count == 0)
			{
				// Plain text file with one key per line
				string trimmed = line.Trim();
				if (UnityCnKey.HasPossibleKeyLength(trimmed.Length) && !trimmed.Contains(' '))
				{
					strings.Add(trimmed);
				}
				else
				{
					continue;
				}
			}

			bool hasKeyWord = ContainsKeyWord(line);
			foreach (string value in strings)
			{
				if (!IsPlausible(value))
				{
					continue;
				}
				if (!UnityCnKey.TryParse(value, $"{source}:{lineNumber}", out IReadOnlyList<UnityCnKeyCandidate> parsed, out _))
				{
					continue;
				}
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
					else if (candidate.Format is "hex" or "base64" || !value.Contains(' '))
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

		return [.. nearKeyWord, .. keyShaped, .. other];
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
