namespace Cthangover.Mods.JuniorReaper.Tests.Lamp
{
    using System;
    using System.IO;
    using System.Linq;
    using Cthangover.Core.Bootstrap;
    using Cthangover.Core.Settings;
    using Cthangover.Core.Utils;
    using Cthangover.Mods.Interface.View;
    using Cthangover.Mods.Rpg;
    using Cthangover.Mods.Rpg.Model;
    using Cthangover.Mods.Time;
    using Godot;

    /// <summary>Real pointer events drag the novel's existing toolbar lamp into Stas's actual office; no map resource is written.</summary>
    public partial class StasLampTestDriver : Node
    {
        private readonly Timer timer = new() { WaitTime = 0.35, Autostart = true };
        private LampBehaviour lamp;
        private RpgMap map;
        private string previousMode;
        private string shots;
        private int step;
        private int passed;
        private int failed;
        private double elapsed;
        private bool finished;
        private Vector2 origin;
        private Vector2 target;
        private float dark;
        private float day;
        private bool pointerDown;
        public override void _Ready()
        {
            foreach (var arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith("--shots=", StringComparison.Ordinal)) shots = arg.Substring(8);
            AddChild(timer); timer.Timeout += Tick;
        }
        private void Tick()
        {
            if (finished) return;
            elapsed += timer.WaitTime;
            if (elapsed > 45) { Check(false, "timeout step " + step); Finish(); return; }
            try { Run(); } catch (Exception ex) { Check(false, ex.ToString()); Finish(); }
        }
        private void Run()
        {
            switch (step)
            {
                case 0:
                    if (!Boot.IsReady || RpgWorldHost.Current?.View?.Map.ID != "jrp_office") return;
                    var box = RpgInput.FindDialogBox();
                    if (box?.IsRunning == true) { if (box.IsWaitingAnswer) box.TrySelectVariant(0); else box.NextAction(); return; }
                    map = RpgWorldHost.Current.Session.Map;
                    previousMode = map.LightMode;
                    lamp = GetTree().Root.FindChildren("*", "", true, false).OfType<LampBehaviour>().Single();
                    SetMode("day");
                    Check(!lamp.Visible, "fixed daytime hides the same shared lamp");
                    target = SampleTarget();
                    step++;
                    return;
                case 1:
                    day = Brightness(target);
                    Check(day > 0.03f, "office artwork is visible by day");
                    SetMode("night");
                    UiLightController.Instance.UpdateLightPos(new Vector2(-1000, -1000));
                    step++;
                    return;
                case 2:
                    dark = Brightness(target);
                    Check(lamp.Visible && lamp.GetGlobalRect().GetCenter().Y < 100, "novel lamp is visible at the top center in Stas's game");
                    Check(dark < day * 0.3f, "office is dark before using the lamp");
                    Shot("stas_office_dark");
                    origin = lamp.GetGlobalRect().GetCenter();
                    MouseButton(true, origin);
                    step++;
                    return;
                case 3:
                    Move(target, true);
                    step++;
                    return;
                case 4:
                    MouseButton(false, target);
                    step++;
                    return;
                case 5:
                    Check(UiLightController.Instance.TryGetPortableLight(out var position, out _, out var radius, out _)
                        && position.DistanceTo(target) < 3, $"real drag/drop places the shared light at the chosen office point (expected {target}, actual {position})");
                    Check(Math.Abs(radius - GameData.Instance.Runtime.LampData.Radius) < 0.01f, "the same saved lamp radius is used");
                    var mat = RpgWorldHost.Current.View.Presentation.SharedMaterial;
                    var renderedPosition = mat.GetShaderParameter("portable_position").AsVector2();
                    Check(renderedPosition.DistanceTo(target) < 3, $"actual RPG render receives the dropped light (expected {target}, actual {renderedPosition})");
                    Check(Brightness(target) > dark + 0.03f, "dragged lamp visibly reveals the office artwork");
                    Shot("stas_office_lamp_dragged");
                    Move(target + new Vector2(160, 60), false);
                    step++;
                    return;
                case 6:
                    UiLightController.Instance.TryGetPortableLight(out var stayed, out _, out _, out _);
                    Check(stayed.DistanceTo(target) < 3, $"released lamp stays where it was dropped instead of following the mouse (expected {target}, actual {stayed})");
                    MouseButton(true, origin);
                    step++;
                    return;
                case 7:
                    // A real click can contain tiny pointer motion; it must still stow a placed lamp.
                    Move(origin + Vector2.One, true);
                    step++;
                    return;
                case 8:
                    MouseButton(false, origin + Vector2.One);
                    step++;
                    return;
                case 9:
                    Check(!UiLightController.Instance.TryGetPortableLight(out _, out _, out _, out _), "clicking the toolbar lamp again stows it despite pointer jitter");
                    Check(Brightness(target) < day * 0.3f, "stowing the lamp conceals the office again");
                    MouseButton(true, origin);
                    step++;
                    return;
                case 10:
                    Move(target, true);
                    step++;
                    return;
                case 11:
                    MouseButton(false, target);
                    step++;
                    return;
                case 12:
                    Check(UiLightController.Instance.TryGetPortableLight(out _, out _, out _, out _), "the same lamp can be dragged out again");
                    SetMode("day");
                    Check(!lamp.Visible && !UiLightController.Instance.TryGetPortableLight(out _, out _, out _, out _), "daytime stows and disables the light cleanly");
                    Finish();
                    return;
            }
        }
        private void SetMode(string mode)
        {
            map.LightMode = mode;
            UiLightController.Instance.SetLocationMode(mode);
            RpgWorldHost.Current.View.Present(0);
        }
        private Vector2 SampleTarget()
        {
            // Pick a bright point on actual office art, avoiding the toolbar and shell background.
            using var image = ViewportCapture.Take(GetViewport());
            var best = 0f;
            var point = GetViewport().GetVisibleRect().Size / 2;
            for (var y = 220; y < image.GetHeight() - 200; y += 12)
                for (var x = image.GetWidth() / 3; x < image.GetWidth() * 2 / 3; x += 12)
                {
                    var c = image.GetPixel(x, y);
                    var value = (c.R + c.G + c.B) / 3;
                    if (value > best) { best = value; point = new Vector2(x, y); }
                }
            return point;
        }
        private float Brightness(Vector2 point)
        {
            using var image = ViewportCapture.Take(GetViewport());
            var color = image.GetPixel((int)point.X, (int)point.Y);
            return (color.R + color.G + color.B) / 3;
        }
        private void MouseButton(bool pressed, Vector2 point)
        {
            GetViewport().WarpMouse(point);
            pointerDown = pressed;
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = Godot.MouseButton.Left, Pressed = pressed, Position = point, GlobalPosition = point });
        }
        private void Move(Vector2 point, bool pressed)
        {
            GetViewport().WarpMouse(point);
            Input.ParseInputEvent(new InputEventMouseMotion { Position = point, GlobalPosition = point,
                ButtonMask = pressed ? MouseButtonMask.Left : (MouseButtonMask)0 });
        }
        private void Shot(string label)
        {
            if (shots == null) return;
            Directory.CreateDirectory(shots);
            ViewportCapture.SavePng(GetViewport(), Path.Combine(shots, label + ".png"));
        }
        private void Check(bool ok, string detail)
        {
            if (ok) passed++; else failed++;
            GameLogger.Log("TEST", (ok ? "[PASS] " : "[FAIL] ") + detail);
        }
        private void Finish()
        {
            if (finished) return;
            finished = true;
            if (pointerDown) MouseButton(false, target);
            if (UiLightController.Instance != null) UiLightController.Instance.UpdateLightPos(new Vector2(-1000, -1000));
            if (map != null && RpgWorldHost.Current?.View != null) SetMode(previousMode);
            GameLogger.Log("TEST", $"[TEST_RESULT] name=stas_lamp passed={passed} failed={failed}");
            ApplicationExit.Quit(GetTree(), failed == 0 ? 0 : 1);
        }
    }
}
