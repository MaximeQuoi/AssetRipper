using System.Text;
using System.Text.RegularExpressions;

namespace AssetRipper.Export.UnityProjects.Shaders.GlslTranslation;

/// <summary>
/// Translates the OpenGL ES programs that Unity's HLSLcc generates (one text with "#ifdef VERTEX" and "#ifdef FRAGMENT")
/// into an HLSLPROGRAM body for the Unity editor.
/// </summary>
/// <remarks>
/// HLSLcc output is very regular: plain uniforms (or std140 blocks), matrices stored as column arrays (hlslcc_mtx4x4X[4]),
/// attributes named in_SEMANTICn, varyings vs_*, outputs SV_Targetn and temporaries u_xlat*.
/// Unity binds built-in and material uniforms by name, so the same names are declared in HLSL.
/// </remarks>
internal static partial class GlslToHlsl
{
	public sealed class TranslationException(string message) : Exception(message);

	private sealed class Variable
	{
		public required string Type { get; init; }
		public required string Name { get; init; }
		public string ArraySize { get; init; } = "";
		public string Qualifier { get; init; } = ""; // flat, centroid...
		public int Location { get; init; } = -1;
	}

	private sealed class Stage
	{
		public List<Variable> Uniforms { get; } = [];
		public List<Variable> Inputs { get; } = [];
		public List<Variable> Outputs { get; } = [];
		public List<Variable> Globals { get; } = [];
		public List<string> GlobalInitializers { get; } = [];
		public List<string> Functions { get; } = [];
		public string Main { get; set; } = "";
		public bool UsesFrontFacing { get; set; }
		public bool UsesFragCoord { get; set; }
	}

	private static readonly Dictionary<string, string> TypeMap = new()
	{
		["vec2"] = "float2", ["vec3"] = "float3", ["vec4"] = "float4",
		["ivec2"] = "int2", ["ivec3"] = "int3", ["ivec4"] = "int4",
		["uvec2"] = "uint2", ["uvec3"] = "uint3", ["uvec4"] = "uint4",
		["bvec2"] = "bool2", ["bvec3"] = "bool3", ["bvec4"] = "bool4",
		["mat2"] = "float2x2", ["mat3"] = "float3x3", ["mat4"] = "float4x4",
	};

	private static readonly Dictionary<string, string> SamplerFunctions = new()
	{
		["sampler2D"] = "tex2D", ["samplerCube"] = "texCUBE", ["sampler3D"] = "tex3D",
	};

	/// <summary>
	/// Helpers for GLSL functions without a direct HLSL equivalent.
	/// </summary>
	private const string Preamble = """
		#define mix lerp
		#define fract frac
		#define inversesqrt rsqrt
		#define dFdx ddx
		#define dFdy ddy
		#define roundEven round
		#define mod(x, y) ((x) - (y) * floor((x) / (y)))
		#define greaterThan(a, b) ((a) > (b))
		#define greaterThanEqual(a, b) ((a) >= (b))
		#define lessThan(a, b) ((a) < (b))
		#define lessThanEqual(a, b) ((a) <= (b))
		#define equal(a, b) ((a) == (b))
		#define notEqual(a, b) ((a) != (b))
		#define uintBitsToFloat asfloat
		#define intBitsToFloat asfloat
		#define floatBitsToUint asuint
		#define floatBitsToInt asint
		""";

	public static string Translate(string combinedSource)
	{
		string vertexSource = ExtractSection(combinedSource, "VERTEX") ?? throw new TranslationException("no VERTEX section");
		string fragmentSource = ExtractSection(combinedSource, "FRAGMENT") ?? throw new TranslationException("no FRAGMENT section");
		Stage vertex = ParseStage(Preprocess(vertexSource), "V_");
		Stage fragment = ParseStage(Preprocess(fragmentSource), "F_");
		return Generate(vertex, fragment);
	}

	// ---------------------------------------------------------------- sections and preprocessing

	private static string? ExtractSection(string source, string name)
	{
		string[] lines = source.Replace("\r", "").Split('\n');
		StringBuilder sb = new();
		bool inside = false;
		int depth = 0;
		foreach (string raw in lines)
		{
			string line = raw.Trim();
			if (!inside)
			{
				if (line == $"#ifdef {name}")
				{
					inside = true;
					depth = 0;
				}
				continue;
			}
			if (line.StartsWith("#if", StringComparison.Ordinal))
			{
				depth++;
			}
			else if (line.StartsWith("#endif", StringComparison.Ordinal))
			{
				if (depth == 0)
				{
					return sb.ToString();
				}
				depth--;
			}
			sb.Append(raw).Append('\n');
		}
		return inside ? sb.ToString() : null;
	}

	[GeneratedRegex(@"^#\s*define\s+(\w+)(\([^)]*\))?\s*(.*)$")]
	private static partial Regex DefineRegex();

	/// <summary>
	/// Minimal preprocessor: object-like defines, #ifdef/#ifndef/#if/#elif/#else/#endif with defined() and numbers.
	/// Function-like macros (UNITY_LOCATION, UNITY_BINDING) are removed by the parser.
	/// </summary>
	private static string Preprocess(string source)
	{
		Dictionary<string, string> defines = new(StringComparer.Ordinal);
		Stack<(bool Active, bool Taken, bool ParentActive)> stack = new();
		bool active = true;
		StringBuilder sb = new();
		foreach (string raw in source.Split('\n'))
		{
			string line = raw.Trim();
			if (line.StartsWith('#'))
			{
				string directive = line[1..].TrimStart();
				if (directive.StartsWith("ifdef ", StringComparison.Ordinal) || directive.StartsWith("ifndef ", StringComparison.Ordinal) || directive.StartsWith("if ", StringComparison.Ordinal))
				{
					bool condition = active && Evaluate(directive, defines);
					stack.Push((condition, condition, active));
					active = condition;
				}
				else if (directive.StartsWith("elif ", StringComparison.Ordinal))
				{
					(bool _, bool taken, bool parent) = stack.Pop();
					bool condition = parent && !taken && Evaluate("if " + directive[5..], defines);
					stack.Push((condition, taken || condition, parent));
					active = condition;
				}
				else if (directive.StartsWith("else", StringComparison.Ordinal))
				{
					(bool _, bool taken, bool parent) = stack.Pop();
					bool condition = parent && !taken;
					stack.Push((condition, true, parent));
					active = condition;
				}
				else if (directive.StartsWith("endif", StringComparison.Ordinal))
				{
					if (stack.Count > 0)
					{
						active = stack.Pop().ParentActive;
					}
				}
				else if (active && directive.StartsWith("define ", StringComparison.Ordinal))
				{
					Match m = DefineRegex().Match("#" + directive);
					if (m.Success && !m.Groups[2].Success)
					{
						defines[m.Groups[1].Value] = m.Groups[3].Value.Trim();
					}
					else if (m.Success)
					{
						defines[m.Groups[1].Value] = "<function>";
					}
				}
				else if (active && directive.StartsWith("undef ", StringComparison.Ordinal))
				{
					defines.Remove(directive[6..].Trim());
				}
				// #version, #extension, #line, #pragma: dropped
				continue;
			}
			if (!active)
			{
				continue;
			}
			string text = raw;
			foreach ((string name, string value) in defines)
			{
				if (value != "<function>" && text.Contains(name, StringComparison.Ordinal))
				{
					text = Regex.Replace(text, $@"\b{Regex.Escape(name)}\b", value);
				}
			}
			sb.Append(text).Append('\n');
		}
		return sb.ToString();
	}

	private static bool Evaluate(string directive, Dictionary<string, string> defines)
	{
		if (directive.StartsWith("ifdef ", StringComparison.Ordinal))
		{
			return defines.ContainsKey(directive[6..].Trim());
		}
		if (directive.StartsWith("ifndef ", StringComparison.Ordinal))
		{
			return !defines.ContainsKey(directive[7..].Trim());
		}
		string expression = directive[3..].Trim();
		// defined(X) / !defined(X)
		expression = Regex.Replace(expression, @"defined\s*\(\s*(\w+)\s*\)", m => defines.ContainsKey(m.Groups[1].Value) ? "1" : "0");
		expression = Regex.Replace(expression, @"defined\s+(\w+)", m => defines.ContainsKey(m.Groups[1].Value) ? "1" : "0");
		expression = Regex.Replace(expression, @"\b[A-Za-z_]\w*\b", m => defines.TryGetValue(m.Value, out string? v) && int.TryParse(v, out _) ? v : "0");
		return EvaluateBoolean(expression);
	}

	/// <summary>
	/// Evaluates "1 && !0 || 0" style expressions.
	/// </summary>
	private static bool EvaluateBoolean(string expression)
	{
		expression = expression.Replace(" ", "");
		foreach (string orPart in expression.Split("||"))
		{
			bool all = true;
			foreach (string andPart in orPart.Split("&&"))
			{
				string term = andPart.Trim('(', ')');
				bool negate = false;
				while (term.StartsWith('!'))
				{
					negate = !negate;
					term = term[1..].Trim('(', ')');
				}
				bool value;
				if (term.Contains(">=") || term.Contains("<=") || term.Contains("==") || term.Contains('>') || term.Contains('<'))
				{
					Match m = Regex.Match(term, @"^(-?\d+)(>=|<=|==|!=|>|<)(-?\d+)$");
					value = m.Success && Compare(int.Parse(m.Groups[1].Value), m.Groups[2].Value, int.Parse(m.Groups[3].Value));
				}
				else
				{
					value = int.TryParse(term, out int number) && number != 0;
				}
				all &= negate ? !value : value;
			}
			if (all)
			{
				return true;
			}
		}
		return false;

		static bool Compare(int a, string op, int b) => op switch
		{
			">=" => a >= b, "<=" => a <= b, "==" => a == b, "!=" => a != b, ">" => a > b, _ => a < b,
		};
	}

	// ---------------------------------------------------------------- parsing

	[GeneratedRegex(@"\b(highp|mediump|lowp|precise|invariant)\s+")]
	private static partial Regex PrecisionRegex();

	[GeneratedRegex(@"^(?<layout>layout\s*\([^)]*\)\s*)?(?<qual>(flat|centroid|noperspective|smooth)\s+)*(?<storage>uniform|in|out)\s+(?<type>\w+)\s+(?<name>\w+)\s*(\[(?<size>[^\]]+)\])?\s*;$")]
	private static partial Regex DeclarationRegex();

	[GeneratedRegex(@"^(?<type>\w+)\s+(?<name>\w+)\s*(\[(?<size>[^\]]+)\])?\s*;$")]
	private static partial Regex GlobalRegex();

	[GeneratedRegex(@"location\s*=\s*(\d+)")]
	private static partial Regex LocationRegex();

	private static Stage ParseStage(string source, string prefix)
	{
		Stage stage = new();
		// Function-like Unity macros
		source = Regex.Replace(source, @"\bUNITY_LOCATION\s*\(\s*\d+\s*\)\s*", "");
		source = Regex.Replace(source, @"\bUNITY_BINDING\s*\(\s*\d+\s*\)\s*", "");
		// "precision highp float;" first: removing the qualifier first would leave "precision float;"
		source = Regex.Replace(source, @"^\s*precision\s+(\w+\s+)?\w+\s*;\s*$", "", RegexOptions.Multiline);
		source = PrecisionRegex().Replace(source, "");

		int position = 0;
		while (position < source.Length)
		{
			// Next top-level statement: a declaration ending with ';' or a function with a body
			int semicolon = source.IndexOf(';', position);
			int brace = source.IndexOf('{', position);
			if (semicolon < 0 && brace < 0)
			{
				break;
			}
			if (brace >= 0 && (semicolon < 0 || brace < semicolon))
			{
				string head = source[position..brace].Trim();
				int end = MatchingBrace(source, brace);
				string body = source[(brace + 1)..end];
				if (Regex.IsMatch(head, @"^(layout\s*\([^)]*\)\s*)?uniform\s+\w+$"))
				{
					// Uniform block: its members become plain uniforms
					foreach (string member in body.Split(';'))
					{
						Match m = GlobalRegex().Match(member.Trim() + ";");
						if (m.Success)
						{
							stage.Uniforms.Add(new Variable { Type = m.Groups["type"].Value, Name = m.Groups["name"].Value, ArraySize = m.Groups["size"].Value });
						}
					}
					position = end + 1;
					int afterBlock = source.IndexOf(';', position);
					if (afterBlock >= 0 && source[position..afterBlock].Trim().Length > 0)
					{
						throw new TranslationException("named uniform block instance");
					}
					position = afterBlock < 0 ? end + 1 : afterBlock + 1;
					continue;
				}
				if (head.EndsWith(')') && head.StartsWith("void main", StringComparison.Ordinal))
				{
					stage.Main = body;
				}
				else if (head.EndsWith(')'))
				{
					stage.Functions.Add(head + "\n{" + body + "}");
				}
				else
				{
					// "const vec4 ImmCB_0[4] = vec4[4](...)": an initializer, up to the ';'
					int statementEnd = source.IndexOf(';', end);
					stage.GlobalInitializers.Add(source[position..(statementEnd + 1)].Trim());
					position = statementEnd + 1;
					continue;
				}
				position = end + 1;
				continue;
			}

			string statement = source[position..(semicolon + 1)].Trim();
			position = semicolon + 1;
			if (statement.Length <= 1)
			{
				continue;
			}
			Match declaration = DeclarationRegex().Match(statement);
			if (declaration.Success)
			{
				string layout = declaration.Groups["layout"].Value;
				Match location = LocationRegex().Match(layout);
				Variable variable = new()
				{
					Type = declaration.Groups["type"].Value,
					Name = declaration.Groups["name"].Value,
					ArraySize = declaration.Groups["size"].Value,
					Qualifier = declaration.Groups["qual"].Value.Trim(),
					Location = location.Success ? int.Parse(location.Groups[1].Value) : -1,
				};
				switch (declaration.Groups["storage"].Value)
				{
					case "uniform":
						stage.Uniforms.Add(variable);
						break;
					case "in":
						stage.Inputs.Add(variable);
						break;
					default:
						stage.Outputs.Add(variable);
						break;
				}
				continue;
			}
			Match global = GlobalRegex().Match(statement);
			if (global.Success)
			{
				stage.Globals.Add(new Variable { Type = global.Groups["type"].Value, Name = global.Groups["name"].Value, ArraySize = global.Groups["size"].Value });
				continue;
			}
			if (statement.Contains('='))
			{
				stage.GlobalInitializers.Add(statement);
				continue;
			}
			throw new TranslationException("unknown global statement: " + statement);
		}
		if (stage.Main.Length == 0)
		{
			throw new TranslationException("no main function");
		}

		// Stage-local globals get a prefix: both stages are in the same HLSL file
		foreach (Variable global in stage.Globals)
		{
			stage.Main = RenameIdentifier(stage.Main, global.Name, prefix + global.Name);
			for (int i = 0; i < stage.Functions.Count; i++)
			{
				stage.Functions[i] = RenameIdentifier(stage.Functions[i], global.Name, prefix + global.Name);
			}
		}
		stage.UsesFrontFacing = stage.Main.Contains("gl_FrontFacing", StringComparison.Ordinal);
		stage.UsesFragCoord = stage.Main.Contains("gl_FragCoord", StringComparison.Ordinal);
		return stage;
	}

	private static string RenameIdentifier(string text, string name, string newName) => Regex.Replace(text, $@"(?<![\w.]){Regex.Escape(name)}\b", newName);

	private static int MatchingBrace(string text, int open)
	{
		int depth = 0;
		for (int i = open; i < text.Length; i++)
		{
			if (text[i] == '{')
			{
				depth++;
			}
			else if (text[i] == '}')
			{
				depth--;
				if (depth == 0)
				{
					return i;
				}
			}
		}
		throw new TranslationException("unbalanced braces");
	}

	// ---------------------------------------------------------------- code translation

	private static string MapType(string type)
	{
		if (TypeMap.TryGetValue(type, out string? mapped))
		{
			return mapped;
		}
		if (type.StartsWith("sampler", StringComparison.Ordinal) || type.StartsWith("isampler", StringComparison.Ordinal) || type.StartsWith("usampler", StringComparison.Ordinal))
		{
			if (!SamplerFunctions.ContainsKey(type))
			{
				throw new TranslationException("unsupported sampler " + type);
			}
			return type == "samplerCube" ? "samplerCUBE" : type;
		}
		return type;
	}

	/// <summary>
	/// Translates GLSL code (statements or expressions) to HLSL.
	/// </summary>
	private static string TranslateCode(string code, IReadOnlyDictionary<string, string> samplers)
	{
		if (Regex.IsMatch(code, @"\b(mat[234](x[234])?)\b"))
		{
			throw new TranslationException("matrix types");
		}
		if (Regex.IsMatch(code, @"\b(texelFetch|textureSize|textureProj|textureGather|textureOffset|textureLodOffset|imageLoad|imageStore|gl_InstanceID|gl_VertexID)\b"))
		{
			throw new TranslationException("unsupported function");
		}
		code = RewriteCalls(code, samplers);
		foreach ((string glsl, string hlsl) in TypeMap)
		{
			code = Regex.Replace(code, $@"\b{glsl}\b", hlsl);
		}
		code = code.Replace("gl_Position", "glsl_Position").Replace("gl_FragCoord", "glsl_FragCoord").Replace("gl_FrontFacing", "glsl_FrontFacing");
		return code;
	}

	[GeneratedRegex(@"\b(vec[234]|ivec[234]|uvec[234]|bvec[234]|texture|textureLod|textureGrad|atan)\s*\(")]
	private static partial Regex CallRegex();

	/// <summary>
	/// Rewrites calls whose arguments must change: single argument constructors, texture sampling, atan(y, x).
	/// </summary>
	private static string RewriteCalls(string code, IReadOnlyDictionary<string, string> samplers)
	{
		StringBuilder sb = new();
		int position = 0;
		while (true)
		{
			Match m = CallRegex().Match(code, position);
			if (!m.Success)
			{
				sb.Append(code, position, code.Length - position);
				return sb.ToString();
			}
			sb.Append(code, position, m.Index - position);
			int open = m.Index + m.Length - 1;
			int close = MatchingParenthesis(code, open);
			string name = m.Groups[1].Value;
			List<string> args = SplitArguments(code[(open + 1)..close]).Select(a => RewriteCalls(a, samplers)).ToList();
			sb.Append(RewriteCall(name, args, samplers));
			position = close + 1;
		}
	}

	private static string RewriteCall(string name, List<string> args, IReadOnlyDictionary<string, string> samplers)
	{
		if (name.StartsWith("vec", StringComparison.Ordinal) || name.StartsWith("ivec", StringComparison.Ordinal) || name.StartsWith("uvec", StringComparison.Ordinal) || name.StartsWith("bvec", StringComparison.Ordinal))
		{
			string type = TypeMap[name];
			return args.Count == 1 ? $"(({type})({args[0]}))" : $"{type}({string.Join(", ", args)})";
		}
		if (name == "atan")
		{
			return args.Count == 2 ? $"atan2({args[0]}, {args[1]})" : $"atan({args[0]})";
		}

		string sampler = args[0].Trim();
		if (!samplers.TryGetValue(sampler, out string? samplerType) || !SamplerFunctions.TryGetValue(samplerType, out string? function))
		{
			throw new TranslationException($"texture call on unknown sampler {sampler}");
		}
		string coordinates = args[1];
		string Expand(string extra) => samplerType == "sampler2D" ? $"float4(({coordinates}).xy, 0.0, {extra})" : $"float4(({coordinates}).xyz, {extra})";
		return name switch
		{
			"texture" when args.Count == 2 => $"{function}({sampler}, {coordinates})",
			"texture" when args.Count == 3 => $"{function}bias({sampler}, {Expand(args[2])})",
			"textureLod" => $"{function}lod({sampler}, {Expand(args[2])})",
			"textureGrad" => $"{function}grad({sampler}, {coordinates}, {args[2]}, {args[3]})",
			_ => throw new TranslationException("unsupported texture call " + name),
		};
	}

	private static int MatchingParenthesis(string text, int open)
	{
		int depth = 0;
		for (int i = open; i < text.Length; i++)
		{
			if (text[i] == '(')
			{
				depth++;
			}
			else if (text[i] == ')')
			{
				depth--;
				if (depth == 0)
				{
					return i;
				}
			}
		}
		throw new TranslationException("unbalanced parentheses");
	}

	private static List<string> SplitArguments(string text)
	{
		List<string> args = [];
		int depth = 0;
		int start = 0;
		for (int i = 0; i < text.Length; i++)
		{
			char c = text[i];
			if (c is '(' or '[')
			{
				depth++;
			}
			else if (c is ')' or ']')
			{
				depth--;
			}
			else if (c == ',' && depth == 0)
			{
				args.Add(text[start..i].Trim());
				start = i + 1;
			}
		}
		if (text[start..].Trim().Length > 0)
		{
			args.Add(text[start..].Trim());
		}
		return args;
	}

	// ---------------------------------------------------------------- generation

	private static string Semantic(string attribute)
	{
		// Older Unity versions: _glesVertex, _glesNormal, _glesMultiTexCoord0...
		switch (attribute)
		{
			case "_glesVertex":
				return "POSITION";
			case "_glesNormal":
				return "NORMAL";
			case "_glesTANGENT":
				return "TANGENT";
			case "_glesColor":
				return "COLOR";
		}
		Match legacy = Regex.Match(attribute, @"^_glesMultiTexCoord(\d+)$");
		if (legacy.Success)
		{
			return "TEXCOORD" + legacy.Groups[1].Value;
		}
		Match m = Regex.Match(attribute, @"^in_([A-Z]+)(\d*)$");
		if (!m.Success)
		{
			throw new TranslationException("unknown vertex input " + attribute);
		}
		string index = m.Groups[2].Value.Length == 0 ? "0" : m.Groups[2].Value;
		return m.Groups[1].Value switch
		{
			"POSITION" => "POSITION",
			"NORMAL" => "NORMAL",
			"TANGENT" => "TANGENT",
			"COLOR" => index == "0" ? "COLOR" : "COLOR" + index,
			"TEXCOORD" => "TEXCOORD" + index,
			_ => throw new TranslationException("unsupported vertex input " + attribute),
		};
	}

	private static string Generate(Stage vertex, Stage fragment)
	{
		StringBuilder sb = new();
		sb.Append("#pragma vertex ar_vert\n#pragma fragment ar_frag\n#pragma target 3.5\n");
		sb.Append(Preamble.Replace("\r", "")).Append('\n');

		// Uniforms of both stages, once
		Dictionary<string, Variable> uniforms = new(StringComparer.Ordinal);
		foreach (Variable uniform in vertex.Uniforms.Concat(fragment.Uniforms))
		{
			uniforms.TryAdd(uniform.Name, uniform);
		}
		Dictionary<string, string> samplers = uniforms.Values.Where(u => u.Type.Contains("sampler", StringComparison.Ordinal)).ToDictionary(u => u.Name, u => u.Type);
		StringBuilder matrixInit = new();
		foreach (Variable uniform in uniforms.Values)
		{
			string type = MapType(uniform.Type);
			if (uniform.Name.StartsWith("hlslcc_mtx4x4", StringComparison.Ordinal))
			{
				// Column arrays, rebuilt from the matrices that Unity binds by name
				string matrix = uniform.Name["hlslcc_mtx4x4".Length..];
				if (!int.TryParse(uniform.ArraySize, out int size) || size % 4 != 0)
				{
					throw new TranslationException("matrix array size " + uniform.ArraySize);
				}
				int count = size / 4;
				sb.Append(count == 1 ? $"float4x4 {matrix};\n" : $"float4x4 {matrix}[{count}];\n");
				sb.Append($"static float4 {uniform.Name}[{size}];\n");
				for (int m = 0; m < count; m++)
				{
					string source = count == 1 ? matrix : $"{matrix}[{m}]";
					for (int column = 0; column < 4; column++)
					{
						matrixInit.Append($"\t{uniform.Name}[{m * 4 + column}] = transpose({source})[{column}];\n");
					}
				}
				continue;
			}
			sb.Append(uniform.ArraySize.Length > 0 ? $"{type} {uniform.Name}[{uniform.ArraySize}];\n" : $"{type} {uniform.Name};\n");
		}

		// Interface variables as statics
		List<Variable> varyings = vertex.Outputs.ToList();
		foreach (Variable variable in vertex.Inputs.Concat(varyings).Concat(fragment.Outputs).Concat(fragment.Inputs.Where(i => varyings.All(v => v.Name != i.Name))))
		{
			sb.Append($"static {MapType(variable.Type)} {variable.Name}{(variable.ArraySize.Length > 0 ? $"[{variable.ArraySize}]" : "")};\n");
		}
		sb.Append("static float4 glsl_Position;\nstatic float4 glsl_FragCoord;\nstatic bool glsl_FrontFacing;\n");
		foreach (Variable global in vertex.Globals.Select(g => (g, "V_")).Concat(fragment.Globals.Select(g => (g, "F_"))).Select(p => new Variable { Type = p.g.Type, Name = p.Item2 + p.g.Name, ArraySize = p.g.ArraySize }))
		{
			sb.Append($"static {MapType(global.Type)} {global.Name}{(global.ArraySize.Length > 0 ? $"[{global.ArraySize}]" : "")};\n");
		}
		foreach (string initializer in vertex.GlobalInitializers.Concat(fragment.GlobalInitializers).Distinct())
		{
			string hlsl = TranslateCode(initializer, samplers);
			hlsl = Regex.Replace(hlsl, @"^const\s+", "static const ");
			hlsl = Regex.Replace(hlsl, @"=\s*\w+\[\d*\]\s*\(", "= {");
			if (hlsl.Contains("= {", StringComparison.Ordinal))
			{
				hlsl = hlsl[..hlsl.LastIndexOf(')')] + "};";
			}
			sb.Append(hlsl).Append('\n');
		}
		foreach (string function in vertex.Functions.Concat(fragment.Functions))
		{
			sb.Append(TranslateCode(function, samplers)).Append('\n');
		}
		sb.Append("void V_main()\n{").Append(TranslateCode(vertex.Main, samplers)).Append("}\n");
		sb.Append("void F_main()\n{").Append(TranslateCode(fragment.Main, samplers)).Append("}\n");

		// Vertex input and varying structures
		sb.Append("struct ar_appdata\n{\n");
		HashSet<string> semantics = [];
		foreach (Variable input in vertex.Inputs)
		{
			string semantic = Semantic(input.Name);
			if (!semantics.Add(semantic))
			{
				throw new TranslationException("duplicate semantic " + semantic);
			}
			sb.Append($"\t{MapType(input.Type)} {input.Name} : {semantic};\n");
		}
		sb.Append("};\nstruct ar_v2f\n{\n\tfloat4 ar_position : SV_POSITION;\n");
		int texcoord = 0;
		foreach (Variable varying in varyings)
		{
			string interpolation = varying.Qualifier == "flat" ? "nointerpolation " : "";
			sb.Append($"\t{interpolation}{MapType(varying.Type)} {varying.Name} : TEXCOORD{texcoord++};\n");
		}
		if (texcoord > 16)
		{
			throw new TranslationException("too many varyings");
		}
		sb.Append("};\n");

		sb.Append("ar_v2f ar_vert(ar_appdata v)\n{\n").Append(matrixInit);
		foreach (Variable input in vertex.Inputs)
		{
			sb.Append($"\t{input.Name} = v.{input.Name};\n");
		}
		sb.Append("\tV_main();\n\tar_v2f o;\n\to.ar_position = glsl_Position;\n");
		foreach (Variable varying in varyings)
		{
			sb.Append($"\to.{varying.Name} = {varying.Name};\n");
		}
		sb.Append("\treturn o;\n}\n");

		List<Variable> targets = fragment.Outputs.OrderBy(o => o.Location).ToList();
		if (targets.Count == 0)
		{
			throw new TranslationException("no fragment output");
		}
		bool multipleTargets = targets.Count > 1;
		if (multipleTargets)
		{
			sb.Append("struct ar_fout\n{\n");
			for (int t = 0; t < targets.Count; t++)
			{
				sb.Append($"\tfloat4 target{t} : SV_Target{(targets[t].Location >= 0 ? targets[t].Location : t)};\n");
			}
			sb.Append("};\n");
		}
		string facing = fragment.UsesFrontFacing ? ", float ar_facing : VFACE" : "";
		sb.Append(multipleTargets ? $"ar_fout ar_frag(ar_v2f i{facing})\n{{\n" : $"float4 ar_frag(ar_v2f i{facing}) : SV_Target\n{{\n").Append(matrixInit);
		foreach (Variable input in fragment.Inputs)
		{
			if (varyings.Any(v => v.Name == input.Name))
			{
				sb.Append($"\t{input.Name} = i.{input.Name};\n");
			}
		}
		sb.Append("\tglsl_FragCoord = i.ar_position;\n");
		if (fragment.UsesFrontFacing)
		{
			sb.Append("\tglsl_FrontFacing = ar_facing > 0;\n");
		}
		sb.Append("\tF_main();\n");
		if (multipleTargets)
		{
			sb.Append("\tar_fout result;\n");
			for (int t = 0; t < targets.Count; t++)
			{
				sb.Append($"\tresult.target{t} = (float4){targets[t].Name};\n");
			}
			sb.Append("\treturn result;\n}\n");
		}
		else
		{
			sb.Append($"\treturn (float4){targets[0].Name};\n}}\n");
		}
		return sb.ToString();
	}
}
