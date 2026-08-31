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
    /// Type-safe, static event bus with priority ordering, one-shot subscriptions,
    /// and optional lifecycle-bound subscriptions.
    /// Provides fully decoupled publish/subscribe — no references between sender and receiver.
    ///
    /// Both the immediate and the deferred path dispatch through a strongly typed
    /// <c>Action&lt;T&gt;</c>: there is no boxing of the event struct and no reflection.
    /// After warm-up, an <see cref="Enqueue{T}"/> to <see cref="ProcessQueue"/> round trip
    /// performs no managed allocation.
    ///
    /// Usage:
    /// <code>
    /// // Preferred for MonoBehaviours — unsubscribes itself when the owner is destroyed.
    /// this.SubscribeWhileAlive&lt;OnPlayerDied&gt;(HandleDeath);
    ///
    /// // Manual lifetime — you must call Unsubscribe yourself.
    /// EventBus.Subscribe&lt;OnPlayerDied&gt;(evt => HandleDeath(evt));
    /// EventBus.Publish(new OnPlayerDied { PlayerId = 1 });
    /// </code>
    /// </summary>
    public static class EventBus
    {
        // ─── Subscriber storage ─────────────────────────────────────

        /// <summary>
        /// Internal wrapper for a subscriber: typed handler, priority, one-shot flag,
        /// optional owning Unity object, and a tombstone flag used for deferred removal.
        /// </summary>
        /// <typeparam name="T">The event struct type this subscriber listens for.</typeparam>
        private sealed class Subscriber<T> where T : struct, IGameEvent
        {
            /// <summary>The strongly typed callback. Invoked directly — never via reflection.</summary>
            public Action<T> Handler;

            /// <summary>Execution order — higher values run before lower values.</summary>
            public int Priority;

            /// <summary>Monotonic registration counter, keeping equal priorities in subscription order.</summary>
            public int Sequence;

            /// <summary>True if this subscriber is removed after its first delivery.</summary>
            public bool Once;

            /// <summary>True if <see cref="Owner"/> is meaningful and this subscription is lifecycle-bound.</summary>
            public bool HasOwner;

            /// <summary>The Unity object whose lifetime gates this subscription, when <see cref="HasOwner"/> is true.</summary>
            public UnityEngine.Object Owner;

            /// <summary>Tombstone. Set instead of removing, so indices stay stable during dispatch.</summary>
            public bool Removed;
        }

        /// <summary>
        /// Non-generic base for a per-event-type channel, so channels of different event types
        /// can live in one collection and be driven without reflection.
        /// </summary>
        private abstract class EventChannel
        {
            /// <summary>Dequeue and dispatch exactly one deferred event, if any remain.</summary>
            public abstract void DispatchNextQueued();

            /// <summary>Remove every subscriber, leaving any queued events in place.</summary>
            public abstract void ClearSubscribers();

            /// <summary>Remove every subscriber and drop every queued event.</summary>
            public abstract void Reset();
        }

        /// <summary>
        /// Holds the subscribers and the deferred queue for a single event type.
        /// The queue is a <c>Queue&lt;T&gt;</c>, so enqueuing a struct event does not box it.
        /// </summary>
        /// <typeparam name="T">The event struct type this channel serves.</typeparam>
        private sealed class EventChannel<T> : EventChannel where T : struct, IGameEvent
        {
            /// <summary>Orders by descending priority, then by ascending subscription sequence.</summary>
            private static readonly Comparison<Subscriber<T>> _priorityOrder = (a, b) =>
            {
                int byPriority = b.Priority.CompareTo(a.Priority);
                return byPriority != 0 ? byPriority : a.Sequence.CompareTo(b.Sequence);
            };

            /// <summary>Matches tombstoned subscribers during compaction.</summary>
            private static readonly Predicate<Subscriber<T>> _isRemoved = s => s.Removed;

            /// <summary>Next value handed to <see cref="Subscriber{T}.Sequence"/>.</summary>
            private static int _nextSequence;

            /// <summary>Live subscribers, in dispatch order once <see cref="_needsSort"/> is cleared.</summary>
            private readonly List<Subscriber<T>> _subscribers = new();

            /// <summary>Deferred events awaiting <see cref="ProcessQueue"/>. Typed, so no boxing occurs.</summary>
            private readonly Queue<T> _queue = new();

            /// <summary>Nesting level of in-flight dispatches; removals are deferred while non-zero.</summary>
            private int _dispatchDepth;

            /// <summary>True when a subscriber was added and the list is not yet re-ordered.</summary>
            private bool _needsSort;

            /// <summary>True when at least one tombstone is waiting to be compacted away.</summary>
            private bool _needsCompact;

            /// <summary>
            /// Register a subscriber. New entries are appended and ordered lazily on the next
            /// dispatch, so subscribing from inside a handler cannot disturb the running loop.
            /// </summary>
            /// <param name="handler">The callback to invoke.</param>
            /// <param name="priority">Execution order — higher values run before lower values.</param>
            /// <param name="once">True to remove the subscriber after its first delivery.</param>
            /// <param name="owner">Optional Unity object whose destruction ends the subscription.</param>
            /// <param name="hasOwner">True when <paramref name="owner"/> gates the subscription lifetime.</param>
            public void Add(Action<T> handler, int priority, bool once, UnityEngine.Object owner, bool hasOwner)
            {
                _subscribers.Add(new Subscriber<T>
                {
                    Handler = handler,
                    Priority = priority,
                    Sequence = _nextSequence++,
                    Once = once,
                    HasOwner = hasOwner,
                    Owner = owner
                });

                _needsSort = true;
            }

            /// <summary>Tombstone every entry whose handler equals <paramref name="handler"/>.</summary>
            /// <param name="handler">The exact delegate reference that was originally subscribed.</param>
            public void Remove(Action<T> handler)
            {
                for (int i = 0; i < _subscribers.Count; i++)
                {
                    var subscriber = _subscribers[i];
                    if (!subscriber.Removed && subscriber.Handler == handler)
                    {
                        subscriber.Removed = true;
                        _needsCompact = true;
                    }
                }

                if (_dispatchDepth == 0 && _needsCompact) Compact();
            }

            /// <summary>Append a deferred event. Typed queue — the struct is stored without boxing.</summary>
            /// <param name="gameEvent">The event data to defer.</param>
            public void EnqueueEvent(T gameEvent) => _queue.Enqueue(gameEvent);

            /// <inheritdoc />
            public override void DispatchNextQueued()
            {
                if (_queue.Count == 0) return;
                Dispatch(_queue.Dequeue());
            }

            /// <inheritdoc />
            public override void ClearSubscribers()
            {
                if (_dispatchDepth > 0)
                {
                    for (int i = 0; i < _subscribers.Count; i++) _subscribers[i].Removed = true;
                    _needsCompact = true;
                    return;
                }

                _subscribers.Clear();
                _needsCompact = false;
                _needsSort = false;
            }

            /// <inheritdoc />
            public override void Reset()
            {
                ClearSubscribers();
                _queue.Clear();
            }

            /// <summary>
            /// Deliver one event to every live subscriber in priority order.
            /// Handler exceptions are caught and logged with the handler's target type, so a
            /// leaked subscriber can be identified; they never interrupt the remaining subscribers.
            /// </summary>
            /// <param name="gameEvent">The event data to deliver.</param>
            public void Dispatch(T gameEvent)
            {
                // Order lazily, and only at the outermost dispatch — re-ordering a list that an
                // enclosing loop is walking would skip or repeat subscribers.
                if (_dispatchDepth == 0 && _needsSort)
                {
                    _subscribers.Sort(_priorityOrder);
                    _needsSort = false;
                }

                _dispatchDepth++;

                try
                {
                    // Entries added while dispatching land past this bound and are not invoked for
                    // the current event — the same semantics the old snapshot copy had, without the
                    // per-publish List allocation.
                    int count = _subscribers.Count;

                    for (int i = 0; i < count; i++)
                    {
                        // A handler may have cleared the bus outright; re-check the live bound.
                        if (i >= _subscribers.Count) break;

                        var subscriber = _subscribers[i];
                        if (subscriber.Removed) continue;

                        // Lifecycle-bound subscriber whose owner has been destroyed. Unity's
                        // overloaded == reports a destroyed object as null while the managed
                        // reference is still alive, which is exactly the leak this catches.
                        if (subscriber.HasOwner && subscriber.Owner == null)
                        {
                            subscriber.Removed = true;
                            _needsCompact = true;
                            continue;
                        }

                        if (subscriber.Once)
                        {
                            subscriber.Removed = true;
                            _needsCompact = true;
                        }

                        try
                        {
                            subscriber.Handler(gameEvent);
                        }
                        catch (Exception ex)
                        {
                            GameLogger.LogError("EventBus",
                                $"Handler {DescribeHandler(subscriber.Handler)} threw while handling " +
                                $"{typeof(T).Name}: {ex}");
                        }
                    }
                }
                finally
                {
                    _dispatchDepth--;
                    if (_dispatchDepth == 0 && _needsCompact) Compact();
                }
            }

            /// <summary>Physically remove tombstoned entries. Only ever called outside a dispatch.</summary>
            private void Compact()
            {
                _subscribers.RemoveAll(_isRemoved);
                _needsCompact = false;
            }
        }

        /// <summary>
        /// Per-event-type channel cache. Resolving <c>ChannelOf&lt;T&gt;.Instance</c> is a static
        /// field read rather than a dictionary lookup, and the runtime specialises it per event type.
        /// </summary>
        /// <typeparam name="T">The event struct type.</typeparam>
        private static class ChannelOf<T> where T : struct, IGameEvent
        {
            /// <summary>The single channel serving <typeparamref name="T"/>, registered on first use.</summary>
            internal static readonly EventChannel<T> Instance = CreateAndRegister();

            /// <summary>Create the channel and publish it to <see cref="_channels"/> for bulk operations.</summary>
            private static EventChannel<T> CreateAndRegister()
            {
                var channel = new EventChannel<T>();
                _channels[typeof(T)] = channel;
                return channel;
            }
        }

        /// <summary>
        /// Every channel created so far, so <see cref="ClearAll"/> and the domain-reload reset can
        /// reach them. Entries are never removed: <c>ChannelOf&lt;T&gt;.Instance</c> is
        /// <c>readonly</c> and would otherwise hold a channel this dictionary no longer knows about.
        /// </summary>
        private static readonly Dictionary<Type, EventChannel> _channels = new();

        /// <summary>
        /// Arrival order of deferred events across all types. Holding channel references (not the
        /// events) keeps global FIFO ordering intact while the events themselves stay in their
        /// typed queues, unboxed.
        /// </summary>
        private static readonly Queue<EventChannel> _dispatchOrder = new();

        /// <summary>Guard flag to prevent re-entrant queue processing.</summary>
        private static bool _isProcessing;

        /// <summary>
        /// Reset static state on domain reload (Editor Enter Play Mode settings).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            ClearAll();
            _isProcessing = false;
        }

        // ─── Subscription ───────────────────────────────────────────

        /// <summary>
        /// Subscribe to an event type. Higher priority values execute first.
        /// The caller owns the lifetime and must call <see cref="Unsubscribe{T}"/>; prefer
        /// <see cref="EventBusExtensions.SubscribeWhileAlive{T}"/> from a MonoBehaviour.
        /// </summary>
        /// <typeparam name="T">The event struct type implementing <see cref="IGameEvent"/>.</typeparam>
        /// <param name="handler">The callback to invoke when the event is published.</param>
        /// <param name="priority">Execution order — higher values run before lower values.</param>
        public static void Subscribe<T>(Action<T> handler, int priority = 0) where T : struct, IGameEvent
        {
            ChannelOf<T>.Instance.Add(handler, priority, false, null, false);
        }

        /// <summary>
        /// Subscribe for as long as <paramref name="owner"/> lives. When the owning Unity object is
        /// destroyed the subscription stops delivering and is dropped — no <c>OnDestroy</c>
        /// bookkeeping, and no handler firing against a destroyed target.
        /// This is the default path for new code.
        /// </summary>
        /// <typeparam name="T">The event struct type implementing <see cref="IGameEvent"/>.</typeparam>
        /// <param name="owner">The Unity object whose destruction ends the subscription.</param>
        /// <param name="handler">The callback to invoke when the event is published.</param>
        /// <param name="priority">Execution order — higher values run before lower values.</param>
        public static void SubscribeWhileAlive<T>(UnityEngine.Object owner, Action<T> handler, int priority = 0)
            where T : struct, IGameEvent
        {
            if (owner == null)
            {
                GameLogger.LogError("EventBus",
                    $"SubscribeWhileAlive<{typeof(T).Name}> called with a null or destroyed owner; ignoring.");
                return;
            }

            ChannelOf<T>.Instance.Add(handler, priority, false, owner, true);
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
            ChannelOf<T>.Instance.Add(handler, priority, true, null, false);
        }

        /// <summary>
        /// Unsubscribe a specific handler from an event type.
        /// Safe to call from inside a handler — the removal takes effect immediately for
        /// subsequent deliveries.
        /// </summary>
        /// <typeparam name="T">The event struct type.</typeparam>
        /// <param name="handler">The exact delegate reference that was originally subscribed.</param>
        public static void Unsubscribe<T>(Action<T> handler) where T : struct, IGameEvent
        {
            ChannelOf<T>.Instance.Remove(handler);
        }

        // ─── Publishing ─────────────────────────────────────────────

        /// <summary>
        /// Publish an event immediately to all subscribers in priority order.
        /// Subscriber exceptions are caught and logged with the handler's target type —
        /// they do not interrupt other subscribers.
        /// </summary>
        /// <typeparam name="T">The event struct type.</typeparam>
        /// <param name="gameEvent">The event data to deliver.</param>
        public static void Publish<T>(T gameEvent) where T : struct, IGameEvent
        {
            ChannelOf<T>.Instance.Dispatch(gameEvent);
        }

        /// <summary>
        /// Queue an event for deferred processing. The event will be delivered
        /// when <see cref="ProcessQueue"/> is called (typically once per frame).
        /// Useful for events raised during physics or jobs that should reach
        /// listeners on the main thread during Update.
        /// The event is stored in a typed queue, so it is not boxed.
        /// </summary>
        /// <typeparam name="T">The event struct type.</typeparam>
        /// <param name="gameEvent">The event data to enqueue.</param>
        public static void Enqueue<T>(T gameEvent) where T : struct, IGameEvent
        {
            var channel = ChannelOf<T>.Instance;
            channel.EnqueueEvent(gameEvent);
            _dispatchOrder.Enqueue(channel);
        }

        /// <summary>
        /// Process all queued events, in the order they were enqueued, across all event types.
        /// Call once per frame from a MonoBehaviour Update loop.
        /// Re-entrant calls are ignored to prevent infinite loops.
        /// Dispatch is a direct <c>Action&lt;T&gt;</c> invocation — no reflection, no boxing.
        /// </summary>
        public static void ProcessQueue()
        {
            if (_isProcessing) return;
            _isProcessing = true;

            try
            {
                while (_dispatchOrder.Count > 0)
                {
                    _dispatchOrder.Dequeue().DispatchNextQueued();
                }
            }
            finally
            {
                _isProcessing = false;
            }
        }

        /// <summary>Number of events currently waiting for <see cref="ProcessQueue"/>.</summary>
        public static int QueuedEventCount => _dispatchOrder.Count;

        // ─── Teardown ───────────────────────────────────────────────

        /// <summary>
        /// Remove all subscribers for a specific event type.
        /// Any events of that type already queued are still dequeued, but reach no one.
        /// </summary>
        /// <typeparam name="T">The event type to clear.</typeparam>
        public static void Clear<T>() where T : struct, IGameEvent
        {
            ChannelOf<T>.Instance.ClearSubscribers();
        }

        /// <summary>
        /// Remove all subscribers for all event types and drain the event queue.
        /// </summary>
        public static void ClearAll()
        {
            foreach (var channel in _channels.Values) channel.Reset();
            _dispatchOrder.Clear();
        }

        // ─── Diagnostics ────────────────────────────────────────────

        /// <summary>
        /// Describe a handler for a log message: its target type, its method, and whether the
        /// target is a destroyed Unity object — the signature of a subscription that outlived
        /// its subscriber.
        /// </summary>
        /// <param name="handler">The delegate that threw.</param>
        /// <returns>A human-readable description of the handler's target and method.</returns>
        private static string DescribeHandler(Delegate handler)
        {
            if (handler == null) return "<null>";

            var target = handler.Target;
            string method = handler.Method != null ? handler.Method.Name : "<unknown>";

            if (target == null)
            {
                string declaring = handler.Method?.DeclaringType?.FullName ?? "<unknown>";
                return $"static {declaring}.{method}";
            }

            string targetType = target.GetType().FullName;

            if (target is UnityEngine.Object unityTarget && unityTarget == null)
            {
                return $"{targetType}.{method} (target already destroyed — a missing Unsubscribe; " +
                       "use SubscribeWhileAlive instead)";
            }

            return $"{targetType}.{method}";
        }
    }

    /// <summary>
    /// MonoBehaviour conveniences for <see cref="EventBus"/>.
    /// Lives beside the bus rather than in CoreExtensions so the lifecycle rules stay next to
    /// the dispatch code that enforces them.
    /// </summary>
    public static class EventBusExtensions
    {
        /// <summary>
        /// Subscribe for as long as this behaviour is alive. When it is destroyed — including by a
        /// scene load — the subscription stops delivering and is dropped, with no <c>OnDestroy</c>
        /// unsubscribe needed. This is the default path for new code.
        /// </summary>
        /// <typeparam name="T">The event struct type implementing <see cref="IGameEvent"/>.</typeparam>
        /// <param name="owner">The subscribing behaviour, whose lifetime bounds the subscription.</param>
        /// <param name="handler">The callback to invoke when the event is published.</param>
        /// <param name="priority">Execution order — higher values run before lower values.</param>
        public static void SubscribeWhileAlive<T>(this MonoBehaviour owner, Action<T> handler, int priority = 0)
            where T : struct, IGameEvent
        {
            EventBus.SubscribeWhileAlive(owner, handler, priority);
        }
    }

    // ─── Common Built-In Events ─────────────────────────────────────

    /// <summary>Raised when a scene finishes loading via <see cref="SceneManager"/>.</summary>
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

    /// <summary>Raised when a settings value is changed (video, audio, controls).</summary>
    public struct OnSettingsChangedEvent : IGameEvent
    {
        /// <summary>The settings category (e.g. "Video", "Audio", "Controls").</summary>
        public string Category;
        /// <summary>The specific setting key (e.g. "Brightness", "Master").</summary>
        public string Key;
        /// <summary>The new value as a float. For bools use 0/1.</summary>
        public float Value;
    }
}
