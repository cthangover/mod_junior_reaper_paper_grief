namespace Cthangover.Mods.JuniorReaper.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;
    using Cthangover.Core.Bootstrap;
    using Cthangover.Core.Factories.Impls;
    using Cthangover.Core.Games;
    using Cthangover.Core.Mods.Resolvers;
    using Cthangover.Core.Quests;
    using Cthangover.Core.Scenes;
    using Cthangover.Core.Settings;
    using Cthangover.Core.UI;
    using Cthangover.Core.Utils;
    using Cthangover.Mods.Dialog;
    using Cthangover.Mods.Interface.View;
    using Cthangover.Mods.Rpg;
    using Godot;

    /// <summary>
    /// Plays a declarative route through the actual maps and point-and-click scenes.
    /// Assertions read public runtime flags, inventory and quest data. Named snapshots
    /// check selective reset and idempotent revisits; a free save slot is deleted after
    /// the real menu/load round trip. The route never writes production story flags.
    /// </summary>
    public partial class InvestigationTestDriver : Node
    {
        private readonly List<JsonElement> steps = new();
        private readonly Dictionary<string, Dictionary<string, string>> snapshots = new();
        private readonly string shots;
        private readonly double timeoutSeconds = 600;
        private string startupFailure;
        private string saveSlot;
        private string savedScene;
        private Vector2I savedCell;
        private RpgDirection savedFacing;
        private Dictionary<string, string> savedFlags;
        private Dictionary<string, int> savedItems;
        private int savedQuestLevel;
        private QuestStatus savedQuestStatus;
        private int index;
                private int choicesTaken;
        private int idleTicks;
        private int waitTicks;
        private int passed;
        private int failed;
        private double elapsed;
        private double stepStarted;
        private bool started;
        private bool hovering;
        private Key heldKey = Key.None;
        private static DialogBox Box => RpgInput.FindDialogBox();
        private static RpgWorldView View => RpgWorldHost.Current?.View;
        private static QuestBase Quest => (QuestBase)QuestFactory.Instance.Get("jrp_guard_case");

        /// <summary>The route path and screenshot directory are supplied by the host job.</summary>
        public InvestigationTestDriver(string routeFile, string shots)
        {
            this.shots = shots;
            Name = "InvestigationTestDriver";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(routeFile));
                if (doc.RootElement.TryGetProperty("timeout_seconds", out var timeout))
                    timeoutSeconds = Math.Clamp(timeout.GetDouble(), 1, 1800);
                foreach (var step in doc.RootElement.GetProperty("steps").EnumerateArray()) steps.Add(step.Clone());
            }
            catch (Exception ex) { startupFailure = "route could not be read: " + ex.Message; }
            var timer = new Timer { WaitTime = 0.1, Autostart = true };
            timer.Timeout += Tick;
            AddChild(timer);
        }

        private void Tick()
        {
            if (index < 0) return;
            elapsed += 0.1;
            try
            {
                Require(elapsed < timeoutSeconds, "route timeout at step " + index + ": " + CurrentStep());
                if (startupFailure != null) throw new InvalidOperationException(startupFailure);
                if (!Boot.IsReady || string.IsNullOrEmpty(SceneManager.GetCurrentSceneName())) return;
                if (!started)
                {
                    Check(TranslationServer.GetLocale().StartsWith("ru", StringComparison.OrdinalIgnoreCase), "game locale is Russian without changing global settings");
                    CheckScenarioModes();
                    started = true;
                    stepStarted = elapsed;
                }
                Require(elapsed - stepStarted < 45, "step timeout: " + CurrentStep());
                if (waitTicks > 0) { waitTicks--; return; }
                if (heldKey != Key.None)
                {
                    KeyEvent(heldKey, false);
                    heldKey = Key.None;
                    waitTicks = 3;
                    return;
                }
                if (index >= steps.Count) { Finish(); return; }
                if (!Run(steps[index])) return;
                GameLogger.Log("TEST", "[STEP] " + index + " " + CurrentStep());
                index++;
                stepStarted = elapsed;
                choicesTaken = idleTicks = 0;
                hovering = false;
            }
            catch (Exception ex)
            {
                failed++;
                GameLogger.Log("TEST", "[FAIL] step " + index + " " + CurrentStep() + ": " + ex.Message, LogLevel.Error);
                Finish();
            }
        }

        private bool Run(JsonElement step)
        {
            var command = S(step, "do");
            switch (command)
            {
                case "advance": return Advance(step);
                case "next": Box?.NextAction(); waitTicks = 3; return true;
                case "map":
                    Check(View != null && View.Map.Width == I(step, "width") && View.Map.Height == I(step, "height") && View.Map.TileSize == I(step, "tile_size") && View.Map.Zoom == F(step, "zoom") && View.Scene.Zoom == F(step, "zoom"), "RPG map and renderer keep the declared dimensions, TileSize and Zoom");
                    return true;
                case "scene":
                    if (SceneManager.GetCurrentSceneName() != S(step, "id") || GodotSceneService.IsTransitioning) return false;
                    Check(true, "entered " + S(step, "id"));
                    waitTicks = 5;
                    return true;
                case "cell":
                    Check(View != null && View.Player.Cell == new Vector2I(I(step, "x"), I(step, "y")) && View.Player.Facing == Enum.Parse<RpgDirection>(S(step, "dir"), true), S(step, "why"));
                    return true;
                case "place":
                    Require(View != null, "map must exist before positioning a test interaction");
                    View.PlacePlayer(I(step, "x"), I(step, "y"), Enum.Parse<RpgDirection>(S(step, "dir"), true));
                    waitTicks = 3;
                    return true;
                case "key":
                    heldKey = Enum.Parse<Key>(S(step, "key"));
                    KeyEvent(heldKey, true);
                    waitTicks = I(step, "ticks", 1);
                    return true;
                case "click":
                    if (hovering) Shot("hover_step_" + index);
                    Mouse(F(step, "x"), F(step, "y"), hovering);
                    if (!hovering) { hovering = true; waitTicks = 3; return false; }
                    waitTicks = 4;
                    return true;
                case "dialog": Check(Box?.IsRunning == true, "input opened the expected dialog"); return true;
                case "panel": UiRegistry.Toggle(S(step, "id")); waitTicks = 5; return true;
                case "hidden":
                    var instance = View?.Find(S(step, "id"));
                    Check(instance != null && instance.Current.Hidden == step.GetProperty("value").GetBoolean(), S(step, "why"));
                    return true;
                case "flag_not": Check(Flag(S(step, "id")) != S(step, "value"), S(step, "why")); return true;
                case "flag": Check(Flag(S(step, "id")) == S(step, "value"), S(step, "why")); return true;
                case "quest":
                    var checkedQuest = step.TryGetProperty("id", out var questId)
                        ? (QuestBase)QuestFactory.Instance.Get(questId.GetString()) : Quest;
                    Check(checkedQuest.Data.Status == I(step, "level"), S(step, "why"));
                    if (step.TryGetProperty("status", out var status)) Check(checkedQuest.Status.ToString() == status.GetString(), "quest status is " + status.GetString());
                    return true;
                case "item":
                    Check(GameData.Instance.Runtime.Inventory.CheckCount(S(step, "id")) == I(step, "count"), S(step, "why"));
                    return true;
                case "snapshot":
                    snapshots[S(step, "name")] = Capture(step);
                    Check(true, "captured " + S(step, "name"));
                    return true;
                case "same":
                    foreach (var pair in snapshots[S(step, "name")]) Check(ReadValue(pair.Key) == pair.Value, S(step, "why") + " [" + pair.Key + "]");
                    return true;
                case "shot": Shot(S(step, "name")); return true;
                case "wait": waitTicks = I(step, "ticks", 5); return true;
                case "save": Save(); return true;
                case "menu": GameSession.OpenMainMenu(GetTree()); waitTicks = 10; return true;
                case "load":
                    if (RpgWorldHost.Current != null) return false;
                    Check(GameSession.LoadAndEnter(this, saveSlot), "real save is loaded from menu");
                    waitTicks = 10;
                    return true;
                case "restored": return CheckRestored();
                default: throw new InvalidOperationException("unknown route command " + command);
            }
        }

        private void CheckScenarioModes()
        {
            var definitions = ScenarioDefs.CollectDefinitions();
            Require(definitions != null, "public scenario definitions are available");
            CheckScenarioMode(definitions, "jrp_arrival", true);
            CheckScenarioMode(definitions, "jrp_mill_inside", false);
            CheckScenarioMode(definitions, "jrp_wife_house", false);
        }

        private void CheckScenarioMode(Dictionary<string, List<ScenarioDefinition>> definitions, string scene, bool once)
        {
            var found = false;
            foreach (var list in definitions.Values)
                foreach (var definition in list)
                    if (definition.Scene == scene && definition.ModId == "junior_reaper_paper_grief")
                    {
                        found = true;
                        Check(definition.IsOneRun == once, scene + " parsed IsOneRun=" + once);
                    }
            Check(found, "scenario definition exists for " + scene);
        }

        private bool Advance(JsonElement step)
        {
            if (Box?.IsRunning == true)
            {
                idleTicks = 0;
                if (Box.IsWaitingAnswer)
                {
                    Require(step.TryGetProperty("choices", out var choices) && choicesTaken < choices.GetArrayLength(), "unexpected choice; route must declare every answer");
                    var answer = choices[choicesTaken++].GetInt32();
                    Check(Box.TrySelectVariant(answer), "selected answer " + answer);
                }
                else Box.NextAction();
                return false;
            }
            if (GodotSceneService.IsTransitioning || ++idleTicks < 6) return false;
            var expected = step.TryGetProperty("choices", out var declared) ? declared.GetArrayLength() : 0;
            Check(choicesTaken == expected, "all declared dialog choices were reached");
            return true;
        }

        private static Dictionary<string, string> Capture(JsonElement step)
        {
            var result = new Dictionary<string, string>();
            foreach (var key in step.GetProperty("keys").EnumerateArray()) result.Add(key.GetString(), ReadValue(key.GetString()));
            return result;
        }

        private static string ReadValue(string key)
        {
            if (key == "quest.level") return Quest.Data.Status.ToString();
            if (key == "quest.status") return Quest.Status.ToString();
            if (key.StartsWith("item:", StringComparison.Ordinal)) return GameData.Instance.Runtime.Inventory.CheckCount(key.Substring(5)).ToString();
            return Flag(key);
        }

        private void Save()
        {
            if (saveSlot != null) SaveService.DeleteSave(saveSlot);
            saveSlot = SaveService.NextSlotName("junior_reaper_paper_grief");
            savedScene = SceneManager.GetCurrentSceneName();
            Require(View != null, "save checkpoint is on an RPG map");
            savedCell = View.Player.Cell;
            savedFacing = View.Player.Facing;
            savedFlags = new Dictionary<string, string>();
            foreach (var pair in GameData.Instance.Runtime.Flags) savedFlags.Add(pair.Key, pair.Value);
            savedItems = new Dictionary<string, int>();
            foreach (var slot in GameData.Instance.Runtime.Inventory.Items) savedItems[slot.Item.ID] = GameData.Instance.Runtime.Inventory.CheckCount(slot.Item.ID);
            savedQuestLevel = Quest.Data.Status;
            savedQuestStatus = Quest.Status;
            SaveService.Save(saveSlot, rememberSession: false);
            Check(SaveService.ReadPackage("junior_reaper_paper_grief", saveSlot) != null, "save package exists on disk");
            waitTicks = 5;
        }

        private bool CheckRestored()
        {
            if (SceneManager.GetCurrentSceneName() != savedScene || GodotSceneService.IsTransitioning) return false;
            Check(TranslationServer.GetLocale().StartsWith("ru", StringComparison.OrdinalIgnoreCase), "game locale remains Russian after loading the save");
            foreach (var pair in savedFlags) Check(Flag(pair.Key) == pair.Value, "save/load restores flag " + pair.Key);
            foreach (var pair in savedItems) Check(GameData.Instance.Runtime.Inventory.CheckCount(pair.Key) == pair.Value, "save/load restores inventory " + pair.Key);
            Check(Quest.Data.Status == savedQuestLevel && Quest.Status == savedQuestStatus, "save/load restores quest status and journal stage");
            Check(View != null && View.Player.Cell == savedCell && View.Player.Facing == savedFacing, "save/load restores player cell and facing");
            Check(true, "save/load restores scene " + savedScene);
            return true;
        }

        private void Shot(string name)
        {
            if (string.IsNullOrEmpty(shots) || DisplayServer.GetName() == "headless") return;
            Directory.CreateDirectory(shots);
            Check(ViewportCapture.SavePng(GetViewport(), Path.Combine(shots, name + ".png")), "saved screenshot " + name);
        }

        private void Mouse(float x, float y, bool click)
        {
            var viewport = GetViewport();
            var viewBox = SceneContextNode.FindNode<Control>("ViewBox");
            var background = viewBox?.GetNodeOrNull<TextureRect>("Background");
            Require(background != null && background.Size.X > 0 && background.Size.Y > 0, "rendered background frame exists for real clue input");
            var logicalPoint = new Vector2(x, y) * background.Size;
            var point = background.GetGlobalTransformWithCanvas() * logicalPoint;
            GameLogger.Log("TEST", "[" + (click ? "CLICK" : "HOVER") + "] frame=" + background.Size + " local=" + logicalPoint + " viewport=" + point);
            viewport.WarpMouse(point);
            viewport.PushInput(new InputEventMouseMotion { Position = point, GlobalPosition = point });
            if (!click) return;
            viewport.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = point, GlobalPosition = point });
            viewport.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = point, GlobalPosition = point });
        }

        private static void KeyEvent(Key key, bool pressed)
            => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = pressed });

        private void Check(bool ok, string message)
        {
            Require(ok, message);
            passed++;
            GameLogger.Log("TEST", "[PASS] " + message);
        }

        private static void Require(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
        }

        private static string Flag(string key)
            => GameData.Instance.Runtime.Flags.TryGetValue(key, out var value) ? value : null;

        private string CurrentStep() => index >= 0 && index < steps.Count ? steps[index].GetRawText() : "end";
        private static string S(JsonElement e, string key) => e.GetProperty(key).GetString();
        private static int I(JsonElement e, string key, int fallback = 0) => e.TryGetProperty(key, out var value) ? value.GetInt32() : fallback;
        private static float F(JsonElement e, string key) => e.GetProperty(key).GetSingle();

        private void Finish()
        {
            if (heldKey != Key.None) KeyEvent(heldKey, false);
            if (saveSlot != null) SaveService.DeleteSave(saveSlot);
            index = -1;
            GameLogger.Log("TEST", "[TEST_RESULT] name=jrp_investigation passed=" + passed + " failed=" + failed);
            ApplicationExit.Quit(GetTree(), failed == 0 ? 0 : 1);
        }
    }
}
