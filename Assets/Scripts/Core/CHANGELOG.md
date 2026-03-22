# Changelog

## [1.0.0] - 2026-03-21

### Added
- **EventBus** — Type-safe publish/subscribe with priority, one-shot, and deferred queue
- **AudioManager** — Channel volumes, SFX pooling, music crossfade, spatial audio, pitch variance
- **ServiceLocator** — Decoupled service access with lazy binding support
- **SaveSystem** — Slot-based saves with AES-256 encryption, versioning, metadata, path sanitization
- **DebugConsole** — Runtime in-game console with custom commands and history
- **GameLogger** — Categorized logging with file output and runtime level control
- **LocalizationSystem** — JSON language files, variable interpolation, pluralization
- **UIManager** — Screen stack, animated transitions, modal popup support
- **ObjectPool** — Generic GameObject pooling with auto-expand, lifecycle callbacks, async pre-warm
- **InputManager** — Multi-device detection, action map switching, runtime rebinding
- **SkylotusSceneManager** — Async scene loading with loading screens and scene history
- **GameStateMachine** — FSM with push/pop, transition guards, enter/exit callbacks
- **TimeManager** — Named timers, cooldowns, hitstop, slow motion
- **DialogueSystem** — Branching dialogue trees with conditions, choices, events
- **NotificationSystem** — Queued toast notifications with priority and stacking
- **SkylotusBootstrapper** — One-component initialization of all systems
- **CoreExtensions** — Quality-of-life extensions for Transform, Vector3, Color, collections
- **SingletonBehaviour** — Generic persistent singleton base class
- **Domain Reload Support** — All static classes properly reset for Enter Play Mode settings