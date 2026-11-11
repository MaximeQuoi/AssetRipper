using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AssetRipper.Tools.DumpCsToAssemblies;

/// <summary>
/// Corrections found by compiling, applied on the next emission.
/// </summary>
internal sealed class RepairState
{
	public HashSet<string> RemovedMembers { get; } = [];
	public HashSet<string> RemovedTypes { get; } = [];
	/// <summary>
	/// Keys: type key + "|" + resolved base text.
	/// </summary>
	public HashSet<string> RemovedBases { get; } = [];
	public HashSet<string> NoOverride { get; } = [];
	/// <summary>
	/// Overrides whose base call does not compile: they get a default body instead.
	/// </summary>
	public HashSet<string> NoBaseCall { get; } = [];
	public HashSet<string> ForcedAbstract { get; } = [];
	/// <summary>
	/// Keys: owner key (type or member) + "|" + generic parameter name.
	/// </summary>
	public Dictionary<string, List<string>> Constraints { get; } = [];
	public List<string> Log { get; } = [];

	public void AddConstraint(string owner, string parameter, string constraint)
	{
		string key = owner + "|" + parameter;
		if (!Constraints.TryGetValue(key, out List<string>? list))
		{
			Constraints[key] = list = [];
		}
		if (!list.Contains(constraint))
		{
			// "class" and "struct" must come first, "new()" last
			if (constraint is "class" or "struct")
			{
				list.Insert(0, constraint);
			}
			else if (constraint == "new()")
			{
				list.Add(constraint);
			}
			else
			{
				int newIndex = list.IndexOf("new()");
				list.Insert(newIndex < 0 ? list.Count : newIndex, constraint);
			}
		}
	}
}

/// <summary>
/// What a line of the emitted source corresponds to.
/// </summary>
internal sealed record class LineInfo(string TypeKey, string? MemberKey, string? MemberDescription, IReadOnlyList<string> GenericOwners)
{
	/// <summary>
	/// The emitted base types, on type declaration lines.
	/// </summary>
	public IReadOnlyList<string>? BaseTypes { get; init; }
}

internal sealed partial class CSharpEmitter
{
	private static readonly HashSet<string> CSharpKeywords =
	[
		"abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue",
		"decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally",
		"fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
		"long", "namespace", "new", "null", "object", "operator", "out", "override", "params", "private", "protected",
		"public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
		"struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort",
		"using", "virtual", "void", "volatile", "while",
	];

	private static readonly Dictionary<string, string> TypeAttributes = new()
	{
		["Serializable"] = "global::System.Serializable",
		["Flags"] = "global::System.Flags",
		["DisallowMultipleComponent"] = "global::UnityEngine.DisallowMultipleComponent",
		["ExecuteInEditMode"] = "global::UnityEngine.ExecuteInEditMode",
		["ExecuteAlways"] = "global::UnityEngine.ExecuteAlways",
	};

	private static readonly Dictionary<string, string> FieldAttributes = new()
	{
		["SerializeField"] = "global::UnityEngine.SerializeField",
		["NonSerialized"] = "global::System.NonSerialized",
		["HideInInspector"] = "global::UnityEngine.HideInInspector",
		["SerializeReference"] = "global::UnityEngine.SerializeReference",
	};

	private readonly TypeResolver resolver;
	private readonly RepairState repair;
	private readonly StringBuilder sb = new();
	private readonly List<LineInfo?> lines = [];
	private int indent;

	public CSharpEmitter(TypeResolver resolver, RepairState repair)
	{
		this.resolver = resolver;
		this.repair = repair;
	}

	public IReadOnlyList<LineInfo?> Lines => lines;

	/// <summary>
	/// Generic parameters declared by each type or method key, to know where a constraint belongs.
	/// Override methods are not registered: they cannot declare constraints.
	/// </summary>
	private static readonly Dictionary<string, IReadOnlyList<string>> OwnerParameters = new(StringComparer.Ordinal);

	public static string? FindGenericOwner(LineInfo info, string parameter)
	{
		foreach (string owner in info.GenericOwners)
		{
			if (OwnerParameters.TryGetValue(owner, out IReadOnlyList<string>? parameters) && parameters.Contains(parameter))
			{
				return owner;
			}
		}
		return null;
	}

	public static string TypeKey(DumpType type) => type.FullName + "`" + type.GenericParameters.Count;

	public static string MemberKey(DumpType type, string kind, string name, int parameterCount, string signature)
		=> $"{TypeKey(type)}::{kind}:{name}/{parameterCount}/{signature}";

	public string Emit(DumpImage image)
	{
		sb.Clear();
		lines.Clear();
		indent = 0;
		WriteLine("// Generated from dump.cs by AssetRipper.Tools.DumpCsToAssemblies. Method bodies are not available.", null);
		WriteLine("#pragma warning disable", null);
		foreach (IGrouping<string, DumpType> group in image.Types
			.Where(t => t.DeclaringType is null && !t.IsCompilerGenerated)
			.GroupBy(t => t.Namespace)
			.OrderBy(g => g.Key, StringComparer.Ordinal))
		{
			bool hasNamespace = !string.IsNullOrEmpty(group.Key);
			if (hasNamespace)
			{
				WriteLine($"namespace {group.Key}", null);
				WriteLine("{", null);
				indent++;
			}
			foreach (DumpType type in group)
			{
				EmitType(type);
			}
			if (hasNamespace)
			{
				indent--;
				WriteLine("}", null);
			}
		}
		return sb.ToString();
	}

	private void WriteLine(string text, LineInfo? info)
	{
		sb.Append('\t', indent).Append(text).Append('\n');
		lines.Add(info);
	}

	private static string Escape(string name) => CSharpKeywords.Contains(name) ? "@" + name : name;

	private static bool IsValidIdentifier(string name) => name.Length > 0 && IdentifierRegex().IsMatch(name);

	[GeneratedRegex(@"^@?[A-Za-z_][A-Za-z0-9_]*$")]
	private static partial Regex IdentifierRegex();

	private static string Access(List<string> modifiers, bool topLevel)
	{
		bool isPublic = modifiers.Contains("public");
		bool isProtected = modifiers.Contains("protected");
		bool isInternal = modifiers.Contains("internal");
		bool isPrivate = modifiers.Contains("private");
		if (topLevel)
		{
			return isPublic ? "public" : "internal";
		}
		if (isPublic)
		{
			return "public";
		}
		if (isProtected && isInternal)
		{
			return "protected internal";
		}
		if (isProtected && isPrivate)
		{
			return "private protected";
		}
		if (isProtected)
		{
			return "protected";
		}
		if (isInternal)
		{
			return "internal";
		}
		return "private";
	}

	private IReadOnlyList<string> GenericOwners(DumpType type, string? memberKey)
	{
		List<string> owners = [];
		if (memberKey is not null)
		{
			owners.Add(memberKey);
		}
		for (DumpType? t = type; t is not null; t = t.DeclaringType)
		{
			owners.Add(TypeKey(t));
		}
		return owners;
	}

	private static string GenericArguments(IReadOnlyList<string> parameters) => parameters.Count == 0 ? "" : "<" + string.Join(", ", parameters) + ">";

	private string GenericParameterList(IReadOnlyList<string> parameters) => parameters.Count == 0 ? "" : "<" + string.Join(", ", parameters) + ">";

	private string ConstraintClauses(string owner, IReadOnlyList<string> parameters)
	{
		StringBuilder clauses = new();
		foreach (string parameter in parameters)
		{
			if (repair.Constraints.TryGetValue(owner + "|" + parameter, out List<string>? list) && list.Count > 0)
			{
				clauses.Append(" where ").Append(parameter).Append(" : ").Append(string.Join(", ", list));
			}
		}
		return clauses.ToString();
	}

	private void EmitType(DumpType type)
	{
		string typeKey = TypeKey(type);
		if (repair.RemovedTypes.Contains(typeKey) || !IsValidIdentifier(type.Name))
		{
			return;
		}
		bool topLevel = type.DeclaringType is null;
		LineInfo typeLine = new(typeKey, null, null, GenericOwners(type, null));
		OwnerParameters[typeKey] = type.GenericParameters;

		// Delegates
		if (type.Kind == DumpTypeKind.Class && type.BaseTypes.Count > 0 && type.BaseTypes[0] == "MulticastDelegate")
		{
			DumpMethod? invoke = type.Methods.FirstOrDefault(m => m.Name == "Invoke");
			if (invoke is null)
			{
				return;
			}
			string returnType = resolver.Resolve(invoke.ReturnType, type);
			string parameters = ParameterList(invoke, type, null);
			WriteLine($"{Access(type.Modifiers, topLevel)} unsafe delegate {returnType} {type.Name}{GenericParameterList(type.GenericParameters)}({parameters}){ConstraintClauses(typeKey, type.GenericParameters)};", typeLine);
			return;
		}

		foreach (string attribute in type.Attributes)
		{
			if (TypeAttributes.TryGetValue(attribute, out string? full))
			{
				WriteLine($"[{full}]", typeLine);
			}
		}

		StringBuilder header = new();
		header.Append(Access(type.Modifiers, topLevel)).Append(' ');
		switch (type.Kind)
		{
			case DumpTypeKind.Enum:
				{
					DumpField? valueField = type.Fields.FirstOrDefault(f => f.Name == "value__");
					string underlying = valueField?.Type ?? "int";
					header.Append("enum ").Append(type.Name);
					if (underlying != "int")
					{
						header.Append(" : ").Append(underlying);
					}
					WriteLine(header.ToString(), typeLine);
					WriteLine("{", typeLine);
					indent++;
					foreach (DumpField field in type.Fields.Where(f => f.Modifiers.Contains("const") && f.Value is not null))
					{
						string memberKey = MemberKey(type, "enum", field.Name, 0, "");
						if (repair.RemovedMembers.Contains(memberKey) || !IsValidIdentifier(field.Name))
						{
							continue;
						}
						WriteLine($"{Escape(field.Name)} = unchecked(({underlying})({NormalizeNumber(field.Value!)})),", new LineInfo(typeKey, memberKey, $"{type.FullName}.{field.Name}", typeLine.GenericOwners));
					}
					indent--;
					WriteLine("}", typeLine);
					return;
				}
			case DumpTypeKind.Interface:
				header.Append("unsafe interface ");
				break;
			case DumpTypeKind.Struct:
				header.Append("unsafe struct ");
				break;
			default:
				if (type.IsStatic)
				{
					header.Append("static ");
				}
				else if (type.IsAbstract || repair.ForcedAbstract.Contains(typeKey))
				{
					header.Append("abstract ");
				}
				else if (type.Modifiers.Contains("sealed"))
				{
					header.Append("sealed ");
				}
				header.Append("unsafe class ");
				break;
		}
		header.Append(type.Name).Append(GenericParameterList(type.GenericParameters));

		List<string> bases = [];
		foreach (string baseText in type.BaseTypes)
		{
			if (baseText is "object" or "ValueType" or "Enum" or "System.Object" or "System.ValueType")
			{
				continue;
			}
			string resolved = resolver.Resolve(baseText, type.DeclaringType ?? type);
			if (repair.RemovedBases.Contains(typeKey + "|" + resolved))
			{
				continue;
			}
			bases.Add(resolved);
		}
		if (bases.Count > 0)
		{
			header.Append(" : ").Append(string.Join(", ", bases));
		}
		header.Append(ConstraintClauses(typeKey, type.GenericParameters));
		typeLine = typeLine with { BaseTypes = bases };
		WriteLine(header.ToString(), typeLine);
		WriteLine("{", typeLine);
		indent++;

		bool isInterface = type.Kind == DumpTypeKind.Interface;
		HashSet<string> accessorNames = [];
		HashSet<string> usedNames = [];

		// Indexers: get_Item(...) / set_Item(...)
		DumpMethod? indexerGetter = type.Methods.FirstOrDefault(m => m.Name == "get_Item" && m.Parameters.Count > 0 && !m.IsStatic);
		DumpMethod? indexerSetter = type.Methods.FirstOrDefault(m => m.Name == "set_Item" && m.Parameters.Count > 1 && !m.IsStatic);

		foreach (DumpProperty property in type.Properties)
		{
			accessorNames.Add("get_" + property.Name);
			accessorNames.Add("set_" + property.Name);
		}
		accessorNames.Add("get_Item");
		accessorNames.Add("set_Item");

		// Fields
		if (!isInterface)
		{
			foreach (DumpField field in type.Fields)
			{
				EmitField(type, field, typeLine, usedNames);
			}
		}

		// Properties
		foreach (DumpProperty property in type.Properties)
		{
			if (property.Name == "Item" && (indexerGetter is not null || indexerSetter is not null))
			{
				continue;
			}
			EmitProperty(type, property, isInterface, typeLine, usedNames);
		}
		if (indexerGetter is not null || indexerSetter is not null)
		{
			EmitIndexer(type, indexerGetter, indexerSetter, isInterface, typeLine);
		}

		// Methods
		bool hasInstanceConstructor = false;
		foreach (DumpMethod method in type.Methods)
		{
			if (method.IsConstructor && !method.IsStatic)
			{
				hasInstanceConstructor |= EmitConstructor(type, method, typeLine);
				continue;
			}
			if (method.IsStaticConstructor || method.IsConstructor || accessorNames.Contains(method.Name))
			{
				continue;
			}
			EmitMethod(type, method, isInterface, typeLine);
		}
		if (!hasInstanceConstructor && type.Kind == DumpTypeKind.Class && !type.IsStatic)
		{
			string? baseCall = BaseConstructorCall(type);
			if (baseCall is not null)
			{
				string memberKey = MemberKey(type, "ctor", "<implicit>", 0, "");
				if (!repair.RemovedMembers.Contains(memberKey))
				{
					WriteLine($"protected {type.Name}(){baseCall} {{ }}", new LineInfo(typeKey, memberKey, $"{type.FullName}..ctor()", typeLine.GenericOwners));
				}
			}
		}

		// Custom UI graphics that build their mesh elsewhere (eg UI particles): without an override,
		// Graphic.OnPopulateMesh draws an opaque rectangle over the whole RectTransform.
		if (type.Kind == DumpTypeKind.Class && !type.IsAbstract && DerivesDirectlyFromGraphic(type)
			&& !type.Methods.Any(m => m.Name == "OnPopulateMesh" && m.Parameters.Count == 1 && m.Parameters[0].Type == "VertexHelper"))
		{
			string memberKey = MemberKey(type, "method", "OnPopulateMesh", 1, "<generated>");
			if (!repair.RemovedMembers.Contains(memberKey))
			{
				WriteLine("protected override void OnPopulateMesh(global::UnityEngine.UI.VertexHelper vh) { vh.Clear(); }", new LineInfo(typeKey, memberKey, $"{type.FullName}.OnPopulateMesh(generated)", typeLine.GenericOwners));
			}
		}

		// Nested types
		foreach (DumpType nested in type.NestedTypes)
		{
			if (!nested.IsCompilerGenerated)
			{
				EmitType(nested);
			}
		}

		indent--;
		WriteLine("}", typeLine);
	}

	private void EmitField(DumpType type, DumpField field, LineInfo typeLine, HashSet<string> usedNames)
	{
		if (!IsValidIdentifier(field.Name) || field.Modifiers.Contains("fixed") || field.Name == "value__")
		{
			return;
		}
		string memberKey = MemberKey(type, "field", field.Name, 0, "");
		if (repair.RemovedMembers.Contains(memberKey) || !usedNames.Add(field.Name))
		{
			return;
		}
		LineInfo info = new(TypeKey(type), memberKey, $"{type.FullName}.{field.Name}", typeLine.GenericOwners);
		foreach (string attribute in field.Attributes)
		{
			if (FieldAttributes.TryGetValue(attribute, out string? full))
			{
				WriteLine($"[{full}]", info);
			}
		}
		string fieldType = resolver.Resolve(field.Type, type);
		StringBuilder line = new();
		line.Append(Access(field.Modifiers, false)).Append(' ');
		bool isConst = field.Modifiers.Contains("const");
		string? constValue = isConst && field.Value is not null ? ConstantValue(field.Type, fieldType, field.Value) : null;
		if (isConst && constValue is not null)
		{
			line.Append("const ");
		}
		else
		{
			if (field.Modifiers.Contains("static") || isConst)
			{
				line.Append("static ");
			}
			if (field.Modifiers.Contains("readonly"))
			{
				line.Append("readonly ");
			}
			if (field.Modifiers.Contains("volatile"))
			{
				line.Append("volatile ");
			}
		}
		if (field.Modifiers.Contains("new"))
		{
			line.Append("new ");
		}
		line.Append(fieldType).Append(' ').Append(Escape(field.Name));
		if (isConst && constValue is not null)
		{
			line.Append(" = ").Append(constValue);
		}
		line.Append(';');
		WriteLine(line.ToString(), info);
	}

	private static string? ConstantValue(string rawType, string resolvedType, string value)
	{
		value = value.Trim();
		if (rawType == "string")
		{
			return value.StartsWith('"') ? value : "null";
		}
		if (rawType == "bool")
		{
			return value is "true" or "false" or "True" or "False" ? value.ToLowerInvariant() : null;
		}
		if (rawType == "char")
		{
			if (value.StartsWith('\''))
			{
				return value;
			}
		}
		string number = NormalizeNumber(value);
		if (!NumberRegex().IsMatch(number))
		{
			return null;
		}
		return $"unchecked(({resolvedType})({number}))";
	}

	[GeneratedRegex(@"^-?[0-9]+(\.[0-9]+)?([Ee][+-]?[0-9]+)?$")]
	private static partial Regex NumberRegex();

	/// <summary>
	/// dump.cs was produced with a French locale: "0,5" means 0.5.
	/// </summary>
	private static string NormalizeNumber(string value)
	{
		value = value.Trim();
		if (DecimalCommaRegex().IsMatch(value))
		{
			value = value.Replace(',', '.');
		}
		if (value is "NaN" or "Infinity" or "-Infinity" or "∞" or "-∞")
		{
			return "0";
		}
		return value;
	}

	[GeneratedRegex(@"^-?[0-9]+,[0-9]+([Ee][+-]?[0-9]+)?$")]
	private static partial Regex DecimalCommaRegex();

	private void EmitProperty(DumpType type, DumpProperty property, bool isInterface, LineInfo typeLine, HashSet<string> usedNames)
	{
		if (!isInterface && TrySplitExplicit(property.Name, out string interfaceName, out string memberName))
		{
			string explicitKey = MemberKey(type, "explicit-property", property.Name, 0, "");
			if (repair.RemovedMembers.Contains(explicitKey) || memberName == "Item")
			{
				return;
			}
			string resolvedInterface = resolver.Resolve(interfaceName, type);
			string explicitType = resolver.Resolve(property.Type, type);
			LineInfo explicitInfo = new(TypeKey(type), explicitKey, $"{type.FullName}.{property.Name}", typeLine.GenericOwners);
			WriteLine($"{explicitType} {resolvedInterface}.{Escape(memberName)} {{ {(property.HasGetter ? "get => default; " : "")}{(property.HasSetter ? "set { } " : "")}}}", explicitInfo);
			return;
		}
		if (!IsValidIdentifier(property.Name))
		{
			return;
		}
		string memberKey = MemberKey(type, "property", property.Name, 0, "");
		if (repair.RemovedMembers.Contains(memberKey) || !usedNames.Add(property.Name))
		{
			return;
		}
		string propertyType = resolver.Resolve(property.Type, type);
		LineInfo info = new(TypeKey(type), memberKey, $"{type.FullName}.{property.Name}", typeLine.GenericOwners);
		if (isInterface)
		{
			WriteLine($"{propertyType} {Escape(property.Name)} {{ {(property.HasGetter ? "get; " : "")}{(property.HasSetter ? "set; " : "")}}}", info);
			return;
		}
		string modifiers = MethodModifiers(type, property.Modifiers, memberKey);
		bool isAbstract = modifiers.Contains("abstract");
		bool callBase = modifiers.Contains("override ") && !repair.NoBaseCall.Contains(memberKey) && !property.Modifiers.Contains("static");
		string name = Escape(property.Name);
		string getter = property.HasGetter ? (isAbstract ? "get; " : callBase ? $"get => base.{name}; " : "get => default; ") : "";
		string setter = property.HasSetter ? (isAbstract ? "set; " : callBase ? $"set => base.{name} = value; " : "set { } ") : "";
		if (getter.Length == 0 && setter.Length == 0)
		{
			return;
		}
		WriteLine($"{modifiers}{propertyType} {Escape(property.Name)} {{ {getter}{setter}}}", info);
	}

	private void EmitIndexer(DumpType type, DumpMethod? getter, DumpMethod? setter, bool isInterface, LineInfo typeLine)
	{
		DumpMethod reference = getter ?? setter!;
		List<DumpParameter> parameters = getter is not null ? getter.Parameters : setter!.Parameters.Take(setter.Parameters.Count - 1).ToList();
		string valueType = getter is not null ? getter.ReturnType : setter!.Parameters[^1].Type;
		string memberKey = MemberKey(type, "indexer", "this", parameters.Count, string.Join(",", parameters.Select(p => p.Type)));
		if (repair.RemovedMembers.Contains(memberKey))
		{
			return;
		}
		LineInfo info = new(TypeKey(type), memberKey, $"{type.FullName}.this[]", typeLine.GenericOwners);
		string parameterList = string.Join(", ", parameters.Select((p, i) => $"{resolver.Resolve(p.Type, type)} {SafeParameterName(p.Name, i)}"));
		string resolvedValue = resolver.Resolve(valueType, type);
		if (isInterface)
		{
			WriteLine($"{resolvedValue} this[{parameterList}] {{ {(getter is not null ? "get; " : "")}{(setter is not null ? "set; " : "")}}}", info);
			return;
		}
		string modifiers = MethodModifiers(type, reference.Modifiers, memberKey);
		bool isAbstract = modifiers.Contains("abstract");
		string get = getter is not null ? (isAbstract ? "get; " : "get => default; ") : "";
		string set = setter is not null ? (isAbstract ? "set; " : "set { } ") : "";
		WriteLine($"{modifiers}{resolvedValue} this[{parameterList}] {{ {get}{set}}}", info);
	}

	private string MethodModifiers(DumpType type, List<string> modifiers, string memberKey)
	{
		StringBuilder sb = new();
		sb.Append(Access(modifiers, false)).Append(' ');
		if (modifiers.Contains("static"))
		{
			sb.Append("static ");
		}
		bool isStruct = type.Kind == DumpTypeKind.Struct;
		bool isOverride = modifiers.Contains("override") && !repair.NoOverride.Contains(memberKey);
		if (isOverride)
		{
			if (modifiers.Contains("sealed"))
			{
				sb.Append("sealed ");
			}
			sb.Append("override ");
		}
		else if (modifiers.Contains("abstract") && (type.IsAbstract || repair.ForcedAbstract.Contains(TypeKey(type))))
		{
			sb.Append("abstract ");
		}
		else if ((modifiers.Contains("virtual") || modifiers.Contains("abstract")) && !isStruct && !type.IsStatic && !modifiers.Contains("static") && !(modifiers.Contains("private") && !modifiers.Contains("protected")))
		{
			if (!type.Modifiers.Contains("sealed"))
			{
				sb.Append("virtual ");
			}
		}
		if (modifiers.Contains("override") && repair.NoOverride.Contains(memberKey))
		{
			sb.Append("new ");
		}
		return sb.ToString();
	}

	private static string SafeParameterName(string name, int index)
	{
		if (!IsValidIdentifier(name))
		{
			return "p" + index;
		}
		return Escape(name);
	}

	private string ParameterList(DumpMethod method, DumpType type, IReadOnlyCollection<string>? generics)
	{
		HashSet<string> names = [];
		List<string> parameters = [];
		for (int i = 0; i < method.Parameters.Count; i++)
		{
			DumpParameter p = method.Parameters[i];
			string name = SafeParameterName(p.Name, i);
			if (!names.Add(name))
			{
				name = "p" + i;
				names.Add(name);
			}
			string resolved = resolver.Resolve(p.Type, type, generics);
			string modifier = p.Modifier;
			if (modifier == "params" && !resolved.EndsWith(']'))
			{
				modifier = "";
			}
			parameters.Add(modifier.Length > 0 ? $"{modifier} {resolved} {name}" : $"{resolved} {name}");
		}
		return string.Join(", ", parameters);
	}

	/// <summary>
	/// The parameter names used by <see cref="ParameterList"/>.
	/// </summary>
	private static List<(string Modifier, string Name)> ParameterNames(DumpMethod method)
	{
		HashSet<string> names = [];
		List<(string, string)> result = [];
		for (int i = 0; i < method.Parameters.Count; i++)
		{
			DumpParameter p = method.Parameters[i];
			string name = SafeParameterName(p.Name, i);
			if (!names.Add(name))
			{
				name = "p" + i;
				names.Add(name);
			}
			result.Add((p.Modifier, name));
		}
		return result;
	}

	/// <summary>
	/// A body that does nothing: out parameters are assigned, non-void methods return default.
	/// Stubs must not throw: Unity calls Awake, OnEnable, OnPopulateMesh... on components.
	/// </summary>
	private static string DefaultBody(DumpMethod method, bool returnsValue)
	{
		IEnumerable<string> outAssignments = ParameterNames(method).Where(p => p.Modifier == "out").Select(p => $"{p.Name} = default; ");
		string assignments = string.Concat(outAssignments);
		return returnsValue ? $" {{ {assignments}return default; }}" : $" {{ {assignments}}}";
	}

	/// <summary>
	/// Overrides call the base implementation, so that components keep the behaviour of their Unity base class.
	/// </summary>
	private static string BaseCallBody(DumpMethod method, string name, string generics, bool returnsValue)
	{
		string arguments = string.Join(", ", ParameterNames(method).Select(p => p.Modifier is "ref" or "out" or "in" ? $"{p.Modifier} {p.Name}" : p.Name));
		string call = $"base.{name}{generics}({arguments})";
		return returnsValue ? $" => {call};" : $" {{ {call}; }}";
	}

	private static string Signature(DumpMethod method) => string.Join(",", method.Parameters.Select(p => p.Modifier + p.Type));

	/// <summary>
	/// "System.IDisposable.Dispose" → ("System.IDisposable", "Dispose")
	/// </summary>
	private static bool TrySplitExplicit(string name, out string interfaceName, out string memberName)
	{
		interfaceName = memberName = "";
		if (!name.Contains('.') || name.Contains('<') && !name.Contains('>'))
		{
			return false;
		}
		int depth = 0;
		for (int i = name.Length - 1; i >= 0; i--)
		{
			char c = name[i];
			if (c == '>')
			{
				depth++;
			}
			else if (c == '<')
			{
				depth--;
			}
			else if (c == '.' && depth == 0)
			{
				interfaceName = name[..i];
				memberName = name[(i + 1)..];
				return interfaceName.Length > 0 && IsValidIdentifier(memberName);
			}
		}
		return false;
	}

	private void EmitExplicitMethod(DumpType type, DumpMethod method, string interfaceName, string memberName, LineInfo typeLine)
	{
		string memberKey = MemberKey(type, "explicit", method.Name, method.Parameters.Count, Signature(method));
		if (repair.RemovedMembers.Contains(memberKey) || memberName.StartsWith("get_", StringComparison.Ordinal) || memberName.StartsWith("set_", StringComparison.Ordinal)
			|| memberName.StartsWith("add_", StringComparison.Ordinal) || memberName.StartsWith("remove_", StringComparison.Ordinal))
		{
			return;
		}
		LineInfo info = new(TypeKey(type), memberKey, $"{type.FullName}.{method.Name}({Signature(method)})", GenericOwners(type, memberKey));
		string resolvedInterface = resolver.Resolve(interfaceName, type);
		string returnType = resolver.Resolve(method.ReturnType, type, method.GenericParameters);
		string parameters = ParameterList(method, type, method.GenericParameters);
		WriteLine($"{returnType} {resolvedInterface}.{Escape(memberName)}{GenericParameterList(method.GenericParameters)}({parameters}){DefaultBody(method, returnType != "void")}", info);
	}

	private void EmitMethod(DumpType type, DumpMethod method, bool isInterface, LineInfo typeLine)
	{
		if (!isInterface && TrySplitExplicit(method.Name, out string interfaceName, out string memberName))
		{
			EmitExplicitMethod(type, method, interfaceName, memberName, typeLine);
			return;
		}
		if (!IsValidIdentifier(method.Name) || method.Name.StartsWith("op_", StringComparison.Ordinal) || method.Name == "Finalize")
		{
			return;
		}
		if (type.Kind == DumpTypeKind.Struct && method.Name is "Equals" or "GetHashCode" or "ToString" && method.Modifiers.Contains("override") is false)
		{
			// keep
		}
		string memberKey = MemberKey(type, "method", method.Name, method.Parameters.Count, Signature(method));
		if (repair.RemovedMembers.Contains(memberKey))
		{
			return;
		}
		if (method.Name == type.Name)
		{
			return; // member names cannot be the same as their enclosing type
		}
		LineInfo info = new(TypeKey(type), memberKey, $"{type.FullName}.{method.Name}({Signature(method)})", GenericOwners(type, memberKey));
		string returnType = resolver.Resolve(method.ReturnType, type, method.GenericParameters);
		string parameters = ParameterList(method, type, method.GenericParameters);
		string generics = GenericParameterList(method.GenericParameters);
		if (isInterface)
		{
			OwnerParameters[memberKey] = method.GenericParameters;
			WriteLine($"{returnType} {Escape(method.Name)}{generics}({parameters}){ConstraintClauses(memberKey, method.GenericParameters)};", info);
			return;
		}
		string modifiers = MethodModifiers(type, method.Modifiers, memberKey);
		bool isOverride = modifiers.Contains("override ");
		if (!isOverride)
		{
			OwnerParameters[memberKey] = method.GenericParameters;
		}
		string constraints = isOverride ? "" : ConstraintClauses(memberKey, method.GenericParameters);
		bool returnsValue = returnType != "void";
		if (isOverride && method.Name == "OnPopulateMesh" && method.Parameters.Count == 1 && DerivesDirectlyFromGraphic(type))
		{
			// Custom UI graphics (UI particles, gradients...): Graphic.OnPopulateMesh would draw an opaque rectangle.
			string mesh = ParameterNames(method)[0].Name;
			WriteLine($"{modifiers}{returnType} {Escape(method.Name)}({parameters}) {{ {mesh}.Clear(); }}", info);
			return;
		}
		string body = modifiers.Contains("abstract ") ? ";"
			: isOverride && !repair.NoBaseCall.Contains(memberKey) ? BaseCallBody(method, Escape(method.Name), GenericArguments(method.GenericParameters), returnsValue)
			: DefaultBody(method, returnsValue);
		WriteLine($"{modifiers}{returnType} {Escape(method.Name)}{generics}({parameters}){constraints}{body}", info);
	}

	private bool DerivesDirectlyFromGraphic(DumpType type)
	{
		if (type.BaseTypes.Count == 0)
		{
			return false;
		}
		string resolved = resolver.Resolve(type.BaseTypes[0], type.DeclaringType ?? type);
		return resolved is "global::UnityEngine.UI.Graphic" or "global::UnityEngine.UI.MaskableGraphic";
	}

	private bool EmitConstructor(DumpType type, DumpMethod method, LineInfo typeLine)
	{
		if (type.IsStatic || type.Kind is DumpTypeKind.Interface or DumpTypeKind.Enum)
		{
			return false;
		}
		if (type.Kind == DumpTypeKind.Struct && method.Parameters.Count == 0)
		{
			return false;
		}
		string memberKey = MemberKey(type, "ctor", ".ctor", method.Parameters.Count, Signature(method));
		if (repair.RemovedMembers.Contains(memberKey))
		{
			return false;
		}
		LineInfo info = new(TypeKey(type), memberKey, $"{type.FullName}..ctor({Signature(method)})", typeLine.GenericOwners);
		string parameters = ParameterList(method, type, null);
		string baseCall = type.Kind == DumpTypeKind.Class ? BaseConstructorCall(type) ?? "" : "";
		string outAssignments = string.Concat(ParameterNames(method).Where(p => p.Modifier == "out").Select(p => $"{p.Name} = default; "));
		string body = type.Kind == DumpTypeKind.Struct ? $"{{ this = default; {outAssignments}}}" : $"{{ {outAssignments}}}";
		WriteLine($"{Access(method.Modifiers, false)} {type.Name}({parameters}){baseCall} {body}", info);
		return true;
	}

	/// <summary>
	/// If the base class has no parameterless constructor, call one of its constructors with default arguments.
	/// </summary>
	private string? BaseConstructorCall(DumpType type)
	{
		if (type.BaseTypes.Count == 0)
		{
			return null;
		}
		string baseText = type.BaseTypes[0];
		string resolved = resolver.Resolve(baseText, type.DeclaringType ?? type);
		if (!resolved.StartsWith("global::", StringComparison.Ordinal))
		{
			return null;
		}
		(string nameWithNamespace, List<string> args) = SplitResolved(resolved[8..]);
		if (!resolver.TryGetType(nameWithNamespace, args.Count, out DumpType? baseType) || baseType is null || baseType.Kind != DumpTypeKind.Class)
		{
			return null;
		}
		List<DumpMethod> constructors = baseType.Methods.Where(m => m.IsConstructor && !m.IsStatic && !m.Modifiers.Contains("private")).ToList();
		if (constructors.Count == 0 || constructors.Any(c => c.Parameters.Count == 0))
		{
			return null;
		}
		DumpMethod chosen = constructors
			.Where(c => c.Parameters.All(p => p.Modifier is "" or "params" or "in"))
			.OrderBy(c => c.Parameters.Count)
			.FirstOrDefault(c => constructors.Count(o => o.Parameters.Count == c.Parameters.Count) == 1)
			?? constructors.OrderBy(c => c.Parameters.Count).First();
		Dictionary<string, string> substitution = new();
		for (int i = 0; i < baseType.GenericParameters.Count && i < args.Count; i++)
		{
			substitution[baseType.GenericParameters[i]] = args[i];
		}
		IEnumerable<string> arguments = chosen.Parameters.Select(p =>
		{
			string parameterType = resolver.Resolve(p.Type, baseType);
			if (substitution.TryGetValue(parameterType, out string? substituted))
			{
				parameterType = substituted;
			}
			string prefix = p.Modifier is "ref" or "out" ? p.Modifier + " " : "";
			return prefix.Length > 0 ? $"{prefix}{parameterType}" : $"default({parameterType})";
		});
		return $" : base({string.Join(", ", arguments)})";
	}

	private static (string Name, List<string> Args) SplitResolved(string resolved)
	{
		int lt = resolved.IndexOf('<');
		if (lt < 0)
		{
			return (resolved, []);
		}
		int depth = 0;
		int close = -1;
		for (int i = lt; i < resolved.Length; i++)
		{
			if (resolved[i] == '<')
			{
				depth++;
			}
			else if (resolved[i] == '>')
			{
				depth--;
				if (depth == 0)
				{
					close = i;
					break;
				}
			}
		}
		if (close != resolved.Length - 1)
		{
			// nested type of a generic type: not handled
			return (resolved, []);
		}
		List<string> args = DumpParser.SplitTopLevel(resolved[(lt + 1)..close], ',').Select(a => a.Trim()).ToList();
		return (resolved[..lt], args);
	}
}
