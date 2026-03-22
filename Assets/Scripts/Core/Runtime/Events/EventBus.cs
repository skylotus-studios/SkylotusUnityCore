using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Marker interface for all game events.
    /// Implement as a struct to avoid GC allocations on publish.
    /// </summary>
    public interface IGameEvent { }

    /// <summary>
    /// Type-safe, static event bus with priority ordering and one-shot subscriptions.
    /// Provides fully decoupled publish/subscribe — no references between sender and receiver.
    ///
    /// Usage:
    /// <code>
    /// EventBus.Subscribe&lt;OnPlayerDied&gt;(evt => HandleDeath(evt));
    /// EventBus.Publish(new OnPlayerDied { PlayerId = 1 });
    /// </code>
    /// </summary>
    public static class EventBus
    {
        /// <summary>Internal wrapper for a subscriber: handler delegate, priority, and one-shot flag.</summary>
        private class SubscriberEntry
        {
            public Delegate Handler;
            public int Priority;
            public bool Once;
        }

        /// <summary>All subscribers grouped by event type.</summary>
        private static readonly Dictionary<Type, List<SubscriberEntry>> _subscribers = new();

        /// <summary>Queue for deferred event processing (call ProcessQueue each frame).</summary>
        private static readonly Queue<(Type type, object evt)> _eventQueue = new();

        /// <summary>Guard flag to prevent re-entrant queue processing.</summary>
        private static bool _isProcessing;

        /// <summary>
        /// Reset static state on domain reload (Editor Enter Play Mode settings).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _subscribers.Clear();
            _eventQueue.Clear();
            _isProcessing = false;
        }

        /// <summary>
        /// Subscribe to an event type. Higher priority values execute first.
        /// </summary>
        /// <typeparam name="T">The event struct type implementing <see cref="IGameEvent"/>.</typeparam>
        /// <param name="handler">The callback to invoke when the event is published.</param>
        /// <param name="priority">Execution order — higher values run before lower values.</param>
        public static void Subscribe<T>(Action<T> handler, int priority = 0) where T : struct, IGameEvent
        {
            var type = typeof(T);

            if (!_subscribers.TryGetValue(type, out var list))
            {
                list = new List<SubscriberEntry>();
                _subscribers[type] = list;
            }

            list.Add(new SubscriberEntry { Handler = handler, Priority = priority, Once = false });

            // Keep the list sorted so highest priority subscribers run first
            list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>
        /// Subscribe to receive an event exactly once, then automatically unsubscribe.
        /// Useful for one-time reactions like awaiting a scene-load completion.
        /// </summary>
        /// <typeparam name="T">The event struct type implementing <see cref="IGameEvent"/>.</typeparam>
        /// <param name="handler">The callback to invoke once.</param>
        /// <param name="priority">Execution order — higher values run before lower values.</param>
        public static void SubscribeOnce<T>(Action<T> handler, int priority = 0) where T : struct, IGameEvent
        {
            var type = typeof(T);

            if (!_subscribers.TryGetValue(type, out var list))
            {
                list = new List<SubscriberEntry>();
                _subscribers[type] = list;
            }

            list.Add(new SubscriberEntry { Handler = handler, Priority = priority, Once = true });
            list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        }

        /// <summary>
        /// Unsubscribe a specific handler from an event type.
        /// </summary>
        /// <typeparam name="T">The event struct type.</typeparam>
        /// <param name="handler">The exact delegate reference that was originally subscribed.</param>
        public static void Unsubscribe<T>(Action<T> handler) where T : struct, IGameEvent
        {
            if (_subscribers.TryGetValue(typeof(T), out var list))
            {
                list.RemoveAll(e => e.Handler.Equals(handler));
            }
        }

        /// <summary>
        /// Publish an event immediately to all subscribers in priority order.
        /// Subscriber exceptions are caught and logged — they do not interrupt other subscribers.
        /// </summary>
        /// <typeparam name="T">The event struct type.</typeparam>
        /// <param name="gameEvent">The event data to deliver.</param>
        public static void Publish<T>(T gameEvent) where T : struct, IGameEvent
        {
            var type = typeof(T);
            if (!_subscribers.TryGetValue(type, out var list)) return;

            // Snapshot the list so removals during iteration are safe
            var snapshot = new List<SubscriberEntry>(list);

            foreach (var entry in snapshot)
            {
                try
                {
                    ((Action<T>)entry.Handler)?.Invoke(gameEvent);
                }
                catch (Exception ex)
                {
                    GameLogger.LogError("EventBus", $"Error in handler for {type.Name}: {ex.Message}");
                }
            }

            // Remove one-shot subscribers after delivery
            list.RemoveAll(e => e.Once);
        }

        /// <summary>
        /// Queue an event for deferred processing. The event will be delivered
        /// when <see cref="ProcessQueue"/> is called (typically once per frame).
        /// Useful for events raised during physics or jobs that should reach
        /// listeners on the main thread during Update.
        /// </summary>
        /// <typeparam name="T">The event struct type.</typeparam>
        /// <param name="gameEvent">The event data to enqueue.</param>
        public static void Enqueue<T>(T gameEvent) where T : struct, IGameEvent
        {
            _eventQueue.Enqueue((typeof(T), gameEvent));
        }

        /// <summary>
        /// Process all queued events. Call once per frame from a MonoBehaviour Update loop.
        /// Re-entrant calls are ignored to prevent infinite loops.
        /// </summary>
        public static void ProcessQueue()
        {
            if (_isProcessing) return;
            _isProcessing = true;

            while (_eventQueue.Count > 0)
            {
                var (type, evt) = _eventQueue.Dequeue();
                if (!_subscribers.TryGetValue(type, out var list)) continue;

                var snapshot = new List<SubscriberEntry>(list);

                foreach (var entry in snapshot)
                {
                    try
                    {
                        entry.Handler.DynamicInvoke(evt);
                    }
                    catch (Exception ex)
                    {
                        GameLogger.LogError("EventBus", $"Error processing queued {type.Name}: {ex.Message}");
                    }
                }

                list.RemoveAll(e => e.Once);
            }

            _isProcessing = false;
        }

        /// <summary>
        /// Remove all subscribers for a specific event type.
        /// </summary>
        /// <typeparam name="T">The event type to clear.</typeparam>
        public static void Clear<T>() where T : struct, IGameEvent
        {
            _subscribers.Remove(typeof(T));
        }

        /// <summary>
        /// Remove all subscribers for all event types and drain the event queue.
        /// </summary>
        public static void ClearAll()
        {
            _subscribers.Clear();
            _eventQueue.Clear();
        }
    }

    // ─── Common Built-In Events ─────────────────────────────────────

    /// <summary>Raised when a scene finishes loading via <see cref="SkylotusSceneManager"/>.</summary>
    public struct OnSceneLoadedEvent : IGameEvent
    {
        /// <summary>The name of the scene that was loaded.</summary>
        public string SceneName;
    }

    /// <summary>Raised when the global game state changes via <see cref="GameStateMachine"/>.</summary>
    public struct OnGameStateChangedEvent : IGameEvent
    {
        /// <summary>The state before the transition.</summary>
        public GameStateType Previous;
        /// <summary>The state after the transition.</summary>
        public GameStateType Current;
    }

    /// <summary>Raised when the active language changes via <see cref="LocalizationSystem"/>.</summary>
    public struct OnLanguageChangedEvent : IGameEvent
    {
        /// <summary>The ISO language code that is now active (e.g. "en", "fr", "ja").</summary>
        public string LanguageCode;
    }

    /// <summary>Raised when an audio channel volume is adjusted.</summary>
    public struct OnAudioVolumeChangedEvent : IGameEvent
    {
        /// <summary>The channel whose volume changed.</summary>
        public AudioChannel Channel;
        /// <summary>The new volume value (0–1).</summary>
        public float Volume;
    }

    /// <summary>Raised when the player switches input device (keyboard ↔ gamepad ↔ touch).</summary>
    public struct OnInputDeviceChangedEvent : IGameEvent
    {
        /// <summary>The newly detected device type.</summary>
        public InputDeviceType DeviceType;
    }

    /// <summary>Raised when a notification is enqueued.</summary>
    public struct OnNotificationEvent : IGameEvent
    {
        /// <summary>The notification message text.</summary>
        public string Message;
        /// <summary>The severity/category of the notification.</summary>
        public NotificationType Type;
    }

    /// <summary>Raised when a save operation completes (success or failure).</summary>
    public struct OnSaveCompletedEvent : IGameEvent
    {
        /// <summary>Hash of the slot name that was saved.</summary>
        public int SlotIndex;
        /// <summary>Whether the save succeeded.</summary>
        public bool Success;
    }

    /// <summary>Raised on dialogue lifecycle events (start, advance, choice, end).</summary>
    public struct OnDialogueEvent : IGameEvent
    {
        /// <summary>The ID of the active dialogue tree.</summary>
        public string DialogueId;
        /// <summary>What happened in the dialogue.</summary>
        public DialogueEventType EventType;
    }

    /// <summary>Enumerates the lifecycle phases of a dialogue.</summary>
    public enum DialogueEventType { Started, LineAdvanced, ChoiceMade, Ended }
}