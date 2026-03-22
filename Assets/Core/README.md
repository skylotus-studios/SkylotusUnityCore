# Skylotus Unity Core

Production-ready core systems library for Unity 6 by **Skylotus Studios**. Drop into any project as a UPM package and get 16 battle-tested systems with zero scene setup required.

---

## Installation

### Step 1 — Install Dependencies

This package requires **LitMotion** and several Unity packages. Add LitMotion via OpenUPM or Git URL:

```
https://github.com/AnnulusGames/LitMotion.git?path=src/LitMotion/Assets/LitMotion
```

### Step 2 — Add the Package

**Option A — Git URL (recommended)**

Add to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.skylotus.core": "https://github.com/skylotus-studios/SkylotusUnityCore.git",
    "com.unity.inputsystem": "1.11.2",
    "com.unity.textmeshpro": "4.0.0",
    "com.unity.addressables": "2.3.1"
  }
}
```

To lock to a specific version, append a tag or commit:

```json
"com.skylotus.core": "https://github.com/skylotus-studios/SkylotusUnityCore.git#v1.0.0"
```

**Option B — Local Package**

Clone the repo into your `Packages/` directory:

```bash
cd YourProject/Packages
git clone https://github.com/skylotus-studios/SkylotusUnityCore.git com.skylotus.core
```

Then reference it in `manifest.json`:

```json
"com.skylotus.core": "file:com.skylotus.core"
```

### Step 3 — Reference the Assembly

In your game's `.asmdef`, add:

```json
{
  "references": [
    "Skylotus.Core.Runtime"
  ]
}
```

If you use LitMotion directly in your own scripts, also add `"LitMotion"` and `"LitMotion.Extensions"` to references.

All code lives under the `Skylotus` namespace.

---

## Quick Start

1. Create an empty GameObject in your boot scene
2. Attach `SkylotusBootstrapper`
3. Assign your `InputActionAsset` (optional)
4. All systems auto-register with `ServiceLocator` and persist across scenes

```csharp
using Skylotus;

// Access any system from anywhere
var audio = ServiceLocator.Get<AudioManager>();
audio.PlaySFX(myClip);

var save = ServiceLocator.Get<SaveSystem>();
save.Save("slot1", myData);
```

---

## Systems Reference

### EventBus

Type-safe publish/subscribe with zero GC (struct events), priority ordering, and one-shot subscriptions.

```csharp
// Define events as structs
public struct OnPlayerDied : IGameEvent
{
    public int PlayerId;
    public Vector3 Position;
}

// Subscribe
EventBus.Subscribe<OnPlayerDied>(evt =>
    Debug.Log($"Player {evt.PlayerId} died at {evt.Position}"));

// Subscribe once (auto-unsubscribes after delivery)
EventBus.SubscribeOnce<OnPlayerDied>(evt => SpawnDeathVFX(evt.Position));

// Publish immediately
EventBus.Publish(new OnPlayerDied { PlayerId = 1, Position = transform.position });

// Priority ordering (higher runs first)
EventBus.Subscribe<OnPlayerDied>(HandleUI, priority: 10);
EventBus.Subscribe<OnPlayerDied>(HandleGameplay, priority: 5);

// Deferred queue (delivered during next Update)
EventBus.Enqueue(new OnPlayerDied { PlayerId = 1 });
```

### AudioManager

Channel-based volume, pooled SFX sources, music crossfade, spatial audio, pitch variance.

```csharp
var audio = ServiceLocator.Get<AudioManager>();

audio.PlayMusic(bgmClip);                        // crossfades from current
audio.PlayMusic(bossClip, fadeDuration: 2f);
audio.StopMusic();

audio.PlaySFX(explosionClip);
audio.PlaySFX(hitClip, pitchVariance: 0.1f);      // random pitch ±10%
audio.PlaySFXAtPosition(hitClip, enemy.position); // spatialized
audio.PlayUI(clickClip);

audio.SetVolume(AudioChannel.Music, 0.7f);
audio.SetVolume(AudioChannel.Master, 0.5f);
```

### SaveSystem

Slot-based saves with AES-256 encryption, format versioning, and metadata.

```csharp
var save = ServiceLocator.Get<SaveSystem>();

save.Save("autosave", new PlayerData { Level = 5, Gold = 1200 });
var data = save.Load<PlayerData>("autosave");

var slots = save.GetAllSlots();                 // ["autosave", "slot1"]
var info = save.GetSlotInfo("autosave");         // (timestamp, version)
save.DeleteSlot("slot1");

// Quick save via PlayerPrefs (small values only)
SaveSystem.QuickSave("lastLevel", "Forest");
var level = SaveSystem.QuickLoad("lastLevel");
```

### InputManager

Wraps Unity Input System with device detection, context switching, runtime rebinding.

```csharp
var input = ServiceLocator.Get<InputManager>();

// Context switching
input.SwitchActionMap("Gameplay");
input.SwitchActionMap("UI");
input.SwitchActionMap("Vehicle");

// Bind callbacks
input.BindAction("Jump", ctx => Jump());
input.BindAction("Move",
    onPerformed: ctx => Move(ctx.ReadValue<Vector2>()),
    onCanceled:  ctx => Move(Vector2.zero));

// Read values directly
var moveDir = input.ReadValue<Vector2>("Move");
if (input.WasPerformed("Fire")) Shoot();

// Device change detection
input.OnDeviceChanged += device =>
{
    if (device == InputDeviceType.Gamepad) ShowGamepadPrompts();
};

// Runtime rebinding
input.StartRebind("Jump", 0,
    onComplete: () => UpdateBindingUI(),
    onCanceled: () => Debug.Log("Rebind canceled"));

string jumpKey = input.GetBindingDisplayString("Jump"); // "Space" or "A"
```


### GameStateMachine

FSM with enter/exit/update callbacks, transition guards, and push/pop for overlays.

```csharp
var gsm = ServiceLocator.Get<GameStateMachine>();

gsm.RegisterState(GameStateType.Gameplay,
    onEnter:  () => { Time.timeScale = 1f; EnablePlayerInput(); },
    onExit:   () => DisablePlayerInput(),
    onUpdate: () => CheckWinCondition());

gsm.RegisterState(GameStateType.Paused,
    onEnter: () => Time.timeScale = 0f,
    onExit:  () => Time.timeScale = 1f);

gsm.TransitionTo(GameStateType.Gameplay);
gsm.PushState(GameStateType.Paused);    // overlay
gsm.PopState();                          // back to Gameplay

// Listen via EventBus
EventBus.Subscribe<OnGameStateChangedEvent>(evt =>
    Debug.Log($"{evt.Previous} -> {evt.Current}"));
```

### TimeManager

Named timers, cooldowns, hit-stop, slow motion.

```csharp
var time = ServiceLocator.Get<TimeManager>();

time.CreateTimer("bomb", 5f, () => Explode());
time.CreateTimer("respawn", 3f, () => Respawn(),
    onTick: progress => respawnBar.fillAmount = progress);

float remaining = time.GetTimerRemaining("bomb");
time.CancelTimer("bomb");

// Cooldowns
if (time.StartCooldown("fireball", 2f))
    CastFireball();

if (!time.IsOnCooldown("dash"))
    Dash();

// Effects
time.HitStop(0.05f);              // freeze frame on impact
time.SlowMotion(0.3f, 2f);        // 30% speed for 2 seconds
time.Pause();
time.Resume();
```

### DialogueSystem

Branching dialogue trees with conditions, choices, localization, and event hooks.

```csharp
var dialogue = ServiceLocator.Get<DialogueSystem>();

dialogue.RegisterCondition("has_key", () => inventory.Has("key"));
dialogue.RegisterEvent("give_reward", () => inventory.Add("gold", 50));

dialogue.OnShowNode += node =>
{
    speakerText.text = node.Speaker;
    bodyText.text = dialogue.GetNodeText(node); // supports "loc:" prefix
};

dialogue.OnShowChoices += choices =>
{
    for (int i = 0; i < choices.Count; i++)
        CreateChoiceButton(choices[i].Text, i);
};

dialogue.OnDialogueEnded += () => HideDialogueUI();

dialogue.StartDialogue("npc_blacksmith");
dialogue.Advance();           // next line
dialogue.SelectChoice(0);     // pick a choice
dialogue.Skip();              // end early
```

### SkylotusSceneManager

Async scene loading with loading screens, progress callbacks, and navigation history.

```csharp
var scenes = ServiceLocator.Get<SkylotusSceneManager>();

scenes.LoadScene("Level_01");
scenes.LoadScene("BossArena", showLoadingScreen: true);
scenes.LoadSceneAdditive("UI_Overlay");
scenes.UnloadScene("UI_Overlay");
scenes.GoBack();                 // history-based back
scenes.ReloadCurrentScene();

scenes.OnProgress += pct => loadBar.fillAmount = pct;
scenes.OnSceneLoaded += name => Debug.Log($"Ready: {name}");
```

### Localization

JSON language files, variable interpolation, pluralization, auto-updating TMP labels.

```csharp
var loc = ServiceLocator.Get<LocalizationSystem>();

loc.SetLanguage("fr");
string text = loc.Get("menu.play");                       // "Jouer"
string greet = loc.Get("greeting", ("name", "Alex"));     // "Bonjour, Alex!"
string items = loc.GetPlural("items.count", 5,
                   ("count", 5));                          // "5 objets"

// Auto-update TMP labels: attach LocalizedText component, set key in inspector
```

### UIManager

Screen stack, animated transitions, modal popups, back navigation.

```csharp
var ui = ServiceLocator.Get<UIManager>();

ui.RegisterScreen("MainMenu", mainMenuScreen);
ui.RegisterScreen("Settings", settingsScreen);

ui.ShowScreen("MainMenu");
ui.ShowScreen("Settings");  // pushes MainMenu to stack
ui.GoBack();                // returns to MainMenu
ui.ClearStackAndShow("MainMenu");

ui.ShowModal(confirmPopup);
ui.CloseModal(confirmPopup);
ui.CloseAllModals();
```

### ObjectPool

Per-prefab pools with warm-up, auto-expand, async pre-warm, and IPoolable lifecycle callbacks.

```csharp
var pool = ServiceLocator.Get<ObjectPool>();

pool.CreatePool(bulletPrefab, initialCount: 20, maxSize: 200);
pool.PrewarmAsync(vfxPrefab, totalCount: 50, batchSize: 5);  // spread across frames

var bullet = pool.Spawn(bulletPrefab, firePoint.position, firePoint.rotation);
pool.Despawn(bullet, delay: 3f);
pool.DespawnAll(bulletPrefab);
pool.DestroyPool(bulletPrefab);         // fully remove a pool

// IPoolable lifecycle hooks
public class Bullet : MonoBehaviour, IPoolable
{
    public void OnSpawnFromPool()  => trail.Clear();
    public void OnReturnToPool()   => rb.linearVelocity = Vector3.zero;
}
```

### NotificationSystem

Queued toasts with priority stacking, click handlers, and achievement style.

```csharp
var notif = ServiceLocator.Get<NotificationSystem>();

notif.Notify("Game saved!");
notif.Notify("Connection lost", NotificationType.Error);
notif.Notify("Click for details", () => ShowDetails(), NotificationType.Info);
notif.Achievement("First Blood!", trophyIcon, duration: 5f);
notif.DismissAll();

notif.OnNotificationShow += n => CreateToastUI(n);
notif.OnNotificationHide += n => DestroyToastUI(n);
```

### DebugConsole

Press **` (backtick)** in-game to toggle. Type `help` for all commands.

```csharp
DebugConsole.Register("god", "Toggle god mode", args =>
{
    player.Invincible = !player.Invincible;
    DebugConsole.Print($"God mode: {player.Invincible}");
});

DebugConsole.Register("spawn", "Spawn enemy (spawn <type> <count>)", args =>
{
    var type = args.Length > 0 ? args[0] : "basic";
    var count = args.Length > 1 ? int.Parse(args[1]) : 1;
    for (int i = 0; i < count; i++) EnemySpawner.Spawn(type);
});
```

Built-in commands: `help`, `clear`, `fps`, `timescale`, `log_level`, `scene`, `gc`, `quit`, `state`, `volume`, `lang`, `saves`, `notify`, `tween_count`

---

## Architecture

```
ServiceLocator (static)          EventBus (static)
      │                                │
      ├── AudioManager          Subscribe / Publish
      ├── SaveSystem               IGameEvent structs
      ├── LocalizationSystem
      ├── InputManager          SkylotusBootstrapper
      ├── UIManager                 │
      ├── SkylotusSceneManager      ├── Creates all systems
      ├── GameStateMachine          ├── Registers with ServiceLocator
      ├── TimeManager               ├── DontDestroyOnLoad
      ├── DialogueSystem            ├── Initializes LitMotion
      ├── NotificationSystem        └── Processes EventBus queue
      └── ObjectPool
```

All systems are decoupled. They communicate through `EventBus` events and are accessed via `ServiceLocator.Get<T>()`. No system holds a direct reference to another.

---