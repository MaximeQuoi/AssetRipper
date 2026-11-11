using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AssetRipper.Tools.DumpCsToAssemblies;

/// <summary>
/// Rebuilds stub assemblies (types, fields, signatures, empty bodies) from the dump.cs of Il2CppDumper,
/// for games whose libil2cpp.so and global-metadata.dat cannot be read (encrypted or packed).
/// The assemblies can then be loaded by AssetRipper like the Managed folder of a Mono game.
/// </summary>
internal static partial class Program
{
	private const int MaxIterations = 80;

	public static int Main(string[] args)
	{
		if (args.Length < 3)
		{
			Console.Error.WriteLine("usage: DumpCsToAssemblies <dump.cs> <Unity editor Contents folder> <output folder> [--sources]");
			return 2;
		}
		// English compiler messages: the repairs parse them
		System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
		System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
		string dumpPath = args[0];
		string unityContents = args[1];
		string output = args[2];
		bool writeSources = args.Contains("--sources");
		Directory.CreateDirectory(output);

		Stopwatch sw = Stopwatch.StartNew();
		List<DumpImage> images = DumpParser.Parse(dumpPath);
		Console.WriteLine($"Parsed {images.Count} images, {images.Sum(i => i.Types.Count)} types in {sw.ElapsedMilliseconds} ms");

		if (args.Contains("--stats"))
		{
			foreach (DumpImage image in images)
			{
				Console.WriteLine($"  {image.Name}: {image.Types.Count} types (first index {image.FirstTypeIndex})");
			}
			foreach (string probe in (string[])["GameObject", "MonoBehaviour", "Component", "List"])
			{
				Console.WriteLine($"  {probe}: " + string.Join(", ", images.SelectMany(i => i.Types).Where(t => t.Name == probe).Select(t => $"{t.FullName}<{t.GenericParameters.Count}> in {t.Image.Name}")));
			}
			return 0;
		}

		List<MetadataReference> references = UnityReferences(unityContents);
		Console.WriteLine($"{references.Count} Unity reference assemblies");

		TypeResolver resolver = new(images);
		List<DumpImage> generated = images.Where(i => !IsEngineOrSystem(i.AssemblyName) && i.Types.Count > 0).ToList();

		// Dependencies between generated assemblies
		Dictionary<DumpImage, HashSet<DumpImage>> dependencies = [];
		foreach (DumpImage image in generated)
		{
			resolver.ReferencedImages.Clear();
			new CSharpEmitter(resolver, new RepairState()).Emit(image);
			dependencies[image] = resolver.ReferencedImages.Where(r => r != image && generated.Contains(r)).ToHashSet();
		}
		List<DumpImage> order = TopologicalOrder(generated, dependencies);

		Dictionary<DumpImage, MetadataReference> built = [];
		using StreamWriter log = new(Path.Combine(output, "DumpCsToAssemblies.log"));
		int failures = 0;
		foreach (DumpImage image in order)
		{
			List<MetadataReference> imageReferences = [.. references, .. dependencies[image].Where(built.ContainsKey).Select(d => built[d])];
			(byte[]? dll, RepairState repair, int iterations, int remainingErrors) = Build(image, resolver, imageReferences, output, writeSources);
			log.WriteLine($"== {image.Name}: {image.Types.Count} types, {iterations} iterations, {repair.Log.Count} repairs{(dll is null ? $", FAILED with {remainingErrors} errors" : "")}");
			foreach (string entry in repair.Log)
			{
				log.WriteLine("  " + entry);
			}
			if (dll is null)
			{
				failures++;
				Console.WriteLine($"FAILED {image.Name} ({remainingErrors} errors left)");
				continue;
			}
			File.WriteAllBytes(Path.Combine(output, image.AssemblyName + ".dll"), dll);
			built[image] = MetadataReference.CreateFromImage(dll);
			Console.WriteLine($"{image.Name}: {image.Types.Count} types, {iterations} passes, {repair.Log.Count} repairs");
		}
		Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:F0} s: {built.Count} assemblies, {failures} failures. See DumpCsToAssemblies.log");
		return failures == 0 ? 0 : 1;
	}

	/// <summary>
	/// Assemblies provided by Unity or .NET: they are referenced, not rebuilt.
	/// </summary>
	private static bool IsEngineOrSystem(string assemblyName)
	{
		if (assemblyName == "UnityEngine.UI")
		{
			return false; // package com.unity.ugui, not part of the editor install
		}
		return assemblyName is "mscorlib" or "netstandard" or "__Generated"
			|| assemblyName.StartsWith("System", StringComparison.Ordinal)
			|| assemblyName.StartsWith("Mono.", StringComparison.Ordinal)
			|| assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal);
	}

	private static List<MetadataReference> UnityReferences(string contents)
	{
		List<string> paths = [];
		string engine = Path.Combine(contents, "Managed", "UnityEngine");
		paths.AddRange(Directory.GetFiles(engine, "UnityEngine*.dll"));
		string mono = Path.Combine(contents, "MonoBleedingEdge", "lib", "mono", "unityjit-macos");
		if (!Directory.Exists(mono))
		{
			mono = Directory.GetDirectories(Path.Combine(contents, "MonoBleedingEdge", "lib", "mono"), "unityjit*").First();
		}
		paths.AddRange(Directory.GetFiles(mono, "*.dll"));
		string facades = Path.Combine(mono, "Facades");
		if (Directory.Exists(facades))
		{
			paths.AddRange(Directory.GetFiles(facades, "netstandard.dll"));
		}
		return paths.Distinct().Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
	}

	private static List<DumpImage> TopologicalOrder(List<DumpImage> images, Dictionary<DumpImage, HashSet<DumpImage>> dependencies)
	{
		List<DumpImage> order = [];
		HashSet<DumpImage> done = [];
		HashSet<DumpImage> visiting = [];
		void Visit(DumpImage image)
		{
			if (done.Contains(image) || !visiting.Add(image))
			{
				return; // cycle: broken here
			}
			foreach (DumpImage dependency in dependencies[image])
			{
				Visit(dependency);
			}
			visiting.Remove(image);
			done.Add(image);
			order.Add(image);
		}
		foreach (DumpImage image in images)
		{
			Visit(image);
		}
		return order;
	}

	private static (byte[]? Dll, RepairState Repair, int Iterations, int RemainingErrors) Build(DumpImage image, TypeResolver resolver, List<MetadataReference> references, string output, bool writeSources)
	{
		RepairState repair = new();
		CSharpParseOptions parseOptions = new(LanguageVersion.Latest);
		CSharpCompilationOptions options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
			.WithAllowUnsafe(true)
			.WithNullableContextOptions(NullableContextOptions.Disable)
			.WithOptimizationLevel(OptimizationLevel.Release)
			.WithWarningLevel(0)
			.WithConcurrentBuild(true);

		int remaining = 0;
		for (int iteration = 1; iteration <= MaxIterations; iteration++)
		{
			CSharpEmitter emitter = new(resolver, repair);
			string source = emitter.Emit(image);
			if (writeSources)
			{
				File.WriteAllText(Path.Combine(output, image.AssemblyName + ".cs"), source);
			}
			SyntaxTree tree = CSharpSyntaxTree.ParseText(SourceText.From(source), parseOptions, path: image.AssemblyName + ".cs");
			CSharpCompilation compilation = CSharpCompilation.Create(image.AssemblyName, [tree], references, options);
			using MemoryStream stream = new();
			Microsoft.CodeAnalysis.Emit.EmitResult result = compilation.Emit(stream);
			List<Diagnostic> errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
			remaining = errors.Count;
			if (result.Success)
			{
				return (stream.ToArray(), repair, iteration, 0);
			}
			if (!ApplyRepairs(errors, emitter.Lines, source, repair))
			{
				foreach (Diagnostic error in errors.Take(20))
				{
					repair.Log.Add("UNFIXED " + error);
				}
				return (null, repair, iteration, remaining);
			}
		}
		return (null, repair, MaxIterations, remaining);
	}

	[GeneratedRegex(@"'([^']*)'")]
	private static partial Regex QuotedRegex();

	private static bool ApplyRepairs(List<Diagnostic> errors, IReadOnlyList<LineInfo?> lines, string source, RepairState repair)
	{
		bool changed = false;
		foreach (Diagnostic error in errors)
		{
			if (!error.Location.IsInSource)
			{
				continue;
			}
			int line = error.Location.GetLineSpan().StartLinePosition.Line;
			LineInfo? info = line < lines.Count ? lines[line] : null;
			if (info is null)
			{
				continue;
			}
			string spanText = source.Substring(error.Location.SourceSpan.Start, error.Location.SourceSpan.Length).Trim();
			string message = error.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
			List<string> quoted = QuotedRegex().Matches(message).Select(m => m.Groups[1].Value).ToList();

			string? constraint = error.Id switch
			{
				"CS0452" => "class",
				"CS0453" => "struct",
				"CS0310" => "new()",
				"CS0311" or "CS0314" or "CS0315" => quoted.Count >= 4 ? "global::" + quoted[^1] : null,
				_ => null,
			};
			if (error.Id is "CS0452" or "CS0453" or "CS0310" or "CS0311" or "CS0314" or "CS0315")
			{
				string parameter = quoted.Count > 0 ? quoted[0] : "";
				if (constraint is not null && IsIdentifier(parameter) && TryAddConstraint(info, parameter, constraint, repair))
				{
					changed = true;
					continue;
				}
			}

			if (info.MemberKey is null)
			{
				// Error on a type declaration: usually a base type or interface
				if (error.Id is "CS0534")
				{
					if (repair.ForcedAbstract.Add(info.TypeKey))
					{
						repair.Log.Add($"made abstract (missing abstract member): {info.TypeKey}: {message}");
						changed = true;
					}
					continue;
				}
				string? baseText = info.BaseTypes?.FirstOrDefault(b => b == spanText)
					?? info.BaseTypes?.FirstOrDefault(b => b.EndsWith("." + spanText, StringComparison.Ordinal) || b.EndsWith(":" + spanText, StringComparison.Ordinal))
					?? info.BaseTypes?.FirstOrDefault(b => spanText.Length > 0 && b.Contains(spanText, StringComparison.Ordinal));
				if (baseText is not null && repair.RemovedBases.Add(info.TypeKey + "|" + baseText))
				{
					repair.Log.Add($"removed base {baseText} from {info.TypeKey}: {error.Id} {message}");
					changed = true;
					continue;
				}
				if (repair.RemovedTypes.Add(info.TypeKey))
				{
					repair.Log.Add($"removed type {info.TypeKey}: {error.Id} {message}");
					changed = true;
				}
				continue;
			}

			if (error.Id is "CS0115" or "CS0506" or "CS0507" or "CS0505" or "CS0544" or "CS0545" or "CS0546" or "CS0508" or "CS0462" or "CS0263")
			{
				if (repair.NoOverride.Add(info.MemberKey))
				{
					repair.Log.Add($"override removed: {info.MemberDescription}: {error.Id}");
					changed = true;
					continue;
				}
			}
			if (repair.RemovedMembers.Add(info.MemberKey))
			{
				repair.Log.Add($"removed {info.MemberDescription}: {error.Id} {message}");
				changed = true;
			}
		}
		return changed;
	}

	private static bool IsIdentifier(string text) => text.Length > 0 && (char.IsLetter(text[0]) || text[0] == '_') && text.All(c => char.IsLetterOrDigit(c) || c == '_');

	/// <summary>
	/// Adds a constraint to the type or method that declares the generic parameter.
	/// </summary>
	private static bool TryAddConstraint(LineInfo info, string parameter, string constraint, RepairState repair)
	{
		string? owner = CSharpEmitter.FindGenericOwner(info, parameter);
		if (owner is null)
		{
			return false;
		}
		string key = owner + "|" + parameter;
		if (repair.Constraints.TryGetValue(key, out List<string>? existing) && existing.Contains(constraint))
		{
			return false;
		}
		repair.AddConstraint(owner, parameter, constraint);
		repair.Log.Add($"constraint {parameter} : {constraint} on {owner}");
		return true;
	}
}
