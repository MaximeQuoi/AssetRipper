using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Generics;
using AssetRipper.Export.Modules.Textures;
using AssetRipper.Numerics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_137;
using AssetRipper.SourceGenerated.Classes.ClassID_18;
using AssetRipper.SourceGenerated.Classes.ClassID_2;
using AssetRipper.SourceGenerated.Classes.ClassID_21;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_28;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_43;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.PPtr_Material;
using AssetRipper.SourceGenerated.Subclasses.SubMesh;
using AssetRipper.SourceGenerated.Subclasses.UnityTexEnv;
using SharpGLTF.Geometry;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using System.Buffers;
using System.Numerics;

namespace AssetRipper.Export.Modules.Models;

public static class GlbLevelBuilder
{
	public static SceneBuilder Build(IEnumerable<IUnityObjectBase> assets, bool isScene)
	{
		SceneBuilder sceneBuilder = new();
		BuildParameters parameters = new BuildParameters(isScene);

		HashSet<IUnityObjectBase> exportedAssets = new();

		foreach (IUnityObjectBase asset in assets)
		{
			if (!exportedAssets.Contains(asset) && asset is IGameObject or IComponent)
			{
				IGameObject root = GetRoot(asset);

				AddGameObjectToScene(sceneBuilder, parameters, null, Transformation.Identity, Transformation.Identity, root.GetTransform());
				AddSkinnedMeshesToScene(sceneBuilder, parameters);
				GlbAnimationBuilder.AddAnimations(root, parameters.Nodes, parameters.AnimationNames, parameters.MakeUniqueNodeName);

				foreach (IEditorExtension exportedAsset in root.FetchHierarchy())
				{
					exportedAssets.Add(exportedAsset);
				}
			}
		}

		return sceneBuilder;
	}

	private static void AddGameObjectToScene(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder? parentNode, Transformation parentGlobalTransform, Transformation parentGlobalInverseTransform, ITransform transform)
	{
		IGameObject? gameObject = transform.GameObject_C4P;
		if (gameObject is null)
		{
			return;
		}

		Transformation localTransform = transform.ToTransformation();
		Transformation localInverseTransform = transform.ToInverseTransformation();
		Transformation globalTransform = localTransform * parentGlobalTransform;
		Transformation globalInverseTransform = parentGlobalInverseTransform * localInverseTransform;

		//Node names must be unique in a tree for SharpGLTF to accept a skin over it
		string nodeName = parameters.MakeUniqueNodeName(gameObject.Name);
		NodeBuilder node = parentNode is null ? new NodeBuilder(nodeName) : parentNode.CreateNode(nodeName);
		if (parentNode is not null || parameters.IsScene)
		{
			node.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
				transform.LocalScale_C4.CastToStruct(),//Scaling is the same in both coordinate systems
				GlbCoordinateConversion.ToGltfQuaternionConvert(transform.LocalRotation_C4),
				GlbCoordinateConversion.ToGltfVector3Convert(transform.LocalPosition_C4));
		}
		sceneBuilder.AddNode(node);
		parameters.Nodes[transform] = node;

		if (gameObject.TryGetComponent(out ISkinnedMeshRenderer? skinnedMeshRenderer))
		{
			//Bones can be anywhere in the hierarchy, so skinned meshes are added once every node exists.
			parameters.SkinnedRenderers.Add((skinnedMeshRenderer, node));
		}
		else if (gameObject.TryGetComponent(out IMeshFilter? meshFilter)
			&& meshFilter.TryGetMesh(out IMesh? mesh)
			&& mesh.IsSet()
			&& parameters.TryGetOrMakeMeshData(mesh, out MeshData meshData))
		{
			if (gameObject.TryGetComponent(out IRenderer? meshRenderer))
			{
				if (ReferencesDynamicMesh(meshRenderer))
				{

					AddDynamicMeshToScene(sceneBuilder, parameters, node, mesh, meshData, new MaterialList(meshRenderer));
				}
				else
				{
					int[] subsetIndices = GetSubsetIndices(meshRenderer);
					AddStaticMeshToScene(sceneBuilder, parameters, node, mesh, meshData, subsetIndices, new MaterialList(meshRenderer), globalTransform, globalInverseTransform);
				}
			}
		}

		foreach (ITransform childTransform in transform.Children_C4P.WhereNotNull())
		{
			AddGameObjectToScene(sceneBuilder, parameters, node, localTransform * parentGlobalTransform, parentGlobalInverseTransform * localInverseTransform, childTransform);
		}
	}

	private static void AddSkinnedMeshesToScene(SceneBuilder sceneBuilder, BuildParameters parameters)
	{
		foreach ((ISkinnedMeshRenderer renderer, NodeBuilder node) in parameters.SkinnedRenderers)
		{
			if (renderer.MeshP is not IMesh mesh
				|| !mesh.IsSet()
				|| !parameters.TryGetOrMakeMeshData(mesh, out MeshData meshData))
			{
				continue;
			}

			MaterialList materialList = new MaterialList(renderer);
			if (TryGetJoints(renderer, parameters, meshData, out (NodeBuilder, Matrix4x4)[]? joints))
			{
				IMeshBuilder<MaterialBuilder> meshBuilder = BuildAllSubMeshes(parameters, mesh, meshData, materialList);
				sceneBuilder.AddSkinnedMesh(meshBuilder, joints);
			}
			else
			{
				//No usable skeleton: the mesh in its bind pose, without skin attributes
				MeshData rigidMeshData = meshData with { Skin = null };
				IMeshBuilder<MaterialBuilder> meshBuilder = BuildAllSubMeshes(parameters, mesh, rigidMeshData, materialList);
				sceneBuilder.AddRigidMesh(meshBuilder, node);
			}
		}
		parameters.SkinnedRenderers.Clear();
	}

	private static bool TryGetJoints(ISkinnedMeshRenderer renderer, BuildParameters parameters, MeshData meshData, [NotNullWhen(true)] out (NodeBuilder, Matrix4x4)[]? joints)
	{
		joints = null;
		Matrix4x4[]? bindPoses = meshData.BindPose;
		int boneCount = renderer.Bones.Count;
		if (!meshData.HasSkin || bindPoses is null || boneCount == 0 || bindPoses.Length < boneCount)
		{
			return false;
		}

		int maxIndex = 0;
		foreach (BoneWeight4 weight in meshData.Skin)
		{
			maxIndex = Math.Max(maxIndex, Math.Max(Math.Max(weight.Index0, weight.Index1), Math.Max(weight.Index2, weight.Index3)));
		}
		if (maxIndex >= boneCount)
		{
			return false;
		}

		joints = new (NodeBuilder, Matrix4x4)[boneCount];
		for (int i = 0; i < boneCount; i++)
		{
			if (renderer.Bones[i].TryGetAsset(renderer.Collection) is not ITransform bone
				|| !parameters.Nodes.TryGetValue(bone, out NodeBuilder? boneNode))
			{
				joints = null;
				return false;
			}
			joints[i] = (boneNode, ToGltfMatrix(bindPoses[i]));
		}
		return true;
	}

	/// <summary>
	/// Converts a Unity matrix (as cast by <see cref="Matrix4x4fExtensions.CastToStruct"/>, row-vector convention) to glTF space.
	/// </summary>
	private static Matrix4x4 ToGltfMatrix(Matrix4x4 unityMatrix)
	{
		Matrix4x4 flip = Matrix4x4.CreateScale(GlbCoordinateConversion.CoordinateSpaceConversionScale);
		return flip * unityMatrix * flip;
	}

	private static IMeshBuilder<MaterialBuilder> BuildAllSubMeshes(BuildParameters parameters, IMesh mesh, MeshData meshData, MaterialList materialList)
	{
		AccessListBase<ISubMesh> subMeshes = mesh.SubMeshes;
		(ISubMesh, MaterialBuilder)[] subMeshArray = new (ISubMesh, MaterialBuilder)[subMeshes.Count];
		for (int i = 0; i < subMeshes.Count; i++)
		{
			subMeshArray[i] = (subMeshes[i], parameters.GetOrMakeMaterial(materialList[i]));
		}
		return GlbSubMeshBuilder.BuildSubMeshes(new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshArray), mesh.Is16BitIndices(), meshData, Transformation.Identity, Transformation.Identity);
	}

	private static void AddDynamicMeshToScene(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder node, IMesh mesh, MeshData meshData, MaterialList materialList)
	{
		AccessListBase<ISubMesh> subMeshes = mesh.SubMeshes;
		(ISubMesh, MaterialBuilder)[] subMeshArray = ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Rent(subMeshes.Count);
		for (int i = 0; i < subMeshes.Count; i++)
		{
			MaterialBuilder materialBuilder = parameters.GetOrMakeMaterial(materialList[i]);
			subMeshArray[i] = (subMeshes[i], materialBuilder);
		}
		ArraySegment<(ISubMesh, MaterialBuilder)> arraySegment = new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshArray, 0, subMeshes.Count);
		IMeshBuilder<MaterialBuilder> subMeshBuilder = GlbSubMeshBuilder.BuildSubMeshes(arraySegment, mesh.Is16BitIndices(), meshData, Transformation.Identity, Transformation.Identity);
		sceneBuilder.AddRigidMesh(subMeshBuilder, node);
		ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Return(subMeshArray);
	}

	private static void AddStaticMeshToScene(SceneBuilder sceneBuilder, BuildParameters parameters, NodeBuilder node, IMesh mesh, MeshData meshData, int[] subsetIndices, MaterialList materialList, Transformation globalTransform, Transformation globalInverseTransform)
	{
		(ISubMesh, MaterialBuilder)[] subMeshArray = ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Rent(subsetIndices.Length);
		AccessListBase<ISubMesh> subMeshes = mesh.SubMeshes;
		for (int i = 0; i < subsetIndices.Length; i++)
		{
			ISubMesh subMesh = subMeshes[subsetIndices[i]];
			MaterialBuilder materialBuilder = parameters.GetOrMakeMaterial(materialList[i]);
			subMeshArray[i] = (subMesh, materialBuilder);
		}
		ArraySegment<(ISubMesh, MaterialBuilder)> arraySegment = new ArraySegment<(ISubMesh, MaterialBuilder)>(subMeshArray, 0, subsetIndices.Length);
		IMeshBuilder<MaterialBuilder> subMeshBuilder = GlbSubMeshBuilder.BuildSubMeshes(arraySegment, mesh.Is16BitIndices(), meshData, globalInverseTransform, globalTransform);
		sceneBuilder.AddRigidMesh(subMeshBuilder, node);
		ArrayPool<(ISubMesh, MaterialBuilder)>.Shared.Return(subMeshArray);
	}

	private static IGameObject GetRoot(IUnityObjectBase asset)
	{
		return asset switch
		{
			IGameObject gameObject => gameObject.GetRoot(),
			IComponent component => component.GameObject_C2P!.GetRoot(),
			_ => throw new InvalidOperationException()
		};
	}

	private static bool ReferencesDynamicMesh(IRenderer renderer)
	{
		return renderer.Has_StaticBatchInfo_C25() && renderer.StaticBatchInfo_C25.SubMeshCount == 0
			|| renderer.Has_SubsetIndices_C25() && renderer.SubsetIndices_C25.Count == 0;
	}

	private static int[] GetSubsetIndices(IRenderer renderer)
	{
		AccessListBase<IPPtr_Material> materials = renderer.Materials_C25;
		if (renderer.Has_SubsetIndices_C25())
		{
			return renderer.SubsetIndices_C25.Select(i => (int)i).ToArray();
		}
		else if (renderer.Has_StaticBatchInfo_C25())
		{
			return Enumerable.Range(renderer.StaticBatchInfo_C25.FirstSubMesh, renderer.StaticBatchInfo_C25.SubMeshCount).ToArray();
		}
		else
		{
			return Array.Empty<int>();
		}
	}

	private readonly record struct BuildParameters(
		MaterialBuilder DefaultMaterial,
		Dictionary<ITexture2D, MemoryImage> ImageCache,
		Dictionary<IMaterial, MaterialBuilder> MaterialCache,
		Dictionary<IMesh, MeshData> MeshCache,
		Dictionary<ITransform, NodeBuilder> Nodes,
		List<(ISkinnedMeshRenderer, NodeBuilder)> SkinnedRenderers,
		Dictionary<string, int> NodeNames,
		HashSet<string> AnimationNames,
		bool IsScene)
	{
		public BuildParameters(bool isScene) : this(new MaterialBuilder("DefaultMaterial"), new(), new(), new(), new(), new(), new(), new(), isScene) { }

		public string MakeUniqueNodeName(string name)
		{
			if (NodeNames.TryAdd(name, 1))
			{
				return name;
			}
			while (true)
			{
				int count = NodeNames[name]++;
				string candidate = $"{name}_{count}";
				if (NodeNames.TryAdd(candidate, 1))
				{
					return candidate;
				}
			}
		}
		public bool TryGetOrMakeMeshData(IMesh mesh, out MeshData meshData)
		{
			if (MeshCache.TryGetValue(mesh, out meshData))
			{
				return true;
			}
			else if (MeshData.TryMakeFromMesh(mesh, out meshData))
			{
				MeshCache.Add(mesh, meshData);
				return true;
			}
			return false;
		}

		public MaterialBuilder GetOrMakeMaterial(IMaterial? material)
		{
			if (material is null)
			{
				return DefaultMaterial;
			}
			if (!MaterialCache.TryGetValue(material, out MaterialBuilder? materialBuilder))
			{
				materialBuilder = MakeMaterialBuilder(material);
				MaterialCache.Add(material, materialBuilder);
			}
			return materialBuilder;
		}

		public bool TryGetOrMakeImage(ITexture2D texture, out MemoryImage image)
		{
			if (!ImageCache.TryGetValue(texture, out image))
			{
				if (TextureConverter.TryConvertToBitmap(texture, out DirectBitmap bitmap))
				{
					using MemoryStream memoryStream = new();
					bitmap.SaveAsPng(memoryStream);
					image = new MemoryImage(memoryStream.ToArray());
					ImageCache.Add(texture, image);
					return true;
				}
				return false;
			}
			else
			{
				return true;
			}
		}

		private MaterialBuilder MakeMaterialBuilder(IMaterial material)
		{
			MaterialBuilder materialBuilder = new MaterialBuilder(material.Name);
			GetTextures(material, out ITexture2D? mainTexture, out ITexture2D? normalTexture);
			if (mainTexture is not null && TryGetOrMakeImage(mainTexture, out MemoryImage mainImage))
			{
				materialBuilder.WithBaseColor(mainImage);
			}
			if (normalTexture is not null && TryGetOrMakeImage(normalTexture, out MemoryImage normalImage))
			{
				materialBuilder.WithNormal(normalImage);
			}
			return materialBuilder;
		}

		private static void GetTextures(IMaterial material, out ITexture2D? mainTexture, out ITexture2D? normalTexture)
		{
			mainTexture = null;
			normalTexture = null;
			ITexture2D? mainReplacement = null;
			foreach ((Utf8String utf8Name, IUnityTexEnv textureParameter) in material.GetTextureProperties())
			{
				string name = utf8Name.String;
				if (IsMainTexture(name))
				{
					mainTexture ??= textureParameter.Texture.TryGetAsset(material.Collection) as ITexture2D;
				}
				else if (IsNormalTexture(name))
				{
					normalTexture ??= textureParameter.Texture.TryGetAsset(material.Collection) as ITexture2D;
				}
				else
				{
					mainReplacement ??= textureParameter.Texture.TryGetAsset(material.Collection) as ITexture2D;
				}
			}
			mainTexture ??= mainReplacement;
		}

		private static bool IsMainTexture(string textureName)
		{
			return textureName is "_MainTex" or "texture" or "Texture" or "_Texture" or "_BaseMap" or "_BaseColorMap" or "_Albedo" or "_AlbedoMap";
		}

		private static bool IsNormalTexture(string textureName)
		{
			return textureName is "_Normal" or "Normal" or "normal" or "_BumpMap" or "_NormalMap";
		}
	}

	private readonly struct MaterialList
	{
		private readonly AccessListBase<IPPtr_Material> materials;
		private readonly AssetCollection file;

		private MaterialList(AccessListBase<IPPtr_Material> materials, AssetCollection file)
		{
			this.materials = materials;
			this.file = file;
		}

		public MaterialList(IRenderer renderer) : this(renderer.Materials_C25, renderer.Collection) { }

		public int Count => materials.Count;

		public IMaterial? this[int index]
		{
			get
			{
				if (index >= materials.Count)
				{
					return null;
				}
				return materials[index].TryGetAsset(file);
			}
		}
	}
}
