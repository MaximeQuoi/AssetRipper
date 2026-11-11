using AssetRipper.SourceGenerated.Classes.ClassID_48;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Extensions.Enums.Shader.SerializedShader;
using AssetRipper.SourceGenerated.Subclasses.SerializedPass;
using AssetRipper.SourceGenerated.Subclasses.SerializedProperty;
using AssetRipper.SourceGenerated.Subclasses.SerializedShader;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderFloatValue;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderRTBlendState;
using AssetRipper.SourceGenerated.Subclasses.SerializedShaderState;
using AssetRipper.SourceGenerated.Subclasses.SerializedStencilOp;
using AssetRipper.SourceGenerated.Subclasses.SerializedSubShader;
using AssetRipper.SourceGenerated.Subclasses.SerializedTagMap;
using System.Text;

namespace AssetRipper.Export.UnityProjects.Shaders;

/// <summary>
/// Dummy shader body that keeps the render state of the original passes (blending, depth, culling, stencil, tags),
/// with a simple unlit "main texture × vertex color × tint" program.
/// </summary>
/// <remarks>
/// The default dummy templates are opaque: transparent UI, particles and effects become white or black rectangles.
/// This keeps them visually close to the original, which matters for UI and effects much more than lighting.
/// </remarks>
internal static class RenderStateDummyShader
{
	private static readonly string[] SkippedLightModes = ["ShadowCaster", "Meta", "DepthOnly", "MotionVectors", "DepthNormals", "SceneSelectionPass", "Picking"];
	private static readonly string[] TextureNames = ["_MainTex", "_BaseMap", "_MainTexture", "_Tex", "_BaseTex"];
	private static readonly string[] ColorNames = ["_Color", "_TintColor", "_BaseColor", "_MainColor", "_MainCol", "_TintCol"];

	/// <summary>
	/// True if a pass is not plain opaque: blending, no depth write, or a transparent queue.
	/// </summary>
	public static bool ShouldUse(IShader shader)
	{
		ISerializedShader parsed = shader.ParsedForm;
		foreach (ISerializedSubShader subShader in parsed.SubShaders)
		{
			if (IsTransparentQueue(subShader.Tags))
			{
				return true;
			}
			foreach (ISerializedPass pass in subShader.Passes)
			{
				if (pass.GetType_() != SerializedPassType.Pass)
				{
					continue;
				}
				if (IsTransparentQueue(pass.State.Tags) || HasBlending(pass.State.RtBlend0) || pass.State.ZWrite.HasName || pass.State.ZWriteValue == ZWrite.Off)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool IsTransparentQueue(ISerializedTagMap tags)
	{
		foreach ((Utf8String key, Utf8String value) in tags.Tags)
		{
			string k = key.String;
			string v = value.String;
			if ((k == "Queue" && (v.StartsWith("Transparent", StringComparison.OrdinalIgnoreCase) || v.StartsWith("Overlay", StringComparison.OrdinalIgnoreCase)))
				|| (k == "RenderType" && v.Contains("Transparent", StringComparison.OrdinalIgnoreCase)))
			{
				return true;
			}
		}
		return false;
	}

	private static bool HasBlending(ISerializedShaderRTBlendState blend)
	{
		return blend.SourceBlend.HasName || blend.DestinationBlend.HasName
			|| blend.SourceBlend.GetValue<BlendMode>() != BlendMode.One
			|| blend.DestinationBlend.GetValue<BlendMode>() != BlendMode.Zero;
	}

	public static void Write(IShader shader, TextWriter writer)
	{
		ISerializedShader parsed = shader.ParsedForm;
		string? texture = FindProperty(parsed, TextureNames, SerializedPropertyType.Texture)
			?? parsed.PropInfo.Props.FirstOrDefault(p => p.GetType_() == SerializedPropertyType.Texture && p.DefTexture.TexDim == 2)?.Name.String;
		string? color = FindProperty(parsed, ColorNames, SerializedPropertyType.Color)
			?? FindProperty(parsed, ColorNames, SerializedPropertyType.Vector);

		bool wroteSubShader = false;
		foreach (ISerializedSubShader subShader in parsed.SubShaders)
		{
			StringBuilder passes = new();
			foreach (ISerializedPass pass in subShader.Passes)
			{
				if (pass.GetType_() != SerializedPassType.Pass || IsSkipped(pass))
				{
					continue;
				}
				WritePass(passes, pass, texture, color);
			}
			if (passes.Length == 0)
			{
				continue;
			}
			wroteSubShader = true;
			writer.Write("\tSubShader {\n");
			WriteTags(writer, subShader.Tags, "\t\t");
			if (subShader.LOD != 0)
			{
				writer.Write($"\t\tLOD {subShader.LOD}\n");
			}
			writer.Write(passes.ToString());
			writer.Write("\t}\n");
		}
		if (!wroteSubShader)
		{
			// Only special passes: a minimal transparent pass
			writer.Write("\tSubShader {\n\t\tTags { \"Queue\"=\"Transparent\" \"RenderType\"=\"Transparent\" }\n");
			StringBuilder pass = new();
			pass.Append("\t\tPass {\n\t\t\tBlend SrcAlpha OneMinusSrcAlpha\n\t\t\tZWrite Off\n");
			WriteProgram(pass, texture, color);
			pass.Append("\t\t}\n");
			writer.Write(pass.ToString());
			writer.Write("\t}\n");
		}
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

	private static string? FindProperty(ISerializedShader parsed, string[] names, SerializedPropertyType type)
	{
		foreach (string name in names)
		{
			if (parsed.PropInfo.Props.Any(p => p.Name.String == name && p.GetType_() == type))
			{
				return name;
			}
		}
		return null;
	}

	private static void WriteTags(TextWriter writer, ISerializedTagMap tags, string indent)
	{
		if (tags.Tags.Count == 0)
		{
			return;
		}
		writer.Write(indent);
		writer.Write("Tags {");
		foreach ((Utf8String key, Utf8String value) in tags.Tags)
		{
			writer.Write($" \"{key}\"=\"{value}\"");
		}
		writer.Write(" }\n");
	}

	private static void WritePass(StringBuilder sb, ISerializedPass pass, string? texture, string? color)
	{
		ISerializedShaderState state = pass.State;
		sb.Append("\t\tPass {\n");
		if (!string.IsNullOrEmpty(state.Name_R.String))
		{
			sb.Append($"\t\t\tName \"{state.Name_R}\"\n");
		}
		using (StringWriter tags = new())
		{
			WriteTags(tags, state.Tags, "\t\t\t");
			sb.Append(tags.ToString());
		}

		ISerializedShaderRTBlendState blend = state.RtBlend0;
		if (HasBlending(blend))
		{
			sb.Append($"\t\t\tBlend {blend.SourceBlend.GetNameOrEnumString<BlendMode>()} {blend.DestinationBlend.GetNameOrEnumString<BlendMode>()}");
			string srcAlpha = blend.SourceBlendAlpha.GetNameOrEnumString<BlendMode>();
			string dstAlpha = blend.DestinationBlendAlpha.GetNameOrEnumString<BlendMode>();
			if (srcAlpha != blend.SourceBlend.GetNameOrEnumString<BlendMode>() || dstAlpha != blend.DestinationBlend.GetNameOrEnumString<BlendMode>())
			{
				sb.Append($", {srcAlpha} {dstAlpha}");
			}
			sb.Append('\n');
		}
		if (blend.BlendOp.HasName || (blend.BlendOp.GetValue<BlendOp>() is not BlendOp.Add and < BlendOp.LogicalClear))
		{
			sb.Append($"\t\t\tBlendOp {blend.BlendOp.GetNameOrEnumString<BlendOp>()}\n");
		}
		if (blend.ColMask.HasName)
		{
			sb.Append($"\t\t\tColorMask [{blend.ColMask.Name}]\n");
		}
		else if ((int)blend.ColMask.Value != 15)
		{
			sb.Append($"\t\t\tColorMask {ColorMask((int)blend.ColMask.Value)}\n");
		}

		sb.Append($"\t\t\tZWrite {state.ZWrite.GetNameOrEnumString<ZWrite>()}\n");
		sb.Append($"\t\t\tZTest {(state.ZTest.HasName ? $"[{state.ZTest.Name}]" : ZTestName(state.ZTestValue))}\n");
		sb.Append($"\t\t\tCull {state.Culling.GetNameOrEnumString<CullMode>()}\n");
		if (state.OffsetFactor.HasName || state.OffsetUnits.HasName || state.OffsetFactor.Value != 0 || state.OffsetUnits.Value != 0)
		{
			sb.Append($"\t\t\tOffset {state.OffsetFactor.GetNameOrFloatString()}, {state.OffsetUnits.GetNameOrFloatString()}\n");
		}
		if (!state.StencilIsDefault)
		{
			WriteStencil(sb, state);
		}
		WriteProgram(sb, texture, color);
		sb.Append("\t\t}\n");
	}

	private static string ZTestName(ZTest value) => value switch
	{
		ZTest.Disabled => "Always",
		_ => value.ToString(),
	};

	private static string StencilCompName(ISerializedShaderFloatValue value)
	{
		if (value.HasName)
		{
			return $"[{value.Name}]";
		}
		StencilComp comp = value.GetValue<StencilComp>();
		return comp is StencilComp.Disabled ? "Always" : comp.ToString();
	}

	private static string StencilOpName(ISerializedShaderFloatValue value)
	{
		if (value.HasName)
		{
			return $"[{value.Name}]";
		}
		return value.GetValue<StencilOp>() switch
		{
			StencilOp.IncrementSaturate => "IncrSat",
			StencilOp.DecrementSaturate => "DecrSat",
			StencilOp.IncrementWrap => "IncrWrap",
			StencilOp.DecrementWrap => "DecrWrap",
			StencilOp op => op.ToString(),
		};
	}

	private static void WriteStencil(StringBuilder sb, ISerializedShaderState state)
	{
		ISerializedStencilOp op = state.StencilOp;
		sb.Append("\t\t\tStencil {\n");
		sb.Append($"\t\t\t\tRef {state.StencilRef.GetNameOrFloatString()}\n");
		sb.Append($"\t\t\t\tReadMask {state.StencilReadMask.GetNameOrFloatString()}\n");
		sb.Append($"\t\t\t\tWriteMask {state.StencilWriteMask.GetNameOrFloatString()}\n");
		sb.Append($"\t\t\t\tComp {StencilCompName(op.Comp)}\n");
		sb.Append($"\t\t\t\tPass {StencilOpName(op.Pass)}\n");
		sb.Append($"\t\t\t\tFail {StencilOpName(op.Fail)}\n");
		sb.Append($"\t\t\t\tZFail {StencilOpName(op.ZFail)}\n");
		sb.Append("\t\t\t}\n");
	}

	private static string ColorMask(int mask)
	{
		if (mask == 0)
		{
			return "0";
		}
		StringBuilder sb = new();
		if ((mask & (int)ColorWriteMask.Red) != 0)
		{
			sb.Append('R');
		}
		if ((mask & (int)ColorWriteMask.Green) != 0)
		{
			sb.Append('G');
		}
		if ((mask & (int)ColorWriteMask.Blue) != 0)
		{
			sb.Append('B');
		}
		if ((mask & (int)ColorWriteMask.Alpha) != 0)
		{
			sb.Append('A');
		}
		return sb.ToString();
	}

	private static void WriteProgram(StringBuilder sb, string? texture, string? color)
	{
		sb.Append("\t\t\tCGPROGRAM\n");
		sb.Append("\t\t\t#pragma vertex vert\n");
		sb.Append("\t\t\t#pragma fragment frag\n");
		sb.Append("\t\t\t#include \"UnityCG.cginc\"\n");
		if (texture is not null)
		{
			sb.Append($"\t\t\tsampler2D {texture};\n\t\t\tfloat4 {texture}_ST;\n");
		}
		if (color is not null)
		{
			sb.Append($"\t\t\tfloat4 {color};\n");
		}
		sb.Append("\t\t\tstruct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };\n");
		sb.Append("\t\t\tstruct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };\n");
		sb.Append("\t\t\tv2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); ");
		sb.Append(texture is not null ? $"o.uv = TRANSFORM_TEX(v.uv, {texture}); " : "o.uv = v.uv; ");
		sb.Append("o.color = v.color; return o; }\n");
		sb.Append("\t\t\tfloat4 frag(v2f i) : SV_Target { float4 c = i.color; ");
		if (texture is not null)
		{
			sb.Append($"c *= tex2D({texture}, i.uv); ");
		}
		if (color is not null)
		{
			sb.Append($"c *= {color}; ");
		}
		sb.Append("return c; }\n");
		sb.Append("\t\t\tENDCG\n");
	}
}
