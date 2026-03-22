using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>Notification severity/category for visual styling.</summary>
    public enum NotificationType { Info, Success, Warning, Error, Achievement }

    /// <summary>
    /// Data payload for a single notification. Contains message, type,
    /// optional icon, click handler, and remaining display time.
    /// </summary>
    [Serializable]
    public class Notification
    {
        /// <summary>Auto-generated unique identifier.</summary>
        public string Id;

        /// <summary>The text message to display.</summary>
        public string Message;

        /// <summary>The notification category (affects visual styling).</summary>
        public NotificationType Type;

        /// <summary>How long the notification stays visible in seconds.</summary>
        public float Duration;

        /// <summary>Optional icon sprite displayed alongside the message.</summary>
        public Sprite Icon;

        /// <summary>Optional click handler invoked when the user taps the notification.</summary>
        public Action OnClick;

        /// <summary>Tracks remaining display time (decremented each frame).</summary>
        internal float TimeRemaining;
    }

    /// <summary>
    /// Queued toast/notification system with priority stacking, auto-dismiss timers,
    /// and clickable notifications. Connect UI rendering via the OnNotificationShow/Hide events.
    ///
    /// Usage:
    /// <code>
    /// var notif = ServiceLocator.Get&lt;NotificationSystem&gt;();
    /// notif.Notify("Game saved!", NotificationType.Success);
    /// notif.Achievement("First Blood!", trophyIcon);
    /// notif.OnNotificationShow += n => CreateToastUI(n);
    /// </code>
    /// </summary>
    public class NotificationSystem : MonoBehaviour
    {
        [Header("Configuration")]
        [Tooltip("Maximum number of notifications visible simultaneously.")]
        [SerializeField] private int _maxVisible = 3;

        [Tooltip("Default display duration in seconds for notifications without a custom duration.")]
        [SerializeField] private float _defaultDuration = 3f;

        [Tooltip("Fade-in animation duration (for UI implementation reference).")]
        [SerializeField] private float _fadeInDuration = 0.3f;

        [Tooltip("Fade-out animation duration (for UI implementation reference).")]
        [SerializeField] private float _fadeOutDuration = 0.3f;

        /// <summary>Pending notifications waiting for a display slot.</summary>
        private readonly Queue<Notification> _queue = new();

        /// <summary>Currently visible notifications.</summary>
        private readonly List<Notification> _active = new();

        /// <summary>Notifications scheduled for removal this frame.</summary>
        private readonly List<Notification> _toRemove = new();

        /// <summary>Auto-incrementing counter for generating unique IDs.</summary>
        private int _idCounter;

        /// <summary>Fired when a notification should appear in the UI.</summary>
        public event Action<Notification> OnNotificationShow;

        /// <summary>Fired when a notification should be removed from the UI.</summary>
        public event Action<Notification> OnNotificationHide;

        /// <summary>Fired when a notification is clicked by the user.</summary>
        public event Action<Notification> OnNotificationClicked;

        // ─── Public API ─────────────────────────────────────────────

        /// <summary>
        /// Show a text notification with the given severity.
        /// </summary>
        /// <param name="message">The message text.</param>
        /// <param name="type">The notification category/severity.</param>
        /// <param name="duration">Custom display duration (-1 uses default).</param>
        /// <returns>The created Notification object.</returns>
        public Notification Notify(string message, NotificationType type = NotificationType.Info,
            float duration = -1f)
        {
            return Enqueue(new Notification
            {
                Message = message,
                Type = type,
                Duration = duration > 0 ? duration : _defaultDuration
            });
        }

        /// <summary>
        /// Show a notification with an icon sprite.
        /// </summary>
        /// <param name="message">The message text.</param>
        /// <param name="icon">Sprite displayed alongside the message.</param>
        /// <param name="type">The notification category/severity.</param>
        /// <param name="duration">Custom display duration (-1 uses default).</param>
        /// <returns>The created Notification object.</returns>
        public Notification Notify(string message, Sprite icon,
            NotificationType type = NotificationType.Info, float duration = -1f)
        {
            return Enqueue(new Notification
            {
                Message = message,
                Type = type,
                Icon = icon,
                Duration = duration > 0 ? duration : _defaultDuration
            });
        }

        /// <summary>
        /// Show a clickable notification that invokes a callback when tapped.
        /// </summary>
        /// <param name="message">The message text.</param>
        /// <param name="onClick">Callback invoked on click.</param>
        /// <param name="type">The notification category/severity.</param>
        /// <param name="duration">Custom display duration (-1 uses default).</param>
        /// <returns>The created Notification object.</returns>
        public Notification Notify(string message, Action onClick,
            NotificationType type = NotificationType.Info, float duration = -1f)
        {
            return Enqueue(new Notification
            {
                Message = message,
                Type = type,
                OnClick = onClick,
                Duration = duration > 0 ? duration : _defaultDuration
            });
        }

        /// <summary>
        /// Show an achievement-style notification with a longer default display time.
        /// </summary>
        /// <param name="message">The achievement description.</param>
        /// <param name="icon">Optional achievement icon.</param>
        /// <param name="duration">Display duration (default 5 seconds).</param>
        /// <returns>The created Notification object.</returns>
        public Notification Achievement(string message, Sprite icon = null, float duration = 5f)
        {
            return Enqueue(new Notification
            {
                Message = message,
                Type = NotificationType.Achievement,
                Icon = icon,
                Duration = duration
            });
        }

        /// <summary>
        /// Dismiss a specific notification by its ID.
        /// </summary>
        /// <param name="id">The notification ID to dismiss.</param>
        public void Dismiss(string id)
        {
            var notif = _active.Find(n => n.Id == id);
            if (notif != null) MarkForRemoval(notif);
        }

        /// <summary>Dismiss all active notifications and clear the queue.</summary>
        public void DismissAll()
        {
            foreach (var n in _active) MarkForRemoval(n);
            _queue.Clear();
        }

        /// <summary>
        /// Handle a user click on a notification. Call this from your UI button handler.
        /// Invokes the notification's OnClick callback and then dismisses it.
        /// </summary>
        /// <param name="id">The notification ID that was clicked.</param>
        public void HandleClick(string id)
        {
            var notif = _active.Find(n => n.Id == id);
            if (notif == null) return;

            notif.OnClick?.Invoke();
            OnNotificationClicked?.Invoke(notif);
            Dismiss(id);
        }

        // ─── Internal ───────────────────────────────────────────────

        /// <summary>Assign an ID, set up timing, and either show or queue the notification.</summary>
        private Notification Enqueue(Notification notification)
        {
            notification.Id = $"notif_{_idCounter++}";
            notification.TimeRemaining = notification.Duration;

            if (_active.Count < _maxVisible)
                ShowNotification(notification);
            else
                _queue.Enqueue(notification);

            EventBus.Publish(new OnNotificationEvent
            {
                Message = notification.Message,
                Type = notification.Type
            });

            return notification;
        }

        /// <summary>Add to the active list and fire the show event for UI rendering.</summary>
        private void ShowNotification(Notification notification)
        {
            _active.Add(notification);
            OnNotificationShow?.Invoke(notification);
        }

        /// <summary>Schedule a notification for removal at the end of this frame's update.</summary>
        private void MarkForRemoval(Notification notification)
        {
            if (!_toRemove.Contains(notification))
                _toRemove.Add(notification);
        }

        /// <summary>Unity Update — tick timers, remove expired, fill from queue.</summary>
        private void Update()
        {
            _toRemove.Clear();

            // Count down active notification timers
            foreach (var notif in _active)
            {
                notif.TimeRemaining -= Time.unscaledDeltaTime;
                if (notif.TimeRemaining <= 0f)
                    _toRemove.Add(notif);
            }

            // Remove expired notifications
            foreach (var notif in _toRemove)
            {
                _active.Remove(notif);
                OnNotificationHide?.Invoke(notif);
            }

            // Fill empty display slots from the queue
            while (_active.Count < _maxVisible && _queue.Count > 0)
            {
                ShowNotification(_queue.Dequeue());
            }
        }
    }
}
