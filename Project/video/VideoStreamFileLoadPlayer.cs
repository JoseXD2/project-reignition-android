using Godot;

namespace Project.Interface.Menus;

[Tool]
public partial class VideoStreamFileLoadPlayer : VideoStreamPlayer
{
	[Export(PropertyHint.File)]
	private string videoFilePath;
	public void SetVideoFilePath(string path) => videoFilePath = path;

	public override void _Ready()
	{
		if (Engine.IsEditorHint())
			return;

		ReloadVideoPath();
	}

	public void ReloadVideoPath()
	{
		if (string.IsNullOrEmpty(videoFilePath))
			return;

		string path = ResolveVideoPath(videoFilePath);
		if (path == null)
		{
			GD.PushWarning($"Couldn't load video file {videoFilePath}!");
			return;
		}

		Stream = ResourceLoader.Load<VideoStream>(path, "VideoStream");
	}

	public static string ResolveVideoPath(string path)
	{
		if (string.IsNullOrEmpty(path))
			return null;

		string ogvPath = path.GetBaseName() + ".ogv";
		string[] candidates = OS.HasFeature("mobile") ? [ogvPath, path] : [path, ogvPath];
		foreach (string candidate in candidates)
		{
			if (ResourceLoader.Exists(candidate, "VideoStream"))
				return candidate;
		}

		return null;
	}
}