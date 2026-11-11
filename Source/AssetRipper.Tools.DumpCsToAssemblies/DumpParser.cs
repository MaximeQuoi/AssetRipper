using System.Text.RegularExpressions;

namespace AssetRipper.Tools.DumpCsToAssemblies;

/// <summary>
/// Parses the dump.cs file produced by Il2CppDumper.
/// </summary>
internal static partial class DumpParser
{
	private static readonly HashSet<string> MemberModifiers =
	[
		"public", "private", "protected", "internal", "static", "readonly", "const", "volatile", "abstract",
		"virtual", "override", "sealed", "extern", "unsafe", "new", "fixed", "event",
	];

	[GeneratedRegex(@"^// Image \d+: (?<name>.+?) - (?<start>\d+)$")]
	private static partial Regex ImageRegex();

	[GeneratedRegex(@"^(?<decl>.*?)\b(?<kind>class|struct|enum|interface) (?<rest>.+?) // TypeDefIndex: (?<index>\d+)$")]
	private static partial Regex TypeRegex();

	public static List<DumpImage> Parse(string path)
	{
		List<DumpImage> images = [];
		string currentNamespace = "";
		List<string> pendingAttributes = [];
		DumpType? currentType = null;
		Dictionary<string, DumpType> byFullName = new(StringComparer.Ordinal);

		foreach (string rawLine in File.ReadLines(path))
		{
			string line = rawLine.TrimEnd('\r');
			if (line.Length == 0)
			{
				continue;
			}

			if (currentType is null)
			{
				Match image = ImageRegex().Match(line);
				if (image.Success)
				{
					images.Add(new DumpImage { Name = image.Groups["name"].Value, FirstTypeIndex = int.Parse(image.Groups["start"].Value) });
					continue;
				}
				if (line.StartsWith("// Namespace: ", StringComparison.Ordinal))
				{
					currentNamespace = line["// Namespace: ".Length..].Trim();
					continue;
				}
				if (line.StartsWith('['))
				{
					pendingAttributes.Add(AttributeName(line));
					continue;
				}
				Match type = TypeRegex().Match(line);
				if (type.Success)
				{
					currentType = CreateType(type, currentNamespace, pendingAttributes, images);
					pendingAttributes = [];
					if (currentType is not null)
					{
						byFullName.TryAdd(currentType.FullName, currentType);
						currentType.Image.Types.Add(currentType);
					}
					else
					{
						currentType = SkipType;
					}
					continue;
				}
				continue;
			}

			if (line is "}" or "{}")
			{
				currentType = null;
				pendingAttributes = [];
				continue;
			}
			if (line == "{" || ReferenceEquals(currentType, SkipType))
			{
				continue;
			}
			if (!line.StartsWith('\t') || line.StartsWith("\t\t", StringComparison.Ordinal))
			{
				continue;
			}
			string member = line[1..];
			if (member.StartsWith("//", StringComparison.Ordinal) || member.StartsWith('|'))
			{
				continue;
			}
			if (member.StartsWith('['))
			{
				pendingAttributes.Add(AttributeName(member));
				continue;
			}
			ParseMember(currentType, StripTrailingComment(member), pendingAttributes);
			pendingAttributes = [];
		}

		// Link nested types to their declaring type.
		// Il2CppDumper writes nested types with an empty namespace, so the declaring type is found by name,
		// in the same image, as close as possible in the dump order.
		foreach (DumpImage image in images)
		{
			Dictionary<string, List<DumpType>> byNestedName = new(StringComparer.Ordinal);
			foreach (DumpType type in image.Types)
			{
				string key = string.Join('.', [.. type.DeclaringNames, type.Name]);
				if (!byNestedName.TryGetValue(key, out List<DumpType>? list))
				{
					byNestedName[key] = list = [];
				}
				list.Add(type);
			}
			// Outer types first, so that namespaces propagate to deeply nested types
			foreach (DumpType type in image.Types.Where(t => t.DeclaringNames.Length > 0).OrderBy(t => t.DeclaringNames.Length))
			{
				string declaringKey = string.Join('.', type.DeclaringNames);
				if (!byNestedName.TryGetValue(declaringKey, out List<DumpType>? candidates))
				{
					continue;
				}
				DumpType declaring = candidates
					.Where(c => c != type)
					.OrderBy(c => Math.Abs(c.Index - type.Index))
					.First();
				type.DeclaringType = declaring;
				type.Namespace = declaring.Namespace;
				declaring.NestedTypes.Add(type);
				// Nested types of generic types repeat the generic parameters of the declaring types.
				foreach (string parameter in AllGenericParameters(declaring))
				{
					if (type.GenericParameters.Count > 0 && type.GenericParameters[0] == parameter)
					{
						type.GenericParameters.RemoveAt(0);
					}
				}
			}
		}
		return images;
	}

	public static IEnumerable<string> AllGenericParameters(DumpType type)
	{
		List<string> result = [];
		for (DumpType? t = type; t is not null; t = t.DeclaringType)
		{
			result.InsertRange(0, t.GenericParameters);
		}
		return result;
	}

	private static readonly DumpType SkipType = new()
	{
		Index = -1,
		Namespace = "",
		Name = "<skip>",
		DeclaringNames = [],
		GenericParameters = [],
		Kind = DumpTypeKind.Class,
		Modifiers = [],
		BaseTypes = [],
		Attributes = [],
		Image = new DumpImage { Name = "<skip>", FirstTypeIndex = -1 },
	};

	private static DumpType? CreateType(Match match, string ns, List<string> attributes, List<DumpImage> images)
	{
		int index = int.Parse(match.Groups["index"].Value);
		DumpImage? image = images.LastOrDefault(i => i.FirstTypeIndex <= index);
		if (image is null)
		{
			return null;
		}
		List<string> modifiers = match.Groups["decl"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
		DumpTypeKind kind = match.Groups["kind"].Value switch
		{
			"struct" => DumpTypeKind.Struct,
			"enum" => DumpTypeKind.Enum,
			"interface" => DumpTypeKind.Interface,
			_ => DumpTypeKind.Class,
		};

		string rest = match.Groups["rest"].Value;
		string namePart = rest;
		List<string> bases = [];
		int colon = IndexOfTopLevel(rest, " : ");
		if (colon >= 0)
		{
			namePart = rest[..colon];
			bases = SplitTopLevel(rest[(colon + 3)..], ',').Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
		}

		List<string> segments = SplitTopLevel(namePart, '.');
		string last = segments[^1];
		(string name, List<string> genericParameters) = SplitGenericName(last);
		string[] declaring = segments.Take(segments.Count - 1).Select(s => SplitGenericName(s).Name).ToArray();

		return new DumpType
		{
			Index = index,
			Namespace = ns,
			Name = name,
			DeclaringNames = declaring,
			GenericParameters = genericParameters,
			Kind = kind,
			Modifiers = modifiers,
			BaseTypes = bases,
			Attributes = [.. attributes],
			Image = image,
		};
	}

	private static void ParseMember(DumpType type, string member, List<string> attributes)
	{
		bool isProperty = member.EndsWith('}') && (member.Contains("{ get;", StringComparison.Ordinal) || member.Contains("{ set;", StringComparison.Ordinal));
		if (isProperty)
		{
			int brace = member.IndexOf(" {", StringComparison.Ordinal);
			string head = member[..brace];
			string accessors = member[brace..];
			(List<string> modifiers, string typeAndName) = TakeModifiers(head);
			(string propertyType, string name) = SplitTypeAndName(typeAndName);
			if (name.Length == 0)
			{
				return;
			}
			type.Properties.Add(new DumpProperty
			{
				Modifiers = modifiers,
				Type = propertyType,
				Name = name,
				HasGetter = accessors.Contains("get;", StringComparison.Ordinal),
				HasSetter = accessors.Contains("set;", StringComparison.Ordinal),
			});
			return;
		}

		bool isMethod = member.EndsWith(") { }", StringComparison.Ordinal) || member.EndsWith(");", StringComparison.Ordinal);
		int equals = IndexOfTopLevel(member, " = ");
		string beforeValue = equals >= 0 ? member[..equals] : member;
		if (isMethod && !beforeValue.Contains('(') && member.EndsWith(';'))
		{
			isMethod = false; // a field with a value containing parentheses
		}

		if (!isMethod)
		{
			if (!member.EndsWith(';'))
			{
				return;
			}
			string body = member[..^1];
			string? value = null;
			if (equals >= 0)
			{
				value = body[(equals + 3)..];
				body = body[..equals];
			}
			(List<string> modifiers, string typeAndName) = TakeModifiers(body);
			(string fieldType, string name) = SplitTypeAndName(typeAndName);
			if (name.Length == 0 || fieldType.Length == 0)
			{
				return;
			}
			type.Fields.Add(new DumpField { Modifiers = modifiers, Type = fieldType, Name = name, Value = value, Attributes = [.. attributes] });
			return;
		}

		string signature = member.EndsWith(" { }", StringComparison.Ordinal) ? member[..^4] : member[..^1];
		int open = IndexOfTopLevelChar(signature, '(');
		if (open < 0 || !signature.EndsWith(')'))
		{
			return;
		}
		string parameterText = signature[(open + 1)..^1];
		(List<string> methodModifiers, string returnAndName) = TakeModifiers(signature[..open]);
		(string returnType, string fullName) = SplitTypeAndName(returnAndName);
		if (fullName.Length == 0)
		{
			return;
		}
		(string methodName, List<string> methodGenerics) = SplitGenericName(fullName);
		List<DumpParameter> parameters = [];
		if (parameterText.Trim().Length > 0)
		{
			foreach (string p in SplitTopLevel(parameterText, ','))
			{
				DumpParameter? parameter = ParseParameter(p.Trim());
				if (parameter is null)
				{
					return;
				}
				parameters.Add(parameter);
			}
		}
		type.Methods.Add(new DumpMethod
		{
			Modifiers = methodModifiers,
			ReturnType = returnType,
			Name = methodName,
			GenericParameters = methodGenerics,
			Parameters = parameters,
			Attributes = [.. attributes],
		});
	}

	private static DumpParameter? ParseParameter(string text)
	{
		while (text.StartsWith('['))
		{
			int close = text.IndexOf(']');
			if (close < 0)
			{
				return null;
			}
			text = text[(close + 1)..].TrimStart();
		}
		int equals = IndexOfTopLevel(text, " = ");
		if (equals >= 0)
		{
			text = text[..equals];
		}
		string modifier = "";
		foreach (string m in (string[])["ref ", "out ", "in ", "params "])
		{
			if (text.StartsWith(m, StringComparison.Ordinal))
			{
				modifier = m.Trim();
				text = text[m.Length..];
				break;
			}
		}
		(string type, string name) = SplitTypeAndName(text);
		if (type.Length == 0)
		{
			return null;
		}
		if (type.EndsWith('&'))
		{
			type = type[..^1];
			if (modifier.Length == 0)
			{
				modifier = "ref";
			}
		}
		return new DumpParameter { Modifier = modifier, Type = type, Name = name };
	}

	private static (List<string> Modifiers, string Remainder) TakeModifiers(string text)
	{
		List<string> modifiers = [];
		string rest = text.Trim();
		while (true)
		{
			int space = rest.IndexOf(' ');
			if (space < 0)
			{
				break;
			}
			string token = rest[..space];
			if (!MemberModifiers.Contains(token))
			{
				break;
			}
			modifiers.Add(token);
			rest = rest[(space + 1)..];
		}
		return (modifiers, rest);
	}

	/// <summary>
	/// Splits "Dictionary&lt;string, int&gt; name" into the type and the name.
	/// </summary>
	private static (string Type, string Name) SplitTypeAndName(string text)
	{
		text = text.Trim();
		int depth = 0;
		for (int i = text.Length - 1; i >= 0; i--)
		{
			char c = text[i];
			if (c is '>' or ']' or ')')
			{
				depth++;
			}
			else if (c is '<' or '[' or '(')
			{
				depth--;
			}
			else if (c == ' ' && depth == 0)
			{
				return (text[..i].Trim(), text[(i + 1)..].Trim());
			}
		}
		return ("", text);
	}

	public static (string Name, List<string> GenericParameters) SplitGenericName(string text)
	{
		int lt = text.IndexOf('<');
		if (lt <= 0 || !text.EndsWith('>'))
		{
			return (text, []);
		}
		List<string> parameters = SplitTopLevel(text[(lt + 1)..^1], ',').Select(p => p.Trim()).ToList();
		return (text[..lt], parameters);
	}

	public static List<string> SplitTopLevel(string text, char separator)
	{
		List<string> result = [];
		int depth = 0;
		int start = 0;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c is '<' or '[' or '(')
			{
				depth++;
			}
			else if (c is '>' or ']' or ')')
			{
				depth--;
			}
			else if (c == separator && depth == 0)
			{
				result.Add(text[start..i]);
				start = i + 1;
			}
		}
		result.Add(text[start..]);
		return result;
	}

	private static int IndexOfTopLevel(string text, string value)
	{
		int depth = 0;
		bool inString = false;
		for (int i = 0; i <= text.Length - value.Length; i++)
		{
			char c = text[i];
			if (c == '"')
			{
				inString = !inString;
			}
			if (inString)
			{
				continue;
			}
			if (c is '<' or '[' or '(')
			{
				depth++;
			}
			else if (c is '>' or ']' or ')')
			{
				depth--;
			}
			else if (depth == 0 && string.CompareOrdinal(text, i, value, 0, value.Length) == 0)
			{
				return i;
			}
		}
		return -1;
	}

	private static int IndexOfTopLevelChar(string text, char value)
	{
		int depth = 0;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c == value && depth == 0)
			{
				return i;
			}
			if (c == '<')
			{
				depth++;
			}
			else if (c == '>')
			{
				depth--;
			}
		}
		return -1;
	}

	private static string AttributeName(string line)
	{
		int end = line.IndexOfAny([']', '(']);
		return end > 1 ? line[1..end].Trim() : line.Trim('[', ']');
	}

	[GeneratedRegex(@" // (0x[0-9A-Fa-f]+|RVA: .*|Slot: .*)$")]
	private static partial Regex TrailingCommentRegex();

	private static string StripTrailingComment(string member)
	{
		// "public int x; // 0x10", "public void M() { } // RVA..."
		return TrailingCommentRegex().Replace(member, "");
	}
}
