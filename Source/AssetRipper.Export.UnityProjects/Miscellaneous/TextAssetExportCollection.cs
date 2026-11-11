using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.SourceGenerated.Classes.ClassID_1031;
using AssetRipper.SourceGenerated.Classes.ClassID_49;
using System.Text.Json;

namespace AssetRipper.Export.UnityProjects.Miscellaneous;

public sealed class TextAssetExportCollection : AssetExportCollection<ITextAsset>
{
	private const string JsonExtension = "json";
	private const string TxtExtension = "txt";
	private const string BytesExtension = "bytes";

	private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		"cs", "dll", "exe", "bat", "cmd", "ps1", "vbs", "js", "msi", "scr", "com"
	};

	/// <summary>
	/// Extensions that the Unity editor imports as TextAsset. Any other extension (eg ".skel", ".atlas", ".lua")
	/// would be imported as a DefaultAsset, breaking every reference to the TextAsset.
	/// </summary>
	private static readonly HashSet<string> UnityTextAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		"txt", "html", "htm", "xml", "bytes", "json", "csv", "yaml", "fnt", "md",
	};

	public TextAssetExportCollection(TextAssetExporter assetExporter, ITextAsset asset) : base(assetExporter, asset)
	{
	}

	protected override string GetExportExtension(IUnityObjectBase asset)
	{
		string? extension = asset.GetBestExtension();
		if (extension is not null)
		{
			if (DangerousExtensions.Contains(extension))
			{
				return TxtExtension;
			}
			if (UnityTextAssetExtensions.Contains(extension))
			{
				return extension;
			}
			// Not a TextAsset extension for Unity: "x.skel" becomes "x.skel.bytes", "x.atlas" becomes "x.atlas.txt"
			return GetExtension((ITextAsset)asset);
		}
		return ((TextAssetExporter)AssetExporter).ExportMode switch
		{
			TextExportMode.Txt => TxtExtension,
			TextExportMode.Parse => GetExtension((ITextAsset)asset),
			_ => BytesExtension,
		};
	}

	private static string GetExtension(ITextAsset asset)
	{
		string text = asset.Script_C49.String;
		if (IsValidJson(text))
		{
			return JsonExtension;
		}
		else if (IsPlainText(text))
		{
			return TxtExtension;
		}
		else
		{
			return BytesExtension;
		}
	}

	private static bool IsValidJson(string text)
	{
		try
		{
			using JsonDocument? parsed = JsonDocument.Parse(text);
			return parsed != null;
		}
		catch
		{
			return false;
		}
	}

	private static bool IsPlainText(string text) => text.All(c => !char.IsControl(c) || char.IsWhiteSpace(c));

	protected override ITextScriptImporter CreateImporter(IExportContainer container)
	{
		ITextScriptImporter importer = TextScriptImporter.Create(container.File, container.ExportVersion);
		if (importer.Has_AssetBundleName_R() && Asset.AssetBundleName is not null)
		{
			importer.AssetBundleName_R = Asset.AssetBundleName;
		}
		return importer;
	}
}
