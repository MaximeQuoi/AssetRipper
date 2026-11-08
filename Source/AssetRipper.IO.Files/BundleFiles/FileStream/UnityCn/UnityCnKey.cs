using System.Text;

namespace AssetRipper.IO.Files.BundleFiles.FileStream.UnityCn;

/// <summary>
/// Parsing and storage of the user supplied UnityCN (Unity China / Tuanjie) AssetBundle decryption key.
/// </summary>
/// <remarks>
/// Accepted formats, all of which produce a valid AES key (16, 24 or 32 bytes):
/// <list type="bullet">
/// <item>32 hexadecimal characters: 16 byte AES-128 key (the format used by most tools).</item>
/// <item>24 characters: either Base64 of a 16 byte key, or the raw 24 character key string (AES-192).</item>
/// <item>32 characters: also tried as the raw 32 character key string (AES-256).</item>
/// <item>16 characters: the raw key string, as passed to <c>AssetBundle.SetAssetBundleDecryptKey</c> (AES-128).</item>
/// <item>48 or 64 hexadecimal characters: AES-192 or AES-256 keys.</item>
/// </list>
/// An explicit prefix (<c>hex:</c>, <c>base64:</c> / <c>b64:</c>, <c>raw:</c> / <c>ascii:</c>) restricts the interpretation.
/// When a key string has several possible interpretations, every candidate is tried
/// and the one that decrypts the bundle signature is kept.
/// </remarks>
public static class UnityCnKey
{
	/// <summary>
	/// Environment variable read when no key was configured explicitly.
	/// </summary>
	public const string EnvironmentVariable = "ASSETRIPPER_UNITYCN_KEY";

	private static string? s_key;
	private static IReadOnlyList<byte[]> s_candidates = [];

	/// <summary>
	/// The configured key string, or <see langword="null"/> if none.
	/// </summary>
	public static string? Current => s_key;

	/// <summary>
	/// The candidate AES keys derived from <see cref="Current"/>, falling back to <see cref="EnvironmentVariable"/>.
	/// </summary>
	public static IReadOnlyList<byte[]> Candidates
	{
		get
		{
			if (s_key is null)
			{
				string? env = Environment.GetEnvironmentVariable(EnvironmentVariable);
				if (!string.IsNullOrWhiteSpace(env) && TryParse(env, out IReadOnlyList<byte[]> envCandidates, out _))
				{
					return envCandidates;
				}
			}
			return s_candidates;
		}
	}

	/// <summary>
	/// Sets the key used for UnityCN bundles. <see langword="null"/> or whitespace clears it.
	/// </summary>
	/// <exception cref="ArgumentException">The key cannot be interpreted as an AES key.</exception>
	public static void Set(string? key)
	{
		if (string.IsNullOrWhiteSpace(key))
		{
			s_key = null;
			s_candidates = [];
			return;
		}

		if (!TryParse(key, out IReadOnlyList<byte[]> candidates, out string? error))
		{
			throw new ArgumentException(error, nameof(key));
		}
		s_key = key.Trim();
		s_candidates = candidates;
	}

	/// <summary>
	/// Converts a key string into the list of AES keys it could represent, most likely first.
	/// </summary>
	public static bool TryParse(string? key, out IReadOnlyList<byte[]> candidates, [NotNullWhen(false)] out string? error)
	{
		List<byte[]> result = [];
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
				AddCandidate(result, Convert.FromHexString(compactHex));
			}
		}

		if (prefix is null or "base64" or "b64")
		{
			if (trimmed.Length % 4 == 0 && TryFromBase64(trimmed, out byte[]? decoded) && IsAesKeyLength(decoded.Length))
			{
				AddCandidate(result, decoded);
			}
		}

		if (prefix is null or "raw" or "ascii")
		{
			byte[] raw = Encoding.UTF8.GetBytes(trimmed);
			if (IsAesKeyLength(raw.Length))
			{
				AddCandidate(result, raw);
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

	private static void AddCandidate(List<byte[]> list, byte[] candidate)
	{
		foreach (byte[] existing in list)
		{
			if (existing.AsSpan().SequenceEqual(candidate))
			{
				return;
			}
		}
		list.Add(candidate);
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
