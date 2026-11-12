using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.Primitives;
using AssetRipper.SourceGenerated.Classes.ClassID_1;
using AssetRipper.SourceGenerated.Classes.ClassID_111;
using AssetRipper.SourceGenerated.Classes.ClassID_221;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Classes.ClassID_74;
using AssetRipper.SourceGenerated.Classes.ClassID_91;
using AssetRipper.SourceGenerated.Classes.ClassID_93;
using AssetRipper.SourceGenerated.Classes.ClassID_90;
using AssetRipper.SourceGenerated.Classes.ClassID_95;
using AssetRipper.SourceGenerated.Subclasses.AvatarConstant;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.AnimationClipOverride;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Quaternionf;
using AssetRipper.SourceGenerated.Subclasses.Keyframe_Vector3f;
using AssetRipper.SourceGenerated.Subclasses.QuaternionCurve;
using AssetRipper.SourceGenerated.Subclasses.Vector3Curve;
using SharpGLTF.Scenes;
using System.Numerics;

namespace AssetRipper.Export.Modules.Models;

/// <summary>
/// Writes the transform curves of animation clips as glTF animations.
/// </summary>
/// <remarks>
/// Clips are read after the editor format processing, which decodes the streamed, dense and constant clips
/// into position, rotation, euler and scale curves with transform paths. The curves are sampled
/// (Hermite interpolation of the Unity keyframes) and written as linear glTF tracks.
/// Humanoid (muscle) curves are not written.
/// </remarks>
internal static class GlbAnimationBuilder
{
	private const float MaxSampleRate = 60;
	private const float DefaultSampleRate = 30;

	/// <summary>
	/// Adds the clips used by the animators and animations of a hierarchy.
	/// When an animator references no clip (an imported model), the clips of its own file are used.
	/// </summary>
	public static void AddAnimations(IGameObject root, IReadOnlyDictionary<ITransform, NodeBuilder> nodes, HashSet<string> trackNames, Func<string, string> makeNodeName)
	{
		foreach (IGameObject gameObject in root.FetchHierarchy().OfType<IGameObject>())
		{
			ITransform? transform = gameObject.TryGetComponent(out ITransform? t) ? t : null;
			if (transform is null)
			{
				continue;
			}

			List<IAnimationClip> clips = new();
			bool hasAnimator = false;
			IAvatar? avatar = null;
			if (gameObject.TryGetComponent(out IAnimator? animator))
			{
				hasAnimator = true;
				avatar = animator.AvatarP;
				IUnityObjectBase? controller = animator.Has_Controller_PPtr_RuntimeAnimatorController_5() ? animator.Controller_PPtr_RuntimeAnimatorController_5P
					: animator.Has_Controller_PPtr_RuntimeAnimatorController_4_3() ? animator.Controller_PPtr_RuntimeAnimatorController_4_3P
					: animator.Has_Controller_PPtr_AnimatorController_4() ? animator.Controller_PPtr_AnimatorController_4P
					: null;
				AddControllerClips(controller, clips, 0);
			}
			if (gameObject.TryGetComponent(out IAnimation? animation))
			{
				hasAnimator = true;
				foreach (var clipPointer in animation.Animations)
				{
					if (clipPointer.TryGetAsset(animation.Collection) is IAnimationClip clip)
					{
						clips.Add(clip);
					}
				}
			}
			if (!hasAnimator)
			{
				continue;
			}
			bool fromCollection = clips.Count == 0;
			if (fromCollection)
			{
				clips.AddRange(gameObject.Collection.OfType<IAnimationClip>());
			}

			if (clips.Count == 0 || !nodes.TryGetValue(transform, out NodeBuilder? animatorNode))
			{
				continue;
			}

			Dictionary<string, NodeBuilder> pathNodes = new();
			AddPaths(transform, string.Empty, nodes, pathNodes);
			if (avatar is not null)
			{
				AddAvatarSkeleton(avatar, pathNodes, makeNodeName);
			}

			IEnumerable<IAnimationClip> selected = fromCollection
				? SelectCollectionClips(clips, gameObject.Name, pathNodes)
				: clips.Distinct();
			foreach (IAnimationClip clip in selected)
			{
				AddClip(clip, pathNodes, trackNames);
			}
		}
	}

	/// <summary>
	/// The clips of a file for a model that references none (an imported model, often <c>model@take</c>).
	/// A file can hold the clips of several models with similar skeletons: for each clip name, the clip that
	/// fits the skeleton best is kept, and for <c>model@take</c> only the take's clip.
	/// </summary>
	private static IEnumerable<IAnimationClip> SelectCollectionClips(List<IAnimationClip> clips, string modelName, Dictionary<string, NodeBuilder> pathNodes)
	{
		List<(IAnimationClip Clip, int Resolved, int Total)> scored = new();
		foreach (IAnimationClip clip in clips.Distinct())
		{
			List<string> paths = GetCurvePaths(clip).Distinct().ToList();
			int resolved = paths.Count(pathNodes.ContainsKey);
			//Most of the clip must fit the skeleton
			if (paths.Count > 0 && resolved >= paths.Count * 0.9)
			{
				scored.Add((clip, resolved, paths.Count));
			}
		}

		int at = modelName.LastIndexOf('@');
		if (at >= 0)
		{
			string take = modelName[(at + 1)..];
			if (scored.Any(s => s.Clip.Name == take))
			{
				scored.RemoveAll(s => s.Clip.Name != take);
			}
		}

		return scored
			.GroupBy(s => s.Clip.Name)
			.Select(group => group
				.OrderByDescending(s => s.Resolved == s.Total)
				.ThenByDescending(s => s.Resolved)
				.First().Clip);
	}

	private static IEnumerable<string> GetCurvePaths(IAnimationClip clip)
	{
		foreach (IQuaternionCurve curve in clip.RotationCurves_C74)
		{
			yield return curve.Path.String;
		}
		foreach (IVector3Curve curve in clip.PositionCurves_C74)
		{
			yield return curve.Path.String;
		}
		foreach (IVector3Curve curve in clip.ScaleCurves_C74)
		{
			yield return curve.Path.String;
		}
		if (clip.Has_EulerCurves_C74())
		{
			foreach (IVector3Curve curve in clip.EulerCurves_C74)
			{
				yield return curve.Path.String;
			}
		}
	}

	private static void AddPaths(ITransform transform, string path, IReadOnlyDictionary<ITransform, NodeBuilder> nodes, Dictionary<string, NodeBuilder> pathNodes)
	{
		if (nodes.TryGetValue(transform, out NodeBuilder? node))
		{
			pathNodes.TryAdd(path, node);
		}
		foreach (ITransform child in transform.Children_C4P.WhereNotNull())
		{
			string name = child.GameObject_C4P?.Name ?? string.Empty;
			AddPaths(child, path.Length == 0 ? name : $"{path}/{name}", nodes, pathNodes);
		}
	}

	/// <summary>
	/// Imported models are often stripped of their bones (only the animator remains).
	/// The avatar keeps the skeleton: its missing bones are added as nodes, in the avatar's pose.
	/// </summary>
	private static void AddAvatarSkeleton(IAvatar avatar, Dictionary<string, NodeBuilder> pathNodes, Func<string, string> makeNodeName)
	{
		Dictionary<uint, string> tos = new();
		foreach ((uint key, Utf8String value) in avatar.TOS)
		{
			tos.TryAdd(key, value.String);
		}

		IAvatarConstant constant = avatar.Avatar;
		if (constant.AvatarSkeleton is null || constant.AvatarSkeletonPose is null)
		{
			return;
		}
		var skeleton = constant.AvatarSkeleton.Data;
		var pose = constant.AvatarSkeletonPose.Data.X;
		int count = Math.Min(skeleton.Node.Count, skeleton.ID.Count);
		string?[] paths = new string?[count];
		for (int i = 0; i < count; i++)
		{
			paths[i] = tos.TryGetValue(skeleton.ID[i], out string? path) ? path : null;
		}
		//Parents come before their children in the avatar skeleton
		for (int i = 0; i < count; i++)
		{
			string? path = paths[i];
			int parentIndex = skeleton.Node[i].ParentId;
			if (path is null || pathNodes.ContainsKey(path) || parentIndex < 0 || parentIndex >= count)
			{
				continue;
			}
			string? parentPath = paths[parentIndex];
			if (parentPath is null || !pathNodes.TryGetValue(parentPath, out NodeBuilder? parent))
			{
				continue;
			}
			int slash = path.LastIndexOf('/');
			NodeBuilder node = parent.CreateNode(makeNodeName(slash < 0 ? path : path[(slash + 1)..]));
			if (i < pose.Count)
			{
				var xform = pose[i];
				Vector3 translation = xform.T3 is { } t3 ? new Vector3(t3.X, t3.Y, t3.Z) : xform.T4 is { } t4 ? new Vector3(t4.X, t4.Y, t4.Z) : Vector3.Zero;
				Vector3 scale = xform.S3 is { } s3 ? new Vector3(s3.X, s3.Y, s3.Z) : xform.S4 is { } s4 ? new Vector3(s4.X, s4.Y, s4.Z) : Vector3.One;
				node.LocalTransform = new SharpGLTF.Transforms.AffineTransform(
					scale,
					GlbCoordinateConversion.ToGltfQuaternionConvert(Quaternion.Normalize(new Quaternion(xform.Q.X, xform.Q.Y, xform.Q.Z, xform.Q.W))),
					GlbCoordinateConversion.ToGltfVector3Convert(translation));
			}
			pathNodes.Add(path, node);
		}
	}

	private static void AddControllerClips(IUnityObjectBase? controller, List<IAnimationClip> clips, int depth)
	{
		switch (controller)
		{
			case IAnimatorController animatorController:
				foreach (IAnimationClip? clip in animatorController.AnimationClipsP)
				{
					if (clip is not null)
					{
						clips.Add(clip);
					}
				}
				break;
			case IAnimatorOverrideController overrideController when depth < 8:
				foreach (IAnimationClipOverride clipOverride in overrideController.Clips)
				{
					IAnimationClip? clip = clipOverride.OverrideClip.TryGetAsset(overrideController.Collection)
						?? clipOverride.OriginalClip.TryGetAsset(overrideController.Collection);
					if (clip is not null)
					{
						clips.Add(clip);
					}
				}
				AddControllerClips(overrideController.ControllerP, clips, depth + 1);
				break;
		}
	}

	private static void AddClip(IAnimationClip clip, Dictionary<string, NodeBuilder> pathNodes, HashSet<string> trackNames)
	{
		string trackName = clip.Name;
		if (string.IsNullOrEmpty(trackName))
		{
			trackName = "Animation";
		}
		if (!trackNames.Add(trackName))
		{
			int index = 1;
			while (!trackNames.Add($"{trackName}_{index}"))
			{
				index++;
			}
			trackName = $"{trackName}_{index}";
		}

		float sampleRate = clip.SampleRate_C74 > 0 ? Math.Min(clip.SampleRate_C74, MaxSampleRate) : DefaultSampleRate;
		bool added = false;

		HashSet<string> rotatedPaths = new();
		foreach (IQuaternionCurve curve in clip.RotationCurves_C74)
		{
			if (pathNodes.TryGetValue(curve.Path.String, out NodeBuilder? node)
				&& curve.Curve.Curve.Count > 0)
			{
				rotatedPaths.Add(curve.Path.String);
				SharpGLTF.Animations.CurveBuilder<Quaternion> track = node.UseRotation(trackName);
				foreach ((float time, Quaternion value) in Sample(curve.Curve.Curve, sampleRate))
				{
					track.SetPoint(time, GlbCoordinateConversion.ToGltfQuaternionConvert(Quaternion.Normalize(value)));
				}
				added = true;
			}
		}

		if (clip.Has_EulerCurves_C74())
		{
			foreach (IVector3Curve curve in clip.EulerCurves_C74)
			{
				if (!rotatedPaths.Contains(curve.Path.String)
					&& pathNodes.TryGetValue(curve.Path.String, out NodeBuilder? node)
					&& curve.Curve.Curve.Count > 0)
				{
					SharpGLTF.Animations.CurveBuilder<Quaternion> track = node.UseRotation(trackName);
					foreach ((float time, Vector3 value) in Sample(curve.Curve.Curve, sampleRate))
					{
						track.SetPoint(time, GlbCoordinateConversion.ToGltfQuaternionConvert(EulerToQuaternion(value)));
					}
					added = true;
				}
			}
		}

		foreach (IVector3Curve curve in clip.PositionCurves_C74)
		{
			if (pathNodes.TryGetValue(curve.Path.String, out NodeBuilder? node)
				&& curve.Curve.Curve.Count > 0)
			{
				SharpGLTF.Animations.CurveBuilder<Vector3> track = node.UseTranslation(trackName);
				foreach ((float time, Vector3 value) in Sample(curve.Curve.Curve, sampleRate))
				{
					track.SetPoint(time, GlbCoordinateConversion.ToGltfVector3Convert(value));
				}
				added = true;
			}
		}

		foreach (IVector3Curve curve in clip.ScaleCurves_C74)
		{
			if (pathNodes.TryGetValue(curve.Path.String, out NodeBuilder? node)
				&& curve.Curve.Curve.Count > 0)
			{
				SharpGLTF.Animations.CurveBuilder<Vector3> track = node.UseScale(trackName);
				foreach ((float time, Vector3 value) in Sample(curve.Curve.Curve, sampleRate))
				{
					track.SetPoint(time, value);
				}
				added = true;
			}
		}

		if (!added)
		{
			trackNames.Remove(trackName);
		}
	}

	/// <summary>
	/// Unity applies euler angles in Z, X, Y order.
	/// </summary>
	private static Quaternion EulerToQuaternion(Vector3 eulerDegrees)
	{
		const float toRadians = MathF.PI / 180f;
		Quaternion x = Quaternion.CreateFromAxisAngle(Vector3.UnitX, eulerDegrees.X * toRadians);
		Quaternion y = Quaternion.CreateFromAxisAngle(Vector3.UnitY, eulerDegrees.Y * toRadians);
		Quaternion z = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, eulerDegrees.Z * toRadians);
		return Quaternion.Normalize(y * x * z);
	}

	private static IEnumerable<(float, Vector3)> Sample(AccessListBase<IKeyframe_Vector3f> keys, float sampleRate)
	{
		int count = keys.Count;
		float start = keys[0].Time;
		float end = keys[count - 1].Time;
		foreach (float time in SampleTimes(start, end, sampleRate))
		{
			int i = FindSegment(keys.Count, k => keys[k].Time, time);
			if (i >= count - 1)
			{
				yield return (time, keys[count - 1].Value.CastToStruct());
				continue;
			}
			IKeyframe_Vector3f a = keys[i];
			IKeyframe_Vector3f b = keys[i + 1];
			Vector3 value = new(
				Hermite(time, a.Time, b.Time, a.Value.X, b.Value.X, a.OutSlope.X, b.InSlope.X),
				Hermite(time, a.Time, b.Time, a.Value.Y, b.Value.Y, a.OutSlope.Y, b.InSlope.Y),
				Hermite(time, a.Time, b.Time, a.Value.Z, b.Value.Z, a.OutSlope.Z, b.InSlope.Z));
			yield return (time, value);
		}
	}

	private static IEnumerable<(float, Quaternion)> Sample(AccessListBase<IKeyframe_Quaternionf> keys, float sampleRate)
	{
		int count = keys.Count;
		float start = keys[0].Time;
		float end = keys[count - 1].Time;
		Quaternion previous = Quaternion.Identity;
		bool first = true;
		foreach (float time in SampleTimes(start, end, sampleRate))
		{
			int i = FindSegment(keys.Count, k => keys[k].Time, time);
			Quaternion value;
			if (i >= count - 1)
			{
				value = keys[count - 1].Value.CastToStruct();
			}
			else
			{
				IKeyframe_Quaternionf a = keys[i];
				IKeyframe_Quaternionf b = keys[i + 1];
				value = new(
					Hermite(time, a.Time, b.Time, a.Value.X, b.Value.X, a.OutSlope.X, b.InSlope.X),
					Hermite(time, a.Time, b.Time, a.Value.Y, b.Value.Y, a.OutSlope.Y, b.InSlope.Y),
					Hermite(time, a.Time, b.Time, a.Value.Z, b.Value.Z, a.OutSlope.Z, b.InSlope.Z),
					Hermite(time, a.Time, b.Time, a.Value.W, b.Value.W, a.OutSlope.W, b.InSlope.W));
			}
			value = Quaternion.Normalize(value);
			//Keep consecutive samples in the same hemisphere so that linear interpolation takes the short way
			if (!first && Quaternion.Dot(previous, value) < 0)
			{
				value = Quaternion.Negate(value);
			}
			previous = value;
			first = false;
			yield return (time, value);
		}
	}

	private static IEnumerable<float> SampleTimes(float start, float end, float sampleRate)
	{
		if (!(end > start))
		{
			yield return Math.Max(start, 0);
			yield break;
		}
		int frames = (int)MathF.Ceiling((end - start) * sampleRate);
		for (int frame = 0; frame < frames; frame++)
		{
			yield return Math.Max(start + frame / sampleRate, 0);
		}
		yield return Math.Max(end, 0);
	}

	/// <summary>
	/// The index of the last key at or before <paramref name="time"/>.
	/// </summary>
	private static int FindSegment(int count, Func<int, float> timeOf, float time)
	{
		int low = 0;
		int high = count - 1;
		while (low < high)
		{
			int middle = (low + high + 1) / 2;
			if (timeOf(middle) <= time)
			{
				low = middle;
			}
			else
			{
				high = middle - 1;
			}
		}
		return low;
	}

	private static float Hermite(float time, float time0, float time1, float value0, float value1, float outSlope0, float inSlope1)
	{
		float duration = time1 - time0;
		if (duration <= 0 || !float.IsFinite(outSlope0) || !float.IsFinite(inSlope1))
		{
			//Infinite slopes are constant (stepped) segments
			return time >= time1 ? value1 : value0;
		}
		float t = (time - time0) / duration;
		float t2 = t * t;
		float t3 = t2 * t;
		float m0 = outSlope0 * duration;
		float m1 = inSlope1 * duration;
		return (2 * t3 - 3 * t2 + 1) * value0 + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * value1 + (t3 - t2) * m1;
	}
}
