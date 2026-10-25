using Godot;
using Project.Core;

namespace Project.Interface;

public partial class Boot : Node
{
	private const string LoadingImagePath = "res://interface/boot/android_loading.png";
	private const string LogoVideoPath = "res://video/boot/logo.mp4";

	public override void _Ready()
	{
		ShowLoadingScreen();
		TransitionManager.Instance.LoadCommonResources();
	}

	private void ShowLoadingScreen()
	{
		if (!OS.HasFeature("mobile") || Menus.VideoStreamFileLoadPlayer.ResolveVideoPath(LogoVideoPath) != null)
			return;

		Texture2D texture = ResourceLoader.Load<Texture2D>(LoadingImagePath);
		if (texture == null)
			return;

		CanvasLayer layer = new() { Layer = 99 };
		ColorRect background = new() { Color = Colors.Black, MouseFilter = Control.MouseFilterEnum.Ignore };
		background.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		TextureRect image = new()
		{
			Texture = texture,
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		image.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		layer.AddChild(background);
		layer.AddChild(image);
		AddChild(layer);
	}

	[Export] private AnimationPlayer animator;

	public override void _Process(double _delta)
	{
		if (!animator.IsPlaying())
			return;

		if (Input.IsActionJustPressed("sys_select"))
			AdvanceVideo();
	}

	private void StartTitleTransition()
	{
		TransitionManager.QueueSceneChange("res://interface/menu/Menu.tscn");
		TransitionManager.StartTransition(new()
		{
			inSpeed = .1f,
			outSpeed = .5f,
			color = Colors.Black
		});
	}

	private void AdvanceVideo() => animator.Advance(animator.CurrentAnimationLength);
}
