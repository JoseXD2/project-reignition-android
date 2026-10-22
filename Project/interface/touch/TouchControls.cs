using System.Collections.Generic;
using Godot;

namespace Project.Interface;

public partial class TouchControls : CanvasLayer
{
	/// <summary> Joypad device id used for the virtual gamepad. </summary>
	public const int VirtualDevice = 7;

	private const string TexturePath = "res://interface/touch/";
	private const string PromptPath = "res://resource/input sprites/navigation/";
	private const float StickRadius = 120f;
	private const float IdleOpacity = 0.75f;

	private sealed class TouchButton
	{
		public Control Root;
		public TextureRect Frame;
		public JoyButton Button = JoyButton.Invalid;
		public JoyAxis Trigger = JoyAxis.Invalid;
		public int TouchIndex = -1;
	}

	private Control root;
	private TextureRect stickBase;
	private TextureRect stickKnob;
	private Vector2 stickDefaultCenter;
	private Vector2 stickCenter;
	private int stickTouchIndex = -1;
	private Vector2 lastStickValue;
	private readonly List<TouchButton> buttons = [];

	private Texture2D frameTexture;
	private Texture2D framePressedTexture;

	public bool IsShown => root?.Visible == true;

	public override void _Ready()
	{
		if (!DisplayServer.IsTouchscreenAvailable())
		{
			QueueFree();
			return;
		}

		Layer = 100;
		ProcessMode = ProcessModeEnum.Always; // Keep working while the game is paused

		frameTexture = GD.Load<Texture2D>(TexturePath + "touch_button.png");
		framePressedTexture = GD.Load<Texture2D>(TexturePath + "touch_button_pressed.png");

		root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Modulate = new Color(1, 1, 1, IdleOpacity) };
		root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(root);

		BuildStick();

		// Face buttons (diamond, bottom-right)
		AddButton("button/button_a.png", JoyButton.A, Corner.BottomRight, new(-250, -120), 180); // Jump / confirm
		AddButton("button/button_b.png", JoyButton.B, Corner.BottomRight, new(-100, -265), 150); // Action / back
		AddButton("button/button_x.png", JoyButton.X, Corner.BottomRight, new(-400, -265), 150); // Attack
		AddButton("button/button_y.png", JoyButton.Y, Corner.BottomRight, new(-250, -410), 150); // Light dash

		// Triggers
		AddTrigger("shoulder/shoulder_rt.png", JoyAxis.TriggerRight, Corner.BottomRight, new(-95, -470), 130); // Speed break
		AddTrigger("shoulder/shoulder_lt.png", JoyAxis.TriggerLeft, Corner.BottomRight, new(-405, -470), 130); // Time break

		// Shoulders / misc (bottom-left, above the stick)
		AddButton("shoulder/shoulder_lb.png", JoyButton.LeftShoulder, Corner.BottomLeft, new(95, -560), 130); // Step left
		AddButton("shoulder/shoulder_rb.png", JoyButton.RightShoulder, Corner.BottomLeft, new(395, -560), 130); // Step right
		AddButton("button/button_ls.png", JoyButton.LeftStick, Corner.BottomLeft, new(245, -600), 110); // Brake

		// Pause
		AddButton("button/button_menu.png", JoyButton.Start, Corner.TopRight, new(-80, 80), 110);

		SetShown(true);
	}

	private enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

	private static void Anchor(Control c, Corner corner, Vector2 center, float size)
	{
		float ax = corner is Corner.TopRight or Corner.BottomRight ? 1 : 0;
		float ay = corner is Corner.BottomLeft or Corner.BottomRight ? 1 : 0;
		c.AnchorLeft = c.AnchorRight = ax;
		c.AnchorTop = c.AnchorBottom = ay;
		c.OffsetLeft = center.X - size * 0.5f;
		c.OffsetRight = center.X + size * 0.5f;
		c.OffsetTop = center.Y - size * 0.5f;
		c.OffsetBottom = center.Y + size * 0.5f;
	}

	private static TextureRect MakeRect(Texture2D texture) => new()
	{
		Texture = texture,
		ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
		MouseFilter = Control.MouseFilterEnum.Ignore,
	};

	private void BuildStick()
	{
		const float baseSize = 360f;
		stickBase = MakeRect(GD.Load<Texture2D>(TexturePath + "touch_stick_base.png"));
		stickBase.Size = new(baseSize, baseSize);
		root.AddChild(stickBase);

		stickKnob = MakeRect(GD.Load<Texture2D>(TexturePath + "touch_stick_knob.png"));
		stickKnob.Size = new(176, 176);
		root.AddChild(stickKnob);

		// Position the stick once the layout is known, and again when the screen is resized/rotated
		Callable.From(ResetStick).CallDeferred();
		GetViewport().SizeChanged += ResetStick;
	}

	private TouchButton AddButton(string prompt, JoyButton button, Corner corner, Vector2 center, float size)
	{
		TouchButton b = CreateButton(prompt, corner, center, size);
		b.Button = button;
		return b;
	}

	private TouchButton AddTrigger(string prompt, JoyAxis axis, Corner corner, Vector2 center, float size)
	{
		TouchButton b = CreateButton(prompt, corner, center, size);
		b.Trigger = axis;
		return b;
	}

	private TouchButton CreateButton(string prompt, Corner corner, Vector2 center, float size)
	{
		Control control = new() { MouseFilter = Control.MouseFilterEnum.Stop };
		Anchor(control, corner, center, size);
		root.AddChild(control);

		TextureRect frame = MakeRect(frameTexture);
		frame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		control.AddChild(frame);

		TextureRect icon = MakeRect(GD.Load<Texture2D>(PromptPath + prompt));
		icon.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		const float inset = 0.22f;
		icon.OffsetLeft = icon.OffsetTop = size * inset;
		icon.OffsetRight = icon.OffsetBottom = -size * inset;
		control.AddChild(icon);

		TouchButton b = new() { Root = control, Frame = frame };
		buttons.Add(b);
		return b;
	}

	public override void _Input(InputEvent e)
	{
		if (root == null)
			return;

		switch (e)
		{
			case InputEventScreenTouch touch:
				if (touch.Pressed)
					OnTouchPressed(touch.Index, touch.Position);
				else
					OnTouchReleased(touch.Index);
				break;
			case InputEventScreenDrag drag:
				if (drag.Index == stickTouchIndex)
					UpdateStick(drag.Position);
				break;
			case InputEventJoypadButton jb when jb.Device != VirtualDevice:
			case InputEventJoypadMotion jm when jm.Device != VirtualDevice && Mathf.Abs(jm.AxisValue) > 0.5f:
			case InputEventKey:
				SetShown(false); // A physical controller/keyboard is being used
				break;
		}
	}

	private void OnTouchPressed(int index, Vector2 position)
	{
		SetShown(true);

		foreach (TouchButton b in buttons)
		{
			if (b.TouchIndex != -1 || !b.Root.GetGlobalRect().Grow(12).HasPoint(position))
				continue;

			b.TouchIndex = index;
			SetButtonState(b, true);
			GetViewport().SetInputAsHandled();
			return;
		}

		// Floating stick: any touch on the left side of the screen grabs it
		if (stickTouchIndex == -1 && position.X < GetViewport().GetVisibleRect().Size.X * 0.45f)
		{
			stickTouchIndex = index;
			stickCenter = position;
			MoveStickBase(stickCenter);
			UpdateStick(position);
			GetViewport().SetInputAsHandled();
		}
	}

	private void OnTouchReleased(int index)
	{
		if (index == stickTouchIndex)
			ResetStick();

		foreach (TouchButton b in buttons)
		{
			if (b.TouchIndex != index)
				continue;

			b.TouchIndex = -1;
			SetButtonState(b, false);
		}
	}

	private void SetButtonState(TouchButton b, bool pressed)
	{
		b.Frame.Texture = pressed ? framePressedTexture : frameTexture;
		b.Root.Modulate = pressed ? new Color(1, 1, 1, 1f / IdleOpacity) : Colors.White;
		b.Root.Scale = pressed ? new Vector2(0.92f, 0.92f) : Vector2.One;
		b.Root.PivotOffset = b.Root.Size * 0.5f;

		if (b.Trigger != JoyAxis.Invalid)
			SendAxis(b.Trigger, pressed ? 1f : 0f);
		else
			Input.ParseInputEvent(new InputEventJoypadButton
			{
				Device = VirtualDevice,
				ButtonIndex = b.Button,
				Pressed = pressed,
				Pressure = pressed ? 1f : 0f,
			});
	}

	private void UpdateStick(Vector2 position)
	{
		Vector2 offset = (position - stickCenter).LimitLength(StickRadius);
		stickKnob.Position = stickCenter + offset - stickKnob.Size * 0.5f;

		Vector2 value = offset / StickRadius;
		if (value.IsEqualApprox(lastStickValue))
			return;

		lastStickValue = value;
		SendAxis(JoyAxis.LeftX, value.X);
		SendAxis(JoyAxis.LeftY, value.Y);
	}

	private void ResetStick()
	{
		stickTouchIndex = -1;
		Vector2 screenSize = GetViewport().GetVisibleRect().Size;
		stickDefaultCenter = new Vector2(250, screenSize.Y - 250);
		MoveStickBase(stickDefaultCenter);
		stickCenter = stickDefaultCenter;
		stickKnob.Position = stickCenter - stickKnob.Size * 0.5f;

		if (lastStickValue != Vector2.Zero)
		{
			lastStickValue = Vector2.Zero;
			SendAxis(JoyAxis.LeftX, 0f);
			SendAxis(JoyAxis.LeftY, 0f);
		}
	}

	private void MoveStickBase(Vector2 center) => stickBase.Position = center - stickBase.Size * 0.5f;

	private static void SendAxis(JoyAxis axis, float value) =>
		Input.ParseInputEvent(new InputEventJoypadMotion { Device = VirtualDevice, Axis = axis, AxisValue = value });

	private void ReleaseAll()
	{
		if (stickTouchIndex != -1 || lastStickValue != Vector2.Zero)
			ResetStick();

		foreach (TouchButton b in buttons)
		{
			if (b.TouchIndex == -1)
				continue;

			b.TouchIndex = -1;
			SetButtonState(b, false);
		}
	}

	private void SetShown(bool shown)
	{
		if (root.Visible == shown)
			return;

		if (!shown)
			ReleaseAll();
		root.Visible = shown;
	}

	public override void _Notification(int what)
	{
		if (what == NotificationApplicationFocusOut || what == NotificationApplicationPaused)
			ReleaseAll();
	}
}
