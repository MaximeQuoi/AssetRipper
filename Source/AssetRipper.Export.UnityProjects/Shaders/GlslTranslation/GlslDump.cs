using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Shaders.GlslTranslation;

/// <summary>
/// Diagnostic: writes the OpenGL ES programs of each exported shader to the folder named by ASSETRIPPER_DUMP_GLSL.
/// </summary>
internal static class GlslDump
{
	private static readonly string? Folder = Environment.GetEnvironmentVariable("ASSETRIPPER_DUMP_GLSL");

	public static void TryDump(IShader shader)
	{
		if (string.IsNullOrEmpty(Folder) || !shader.Has_ParsedForm())
		{
			return;
		}
		try
		{
			StringBuilder sb = new();
			ShaderBlob.FirstProgramHex = null;
			Dictionary<int, byte[]> entries = ShaderBlob.ReadEntries(shader, GPUPlatform.Gles3x);
			int gles = shader.Platforms.IndexOf((uint)GPUPlatform.Gles3x);
			sb.Append($"// Shader {shader.ParsedForm.Name}: {entries.Count} GLES3 entries, platforms {string.Join(",", shader.Platforms)}{(ShaderBlob.LastError is null ? "" : " ERROR " + ShaderBlob.LastError)}\n");
			foreach (var subShader in shader.ParsedForm.SubShaders)
			{
				foreach (var pass in subShader.Passes)
				{
					foreach ((string stage, var program) in new[] { ("vertex", pass.ProgVertex), ("fragment", pass.ProgFragment) })
					{
						if (gles < 0 || program.PlayerSubPrograms.Count <= gles)
						{
							continue;
						}
						foreach (var sub in program.PlayerSubPrograms[gles])
						{
							int index = (int)sub.BlobIndex;
							string keywords = string.Join(" ", sub.KeywordIndices.Select(k => k < shader.ParsedForm.KeywordNames.Count ? shader.ParsedForm.KeywordNames[k].String : k.ToString()));
							sb.Append($"\n//////// pass {pass.State.Name_R} {stage}, blob {index}, type {sub.GpuProgramType}, keywords: {keywords}\n");
							if (entries.TryGetValue(index, out byte[]? entry))
							{
								BlobSubProgram? parsed = ShaderBlob.TryReadProgram(entry);
								sb.Append(parsed is not null ? parsed.CodeText : "// unreadable: " + ShaderBlob.FirstProgramHex).Append('\n');
							}
						}
					}
				}
			}
			Directory.CreateDirectory(Folder);
			File.WriteAllLines(Path.Combine(Folder, "_failures.txt"), GlslShaderTranslator.Failures.Select(f => $"{f.Key}: {f.Value}"));
			string name = string.Concat(shader.ParsedForm.Name.String.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
			File.WriteAllText(Path.Combine(Folder, name + ".glsl"), sb.ToString());
		}
		catch (Exception e)
		{
			File.AppendAllText(Path.Combine(Folder, "_errors.txt"), $"{shader.ParsedForm.Name}: {e}\n");
		}
	}
}
