using System;
using System.Collections.Generic;
using Godot;
using Project.Gameplay;

namespace Project.Interface;

public partial class TouchControls : CanvasLayer
{
	/// <summary> Joypad device id used for the virtual gamepad. </summary>
	public const int VirtualDevice = 7;

	private const string TexturePath = "res://interface/touch/";
	private const string PromptPath = "res://resource/input sprites/navigation/";
	private const string SettingsFile = "user://touch_controls.cfg";
	private const float StickBaseSize = 340f;
	private const float StickKnobSize = 170f;
	private const float StickRadius = 115f;
	/// <summary> Extra distance kept from the safe area edges. </summary>
	private const float EdgePadding = 40f;
	private const float DefaultOpacity = 0.75f;
	private const float MinItemScale = 0.5f;
	private const float MaxItemScale = 1.8f;

	private enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

	/// <summary> Something that can be moved/resized in the layout editor (a button, the stick or the whole d-pad). </summary>
	private sealed class LayoutItem
	{
		public string Id;
		public bool IsGameplay;
		public Corner Corner;
		public Vector2 DefaultOffset;
		public float HitRadius;
		public readonly List<(Control Part, Vector2 Offset)> Parts = [];

		/// <summary> Custom center as a ratio of the screen size, or null for the default position. </summary>
		public Vector2? CustomCenter;
		public float Scale = 1f;
		public Vector2 Center;
	}

	private sealed class TouchButton
	{
		public LayoutItem Item;
		public Control Root;
		public TextureRect Frame;
		public float Size;
		public JoyButton Button = JoyButton.Invalid;
		public JoyAxis Trigger = JoyAxis.Invalid;
		public Action OnPressed;
		public bool IsDpad;
		public int TouchIndex = -1;
	}

	private Control root;
	private Control gameplayGroup;
	private Control menuGroup;
	private readonly List<LayoutItem> items = [];
	private readonly List<TouchButton> gameplayButtons = [];
	private readonly List<TouchButton> menuButtons = [];
	private List<TouchButton> ActiveButtons => isGameplayMode ? gameplayButtons : menuButtons;
	private bool isGameplayMode;
	private float opacity = DefaultOpacity;

	private LayoutItem stickItem;
	private TextureRect stickBase;
	private TextureRect stickKnob;
	private Vector2 stickCenter;
	private int stickTouchIndex = -1;
	private Vector2 lastStickValue;
	private float StickScale => stickItem.Scale;

	/// <summary> Safe area of the screen (notch, rounded corners, navigation bar) in viewport coordinates. </summary>
	private Rect2 safeRect;

	private Texture2D frameTexture;
	private Texture2D framePressedTexture;

	public override void _Ready()
	{
		if (!DisplayServer.IsTouchscreenAvailable())
		{
			QueueFree();
			return;
		}

		// Touches would otherwise also be sent as mouse clicks, and left click is mapped to jump.
		Input.EmulateMouseFromTouch = false;

		Layer = 100;
		ProcessMode = ProcessModeEnum.Always; // Keep working while the game is paused

		frameTexture = GD.Load<Texture2D>(TexturePath + "touch_button.png");
		framePressedTexture = GD.Load<Texture2D>(TexturePath + "touch_button_pressed.png");

		root = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
		root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		AddChild(root);

		BuildEditorBackground();
		gameplayGroup = CreateGroup();
		menuGroup = CreateGroup();

		BuildGameplayControls();
		BuildMenuControls();
		BuildEditorPanel();
		LoadSettings();

		GetViewport().SizeChanged += Layout;
		Callable.From(Layout).CallDeferred();

		isGameplayMode = IsGameplay();
		UpdateGroupVisibility();
	}

	private Control CreateGroup()
	{
		Control group = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
		group.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		root.AddChild(group);
		return group;
	}

	#region Building
	/// <summary> Analog stick and every button used while playing a stage. </summary>
	private void BuildGameplayControls()
	{
		stickBase = MakeRect(GD.Load<Texture2D>(TexturePath + "touch_stick_base.png"));
		stickBase.Size = new(StickBaseSize, StickBaseSize);
		stickBase.PivotOffset = stickBase.Size * 0.5f;
		gameplayGroup.AddChild(stickBase);

		stickKnob = MakeRect(GD.Load<Texture2D>(TexturePath + "touch_stick_knob.png"));
		stickKnob.Size = new(StickKnobSize, StickKnobSize);
		stickKnob.PivotOffset = stickKnob.Size * 0.5f;
		gameplayGroup.AddChild(stickKnob);

		stickItem = CreateItem("gameplay_stick", true, Corner.BottomLeft, new(StickBaseSize * 0.5f, -StickBaseSize * 0.5f), StickBaseSize * 0.5f);
		stickItem.Parts.Add((stickBase, Vector2.Zero));

		// Face buttons (diamond, bottom-right)
		AddButton(true, "gameplay_a", "button/button_a.png", JoyButton.A, Corner.BottomRight, new(-230, -105), 170); // Jump
		AddButton(true, "gameplay_b", "button/button_b.png", JoyButton.B, Corner.BottomRight, new(-85, -240), 140); // Action
		AddButton(true, "gameplay_x", "button/button_x.png", JoyButton.X, Corner.BottomRight, new(-375, -240), 140); // Attack
		AddButton(true, "gameplay_y", "button/button_y.png", JoyButton.Y, Corner.BottomRight, new(-230, -375), 140); // Light dash

		// Triggers
		AddTrigger("gameplay_rt", "shoulder/shoulder_rt.png", JoyAxis.TriggerRight, Corner.BottomRight, new(-75, -425), 120); // Speed break
		AddTrigger("gameplay_lt", "shoulder/shoulder_lt.png", JoyAxis.TriggerLeft, Corner.BottomRight, new(-385, -425), 120); // Time break

		// Above the stick
		AddButton(true, "gameplay_lb", "shoulder/shoulder_lb.png", JoyButton.LeftShoulder, Corner.BottomLeft, new(70, -470), 120); // Step left
		AddButton(true, "gameplay_rb", "shoulder/shoulder_rb.png", JoyButton.RightShoulder, Corner.BottomLeft, new(310, -470), 120); // Step right
		AddButton(true, "gameplay_ls", "button/button_ls.png", JoyButton.LeftStick, Corner.BottomLeft, new(190, -510), 100); // Brake

		AddButton(true, "gameplay_start", "button/button_menu.png", JoyButton.Start, Corner.TopRight, new(-55, 55), 100); // Pause
	}

	/// <summary> D-pad and the buttons menus use (confirm, back, sort, clear, page and start). </summary>
	private void BuildMenuControls()
	{
		const float spacing = 120f;
		LayoutItem dpad = CreateItem("menu_dpad", false, Corner.BottomLeft, new(190, -190), spacing + 65f);
		AddDpad(dpad, JoyButton.DpadUp, 0, new(0, -spacing));
		AddDpad(dpad, JoyButton.DpadDown, 180, new(0, spacing));
		AddDpad(dpad, JoyButton.DpadLeft, -90, new(-spacing, 0));
		AddDpad(dpad, JoyButton.DpadRight, 90, new(spacing, 0));

		AddButton(false, "menu_a", "button/button_a.png", JoyButton.A, Corner.BottomRight, new(-200, -110), 170); // Confirm
		AddButton(false, "menu_b", "button/button_b.png", JoyButton.B, Corner.BottomRight, new(-65, -245), 130); // Back
		AddButton(false, "menu_x", "button/button_x.png", JoyButton.X, Corner.BottomRight, new(-335, -245), 110); // Sort
		AddButton(false, "menu_y", "button/button_y.png", JoyButton.Y, Corner.BottomRight, new(-200, -370), 110); // Clear

		AddButton(false, "menu_lb", "shoulder/shoulder_lb.png", JoyButton.LeftShoulder, Corner.BottomLeft, new(60, -450), 110); // Previous page
		AddButton(false, "menu_rb", "shoulder/shoulder_rb.png", JoyButton.RightShoulder, Corner.BottomLeft, new(320, -450), 110); // Next page

		AddButton(false, "menu_start", "button/button_menu.png", JoyButton.Start, Corner.TopRight, new(-55, 55), 100); // Start

		// Opens the touch controls editor
		LayoutItem gearItem = CreateItem("menu_edit", false, Corner.TopRight, new(-175, 55), 50);
		TouchButton gear = CreateButton(gearItem, false, TexturePath + "touch_gear.png", Vector2.Zero, 100);
		gear.OnPressed = () => SetEditing(true);
	}

	private LayoutItem CreateItem(string id, bool isGameplay, Corner corner, Vector2 offset, float hitRadius)
	{
		LayoutItem item = new() { Id = id, IsGameplay = isGameplay, Corner = corner, DefaultOffset = offset, HitRadius = hitRadius };
		items.Add(item);
		return item;
	}

	private void AddDpad(LayoutItem dpad, JoyButton button, float rotationDegrees, Vector2 offset)
	{
		TouchButton b = CreateButton(dpad, false, TexturePath + "touch_arrow.png", offset, 130, rotationDegrees);
		b.Button = button;
		b.IsDpad = true;
	}

	private void AddButton(bool isGameplay, string id, string prompt, JoyButton button, Corner corner, Vector2 offset, float size)
	{
		LayoutItem item = CreateItem(id, isGameplay, corner, offset, size * 0.5f);
		CreateButton(item, isGameplay, PromptPath + prompt, Vector2.Zero, size).Button = button;
	}

	private void AddTrigger(string id, string prompt, JoyAxis axis, Corner corner, Vector2 offset, float size)
	{
		LayoutItem item = CreateItem(id, true, corner, offset, size * 0.5f);
		CreateButton(item, true, PromptPath + prompt, Vector2.Zero, size).Trigger = axis;
	}

	private static TextureRect MakeRect(Texture2D texture) => new()
	{
		Texture = texture,
		ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
		StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
		MouseFilter = Control.MouseFilterEnum.Ignore,
	};

	private TouchButton CreateButton(LayoutItem item, bool isGameplay, string iconPath, Vector2 partOffset, float size, float iconRotation = 0)
	{
		Control control = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Size = new(size, size), PivotOffset = new(size * 0.5f, size * 0.5f) };
		(isGameplay ? gameplayGroup : menuGroup).AddChild(control);

		TextureRect frame = MakeRect(frameTexture);
		frame.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		control.AddChild(frame);

		const float inset = 0.22f;
		TextureRect icon = MakeRect(GD.Load<Texture2D>(iconPath));
		icon.Position = new(size * inset, size * inset);
		icon.Size = new(size * (1 - 2 * inset), size * (1 - 2 * inset));
		icon.PivotOffset = icon.Size * 0.5f;
		icon.RotationDegrees = iconRotation;
		control.AddChild(icon);

		item.Parts.Add((control, partOffset));
		TouchButton b = new() { Item = item, Root = control, Frame = frame, Size = size };
		(isGameplay ? gameplayButtons : menuButtons).Add(b);
		return b;
	}
	#endregion

	#region Layout
	private void UpdateSafeRect()
	{
		Vector2 viewportSize = GetViewport().GetVisibleRect().Size;
		Vector2I windowSize = DisplayServer.WindowGetSize();
		Rect2I safeArea = DisplayServer.GetDisplaySafeArea();

		Vector2 scale = Vector2.One;
		if (windowSize.X > 0 && windowSize.Y > 0)
			scale = viewportSize / windowSize;

		float left = Mathf.Max(safeArea.Position.X, 0) * scale.X;
		float top = Mathf.Max(safeArea.Position.Y, 0) * scale.Y;
		float right = Mathf.Max(windowSize.X - safeArea.End.X, 0) * scale.X;
		float bottom = Mathf.Max(windowSize.Y - safeArea.End.Y, 0) * scale.Y;
		if (safeArea.Size.X <= 0 || safeArea.Size.Y <= 0) // Safe area not reported
			left = top = right = bottom = 0;

		safeRect = new Rect2(left + EdgePadding, top + EdgePadding,
			viewportSize.X - left - right - EdgePadding * 2, viewportSize.Y - top - bottom - EdgePadding * 2);
	}

	private Vector2 CornerPoint(Corner corner) => corner switch
	{
		Corner.TopLeft => safeRect.Position,
		Corner.TopRight => new(safeRect.End.X, safeRect.Position.Y),
		Corner.BottomLeft => new(safeRect.Position.X, safeRect.End.Y),
		_ => safeRect.End,
	};

	/// <summary> Places every control relative to the safe area (or where the player moved it). </summary>
	private void Layout()
	{
		ReleaseAll();
		UpdateSafeRect();
		foreach (LayoutItem item in items)
			ApplyItem(item);
		ResetStick();
		LayoutEditorPanel();
	}

	private void ApplyItem(LayoutItem item)
	{
		Vector2 screenSize = GetViewport().GetVisibleRect().Size;
		item.Center = item.CustomCenter.HasValue ? item.CustomCenter.Value * screenSize : CornerPoint(item.Corner) + item.DefaultOffset;

		foreach ((Control part, Vector2 offset) in item.Parts)
		{
			part.Scale = Vector2.One * item.Scale;
			part.Position = item.Center + offset * item.Scale - part.Size * 0.5f;
		}

		if (item == stickItem)
			stickKnob.Scale = Vector2.One * item.Scale;
	}
	#endregion

	#region Mode
	/// <summary> True while a stage is being played (not paused, not on results or loading). </summary>
	private bool IsGameplay()
	{
		StageSettings stage = StageSettings.Instance;
		return stage != null && IsInstanceValid(stage) && stage.IsInsideTree() && stage.IsLevelIngame && !GetTree().Paused;
	}

	public override void _Process(double _)
	{
		if (root == null || isEditing)
			return;

		bool gameplay = IsGameplay();
		if (gameplay == isGameplayMode)
			return;

		ReleaseAll();
		isGameplayMode = gameplay;
		UpdateGroupVisibility();
	}

	private void UpdateGroupVisibility()
	{
		gameplayGroup.Visible = isGameplayMode;
		menuGroup.Visible = !isGameplayMode;
	}
	#endregion

	#region Input
	public override void _Input(InputEvent e)
	{
		if (root == null)
			return;

		if (isEditing)
		{
			EditorInput(e);
			return;
		}

		switch (e)
		{
			case InputEventScreenTouch touch:
				if (touch.Pressed)
					OnTouchPressed(touch.Index, touch.Position);
				else
					OnTouchReleased(touch.Index);
				break;
			case InputEventScreenDrag drag:
				OnTouchDragged(drag.Index, drag.Position);
				break;
			case InputEventJoypadButton jb when jb.Device != VirtualDevice:
			case InputEventJoypadMotion jm when jm.Device != VirtualDevice && Mathf.Abs(jm.AxisValue) > 0.5f:
			case InputEventKey:
				SetShown(false); // A physical controller/keyboard is being used
				break;
		}
	}

	private static bool HitsButton(TouchButton b, Vector2 position)
	{
		Vector2 center = b.Root.Position + b.Root.Size * 0.5f;
		return center.DistanceTo(position) <= b.Size * 0.5f * b.Item.Scale + 12f; // Circular hit area
	}

	private TouchButton FindButton(Vector2 position, bool dpadOnly = false)
	{
		foreach (TouchButton b in ActiveButtons)
		{
			if (dpadOnly && !b.IsDpad)
				continue;
			if (HitsButton(b, position))
				return b;
		}

		return null;
	}

	private void OnTouchPressed(int index, Vector2 position)
	{
		if (!root.Visible)
		{
			SetShown(true);
			return;
		}

		TouchButton b = FindButton(position);
		if (b != null && b.TouchIndex == -1)
		{
			b.TouchIndex = index;
			SetButtonState(b, true);
			GetViewport().SetInputAsHandled();
			return;
		}

		// Floating stick: any free touch on the left side of the screen grabs it (gameplay only)
		if (isGameplayMode && stickTouchIndex == -1 && position.X < GetViewport().GetVisibleRect().Size.X * 0.45f)
		{
			stickTouchIndex = index;
			stickCenter = position;
			MoveStickBase(stickCenter);
			UpdateStick(position);
			GetViewport().SetInputAsHandled();
		}
	}

	private void OnTouchDragged(int index, Vector2 position)
	{
		if (index == stickTouchIndex)
		{
			UpdateStick(position);
			return;
		}

		// Let the finger slide between d-pad arrows
		if (isGameplayMode)
			return;

		TouchButton current = menuButtons.Find(b => b.TouchIndex == index);
		if (current == null || !current.IsDpad)
			return;

		TouchButton target = FindButton(position, true);
		if (target == null || target == current || target.TouchIndex != -1)
			return;

		current.TouchIndex = -1;
		SetButtonState(current, false);
		target.TouchIndex = index;
		SetButtonState(target, true);
	}

	private void OnTouchReleased(int index)
	{
		if (index == stickTouchIndex)
			ResetStick();

		foreach (TouchButton b in ActiveButtons)
		{
			if (b.TouchIndex != index)
				continue;

			b.TouchIndex = -1;
			SetButtonState(b, false);
		}
	}
	#endregion

	#region Output
	private void SetButtonState(TouchButton b, bool pressed)
	{
		b.Frame.Texture = pressed ? framePressedTexture : frameTexture;
		b.Root.Modulate = pressed ? new Color(1, 1, 1, 1f / Mathf.Max(opacity, 0.01f)) : Colors.White;
		b.Root.Scale = Vector2.One * b.Item.Scale * (pressed ? 0.92f : 1f);

		if (b.OnPressed != null)
		{
			if (!pressed)
				b.OnPressed(); // Trigger on release, so the same touch doesn't hit the editor
			return;
		}

		if (b.Trigger != JoyAxis.Invalid)
		{
			SendAxis(b.Trigger, pressed ? 1f : 0f);
			return;
		}

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
		float radius = StickRadius * StickScale;
		Vector2 offset = (position - stickCenter).LimitLength(radius);
		stickKnob.Position = stickCenter + offset - stickKnob.Size * 0.5f;

		Vector2 value = offset / radius;
		if (value.IsEqualApprox(lastStickValue))
			return;

		lastStickValue = value;
		SendAxis(JoyAxis.LeftX, value.X);
		SendAxis(JoyAxis.LeftY, value.Y);
	}

	private void ResetStick()
	{
		stickTouchIndex = -1;
		stickCenter = stickItem.Center;
		MoveStickBase(stickCenter);
		stickKnob.Position = stickCenter - stickKnob.Size * 0.5f;

		if (lastStickValue == Vector2.Zero)
			return;

		lastStickValue = Vector2.Zero;
		SendAxis(JoyAxis.LeftX, 0f);
		SendAxis(JoyAxis.LeftY, 0f);
	}

	private void MoveStickBase(Vector2 center) => stickBase.Position = center - stickBase.Size * 0.5f;

	private static void SendAxis(JoyAxis axis, float value) =>
		Input.ParseInputEvent(new InputEventJoypadMotion { Device = VirtualDevice, Axis = axis, AxisValue = value });

	private void ReleaseAll()
	{
		if (stickItem != null)
			ResetStick();

		foreach (List<TouchButton> list in new[] { gameplayButtons, menuButtons })
		{
			foreach (TouchButton b in list)
			{
				if (b.TouchIndex == -1)
					continue;

				b.TouchIndex = -1;
				b.Frame.Texture = frameTexture;
				b.Root.Modulate = Colors.White;
				b.Root.Scale = Vector2.One * b.Item.Scale;
				if (b.OnPressed == null) // Don't open the editor because of a forced release
					SetButtonState(b, false);
			}
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
		if (root != null && (what == NotificationApplicationFocusOut || what == NotificationApplicationPaused))
			ReleaseAll();
	}
	#endregion

	#region Editor
	private bool isEditing;
	private bool isEditingGameplay = true;
	private LayoutItem selectedItem;
	private int dragTouchIndex = -1;
	private Vector2 dragOffset;

	private ColorRect editorBackground;
	private PanelContainer editorPanel;
	private Button gameplayTabButton;
	private Button menuTabButton;
	private HSlider opacitySlider;
	private HSlider sizeSlider;
	private Label sizeLabel;
	private bool isUpdatingSliders;

	private static readonly Color Gold = new(0.85f, 0.64f, 0.25f);
	private static readonly Color DarkBrown = new(0.2f, 0.12f, 0.05f, 0.94f);
	private static readonly Color SelectedTint = new(1f, 0.85f, 0.45f);

	private void BuildEditorBackground()
	{
		editorBackground = new ColorRect { Color = new Color(0, 0, 0, 0.45f), Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
		editorBackground.SetAnchorsPreset(Control.LayoutPreset.FullRect);
		root.AddChild(editorBackground);
	}

	private static StyleBoxFlat MakeStyle(Color background, Color border, int borderWidth, int radius)
	{
		StyleBoxFlat style = new() { BgColor = background, BorderColor = border };
		style.SetBorderWidthAll(borderWidth);
		style.SetCornerRadiusAll(radius);
		style.SetContentMarginAll(16);
		return style;
	}

	private Button MakeEditorButton(string text, Action onPressed)
	{
		Button button = new() { Text = text, CustomMinimumSize = new(210, 76), FocusMode = Control.FocusModeEnum.None };
		button.AddThemeFontSizeOverride("font_size", 32);
		button.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.7f));
		button.AddThemeColorOverride("font_pressed_color", Colors.White);
		button.AddThemeStyleboxOverride("normal", MakeStyle(new Color(0.35f, 0.22f, 0.1f), Gold, 3, 12));
		button.AddThemeStyleboxOverride("hover", MakeStyle(new Color(0.35f, 0.22f, 0.1f), Gold, 3, 12));
		button.AddThemeStyleboxOverride("pressed", MakeStyle(new Color(0.6f, 0.42f, 0.15f), Gold, 3, 12));
		button.Pressed += onPressed;
		return button;
	}

	private HBoxContainer MakeSliderRow(string text, double min, double max, out Label label, out HSlider slider, Action<double> onChanged)
	{
		HBoxContainer row = new();
		row.AddThemeConstantOverride("separation", 20);

		label = new Label { Text = text, CustomMinimumSize = new(260, 0), VerticalAlignment = VerticalAlignment.Center };
		label.AddThemeFontSizeOverride("font_size", 30);
		label.AddThemeColorOverride("font_color", new Color(1f, 0.92f, 0.7f));
		row.AddChild(label);

		slider = new HSlider { MinValue = min, MaxValue = max, Step = 0.01, CustomMinimumSize = new(520, 64), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, FocusMode = Control.FocusModeEnum.None };
		StyleBoxFlat track = MakeStyle(new Color(0.12f, 0.07f, 0.03f), Gold, 2, 10);
		track.SetContentMarginAll(8);
		slider.AddThemeStyleboxOverride("slider", track);
		slider.AddThemeStyleboxOverride("grabber_area", MakeStyle(Gold, Gold, 0, 10));
		slider.AddThemeStyleboxOverride("grabber_area_highlight", MakeStyle(Gold, Gold, 0, 10));
		slider.ValueChanged += value => { if (!isUpdatingSliders) onChanged(value); };
		row.AddChild(slider);
		return row;
	}

	private void BuildEditorPanel()
	{
		editorPanel = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Stop };
		editorPanel.AddThemeStyleboxOverride("panel", MakeStyle(DarkBrown, Gold, 4, 18));
		root.AddChild(editorPanel);

		VBoxContainer column = new();
		column.AddThemeConstantOverride("separation", 14);
		editorPanel.AddChild(column);

		HBoxContainer tabs = new();
		tabs.AddThemeConstantOverride("separation", 14);
		gameplayTabButton = MakeEditorButton("Gameplay", () => SwitchEditorLayout(true));
		menuTabButton = MakeEditorButton("Menus", () => SwitchEditorLayout(false));
		tabs.AddChild(gameplayTabButton);
		tabs.AddChild(menuTabButton);
		tabs.AddChild(MakeEditorButton("Reset", ResetLayout));
		tabs.AddChild(MakeEditorButton("Done", () => SetEditing(false)));
		column.AddChild(tabs);

		column.AddChild(MakeSliderRow("Opacity", 0.2, 1.0, out _, out opacitySlider, value =>
		{
			opacity = (float)value;
			root.Modulate = new Color(1, 1, 1, opacity);
		}));

		column.AddChild(MakeSliderRow("Size", MinItemScale, MaxItemScale, out sizeLabel, out sizeSlider, value =>
		{
			if (selectedItem == null)
				return;

			selectedItem.Scale = (float)value;
			ApplyItem(selectedItem);
			if (selectedItem == stickItem)
				ResetStick();
		}));
	}

	private void LayoutEditorPanel()
	{
		if (editorPanel == null)
			return;

		editorPanel.ResetSize();
		Vector2 size = editorPanel.GetCombinedMinimumSize();
		Vector2 screenSize = GetViewport().GetVisibleRect().Size;
		editorPanel.Position = new((screenSize.X - size.X) * 0.5f, safeRect.Position.Y);
	}

	private void SetEditing(bool editing)
	{
		ReleaseAll();
		isEditing = editing;

		// The editor uses regular GUI buttons/sliders, which need touches converted to mouse clicks
		Input.EmulateMouseFromTouch = editing;

		editorBackground.Visible = editing;
		editorPanel.Visible = editing;
		SelectItem(null);

		if (editing)
		{
			isUpdatingSliders = true;
			opacitySlider.Value = opacity;
			isUpdatingSliders = false;
			SwitchEditorLayout(isGameplayMode || isEditingGameplay);
			LayoutEditorPanel();
			return;
		}

		SaveSettings();
		isGameplayMode = IsGameplay();
		UpdateGroupVisibility();
	}

	private void SwitchEditorLayout(bool gameplay)
	{
		isEditingGameplay = gameplay;
		SelectItem(null);
		gameplayGroup.Visible = gameplay;
		menuGroup.Visible = !gameplay;
		gameplayTabButton.Modulate = gameplay ? Colors.White : new Color(1, 1, 1, 0.5f);
		menuTabButton.Modulate = gameplay ? new Color(1, 1, 1, 0.5f) : Colors.White;
	}

	private void SelectItem(LayoutItem item)
	{
		if (selectedItem != null)
			foreach ((Control part, _) in selectedItem.Parts)
				part.SelfModulate = Colors.White;

		selectedItem = item;
		if (selectedItem != null)
			foreach ((Control part, _) in selectedItem.Parts)
				part.SelfModulate = SelectedTint;

		isUpdatingSliders = true;
		sizeSlider.Editable = item != null;
		sizeSlider.Value = item?.Scale ?? 1f;
		sizeLabel.Text = item != null ? "Size" : "Size (tap one)";
		isUpdatingSliders = false;
	}

	private LayoutItem FindEditableItem(Vector2 position)
	{
		LayoutItem best = null;
		float bestDistance = float.MaxValue;
		foreach (LayoutItem item in items)
		{
			if (item.IsGameplay != isEditingGameplay || item.Id == "menu_edit")
				continue;

			float distance = item.Center.DistanceTo(position);
			if (distance <= item.HitRadius * item.Scale + 12f && distance < bestDistance)
			{
				best = item;
				bestDistance = distance;
			}
		}

		return best;
	}

	private void EditorInput(InputEvent e)
	{
		switch (e)
		{
			case InputEventScreenTouch touch:
				if (!touch.Pressed)
				{
					if (touch.Index == dragTouchIndex)
						dragTouchIndex = -1;
					return;
				}

				if (editorPanel.GetGlobalRect().HasPoint(touch.Position))
					return; // Let the GUI handle the panel

				LayoutItem item = FindEditableItem(touch.Position);
				SelectItem(item);
				if (item != null)
				{
					dragTouchIndex = touch.Index;
					dragOffset = item.Center - touch.Position;
				}
				break;
			case InputEventScreenDrag drag when drag.Index == dragTouchIndex && selectedItem != null:
				Vector2 screenSize = GetViewport().GetVisibleRect().Size;
				Vector2 center = (drag.Position + dragOffset).Clamp(Vector2.Zero, screenSize);
				selectedItem.CustomCenter = center / screenSize;
				ApplyItem(selectedItem);
				if (selectedItem == stickItem)
					ResetStick();
				break;
		}
	}

	private void ResetLayout()
	{
		foreach (LayoutItem item in items)
		{
			if (item.IsGameplay != isEditingGameplay)
				continue;

			item.CustomCenter = null;
			item.Scale = 1f;
			ApplyItem(item);
		}

		ResetStick();
		SelectItem(null);
	}
	#endregion

	#region Settings
	private void LoadSettings()
	{
		ConfigFile config = new();
		if (config.Load(SettingsFile) != Error.Ok)
		{
			root.Modulate = new Color(1, 1, 1, opacity);
			return;
		}

		opacity = (float)config.GetValue("general", "opacity", DefaultOpacity);
		root.Modulate = new Color(1, 1, 1, opacity);

		foreach (LayoutItem item in items)
		{
			item.Scale = Mathf.Clamp((float)config.GetValue(item.Id, "scale", 1f), MinItemScale, MaxItemScale);
			if (config.HasSectionKey(item.Id, "center"))
				item.CustomCenter = (Vector2)config.GetValue(item.Id, "center");
		}
	}

	private void SaveSettings()
	{
		ConfigFile config = new();
		config.SetValue("general", "opacity", opacity);
		foreach (LayoutItem item in items)
		{
			config.SetValue(item.Id, "scale", item.Scale);
			if (item.CustomCenter.HasValue)
				config.SetValue(item.Id, "center", item.CustomCenter.Value);
		}

		config.Save(SettingsFile);
	}
	#endregion
}
