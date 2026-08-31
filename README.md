# Skylotus Unity Core

Core systems Unity project (**Unity 6000.3.8f1**, URP 2D) by **Skylotus Studios**. Clone it,
delete the `.git` folder, run one configuration command, and you have twelve
`ServiceLocator`-registered systems plus a static event bus, logger and debug console — with
no scene setup required in the scene you are editing.

**Stack:** Unity 6000.3.8f1 · Universal RP 17.3.0 (2D renderer) · Input System 1.18 (no legacy
input) · LitMotion (tweening) · TextMeshPro · Test Framework 1.6.0.

---

## Table of contents

- [What is actually in here](#what-is-actually-in-here)
- [Prerequisites](#prerequisites)
- [Clone and initialize](#clone-and-initialize)
- [Post-clone checklist](#post-clone-checklist)
- [Verification harness](#verification-harness)
- [Bootstrap flow](#bootstrap-flow)
- [Editor auto-bootstrap](#editor-auto-bootstrap)
- [Referencing the assemblies](#referencing-the-assemblies)
- [Systems reference](#systems-reference)
- [Editor entry points](#editor-entry-points)
- [Tests](#tests)
- [Architecture](#architecture)
- [Known open items](#known-open-items)
- [Conventions](#conventions)

---

## What is actually in here

Twelve systems are registered with `ServiceLocator` by `Bootstrapper`, in this order:

| # | System | Kind | Registered |
|---|--------|------|-----------|
| 1 | `SaveSystem` | plain C# | always |
| 2 | `LocalizationSystem` | plain C# | always |
| 3 | `SettingsService` | plain C# | always |
| 4 | `AudioManager` | MonoBehaviour (prefab) | always |
| 5 | `ObjectPool` | MonoBehaviour (prefab) | always |
| 6 | `SkylotusSceneManager` | MonoBehaviour (prefab) | always |
| 7 | `GameStateMachine` | MonoBehaviour (prefab) | always |
| 8 | `TimeManager` | MonoBehaviour (prefab) | always |
| 9 | `InputManager` | MonoBehaviour (prefab) | only with an `InputActionAsset` |
| 10 | `UIManager` | MonoBehaviour (prefab) | always |
| 11 | `DialogueSystem` | MonoBehaviour (prefab) | always |
| 12 | `NotificationSystem` | MonoBehaviour (prefab) | always |

A `ColorPalette` ScriptableObject is also registered when one is assigned on the
`Bootstrapper`. It is an asset, not a system.

Not in that list, because they are not resolved through `ServiceLocator`:

- **`EventBus`** — static, type-safe pub/sub.
- **`GameLogger`** — static, levelled, category-scoped logging.
- **`ServiceLocator`** — static registry.
- **`DebugConsole`** — owns its own singleton, and is compiled out of release players entirely.
- **`BrightnessController`** — lives in the separate `Skylotus.Core.Rendering` assembly and
  installs itself from a `RuntimeInitializeOnLoadMethod`. Nothing places it in a scene.

---

## Prerequisites

1. **Unity 6000.3.8f1.** Install it through Unity Hub. Add the *Windows Build Support (IL2CPP)*
   module if you intend to make release builds — see [Known open items](#known-open-items).
2. **Git** — <https://git-scm.com/downloads>
3. **Git LFS** — <https://git-lfs.com/>. This repository tracks binary assets through LFS
   (see `.gitattributes`); a clone without LFS gets pointer files instead of art and audio.

---

## Clone and initialize

```bash
cd PathToFolder
git clone https://github.com/skylotus-studios/SkylotusUnityCore.git
```

Delete the `.git` folder (usually hidden), then start your project's own history:

```bash
git init
git lfs install
git add -A
git commit -m "Project Init"
git remote add origin https://github.com/user/project.git
git push -u origin master
```

---

## Post-clone checklist

Everything below has to be decided per project. `SkylotusCI.ConfigureProject` handles most of
it in one command and **names in its log every value still sitting on a template default**, so
run it first and read what it says.

### 1. Tell the project who it is

Values resolve in this order, first hit wins:

1. **Environment variables** —
   `SKYLOTUS_COMPANY_NAME`, `SKYLOTUS_PRODUCT_NAME`, `SKYLOTUS_BUNDLE_ID`,
   `SKYLOTUS_BUNDLE_VERSION`, `SKYLOTUS_SCRIPTING_BACKEND`.
2. **A JSON file** at `<project root>/SkylotusProject.json` — beside `Assets/`. Override the
   path with `SKYLOTUS_PROJECT_CONFIG`.
3. **A template default**, which the run reports as `NEEDS A REAL VALUE`.

```json
{
  "companyName": "Your Studio",
  "productName": "Your Game",
  "applicationIdentifier": "com.yourstudio.yourgame",
  "bundleVersion": "0.1.0",
  "scriptingBackend": "IL2CPP",
  "iconPath": "Assets/Art/Icon.png",
  "sortingLayers": ["Background", "Ground", "Entities", "Foreground", "UI", "Overlay"],
  "tags": ["Player", "Enemy"],
  "layers": [{ "index": 8, "name": "Interactable" }]
}
```

Every field is optional. `SkylotusProject.json` **is not in this repository and is not
gitignored** — decide whether your project commits it (reproducible for the whole team) or
keeps it local (per-developer). That decision is still open here.

The template defaults you must replace are `companyName = "Skylotus Studios"`,
`productName = "CoreProject"` and `bundleVersion = "0.1.0"`. The application identifier is
*derived* from the company and product names when you do not supply one, so it is well-formed
but still wrong until those two are real.

### 2. Run the configuration command

```powershell
.\Tools\unity-verify.ps1 -Mode method -Method Skylotus.Editor.SkylotusCI.ConfigureProject
```

Or, from inside the Editor: **Skylotus → Configure New Project**.

It writes bundle identity (Standalone, Android, iOS), the standalone scripting backend,
managed stripping level `Low`, IL2CPP compiler configuration `Release`, and the 2D sorting
layer stack — all through `PlayerSettings` and a `SerializedObject` over `TagManager.asset`,
never by editing YAML. It is idempotent: a second run reports no changes.

### 3. Resolve the licence

`LICENSE` **is a placeholder, not a licence.** Its first line reads
`LICENSE DECISION REQUIRED — DO NOT SHIP THIS FILE AS-IS`. A human with authority over
Skylotus Studios' IP has to decide whether this repository is internal-only or public, and
whether the studio's own code and the vendored third-party assets need different terms. Do not
ship, publish or fork commercially until that file has been replaced.

### 4. Set the application icon

There is none. `ConfigureProject` deliberately does not invent one — a placeholder icon looks
intentional and survives to release. Add a square texture under `Assets/`, point `iconPath` at
it, and re-run.

### 5. Set the save encryption key (optional)

`Bootstrapper._saveEncryptionKey` is empty, so saves are plaintext JSON. Setting it turns on
AES-256-CBC obfuscation. **Read `SaveSystem`'s class summary before you rely on it** — the key
is a `[SerializeField]` on a scene object, so it ships inside the build and is recoverable in
minutes. It is anti-tamper friction, not security.

### 6. Decide what the template leaves open

- IL2CPP is configured but may not be buildable on your machine — see
  [Known open items](#known-open-items).
- `Assets/Resources/` is the only asset-loading path. Fine at this scale; decide the
  Addressables migration point before the asset count grows.
- There is no gameplay layer: no player controller, no camera follow (no Cinemachine), no
  health/damage, no inventory, no AI, no save-data model, no HUD prefab, no netcode.
  `Gameplay.unity` is a Main Camera, an EventSystem and a cursor canvas.

---

## Verification harness

`Tools/unity-verify.ps1` drives the Editor headlessly. **The Unity Editor must be closed** —
Unity locks a project folder to one process, and a batchmode call against an open Editor fails
in a way that reads like a compile error. The script detects a GUI Editor by process and exits
**2** with a clear message rather than letting you misread it.

| Mode | What it does |
|------|--------------|
| `compile` | Opens the project headless. Non-zero exit means scripts failed to compile. |
| `method` | Runs a static method via `-executeMethod` (compiles first). Asset generation, project stamping, validators. |
| `tests` | Runs the test framework for one platform and parses the NUnit XML. |
| `all` | `compile` → EditMode tests → PlayMode tests. The gate. |

```powershell
.\Tools\unity-verify.ps1 -Mode compile
.\Tools\unity-verify.ps1 -Mode method -Method Skylotus.Editor.SkylotusCI.ValidateAssemblyReferences
.\Tools\unity-verify.ps1 -Mode tests -TestPlatform PlayMode
.\Tools\unity-verify.ps1 -Mode all
```

Useful switches: `-Graphics` omits `-nographics` (required for asset generation that touches
the render pipeline, such as the loading-screen canvas or a volume profile), `-UnityExe` points
at a different install, `-TimeoutMinutes` (default 30). Logs land in `Logs/ci/`.

Runs are serialized by a named system mutex, so two callers queue instead of colliding on the
project lock. Always go through the script — a raw `Unity.exe -batchmode` call bypasses the
mutex.

Exit codes: **0** pass, **1** a step failed, **2** the project is locked by the Editor GUI (or
the mutex wait timed out).

---

## Bootstrap flow

`BootScene.unity` holds one `Bootstrapper`. Everything else follows from it.

```
Bootstrapper.Awake
  ├── GameLogger.Initialize                       (first, so everything below can log)
  ├── new SaveSystem(encryptionKey)               → ServiceLocator
  ├── new LocalizationSystem + LoadLanguage("en") → ServiceLocator
  ├── new SettingsService                         → ServiceLocator
  ├── Instantiate(_coreSystemsPrefab)             → DontDestroyOnLoad
  │     AudioManager · ObjectPool · SkylotusSceneManager · GameStateMachine ·
  │     TimeManager · InputManager · UIManager · DialogueSystem ·
  │     NotificationSystem · DebugConsole         → ServiceLocator
  ├── SettingsService.ApplyAll()                  (before the first scene renders)
  └── AddComponent<EventQueueProcessor>           (drives EventBus.ProcessQueue each frame)

Bootstrapper.Start
  └── SkylotusSceneManager.LoadScene(_firstScene, showLoadingScreen: false)
```

### The core systems prefab is the point

Every MonoBehaviour system comes from
**`Assets/Resources/Prefabs/SkylotusCoreSystems.prefab`**, instantiated once at boot.

This matters because a component created at runtime with `AddComponent` gets the *compile-time
default* for every `[SerializeField]`, permanently and unreachably. That is how loading screens
in this template were a silent no-op for a long time: `SkylotusSceneManager._loadingScreen` was
always `null`, so `LoadScene(name, showLoadingScreen: true)` did nothing at all. The prefab is
the only place those values can be authored.

The prefab carries the loading-screen canvas (a `CanvasGroup` and a `Slider` wired into
`SkylotusSceneManager`), the screen and modal container transforms wired into `UIManager`, the
`InputActionAsset` on `InputManager`, the `AudioMixer` on `AudioManager`, and the tuning knobs
on everything else.

**Never hand-edit the prefab YAML.** Regenerate it:

```powershell
.\Tools\unity-verify.ps1 -Mode method -Graphics `
  -Method Skylotus.Editor.SkylotusCI.GenerateCoreSystemsPrefab
```

`-Graphics` is required — the loading-screen canvas needs a render context. The same method
opens `BootScene.unity`, assigns the prefab to the `Bootstrapper`, and saves. The generator
rebuilds the hierarchy from scratch, so **any change you make by dragging in the inspector is
discarded on the next run** — change the generator, not the asset.

Validate an existing prefab without rebuilding it:

```powershell
.\Tools\unity-verify.ps1 -Mode method -Method Skylotus.Editor.SkylotusCI.ValidateCoreSystemsPrefab
```

### Code-construction fallback

If `_coreSystemsPrefab` is null, `Bootstrapper` builds every system in code so an un-migrated
boot scene still runs. It logs a warning, and every inspector value is stuck at its default: no
loading screen, no UI containers, no audio or notification tuning. This path exists for
recovery, not for use.

---

## Editor auto-bootstrap

**Press Play in any scene.** `Assets/Scripts/Editor/EditorBootstrap.cs` notices that the scene
you are in has no `Bootstrapper`, spawns a real one before the scene's own `Awake` calls run,
and leaves you in the scene you were editing.

It is not a parallel boot path: it creates a dormant GameObject, adds an actual `Bootstrapper`,
injects the core systems prefab, the colour palette and the input actions that `BootScene`
would have carried, and activates it. From there the ordinary boot sequence runs, fallback
included, so any change to `Bootstrapper` is picked up for free.

- Booting from `BootScene` normally still initializes exactly once — the hook stands down when
  the scene already has a bootstrapper.
- If you have deliberately set `EditorSceneManager.playModeStartScene`, the hook stands down
  and lets that scene boot.
- The file lives under `Assets/Scripts/Editor/`, so it compiles into `Assembly-CSharp-Editor`
  and has **zero effect on built players**.

This exists because `ServiceLocator.Get<T>()` *throws* when a service is missing — it does not
return null — so any screen that resolves a system in `Awake` dies on its first line in a scene
with no systems.

---

## Referencing the assemblies

Three runtime assemblies ship here:

| Assembly | Contents | References |
|----------|----------|-----------|
| `Skylotus.Core.Runtime` | every core system | `Unity.InputSystem`, `Unity.TextMeshPro`, `LitMotion`, `LitMotion.Extensions` |
| `Skylotus.Core.Rendering` | `BrightnessController` | `Skylotus.Core.Runtime` + the two URP assemblies |
| `Assembly-CSharp` | `Assets/Scripts/MainMenu/**` and anything without an asmdef | auto-referenced |

In your game's `.asmdef`:

```json
{
  "references": [
    "Skylotus.Core.Runtime",
    "LitMotion",
    "LitMotion.Extensions"
  ]
}
```

`Skylotus.Core.Rendering` is separate on purpose: `Skylotus.Core.Runtime` has no URP references
and nothing outside the brightness feature needs them. Add it only if you reference
`BrightnessController` directly.

Namespaces: `Skylotus` (everything core), `Skylotus.Core.UI` (`ColorPalette`, settings widgets),
`Skylotus.Core.Rendering`, `Skylotus.Editor`.

---

## Systems reference

### ServiceLocator

Static registry. `Get<T>()` **throws** `InvalidOperationException` when nothing is registered —
use `TryGet<T>()` where absence is legitimate. Writing `ServiceLocator.Get<T>()?.Foo()` is
misleading: the `?.` can never fire.

```csharp
using Skylotus;

var audio = ServiceLocator.Get<AudioManager>();
audio.PlaySFX(myClip);

if (ServiceLocator.TryGet<SettingsService>(out var settings))
    settings.Flush();

bool up = ServiceLocator.IsRegistered<TimeManager>();
```

`Register<T>`, `RegisterLazy<TInterface, TImplementation>`, `Unregister<T>` and `Reset()` are
also public; `Bootstrapper` owns the normal lifecycle and calls `Reset()` on quit.

### EventBus

Static, type-safe publish/subscribe. Struct events, priority ordering, one-shot subscriptions,
a lifetime-bound subscription helper, and a deferred queue that allocates nothing per event.

```csharp
// Define events as structs
public struct OnPlayerDied : IGameEvent
{
    public int PlayerId;
    public Vector3 Position;
}

// Preferred: the subscription dies with the behaviour, no OnDestroy bookkeeping
public class DeathWatcher : MonoBehaviour
{
    private void Awake() => this.SubscribeWhileAlive<OnPlayerDied>(HandleDeath);

    private void HandleDeath(OnPlayerDied evt) =>
        Debug.Log($"Player {evt.PlayerId} died at {evt.Position}");
}

// Manual subscribe — you own the matching Unsubscribe
EventBus.Subscribe<OnPlayerDied>(HandleDeath);
EventBus.Unsubscribe<OnPlayerDied>(HandleDeath);

// One-shot, auto-unsubscribes after delivery
EventBus.SubscribeOnce<OnPlayerDied>(evt => SpawnDeathVFX(evt.Position));

// Priority — higher runs first
EventBus.Subscribe<OnPlayerDied>(HandleUI, priority: 10);
EventBus.Subscribe<OnPlayerDied>(HandleGameplay, priority: 5);

// Immediate
EventBus.Publish(new OnPlayerDied { PlayerId = 1, Position = transform.position });

// Deferred — delivered on the next EventBus.ProcessQueue, driven every frame by
// EventQueueProcessor. Zero allocations per event, verified by test.
EventBus.Enqueue(new OnPlayerDied { PlayerId = 1 });

int pending = EventBus.QueuedEventCount;
EventBus.Clear<OnPlayerDied>();
EventBus.ClearAll();
```

`SubscribeWhileAlive` is the default path for new code. The static bus outlives scene loads;
subscribers do not, and a leaked handler firing against a destroyed object degrades into
permanent log spam rather than a loud failure.

Built-in events: `OnSceneLoadedEvent`, `OnGameStateChangedEvent`, `OnLanguageChangedEvent`,
`OnAudioVolumeChangedEvent`, `OnInputDeviceChangedEvent`, `OnNotificationEvent`,
`OnSaveCompletedEvent`, `OnDialogueEvent`, `OnDialogueNodeEvent`, `OnSettingsChangedEvent`.

### SettingsService

**The single owner of every settings key**, and of both directions of the flow. It is
constructed and registered by `Bootstrapper`, and `ApplyAll()` runs before the first scene
loads — so a relaunched game comes up at the volume, quality, resolution and brightness the
player chose, without anyone opening the settings screen.

```csharp
var settings = ServiceLocator.Get<SettingsService>();

// Audio (writes through to AudioManager)
settings.SetVolume(AudioChannel.Master, 0.5f);
float music = settings.GetVolume(AudioChannel.Music);

// Video
settings.SetResolutionIndex(0);
settings.SetFullscreenIndex(1);
settings.SetQualityIndex(2);
settings.SetVSync(true);
settings.SetBrightness(0.8f);

string[] resolutions = settings.ResolutionLabels;
string[] windowModes = SettingsService.FullscreenOptions;

// Gameplay
settings.SetLanguageIndex(0);
settings.SetVibration(false);
bool shake = settings.Screenshake;

// Persistence
settings.Flush();            // one disk write; setters never flush
settings.ResetToDefaults();
settings.ApplyAll();         // re-push everything onto Unity
```

Setters do **not** call `PlayerPrefs.Save()` — dragging a slider across its full range performs
at most one disk write. `Bootstrapper` flushes on `OnApplicationPause` and `OnApplicationQuit`.
`SettingsScreen`'s Save button also calls `PlayerPrefs.Save()` directly, which is equivalent but
bypasses the service's dirty flag; routing it through `SettingsService.Flush()` would be tidier.

Key names are constants on this class (`SettingsService.QualityKey`,
`SettingsService.AudioVolumeKey(AudioChannel.Master)`, …). No `Settings*Tab` class touches
`PlayerPrefs`; the tabs are pure view code.

### AudioManager

Channel-based volume through an `AudioMixer`, pooled SFX sources, music and ambience crossfade,
spatial audio, pitch variance, a dedicated voice source.

```csharp
var audio = ServiceLocator.Get<AudioManager>();

audio.PlayMusic(bgmClip);                          // crossfades from current
audio.PlayMusic(bossClip, fadeDuration: 2f);
audio.StopMusic();

audio.PlayAmbience(rainClip);
audio.StopAmbience();

audio.PlayVoice(lineClip);
audio.StopVoice();

audio.PlaySFX(explosionClip);
audio.PlaySFX(hitClip, pitchVariance: 0.1f);       // random pitch ±10%
audio.PlaySFXAtPosition(hitClip, enemy.position);  // spatialized
audio.PlayUI(clickClip);

audio.SetVolume(AudioChannel.Music, 0.7f);
float master = audio.GetVolume(AudioChannel.Master);
```

`AudioChannel` is `Master, Music, SFX, UI, Ambience, Voice` — every member has a mixer group
and a play path. Volumes are written to exposed mixer parameters on a logarithmic curve
(`AudioManager.LinearToDecibels` / `DecibelsToLinear`, floor
`AudioManager.MinimumVolumeDecibels` = −80 dB), so a slider at 50% sounds like half volume. The
exposed parameter name for a channel is `AudioManager.GetVolumeParameter(channel)`, i.e.
`"MasterVolume"`.

The mixer is at `Assets/Resources/Audio/SkylotusAudioMixer.mixer`. It is assigned onto the
prefab by the prefab generator, with a `Resources.Load` fallback for the code-construction
path. Regenerate it with `SkylotusCI.GenerateAudioMixer`; validate with
`SkylotusCI.ValidateAudioMixer`.

Every pooled source has `pitch`, `volume`, `spatialBlend` and `loop` reset on acquisition, so a
UI sound played straight after a pitch-varied SFX plays at pitch 1.0.

### SaveSystem

Slot-based saves in `Application.persistentDataPath/Saves`, with crash-safe atomic writes,
versioning with registerable migrations, optional AES-256 obfuscation, and async I/O.

```csharp
var save = ServiceLocator.Get<SaveSystem>();

[System.Serializable]
public class PlayerData
{
    public int Level;
    public int Gold;
}

save.Save("autosave", new PlayerData { Level = 5, Gold = 1200 });
var data = save.Load<PlayerData>("autosave");

if (save.TryLoad<PlayerData>("slot1", out var loaded))
    Apply(loaded);

// Async — await from the main thread; never block on the result
await save.SaveAsync("autosave", data);
var async = await save.LoadAsync<PlayerData>("autosave");

// Migrations — register at boot, before any load
save.RegisterMigration(1, 2, json => json.Replace("\"hp\"", "\"health\""));

bool exists = save.SlotExists("autosave");
string[] slots = save.GetAllSlots();
var info = save.GetSlotInfo("autosave");   // (DateTime timestamp, int version)?
save.DeleteSlot("slot1");

// Small values via PlayerPrefs
SaveSystem.QuickSave("lastLevel", "Forest");
string level = SaveSystem.QuickLoad("lastLevel");
```

Each slot occupies up to three files: `slot.sav`, `slot.bak` (the previous good save, kept
automatically), and `slot.tmp` (exists only mid-write). A write goes to `.tmp`, is flushed to
the device, and is swapped onto `.sav` with `File.Replace`. Losing power leaves either the old
save or the new one readable, never a half-written one; an unparseable primary falls back to
`.bak` and says so.

A version mismatch is **not** deserialized optimistically. The system walks registered
migrations, and refuses the load loudly when no path exists.

**`JsonUtility` constraints** — these shape your save models, so read them before designing
one. No `Dictionary<K,V>` (use parallel lists or a list of key/value structs), no polymorphism
(a base-typed field loses its derived data), no `Nullable<T>`, no top-level arrays or bare
primitives (wrap in a class or struct), no properties, statics, consts or readonly fields. A
null string or list is written as empty, so a loaded object cannot tell "was null" from "was
empty".

**Encryption is not security.** See [step 5 of the checklist](#5-set-the-save-encryption-key-optional).

### InputManager

Wraps the Input System with device detection, action-map switching, and runtime rebinding.

`Assets/InputSystem_Actions.inputactions` defines exactly two maps:

- **`Player`** — `Move`, `Look`, `Attack`, `Interact`, `Crouch`, `Jump`, `Previous`, `Next`, `Sprint`
- **`UI`** — `Navigate`, `Submit`, `Cancel`, `Point`, `Click`, `RightClick`, `MiddleClick`,
  `ScrollWheel`, `TrackedDevicePosition`, `TrackedDeviceOrientation`, `TabLeft`, `TabRight`

Control schemes: `Keyboard&Mouse`, `Gamepad`, `Touch`, `Joystick`, `XR`.

```csharp
var input = ServiceLocator.Get<InputManager>();

// Context switching — these are the map names that exist
input.SwitchActionMap("Player");
input.SwitchActionMap("UI");

input.EnableActionMap("UI");
input.DisableActionMap("Player");

// Bind callbacks
input.BindAction("Jump", ctx => Jump());
input.BindAction("Move",
    onPerformed: ctx => Move(ctx.ReadValue<Vector2>()),
    onCanceled:  ctx => Move(Vector2.zero));
input.UnbindAction("Jump");

// Read values directly
Vector2 moveDir = input.ReadValue<Vector2>("Move");
if (input.WasPerformed("Attack")) Shoot();

// Device change detection
input.OnDeviceChanged += device =>
{
    if (device == InputDeviceType.Gamepad) ShowGamepadPrompts();
};

// Runtime rebinding
input.StartRebind("Jump", 0,
    onComplete: () => UpdateBindingUI(),
    onCanceled: () => Debug.Log("Rebind canceled"));

input.ResetBinding("Jump", 0);
input.ResetAllBindings();

string jumpKey = input.GetBindingDisplayString("Jump");   // "Space" or "A"
string glyph   = input.GetGlyphPath("Jump");
```

`InputManager` does no work in `Awake`. `Bootstrapper` calls `Initialize()` once the
`InputActionAsset` is in place, so the asset is cloned and saved rebinds are loaded at a
controlled moment. `InputDeviceType` is `KeyboardMouse, Gamepad, Touch`.

### GameStateMachine

FSM with enter/exit/update callbacks, transition guards, and push/pop for overlays.

```csharp
var gsm = ServiceLocator.Get<GameStateMachine>();
var time = ServiceLocator.Get<TimeManager>();

gsm.RegisterState(GameStateType.Gameplay,
    onEnter:  () => EnablePlayerInput(),
    onExit:   () => DisablePlayerInput(),
    onUpdate: () => CheckWinCondition());

// TimeManager is the sole writer of Time.timeScale. Never assign it here.
gsm.RegisterState(GameStateType.Paused,
    onEnter: () => time.Pause(),
    onExit:  () => time.Resume());

gsm.BlockTransition(GameStateType.GameOver, GameStateType.Paused);

gsm.TransitionTo(GameStateType.Gameplay);
gsm.PushState(GameStateType.Paused);    // overlay
gsm.PopState();                          // back to Gameplay

if (gsm.IsInAny(GameStateType.Paused, GameStateType.Dialogue)) return;

EventBus.Subscribe<OnGameStateChangedEvent>(evt =>
    Debug.Log($"{evt.Previous} -> {evt.Current}"));
```

`GameStateType` is `Boot, MainMenu, Loading, Gameplay, Paused, Cutscene, Dialogue, GameOver,
Victory`. Extend the enum for project-specific states.

> **Do not write `Time.timeScale` in a state callback.** That pattern is what this template
> used to teach, and it is exactly the bug `TimeManager` was rebuilt to prevent: a state exit
> setting `1f` silently destroys an in-flight slow motion or a modal's pause. Use
> `TimeManager.Pause()` / `Resume()`, or `PushPause` / `ReleasePause` when the pause has an
> owner.

### TimeManager

Named timers, cooldowns, hit-stop, slow motion — and **the only writer of `Time.timeScale` in
this project.**

```csharp
var time = ServiceLocator.Get<TimeManager>();

// Timers
time.CreateTimer("bomb", 5f, () => Explode());
time.CreateTimer("respawn", 3f, () => Respawn(),
    onTick: progress => respawnBar.fillAmount = progress);
time.CreateTimer("tick", 1f, () => Tick(), loop: true);

float remaining = time.GetTimerRemaining("bomb");
bool running = time.IsTimerActive("bomb");
time.PauseTimer("bomb");
time.ResumeTimer("bomb");
time.CancelTimer("bomb");

// Cooldowns — StartCooldown returns false while still cooling
if (time.StartCooldown("fireball", 2f))
    CastFireball();

if (!time.IsOnCooldown("dash"))
    Dash();

float left = time.GetCooldownRemaining("fireball");
time.ResetCooldown("dash");

// Effects
time.HitStop(0.05f);              // freeze frame on impact
time.SlowMotion(0.3f, 2f);        // 30% speed for 2 seconds
time.GameTimeScale = 0.5f;        // sustained slow-mo

// Pause tokens — overlapping requests compose instead of overwriting
time.PushPause(this);
time.ReleasePause(this);
bool mine = time.HasPauseRequest(this);
time.ReleaseAllPauses();          // hard reset, e.g. a scene transition mid-modal

// Convenience pair for "the game is paused", with an internal owner
time.Pause();
time.Resume();
```

`EffectiveTimeScale` is computed as **hit-stop > pause tokens > `GameTimeScale`**. So:

- Opening a pausing modal during `SlowMotion(0.3f, 10f)` and closing it returns to **0.3**, not
  1.0.
- Two overlapping pausing modals: closing the first leaves the game paused.
- A hit-stop that expires during a pause does not resume the game.
- Destroyed Unity objects are dropped from the token set automatically, so a leaked request
  cannot freeze the game forever.

`IsPaused`, `PauseRequestCount`, `IsHitStopped`, `UnscaledTime` and `DeltaTime` are read-only.

### SkylotusSceneManager

Async scene loading with a real loading screen, progress callbacks, and navigation history.

```csharp
var scenes = ServiceLocator.Get<SkylotusSceneManager>();

scenes.LoadScene("Level_01");
scenes.LoadScene("BossArena", showLoadingScreen: true);
scenes.LoadScene("Credits", showLoadingScreen: false, addToHistory: false);
scenes.LoadSceneAdditive("UI_Overlay");
scenes.UnloadScene("UI_Overlay", onComplete: () => Debug.Log("gone"));
scenes.GoBack();                 // history-based back
scenes.ReloadCurrentScene();

string current = scenes.CurrentScene;
bool busy = scenes.IsLoading;

scenes.OnProgress += pct => loadBar.fillAmount = pct;
scenes.OnSceneLoaded += name => Debug.Log($"Ready: {name}");
scenes.OnSceneUnloading += name => SaveCheckpoint();
```

`LoadScene` fades the overlay in, holds `allowSceneActivation` until progress reaches 0.9,
enforces a minimum display time so the screen cannot flicker, drives the progress bar 0→1, and
fades out. The overlay and bar come from the core systems prefab. If a load requests a loading
screen and none is assigned, it logs a warning naming the prefab rather than silently doing
nothing.

The class is named `SkylotusSceneManager`, not `SceneManager`, so it does not collide with
`UnityEngine.SceneManagement.SceneManager`. No file in this project needs to fully qualify
Unity's version.

### Localization

JSON language files under `Assets/Resources/Localization/`, variable interpolation, CLDR plural
rules, and auto-updating TMP labels.

```csharp
var loc = ServiceLocator.Get<LocalizationSystem>();

loc.LoadLanguage("fr");
loc.SetLanguage("fr");
loc.SetFallbackLanguage("en");

string text  = loc.Get("menu.play");
string greet = loc.Get("greeting", ("name", "Alex"));          // "Hello, Alex!"
string score = loc.Get("hud.score", ("score", 40));            // nested keys are dotted
string items = loc.GetPlural("items.count", 5, ("count", 5));  // "5 items"

bool has = loc.HasKey("menu.quit");
string[] languages = loc.GetAvailableLanguages();

// Plural rules per language
loc.RegisterPluralRule("pl", PluralRuleSet.Polish);
PluralCategory cat = loc.GetPluralCategory("ru", 3);

// Cross-language validation
var report = loc.ValidateLanguages("en");
if (!report.IsValid)
    Debug.LogWarning(report.ToString());

loc.LogValidationReport("en");
loc.OnLanguageChanged += code => RefreshUI();
```

The file format is a JSON object of string values, optionally nested — a nested object
flattens to dotted keys, so `{"hud": {"score": "Score: {score}"}}` is `hud.score`. Parsing is
strict and real: unicode escapes, escaped quotes and nesting all work, and a malformed file
raises `LocalizationParseException` with the source name, line and column rather than
half-loading. Plural forms are pipe-separated in one value (`"{count} item|{count} items"`).

Built-in rule sets: `English`, `French`, `SingleForm`, `Polish`, `Russian`, `Arabic`, `Czech`.
`PluralRuleSet` takes a name, its form list, and a selector function, so a language not in that
list is a few lines rather than a fork.

For auto-updating labels, attach a `LocalizedText` component to a TMP label and set the key in
the inspector.

Only `en.json` ships. **Non-Latin scripts need a TMP font atlas per script**, and neither this
system nor bare TMP can render RTL — if you commit to a second shipped language, especially an
RTL one, migrate to `com.unity.localization` rather than hardening this. With three call sites
and zero `LocalizedText` components in any scene today, that migration will never be cheaper.

### UIManager

Screen stack, animated transitions, modal popups, back navigation.

```csharp
var ui = ServiceLocator.Get<UIManager>();

ui.RegisterScreen("MainMenu", mainMenuScreen);
ui.RegisterScreen("Settings", settingsScreen);

ui.ShowScreen("MainMenu");
ui.ShowScreen("Settings");          // pushes MainMenu onto the stack
ui.ShowScreenImmediate("MainMenu"); // no fade
ui.GoBack();                        // returns to MainMenu
ui.ClearStackAndShow("MainMenu");

ui.ShowModal(confirmPopup);
ui.CloseModal(confirmPopup);
ui.CloseAllModals();

UIScreen top = ui.CurrentScreen;
int depth = ui.StackDepth;
```

Derive screens from `UIScreen` and override `OnShow`, `OnHide`, `OnFocus`, `OnFocusLost` and
`OnBackPressed` (return `false` to block back).

Tick **Pause Game When Open** on a `UIScreen` and `UIManager` takes a `TimeManager` pause token
in its name when the modal opens and releases exactly that token when it closes. It never
writes `Time.timeScale`, and never touches a pause it does not own — so closing a
non-pausing modal cannot stomp an in-flight slow motion.

`ScreenContainer` and `ModalContainer` are optional persistent overlay transforms supplied by
the core systems prefab. Registration deliberately never re-parents a screen into them: screens
are authored inside `MainMenu.unity`, and reparenting them under a `DontDestroyOnLoad` canvas
would leak them across scene loads. Use them for runtime-instantiated persistent UI — a global
pause modal, a toast layer — by parenting instances yourself. **Nothing in the project reads
them today**; delete them rather than leaving dead serialized fields if your project has no use
for them.

### ObjectPool

Per-prefab pools with warm-up, auto-expand, async pre-warm, and `IPoolable` lifecycle callbacks.

```csharp
var pool = ServiceLocator.Get<ObjectPool>();

pool.CreatePool(bulletPrefab, initialCount: 20, maxSize: 200);
pool.PrewarmAsync(vfxPrefab, totalCount: 50, batchSize: 5);   // spread across frames

var bullet = pool.Spawn(bulletPrefab, firePoint.position, firePoint.rotation);
var rb     = pool.Spawn<Rigidbody2D>(bulletPrefab, firePoint.position, firePoint.rotation);

pool.Despawn(bullet, delay: 3f);
pool.DespawnAll(bulletPrefab);
pool.DestroyPool(bulletPrefab);

int available = pool.GetAvailableCount(bulletPrefab);
int active    = pool.GetActiveCount(bulletPrefab);
int total     = pool.GetTotalCount(bulletPrefab);

public class Bullet : MonoBehaviour, IPoolable
{
    public void OnSpawnFromPool() => trail.Clear();
    public void OnReturnToPool()  => rb.linearVelocity = Vector3.zero;
}
```

**Scene lifetime.** The pool and its per-prefab containers live on a `DontDestroyOnLoad` root,
so *inactive* instances survive scene loads. An instance spawned with a `null` parent is
explicitly moved to the **active** scene, so a single-mode `LoadScene` destroys it — and the
pool purges the destroyed reference on `sceneUnloaded` / `sceneLoaded`, so counts stay honest
instead of accumulating tombstones. Pass a parent under the pool root if you want an active
instance to persist.

`maxSize` is a real cap on `active + available`, not on active alone. `Despawn` is O(1) —
ownership is tracked in an instance-ID dictionary, with no scan over the pool set.

### NotificationSystem

Queued toasts with auto-dismiss timers, click handlers, and an achievement style.

```csharp
var notif = ServiceLocator.Get<NotificationSystem>();

notif.Notify("Game saved!");
notif.Notify("Connection lost", NotificationType.Error);
notif.Notify("New item", trophyIcon);
notif.Notify("Click for details", () => ShowDetails());
notif.Achievement("First Blood!", trophyIcon, duration: 5f);
notif.Dismiss(id);
notif.DismissAll();

notif.OnNotificationShow    += n => CreateToastUI(n);
notif.OnNotificationHide    += n => DestroyToastUI(n);
notif.OnNotificationClicked += n => Debug.Log(n.Message);
```

`NotificationType` is `Info, Success, Warning, Error, Achievement`. Every `Notify` overload
returns the `Notification` it created. Delivery is FIFO with a `_maxVisible` cap (3 on the
prefab): anything over the cap waits in a queue and is shown as a slot frees up. There is no
priority field despite what the class summary claims.

This system raises events; **it does not draw anything** — supply your own toast UI from
`OnNotificationShow`.

### DebugConsole

Press **`` ` `` (backtick)** in-game to toggle. Type `help` for the command list.

```csharp
DebugConsole.Register("god", "Toggle god mode", args =>
{
    player.Invincible = !player.Invincible;
    DebugConsole.Print($"God mode: {player.Invincible}");
});

DebugConsole.Register("spawn", "Spawn enemy (spawn <type> <count>)", args =>
{
    var type  = args.Length > 0 ? args[0] : "basic";
    var count = args.Length > 1 ? int.Parse(args[1]) : 1;
    for (int i = 0; i < count; i++) EnemySpawner.Spawn(type);
});
```

Built-in commands: `help`, `clear`, `fps`, `timescale`, `log_level`, `scene`, `gc`, `quit`,
`state`, `volume`, `lang`, `saves`, `notify`, `tween_count`.

**The console is compiled out of release players.** The class body, the bootstrapper's creation
block, and command registration are all behind `#if UNITY_EDITOR || DEVELOPMENT_BUILD`, so a
non-development standalone build contains no console and backtick does nothing. `Register` and
`Print` remain **callable no-ops** in release, so game code that registers commands still
compiles and runs. Verified by building two players and searching both:
`SkylotusCI.VerifyReleaseConsoleStripped`.

Its `timescale` command routes through `TimeManager.GameTimeScale` rather than assigning
`Time.timeScale`, so it composes with pauses and slow motion instead of being reverted on the
next frame.

### GameLogger

Static, levelled, category-scoped logging.

```csharp
GameLogger.Log("Combat", "Boss spawned");
GameLogger.LogDebug("Combat", "hp=42");
GameLogger.LogWarning("Save", "Falling back to .bak");
GameLogger.LogError("Net", "Handshake failed");

GameLogger.GlobalLevel = LogLevel.Warning;
GameLogger.SetCategoryLevel("Combat", LogLevel.Trace);
```

`LogLevel` is `Trace, Debug, Info, Warning, Error, Fatal, Off`. `Bootstrapper.Initialize`
configures it first, before any other system, and `_enableFileLogging` (default **off**) writes
a timestamped file into `Application.persistentDataPath/Logs`. Nothing collects those files.

**`GameLogger` is not thread-safe** — see [Known open items](#known-open-items).

### BrightnessController (`Skylotus.Core.Rendering`)

Realizes the brightness setting against the URP volume stack. Nothing needs to place it in a
scene or on a prefab: it installs itself at `RuntimeInitializeLoadType.AfterSceneLoad`, which is
after `Bootstrapper` has registered `SettingsService` and before the first real scene loads, and
assigns itself to `SettingsService.BrightnessController` — which immediately pushes the saved
value, so the main menu's first frame already renders at the player's brightness.

```csharp
using Skylotus.Core.Rendering;

var brightness = BrightnessController.Instance;   // null before AfterSceneLoad
brightness.SetBrightness(0.5f);

float current  = brightness.Brightness;
float exposure = brightness.PostExposure;
```

Normally you never touch it — drive brightness through `SettingsService.SetBrightness`.

It creates a persistent GameObject carrying a global `Volume` at priority 1000 whose profile is
built at runtime (nothing on disk is mutated by a slider drag) and overrides exactly one field:
`ColorAdjustments.postExposure`. Brightness maps linearly onto exposure stops, from −2 EV at 0
to 0 EV at default — so the slider darkens but never brightens past what the artist authored.

**It turns post-processing on.** All three scenes ship with `Render Post Processing` unticked on
every camera, and a colour-grading brightness does not exist without that pass. While a
non-default brightness is in effect the controller enables `renderPostProcessing` on every base
camera that has it off, and restores those cameras when brightness returns to default. Ticking
the box in the three scenes is the cleaner fix and would let that workaround be deleted.

### Extensions

`CoreExtensions` adds `Transform.ResetLocal/DestroyChildren/SetX/SetY/SetZ`,
`GameObject.GetOrAddComponent<T>/SetLayerRecursive/HasComponent<T>`,
`Vector3.Flat/WithX/WithY/WithZ/FlatDistance/RandomPointXZ`, `Color.WithAlpha`,
`IList<T>.RandomElement/Shuffle`, and `MonoBehaviour.Delay(seconds, action)/NextFrame(action)`.

`SingletonBehaviour<T>` also exists and is **used by no core system** — they all use
`ServiceLocator`. Two competing patterns; pick one for your project rather than mixing them.

---

## Editor entry points

Every one of these is a static method on the partial class `Skylotus.Editor.SkylotusCI`, run
through `unity-verify.ps1 -Mode method`. `SkylotusCI` is split across
`SkylotusCI.cs` (core + prefab), `.Audio.cs`, `.Brightness.cs`, `.Build.cs`, `.Console.cs` and
`.Project.cs` — **add a new `SkylotusCI.<Area>.cs` partial rather than editing an existing one.**

| Method | Purpose |
|--------|---------|
| `CompileCheck` | Plumbing smoke test; logs assembly counts. |
| `ValidateAssemblyReferences` | Fails if any asmdef under `Assets/` references an assembly that does not exist. Scoped to `Assets/` on purpose — Unity's own packages routinely reference uninstalled assemblies. |
| `GenerateCoreSystemsPrefab` | Rebuilds the core systems prefab and re-points `BootScene`. Needs `-Graphics`. |
| `ValidateCoreSystemsPrefab` | Checks the prefab's components and wiring without rebuilding. |
| `GenerateAudioMixer` / `ValidateAudioMixer` | The `AudioMixer` asset and its groups/exposed parameters. |
| `GenerateBrightnessProfile` / `ValidateBrightness` | The URP volume profile side of WP-3. |
| `ConfigureProject` | Post-clone project settings. Also **Skylotus → Configure New Project**. |
| `VerifyReleaseConsoleStripped` | Builds a development and a non-development player and proves the console symbols are absent from the release one. |
| `BuildWindows64` / `BuildLinux64` | Headless player builds for CI. |

Other Editor tooling: **Skylotus → Fix Disabled ButtonExtended (Scene)**
(`ButtonExtendedRepair.cs`), and a colour palette window (`ColorPaletteWindow.cs`).

> **Never hand-author `.prefab`, `.unity`, `.mixer` or `.asset` YAML.** Those files are
> generated by the Editor through `-executeMethod`. A hand edit is silently discarded the next
> time a generator runs.

> **A player build mutates tracked files.** `ProjectSettings/UnityConnectSettings.asset` has
> been observed flipping `m_Enabled: 0 → 1` — the Unity Connect / Analytics service enable —
> by itself during a build. Check that file after any build step, or the project ships with
> telemetry silently on. `Assets/Settings/UniversalRP.asset` and
> `.../UniversalRenderPipelineGlobalSettings.asset` also get rewritten as build caches; those
> are noise and can be reverted.

> **Anything touching `QualitySettings` from a batch target must save and restore per level.**
> `QualitySettings.vSyncCount` is a property of the *active* quality level, not a global, so
> restoring it after switching levels writes onto the wrong level.

---

## Tests

```powershell
.\Tools\unity-verify.ps1 -Mode all
```

**108 EditMode + 45 PlayMode, 0 failures.**

| Suite | Covers |
|-------|--------|
| `EventBusTests` | priority ordering, `SubscribeOnce`, unsubscribe during publish, `SubscribeWhileAlive` lifetime, queue processing, zero-allocation `Enqueue`→`ProcessQueue` |
| `SaveSystemTests` | round-trip plain and encrypted, slot-name sanitization including path traversal, version mismatch and migrations, missing slot, `.bak` fallback |
| `ServiceLocatorTests` | register, resolve, lazy, overwrite, reset |
| `LocalizationSystemTests` | interpolation, pluralization, fallback, missing key, malformed JSON, unicode escapes, nested objects |
| `GameStateMachineTests` | transitions, blocked transitions, push/pop, event emission |
| `SettingsServiceTests` | key ownership, defaults, apply, dirty/flush semantics |
| `TestAssemblyLayoutTests` | the test assemblies stay excluded from player builds |
| `BootstrapTests` (PlayMode) | every system registers in the documented order with no double-registration; a serialized value on the prefab drives runtime behaviour |
| `LoadingScreenTests` (PlayMode) | the overlay fades in, the bar is driven 0→1, it fades out, and the scene actually changes |
| `ObjectPoolTests` (PlayMode) | spawn/despawn, the `maxSize` cap, scene-load survival |
| `TimeManagerTests` (PlayMode) | timers, cooldowns, pause tokens, hit-stop precedence |
| `UIManagerPauseTests` (PlayMode) | modals take and release their own pause token and nothing else's |

Two practices this suite is built on, both learned the hard way here:

- **Break every assertion once.** Six tests were negative-controlled — the assertion was made
  to fail, the failure confirmed, and the change reverted. That is what separates "green" from
  "green and meaningful", especially for criteria whose original authors could only reason
  about them.
- **Do not use `GC.GetAllocatedBytesForCurrentThread()` in a Unity test.** It returns 0
  unconditionally on this runtime, so an allocation test built on it passes with a deliberate
  `new object()` inside the measured window. Use `Is.Not.AllocatingGCMemory()`, which is backed
  by the GC.Alloc profiler recorder.

CI runs the same two suites on every PR (`.github/workflows/unity-tests.yml`, GameCI, with LFS
checkout and a `Library/` cache).

> **One test is intermittently flaky.**
> `EventBusTests.EnqueueThenProcessQueue_AllocatesNothingPerEvent` failed 2 of 7 runs during
> WP-10 verification, both times on the first EditMode run immediately after a script
> recompile, and passed on the immediate re-run with no source change. `Is.Not.AllocatingGCMemory()`
> is backed by a **process-wide** GC.Alloc profiler recorder, so allocations made by the Editor
> itself inside the sampled window are attributed to the measured delegate. The assertion is
> correct and should not be weakened; if it fails, re-run before investigating. It also passed
> on a clean-tree recompile, so it is not tied to any particular change.

### PlayMode from the CLI

`-Mode tests -TestPlatform PlayMode` **enters play mode even with zero tests**, and the log
carries the whole boot with call stacks. That is the way to settle a runtime question headlessly
(it is how the editor auto-bootstrap's `RuntimeInitializeOnLoadMethod` timing was proven). Its
limit: batchmode play mode always starts in an empty untitled scene, so scene-specific
behaviour needs a real PlayMode test or a human.

---

## Architecture

```
ServiceLocator (static)              EventBus (static)
      │                                    │
      ├── SaveSystem                Subscribe / Publish / Enqueue
      ├── LocalizationSystem            IGameEvent structs
      ├── SettingsService
      ├── AudioManager              Bootstrapper
      ├── ObjectPool                    │
      ├── SkylotusSceneManager          ├── Initializes GameLogger
      ├── GameStateMachine              ├── Constructs the plain-C# systems
      ├── TimeManager                   ├── Instantiates SkylotusCoreSystems.prefab
      ├── InputManager                  ├── Registers everything with ServiceLocator
      ├── UIManager                     ├── SettingsService.ApplyAll()
      ├── DialogueSystem                ├── DontDestroyOnLoad
      └── NotificationSystem            └── Drives the EventBus queue each frame
```

Systems never reference each other directly. Cross-system communication goes through `EventBus`
for "something happened" notifications, and `ServiceLocator` for "I need to call a method on
that". A new system does not get a `[SerializeField] private OtherSystem _other;`.

Two invariants worth stating separately, because both were violated before and both cost real
debugging time:

- **`TimeManager` is the sole writer of `Time.timeScale`.** Nothing else assigns it — not
  `UIManager`, not a state callback, not the debug console.
- **`SettingsService` is the sole owner of settings keys.** No `Settings*Tab` touches
  `PlayerPrefs`.

---

## Known open items

Things a fresh clone will actually hit. None of these is hidden anywhere else.

### LICENSE is unresolved

`LICENSE` is a placeholder that says so on its first line. Nothing about this repository's
distribution terms has been decided. See [step 3 of the checklist](#3-resolve-the-licence).

### IL2CPP is configured but may not be buildable

The Standalone scripting backend is set to IL2CPP, but a Unity install without the *Windows
Build Support (IL2CPP)* module has only `*_mono` variations and the build fails. Add the module
via **Unity Hub → Installs → Add Modules**. `ConfigureProject` warns when it detects this.
Escape hatch: `SKYLOTUS_SCRIPTING_BACKEND=Mono2x`, or `"scriptingBackend": "Mono2x"` in
`SkylotusProject.json`.

### `CustomCursor` throws on mouseless devices

`CustomCursor.UpdateMouseCursor` falls back to legacy `UnityEngine.Input.mousePosition` when
`Mouse.current == null`. This project runs `activeInputHandler: 1` — **Input System only** —
where touching legacy `Input` throws. On any device with no mouse the cursor code therefore
throws *every frame*. It surfaced by breaking 19 of 45 PlayMode tests the moment one loaded
`Gameplay.unity` (which carries `CursorCanvas`); the tests route around it by loading
`BootScene` instead. **This is not a test-only problem and it is not fixed.**

### `GameLogger` is not thread-safe

`GameLogger` holds a single `private static readonly StringBuilder _buffer`, and `WriteLog`
does `Clear()` → `Append()` → `ToString()` on it with no lock. Two threads logging concurrently
interleave into one buffer and produce corrupted lines or a torn read. Nothing calls it off the
main thread **today** — `SaveSystem`'s worker-thread helpers are deliberately Unity-API-free and
log-free — but that is a constraint every future async system inherits. The fix is a
`[ThreadStatic]` buffer, a lock, or a plain local `StringBuilder`.

### Smaller items

- **`Bootstrapper._inputActions` is dead on the prefab path.** The prefab's `InputManager`
  carries the reference; the Bootstrapper field now feeds only the code-construction fallback.
  Two places hold the same asset and can drift.
- **`UIManager.ScreenContainer` / `ModalContainer` are read by nothing.** They are a facility,
  not a feature. Delete them if your project has no use for them.
- **`DialogueSystem` has no sample data**, no authoring tool, and no dialogue JSON anywhere in
  the repository. It has never been exercised against a real tree.
- **`MotionDebugger.Items.Count`** is called unguarded by the `tween_count` console command;
  LitMotion's debugger may be editor-only. Verify before a release build (the console is
  stripped from release players, so this is a development-build concern).
- **`.editorconfig` declares `end_of_line = lf` for all `*.cs`**, but `Assets/Scripts/MainMenu/*.cs`
  are CRLF. With `core.autocrlf=true`, an editor honouring `.editorconfig` rewrites those files
  wholesale on first save. Normalize them or relax the rule.
- **Every batchmode run migrates project settings.** Unity 6 rewrites settings assets on open
  (`TimeManager.asset`'s fixed-timestep form, `QualitySettings.asset`'s `serializedVersion`).
  Benign, and it will recur on any Editor open.
- **Two stale `SkylotusBootstrapper` references survive**, both cosmetic. `BootScene.unity`'s
  `m_EditorClassIdentifier` names it where the type is `Bootstrapper` — the component resolves
  by GUID, and scene YAML is not hand-edited, so it was left alone. `ServiceLocator.Get<T>()`'s
  exception message also says "Did you forget to register it in SkylotusBootstrapper?"; the
  class is `Bootstrapper`.
- **`UnityEditorDarkMode.dll`** (`Assets/Plugins/`) is a native Windows DLL that patches the
  running Editor. MIT-licensed and correctly restricted to Editor + Windows in its `.meta`, so
  it never enters a build — but it is Windows-only tooling that macOS and Linux contributors
  inherit. Confirm the team wants it.

---

## Conventions

Enforced by `.editorconfig` and described in `CLAUDE.md`:

- 4-space indentation, Allman braces, `_camelCase` private fields.
- **No file-scoped namespaces.**
- **Every public member gets an XML doc comment.** This codebase documents its whole public
  surface; a new public method without one is incomplete.
- No new `Resources.Load` calls.
- Systems never hold direct references to one another.

`CORE_FIXES.md` is the historical record: the defect list this template was built from, the
work packages that fixed each one, and the wave-by-wave outcomes — including three premises in
the original plan that turned out to be wrong. Read it before changing anything under
`Assets/Scripts/Core/Runtime/` or `Assets/Scripts/Editor/`.
