using System.Text;

namespace AssetRipper.IO.Files.BundleFiles.FileStream.UnityCn;

/// <summary>
/// An AES key that may decrypt UnityCN bundles.
/// </summary>
/// <param name="Key">The AES key (16, 24 or 32 bytes).</param>
/// <param name="Text">The key string it was derived from.</param>
/// <param name="Format">How <paramref name="Text"/> was interpreted: hex, base64 or raw.</param>
/// <param name="Source">Where the key comes from: settings, environment, or a key file.</param>
public sealed record class UnityCnKeyCandidate(byte[] Key, string Text, string Format, string Source)
{
	public override string ToString() => $"{Text} ({Format}, AES-{Key.Length * 8}, {Source})";

	public bool Equals(UnityCnKeyCandidate? other) => other is not null && Key.AsSpan().SequenceEqual(other.Key);

	public override int GetHashCode()
	{
		HashCode hash = new();
		hash.AddBytes(Key);
		return hash.ToHashCode();
	}
}

/// <summary>
/// Parsing and storage of the UnityCN (Unity China / Tuanjie) AssetBundle decryption keys.
/// </summary>
/// <remarks>
/// Accepted key formats, all of which produce a valid AES key (16, 24 or 32 bytes):
/// <list type="bullet">
/// <item>32 hexadecimal characters: 16 byte AES-128 key (the format used by most tools).</item>
/// <item>24 characters: either Base64 of a 16 byte key, or the raw 24 character key string (AES-192).</item>
/// <item>32 characters: also tried as the raw 32 character key string (AES-256).</item>
/// <item>16 characters: the raw key string, as passed to <c>AssetBundle.SetAssetBundleDecryptKey</c> (AES-128).</item>
/// <item>48 or 64 hexadecimal characters: AES-192 or AES-256 keys.</item>
/// </list>
/// An explicit prefix (<c>hex:</c>, <c>base64:</c> / <c>b64:</c>, <c>raw:</c> / <c>ascii:</c>) restricts the interpretation.
/// Several keys can be given, separated by <c>;</c> or new lines.<br/>
/// Keys can also be extracted from a file, typically the <c>dump.cs</c> or <c>stringliteral.json</c> produced by Il2CppDumper
/// (see <see cref="UnityCnKeyExtractor"/>).<br/>
/// Every candidate is tried on each encrypted bundle, and the one that decrypts the bundle signature is kept.
/// Keys that already worked are tried first, and publicly known keys (<see cref="UnityCnKnownKeys"/>) are tried too.
/// </remarks>
public static class UnityCnKey
{
	/// <summary>
	/// Environment variable read when no key was configured explicitly.
	/// </summary>
	public const string EnvironmentVariable = "ASSETRIPPER_UNITYCN_KEY";
	/// <summary>
	/// Environment variable read when no key file was configured explicitly.
	/// </summary>
	public const string KeyFileEnvironmentVariable = "ASSETRIPPER_UNITYCN_KEYFILE";

	private static readonly Lock s_lock = new();
	private static string? s_key;
	private static string? s_keyFile;
	private static UnityCnKeyCandidate[] s_manual = [];
	private static UnityCnKeyCandidate[] s_file = [];
	private static bool s_environmentLoaded;
	private static readonly List<UnityCnKeyCandidate> s_found = [];
	private static UnityCnKeyCandidate[] s_foundSnapshot = [];

	/// <summary>
	/// The configured key string, or <see langword="null"/> if none.
	/// </summary>
	public static string? Current => s_key;

	/// <summary>
	/// The configured key file, or <see langword="null"/> if none.
	/// </summary>
	public static string? CurrentKeyFile => s_keyFile;

	/// <summary>
	/// Whether the publicly known keys (<see cref="UnityCnKnownKeys"/>) are tried. True by default.
	/// </summary>
	public static bool UseKnownKeys { get; set; } = true;

	/// <summary>
	/// The configured candidates (manual keys first, then the key file), without the environment fallbacks and known keys.
	/// </summary>
	public static IReadOnlyList<UnityCnKeyCandidate> Candidates
	{
		get
		{
			lock (s_lock)
			{
				return [.. s_manual, .. s_file];
			}
		}
	}

	/// <summary>
	/// The keys that decrypted at least one bundle, in the order they were found.
	/// </summary>
	public static IReadOnlyList<UnityCnKeyCandidate> FoundKeys
	{
		get
		{
			lock (s_lock)
			{
				return s_foundSnapshot;
			}
		}
	}

	/// <summary>
	/// Sets the manual keys (separated by <c>;</c> or new lines) used for UnityCN bundles. <see langword="null"/> or whitespace clears them.
	/// </summary>
	/// <exception cref="ArgumentException">A key cannot be interpreted as an AES key.</exception>
	public static void Set(string? key) => Configure(key, s_keyFile);

	/// <summary>
	/// Sets the manual keys and the key file, and resets the found keys.
	/// </summary>
	/// <param name="key">Manual keys, separated by <c>;</c> or new lines.</param>
	/// <param name="keyFile">A file to extract keys from, eg the <c>dump.cs</c> or <c>stringliteral.json</c> of Il2CppDumper.</param>
	/// <returns>The number of candidates.</returns>
	/// <exception cref="ArgumentException">A manual key cannot be interpreted as an AES key.</exception>
	/// <exception cref="IOException">The key file cannot be read.</exception>
	public static int Configure(string? key, string? keyFile)
	{
		List<UnityCnKeyCandidate> manual = [];
		string? normalizedKey = string.IsNullOrWhiteSpace(key) ? null : key.Trim();
		string? normalizedFile = string.IsNullOrWhiteSpace(keyFile) ? null : keyFile.Trim().Trim('"');

		if (normalizedKey is not null)
		{
			foreach (string part in SplitKeys(normalizedKey))
			{
				if (!TryParse(part, "settings", out IReadOnlyList<UnityCnKeyCandidate> parsed, out string? error))
				{
					throw new ArgumentException(error, nameof(key));
				}
				manual.AddRange(parsed);
			}
		}

		IReadOnlyList<UnityCnKeyCandidate> file = normalizedFile is null ? [] : UnityCnKeyExtractor.ExtractFromFile(normalizedFile);

		lock (s_lock)
		{
			s_key = normalizedKey;
			s_keyFile = normalizedFile;
			s_manual = manual.Distinct().ToArray();
			s_file = file.Distinct().Except(s_manual).ToArray();
			s_environmentLoaded = false;
			s_found.Clear();
			s_foundSnapshot = [];
			return s_manual.Length + s_file.Length;
		}
	}

	/// <summary>
	/// The candidates to try on a bundle, in order: keys already found, manual keys, publicly known keys, then the key file.
	/// If nothing is configured, <see cref="EnvironmentVariable"/> and <see cref="KeyFileEnvironmentVariable"/> are used.
	/// </summary>
	public static IEnumerable<UnityCnKeyCandidate> GetCandidates()
	{
		LoadEnvironmentIfNeeded();

		UnityCnKeyCandidate[] found;
		UnityCnKeyCandidate[] manual;
		UnityCnKeyCandidate[] file;
		lock (s_lock)
		{
			found = s_foundSnapshot;
			manual = s_manual;
			file = s_file;
		}
		IReadOnlyList<UnityCnKeyCandidate> known = UseKnownKeys ? UnityCnKnownKeys.Candidates : [];

		HashSet<UnityCnKeyCandidate> seen = [];
		foreach (IReadOnlyList<UnityCnKeyCandidate> list in (IReadOnlyList<UnityCnKeyCandidate>[])[found, manual, known, file])
		{
			foreach (UnityCnKeyCandidate candidate in list)
			{
				if (seen.Add(candidate))
				{
					yield return candidate;
				}
			}
		}
	}

	/// <summary>
	/// Records a key that decrypted a bundle, so that it is tried first next time.
	/// </summary>
	public static void ReportFound(UnityCnKeyCandidate candidate)
	{
		lock (s_lock)
		{
			if (!s_found.Contains(candidate))
			{
				s_found.Add(candidate);
				s_foundSnapshot = s_found.ToArray();
			}
		}
	}

	private static void LoadEnvironmentIfNeeded()
	{
		lock (s_lock)
		{
			if (s_environmentLoaded || s_key is not null || s_keyFile is not null)
			{
				return;
			}
			s_environmentLoaded = true;
		}

		List<UnityCnKeyCandidate> manual = [];
		string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
		if (!string.IsNullOrWhiteSpace(env))
		{
			foreach (string part in SplitKeys(env))
			{
				if (TryParse(part, "environment", out IReadOnlyList<UnityCnKeyCandidate> parsed, out _))
				{
					manual.AddRange(parsed);
				}
			}
		}
		IReadOnlyList<UnityCnKeyCandidate> file = [];
		string? path = Environment.GetEnvironmentVariable(KeyFileEnvironmentVariable);
		if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
		{
			file = UnityCnKeyExtractor.ExtractFromFile(path);
		}

		lock (s_lock)
		{
			if (s_key is null && s_keyFile is null)
			{
				s_manual = manual.Distinct().ToArray();
				s_file = file.Distinct().Except(s_manual).ToArray();
			}
		}
	}

	private static IEnumerable<string> SplitKeys(string keys)
	{
		return keys.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	/// <summary>
	/// Converts a key string into the list of AES keys it could represent, most likely first.
	/// </summary>
	public static bool TryParse(string? key, out IReadOnlyList<byte[]> candidates, [NotNullWhen(false)] out string? error)
	{
		bool result = TryParse(key, "settings", out IReadOnlyList<UnityCnKeyCandidate> parsed, out error);
		candidates = parsed.Select(c => c.Key).ToArray();
		return result;
	}

	/// <summary>
	/// Converts a key string into the list of AES keys it could represent, most likely first.
	/// </summary>
	public static bool TryParse(string? key, string source, out IReadOnlyList<UnityCnKeyCandidate> candidates, [NotNullWhen(false)] out string? error)
	{
		List<UnityCnKeyCandidate> result = [];
		candidates = result;
		error = null;

		if (string.IsNullOrWhiteSpace(key))
		{
			error = "The UnityCN key is empty.";
			return false;
		}

		string trimmed = key.Trim();
		string? prefix = null;
		int colon = trimmed.IndexOf(':');
		if (colon > 0)
		{
			string possiblePrefix = trimmed[..colon].ToLowerInvariant();
			if (possiblePrefix is "hex" or "base64" or "b64" or "raw" or "ascii")
			{
				prefix = possiblePrefix;
				trimmed = trimmed[(colon + 1)..].Trim();
			}
		}

		// Hex keys are often copied with separators, eg "5A-4B-..." or "0x5A 0x4B ...".
		string compactHex = RemoveHexSeparators(trimmed);

		if (prefix is null or "hex")
		{
			if (IsHex(compactHex) && compactHex.Length is 32 or 48 or 64)
			{
				AddCandidate(result, new(Convert.FromHexString(compactHex), trimmed, "hex", source));
			}
		}

		if (prefix is null or "base64" or "b64")
		{
			if (trimmed.Length % 4 == 0 && TryFromBase64(trimmed, out byte[]? decoded) && IsAesKeyLength(decoded.Length))
			{
				AddCandidate(result, new(decoded, trimmed, "base64", source));
			}
		}

		if (prefix is null or "raw" or "ascii")
		{
			byte[] raw = Encoding.UTF8.GetBytes(trimmed);
			if (IsAesKeyLength(raw.Length))
			{
				AddCandidate(result, new(raw, trimmed, "raw", source));
			}
		}

		if (result.Count == 0)
		{
			error = $"Invalid UnityCN key '{trimmed}' ({trimmed.Length} characters). Expected 32 hexadecimal characters, " +
				"24 characters (Base64 or raw key), 32 characters (raw key), or a raw key of 16 characters.";
			return false;
		}
		return true;
	}

	/// <summary>
	/// Quick length check, before trying to parse a string as a key.
	/// </summary>
	internal static bool HasPossibleKeyLength(int length) => length is 16 or 24 or 32 or 48 or 64;

	private static void AddCandidate(List<UnityCnKeyCandidate> list, UnityCnKeyCandidate candidate)
	{
		if (!list.Contains(candidate))
		{
			list.Add(candidate);
		}
	}

	private static bool IsAesKeyLength(int length) => length is 16 or 24 or 32;

	private static bool IsHex(string value)
	{
		if (value.Length == 0)
		{
			return false;
		}
		foreach (char c in value)
		{
			if (!char.IsAsciiHexDigit(c))
			{
				return false;
			}
		}
		return true;
	}

	private static string RemoveHexSeparators(string value)
	{
		if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && !value.Contains(' ') && !value.Contains(','))
		{
			return value[2..];
		}
		StringBuilder sb = new(value.Length);
		string[] parts = value.Split([' ', '-', ',', ':'], StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length <= 1)
		{
			return value;
		}
		foreach (string part in parts)
		{
			sb.Append(part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part);
		}
		return sb.ToString();
	}

	private static bool TryFromBase64(string value, [NotNullWhen(true)] out byte[]? bytes)
	{
		byte[] buffer = new byte[value.Length];
		if (Convert.TryFromBase64String(value, buffer, out int written))
		{
			bytes = buffer[..written];
			return true;
		}
		bytes = null;
		return false;
	}
}
