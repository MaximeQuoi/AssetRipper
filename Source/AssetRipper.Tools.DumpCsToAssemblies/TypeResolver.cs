using System.Text;

namespace AssetRipper.Tools.DumpCsToAssemblies;

/// <summary>
/// Resolves the short type names of dump.cs (eg "List&lt;RaycastResult&gt;") to fully qualified C# names.
/// </summary>
internal sealed class TypeResolver
{
	private static readonly HashSet<string> Keywords =
	[
		"bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong",
		"short", "ushort", "object", "string", "void", "nint", "nuint", "dynamic",
	];

	private static readonly string[] PreferredNamespaces =
	[
		"UnityEngine", "System", "System.Collections.Generic", "System.Collections", "UnityEngine.UI", "UnityEngine.EventSystems",
		"System.IO", "System.Text", "UnityEngine.Events", "UnityEngine.Rendering", "LuaInterface", "System.Threading.Tasks",
	];

	private readonly Dictionary<string, List<DumpType>> topLevelByName = new(StringComparer.Ordinal);
	private readonly HashSet<string> namespaces = new(StringComparer.Ordinal);
	private readonly Dictionary<string, DumpType> byFullName = new(StringComparer.Ordinal);

	public TypeResolver(IEnumerable<DumpImage> images)
	{
		foreach (DumpType type in images.SelectMany(i => i.Types))
		{
			if (type.IsCompilerGenerated)
			{
				continue;
			}
			byFullName.TryAdd(type.FullName + "`" + type.GenericParameters.Count, type);
			if (type.DeclaringType is null)
			{
				string key = Key(type.Name, type.GenericParameters.Count);
				if (!topLevelByName.TryGetValue(key, out List<DumpType>? list))
				{
					topLevelByName[key] = list = [];
				}
				list.Add(type);
			}
			if (!string.IsNullOrEmpty(type.Namespace))
			{
				string ns = type.Namespace;
				while (true)
				{
					namespaces.Add(ns);
					int dot = ns.LastIndexOf('.');
					if (dot < 0)
					{
						break;
					}
					ns = ns[..dot];
				}
			}
		}
	}

	private static string Key(string name, int arity) => $"{name}`{arity}";

	/// <summary>
	/// The dump types referenced by the last resolutions, used for assembly dependencies.
	/// </summary>
	public HashSet<DumpImage> ReferencedImages { get; } = [];

	public bool TryGetType(string fullName, int arity, out DumpType? type) => byFullName.TryGetValue(fullName + "`" + arity, out type);

	/// <summary>
	/// Resolves a type expression in the context of a type and, optionally, method generic parameters.
	/// </summary>
	public string Resolve(string typeText, DumpType? context, IReadOnlyCollection<string>? methodGenerics = null)
	{
		HashSet<string> genericScope = new(StringComparer.Ordinal);
		if (context is not null)
		{
			foreach (string p in DumpParser.AllGenericParameters(context))
			{
				genericScope.Add(p);
			}
		}
		if (methodGenerics is not null)
		{
			foreach (string p in methodGenerics)
			{
				genericScope.Add(p);
			}
		}
		return ResolveInternal(typeText.Trim(), context, genericScope);
	}

	private string ResolveInternal(string text, DumpType? context, HashSet<string> genericScope)
	{
		// Suffixes: arrays, nullable, pointers
		if (text.EndsWith('?'))
		{
			return ResolveInternal(text[..^1], context, genericScope) + "?";
		}
		if (text.EndsWith('*'))
		{
			return ResolveInternal(text[..^1], context, genericScope) + "*";
		}
		if (text.EndsWith(']'))
		{
			int open = FindMatchingOpen(text, text.Length - 1, '[', ']');
			if (open > 0)
			{
				return ResolveInternal(text[..open], context, genericScope) + text[open..];
			}
		}

		List<string> segments = DumpParser.SplitTopLevel(text, '.');
		List<(string Name, List<string> Args)> parts = segments
			.Select(s => DumpParser.SplitGenericName(s.Trim()))
			.Select(p => (p.Name, p.GenericParameters))
			.ToList();

		if (parts.Count == 1 && parts[0].Args.Count == 0)
		{
			string name = parts[0].Name;
			if (Keywords.Contains(name) || genericScope.Contains(name))
			{
				return name;
			}
		}

		string ResolveArgs(List<string> args) => args.Count == 0 ? "" : "<" + string.Join(", ", args.Select(a => ResolveInternal(a.Trim(), context, genericScope))) + ">";

		// Namespace qualified names: "System.Collections.Generic.List<T>"
		for (int k = parts.Count - 1; k >= 1; k--)
		{
			string ns = string.Join('.', parts.Take(k).Select(p => p.Name));
			if (!namespaces.Contains(ns) && ns != "System")
			{
				continue;
			}
			if (topLevelByName.TryGetValue(Key(parts[k].Name, parts[k].Args.Count), out List<DumpType>? inNamespace))
			{
				DumpType? match = inNamespace.FirstOrDefault(t => t.Namespace == ns);
				if (match is not null)
				{
					return BuildName(match, parts, k, ResolveArgs);
				}
			}
		}

		DumpType? first = FindFirst(parts[0].Name, parts[0].Args.Count, context);
		if (parts.Count == 2 && parts[0].Args.Count == 0 && parts[1].Args.Count > 0)
		{
			// Il2CppDumper writes "List.Enumerator<T>" for List<T>.Enumerator: the arguments belong to the declaring type.
			DumpType? genericOuter = FindFirst(parts[0].Name, parts[1].Args.Count, context);
			if (genericOuter is not null && genericOuter.NestedTypes.Any(n => n.Name == parts[1].Name && n.GenericParameters.Count == 0))
			{
				parts = [(parts[0].Name, parts[1].Args), (parts[1].Name, [])];
				first = genericOuter;
			}
		}
		if (first is null)
		{
			// Unknown type: keep it as written, the compiler will report it.
			StringBuilder fallback = new();
			for (int i = 0; i < parts.Count; i++)
			{
				if (i > 0)
				{
					fallback.Append('.');
				}
				fallback.Append(parts[i].Name).Append(ResolveArgs(parts[i].Args));
			}
			return fallback.ToString();
		}
		return BuildName(first, parts, 0, ResolveArgs);
	}

	private string BuildName(DumpType type, List<(string Name, List<string> Args)> parts, int index, Func<List<string>, string> resolveArgs)
	{
		ReferencedImages.Add(type.Image);
		StringBuilder sb = new("global::");
		if (!string.IsNullOrEmpty(type.Namespace))
		{
			sb.Append(type.Namespace).Append('.');
		}
		// Declaring types of a nested type found by simple name
		List<DumpType> chain = [];
		for (DumpType? t = type.DeclaringType; t is not null; t = t.DeclaringType)
		{
			chain.Insert(0, t);
		}
		foreach (DumpType declaring in chain)
		{
			sb.Append(declaring.Name);
			if (declaring.GenericParameters.Count > 0)
			{
				sb.Append('<').Append(string.Join(", ", declaring.GenericParameters)).Append('>');
			}
			sb.Append('.');
		}
		sb.Append(type.Name).Append(resolveArgs(parts[index].Args));

		DumpType current = type;
		for (int i = index + 1; i < parts.Count; i++)
		{
			DumpType? nested = current.NestedTypes.FirstOrDefault(n => !n.IsCompilerGenerated && n.Name == parts[i].Name && n.GenericParameters.Count == parts[i].Args.Count)
				?? current.NestedTypes.FirstOrDefault(n => !n.IsCompilerGenerated && n.Name == parts[i].Name);
			sb.Append('.').Append(parts[i].Name).Append(resolveArgs(parts[i].Args));
			if (nested is null)
			{
				break;
			}
			current = nested;
		}
		return sb.ToString();
	}

	private DumpType? FindFirst(string name, int arity, DumpType? context)
	{
		// 1. Nested types visible from the context, the context itself and its declaring types
		for (DumpType? t = context; t is not null; t = t.DeclaringType)
		{
			if (t.Name == name && t.GenericParameters.Count == arity)
			{
				return t;
			}
			DumpType? nested = t.NestedTypes.FirstOrDefault(n => !n.IsCompilerGenerated && n.Name == name && n.GenericParameters.Count == arity);
			if (nested is not null)
			{
				return nested;
			}
			// Nested types of base types
			foreach (DumpType baseType in BaseDumpTypes(t))
			{
				nested = baseType.NestedTypes.FirstOrDefault(n => !n.IsCompilerGenerated && n.Name == name && n.GenericParameters.Count == arity);
				if (nested is not null)
				{
					return nested;
				}
			}
		}

		if (!topLevelByName.TryGetValue(Key(name, arity), out List<DumpType>? candidates) || candidates.Count == 0)
		{
			return null;
		}
		if (candidates.Count == 1)
		{
			return candidates[0];
		}

		string contextNamespace = context?.Namespace ?? "";
		// 2. Same namespace, then enclosing namespaces
		for (string ns = contextNamespace; ; )
		{
			DumpType? inNamespace = candidates.FirstOrDefault(c => c.Namespace == ns);
			if (inNamespace is not null)
			{
				return inNamespace;
			}
			if (ns.Length == 0)
			{
				break;
			}
			int dot = ns.LastIndexOf('.');
			ns = dot < 0 ? "" : ns[..dot];
		}
		// 3. Same assembly
		if (context is not null)
		{
			List<DumpType> sameImage = candidates.Where(c => c.Image == context.Image).ToList();
			if (sameImage.Count == 1)
			{
				return sameImage[0];
			}
		}
		// 4. Usual namespaces
		foreach (string ns in PreferredNamespaces)
		{
			DumpType? preferred = candidates.FirstOrDefault(c => c.Namespace == ns);
			if (preferred is not null)
			{
				return preferred;
			}
		}
		// 5. Public types first
		return candidates.FirstOrDefault(c => c.Modifiers.Contains("public")) ?? candidates[0];
	}

	private IEnumerable<DumpType> BaseDumpTypes(DumpType type)
	{
		HashSet<DumpType> seen = [];
		DumpType? current = type;
		for (int depth = 0; depth < 16 && current is not null; depth++)
		{
			if (current.Kind != DumpTypeKind.Class || current.BaseTypes.Count == 0)
			{
				yield break;
			}
			(string name, List<string> args) = DumpParser.SplitGenericName(DumpParser.SplitTopLevel(current.BaseTypes[0], '.')[^1].Trim());
			if (!topLevelByName.TryGetValue(Key(name, args.Count), out List<DumpType>? list))
			{
				yield break;
			}
			DumpType? next = list.FirstOrDefault(t => t.Namespace == current.Namespace) ?? list[0];
			if (!seen.Add(next) || next.Kind != DumpTypeKind.Class)
			{
				yield break;
			}
			yield return next;
			current = next;
		}
	}

	private static int FindMatchingOpen(string text, int closeIndex, char open, char close)
	{
		int depth = 0;
		for (int i = closeIndex; i >= 0; i--)
		{
			if (text[i] == close)
			{
				depth++;
			}
			else if (text[i] == open)
			{
				depth--;
				if (depth == 0)
				{
					return i;
				}
			}
		}
		return -1;
	}
}
