using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader.SerializedShader;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedPlayerSubProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedProgram;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubShader;
using System.Collections.Concurrent;

namespace AssetRipper.Export.UnityProjects.Shaders.GlslTranslation;

/// <summary>
/// Rebuilds the programs of a shader from its OpenGL ES 3 code, translated to HLSL.
/// </summary>
internal static class GlslShaderTranslator
{
	/// <summary>
	/// Keywords of variants that need data the editor does not provide in a simple scene.
	/// </summary>
	private static readonly string[] AvoidedKeywords =
	[
		"INSTANCING_ON", "PROCEDURAL_INSTANCING_ON", "STEREO_", "UNITY_SINGLE_PASS_STEREO", "SHADOWS_SCREEN", "SHADOWS_DEPTH", "SHADOWS_CUBE",
		"LIGHTMAP_ON", "DYNAMICLIGHTMAP_ON", "DIRLIGHTMAP_COMBINED", "FOG_LINEAR", "FOG_EXP", "FOG_EXP2", "UNITY_HDR_ON", "LOD_FADE_CROSSFADE",
	];

	private static readonly string[] SkippedLightModes = ["ShadowCaster", "Meta", "DepthOnly", "MotionVectors", "DepthNormals", "SceneSelectionPass", "Picking"];

	/// <summary>
	/// Why each shader was not translated, for diagnostics.
	/// </summary>
	public static ConcurrentDictionary<string, string> Failures { get; } = new();

	/// <summary>
	/// Translated programs by pass, or null if any rendered pass cannot be translated.
	/// </summary>
	public static Func<ISerializedPass, string?>? TryCreate(IShader shader)
	{
		if (!shader.Has_ParsedForm() || !shader.Platforms.Contains((uint)GPUPlatform.Gles3x))
		{
			return null;
		}
		string shaderName = shader.ParsedForm.Name.String;
		Dictionary<int, byte[]> entries = ShaderBlob.ReadEntries(shader, GPUPlatform.Gles3x);
		if (entries.Count == 0)
		{
			Failures[shaderName] = "blob: " + (ShaderBlob.LastError ?? "no entries");
			return null;
		}

		Dictionary<ISerializedPass, string> programs = new(ReferenceEqualityComparer.Instance);
		foreach (ISerializedSubShader subShader in shader.ParsedForm.SubShaders)
		{
			foreach (ISerializedPass pass in subShader.Passes)
			{
				if (pass.GetType_() != SerializedPassType.Pass || IsSkipped(pass))
				{
					continue;
				}
				string? code = SelectProgram(shader, pass.ProgVertex, entries);
				if (code is null)
				{
					Failures[shaderName] = $"pass {pass.State.Name_R}: no OpenGL ES program";
					return null;
				}
				try
				{
					programs[pass] = GlslToHlsl.Translate(code);
				}
				catch (GlslToHlsl.TranslationException e)
				{
					Failures[shaderName] = $"pass {pass.State.Name_R}: {e.Message}";
					return null;
				}
				catch (Exception e)
				{
					Failures[shaderName] = $"pass {pass.State.Name_R}: {e.GetType().Name} {e.Message}";
					return null;
				}
			}
		}
		if (programs.Count == 0)
		{
			return null;
		}
		Failures.TryRemove(shaderName, out _);
		return pass => programs.TryGetValue(pass, out string? program) ? program : null;
	}

	private static bool IsSkipped(ISerializedPass pass)
	{
		foreach ((Utf8String key, Utf8String value) in pass.State.Tags.Tags)
		{
			if (key.String == "LightMode" && SkippedLightModes.Contains(value.String, StringComparer.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// The combined OpenGL ES source of the simplest variant: fewest keywords, avoiding instancing, shadows, lightmaps and fog.
	/// </summary>
	private static string? SelectProgram(IShader shader, ISerializedProgram program, Dictionary<int, byte[]> entries)
	{
		List<(int Score, string Code)> candidates = [];
		IEnumerable<ISerializedPlayerSubProgram> subPrograms = program.PlayerSubPrograms.SelectMany(list => list);
		foreach (ISerializedPlayerSubProgram sub in subPrograms)
		{
			if (sub.GpuProgramType is < 2 or > 5 || !entries.TryGetValue((int)sub.BlobIndex, out byte[]? entry))
			{
				continue;
			}
			BlobSubProgram? parsed = ShaderBlob.TryReadProgram(entry);
			if (parsed is null)
			{
				continue;
			}
			string code = parsed.CodeText;
			if (!code.Contains("#ifdef VERTEX", StringComparison.Ordinal) || !code.Contains("#ifdef FRAGMENT", StringComparison.Ordinal))
			{
				continue;
			}
			List<string> keywords = sub.KeywordIndices
				.Select(k => k < shader.ParsedForm.KeywordNames.Count ? shader.ParsedForm.KeywordNames[k].String : "")
				.ToList();
			int score = keywords.Count + 100 * keywords.Count(k => AvoidedKeywords.Any(a => k.StartsWith(a, StringComparison.Ordinal)));
			candidates.Add((score, code));
		}
		return candidates.Count == 0 ? null : candidates.MinBy(c => c.Score).Code;
	}
}
