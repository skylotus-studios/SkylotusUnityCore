using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// A single dialogue tree containing an ordered list of nodes.
    /// Serialize from JSON or construct in code.
    /// </summary>
    [Serializable]
    public class DialogueData
    {
        /// <summary>Unique identifier for this dialogue tree.</summary>
        public string Id;

        /// <summary>Ordered list of dialogue nodes in this tree.</summary>
        public List<DialogueNode> Nodes = new();
    }

    /// <summary>
    /// A single node in a dialogue tree — one line of dialogue with optional
    /// branching choices, conditions, and event hooks.
    /// </summary>
    [Serializable]
    public class DialogueNode
    {
        /// <summary>Unique ID of this node within its tree.</summary>
        public string NodeId;

        /// <summary>Display name of the character speaking.</summary>
        public string Speaker;

        /// <summary>The dialogue text. Prefix with "loc:" to use a localization key.</summary>
        public string Text;

        /// <summary>Resource path for the speaker's portrait sprite (optional).</summary>
        public string Portrait;

        /// <summary>Auto-advance delay in seconds (0 = manual advance only).</summary>
        public float AutoAdvanceTime;

        /// <summary>Branching choices shown to the player (empty = linear flow).</summary>
        public List<DialogueChoice> Choices = new();

        /// <summary>ID of the next node (used when there are no choices).</summary>
        public string NextNodeId;

        /// <summary>Event key fired when this node is entered (optional).</summary>
        public string OnEnterEvent;

        /// <summary>Event key fired when this node is exited (optional).</summary>
        public string OnExitEvent;

        /// <summary>Condition key — if set and evaluates false, the node is skipped.</summary>
        public string Condition;
    }

    /// <summary>
    /// A player-facing dialogue choice that leads to a different node.
    /// </summary>
    [Serializable]
    public class DialogueChoice
    {
        /// <summary>Display text for this choice.</summary>
        public string Text;

        /// <summary>Node ID to jump to when this choice is selected.</summary>
        public string NextNodeId;

        /// <summary>Condition key — if set and evaluates false, this choice is hidden.</summary>
        public string Condition;

        /// <summary>Event key fired when this choice is selected (optional).</summary>
        public string OnSelectEvent;
    }

    /// <summary>EventBus event fired on dialogue node enter/exit with a custom event key.</summary>
    public struct OnDialogueNodeEvent : IGameEvent
    {
        /// <summary>The active dialogue tree ID.</summary>
        public string DialogueId;

        /// <summary>The node ID that triggered the event.</summary>
        public string NodeId;

        /// <summary>The custom event key string.</summary>
        public string EventKey;
    }

    /// <summary>
    /// Dialogue system that processes branching dialogue trees with conditional
    /// choices, localization integration, speaker portraits, and event hooks.
    ///
    /// Wire up UI callbacks (OnShowNode, OnShowChoices, OnDialogueEnded) to render
    /// the dialogue in your game's UI.
    ///
    /// Usage:
    /// <code>
    /// var dialogue = ServiceLocator.Get&lt;DialogueSystem&gt;();
    /// dialogue.RegisterCondition("has_key", () => inventory.Has("key"));
    /// dialogue.OnShowNode += node => dialogueUI.Show(node);
    /// dialogue.StartDialogue("npc_blacksmith");
    /// </code>
    /// </summary>
    public class DialogueSystem : MonoBehaviour
    {
        /// <summary>All registered dialogue trees keyed by ID.</summary>
        private readonly Dictionary<string, DialogueData> _dialogues = new();

        /// <summary>Registered condition functions keyed by condition name.</summary>
        private readonly Dictionary<string, Func<bool>> _conditions = new();

        /// <summary>Registered event handlers keyed by event name.</summary>
        private readonly Dictionary<string, Action> _dialogueEvents = new();

        /// <summary>The currently playing dialogue tree (null if none).</summary>
        private DialogueData _activeDialogue;

        /// <summary>The currently displayed node.</summary>
        private DialogueNode _currentNode;

        /// <summary>Whether a dialogue is currently in progress.</summary>
        private bool _isActive;

        /// <summary>Whether a dialogue is currently in progress.</summary>
        public bool IsActive => _isActive;

        /// <summary>The node currently being displayed.</summary>
        public DialogueNode CurrentNode => _currentNode;

        /// <summary>Fired when a node should be displayed in the UI.</summary>
        public event Action<DialogueNode> OnShowNode;

        /// <summary>Fired when choices should be displayed (only valid choices included).</summary>
        public event Action<List<DialogueChoice>> OnShowChoices;

        /// <summary>Fired when a dialogue begins.</summary>
        public event Action OnDialogueStarted;

        /// <summary>Fired when a dialogue ends (naturally or via Skip).</summary>
        public event Action OnDialogueEnded;

        // ─── Data Management ────────────────────────────────────────

        /// <summary>
        /// Register a dialogue tree for later playback.
        /// </summary>
        /// <param name="data">The dialogue data to register.</param>
        public void RegisterDialogue(DialogueData data)
        {
            _dialogues[data.Id] = data;
        }

        /// <summary>
        /// Load and register a dialogue tree from a JSON TextAsset.
        /// </summary>
        /// <param name="asset">A TextAsset containing JSON-serialized DialogueData.</param>
        public void LoadDialogue(TextAsset asset)
        {
            var data = JsonUtility.FromJson<DialogueData>(asset.text);
            RegisterDialogue(data);
        }

        /// <summary>
        /// Register a condition function that dialogue nodes and choices can reference.
        /// </summary>
        /// <param name="key">The condition name (referenced in DialogueNode.Condition / DialogueChoice.Condition).</param>
        /// <param name="condition">A function returning true/false.</param>
        public void RegisterCondition(string key, Func<bool> condition)
        {
            _conditions[key] = condition;
        }

        /// <summary>
        /// Register an event handler that dialogue nodes can trigger on enter/exit.
        /// </summary>
        /// <param name="key">The event name (referenced in DialogueNode.OnEnterEvent / OnExitEvent).</param>
        /// <param name="handler">The action to invoke.</param>
        public void RegisterEvent(string key, Action handler)
        {
            _dialogueEvents[key] = handler;
        }

        // ─── Playback ───────────────────────────────────────────────

        /// <summary>
        /// Start playing a dialogue by its registered ID. Begins at the first node.
        /// </summary>
        /// <param name="dialogueId">The ID of a previously registered dialogue tree.</param>
        public void StartDialogue(string dialogueId)
        {
            if (!_dialogues.TryGetValue(dialogueId, out var data))
            {
                GameLogger.LogError("Dialogue", $"Dialogue not found: {dialogueId}");
                return;
            }

            if (data.Nodes.Count == 0) return;

            _activeDialogue = data;
            _isActive = true;

            OnDialogueStarted?.Invoke();
            EventBus.Publish(new OnDialogueEvent
            {
                DialogueId = dialogueId,
                EventType = DialogueEventType.Started
            });

            ShowNode(data.Nodes[0]);
        }

        /// <summary>
        /// Advance to the next node in a linear dialogue. If the current node has
        /// unanswered choices, this is a no-op (use SelectChoice instead).
        /// </summary>
        public void Advance()
        {
            if (!_isActive || _currentNode == null) return;

            // Don't auto-advance if there are pending choices
            var validChoices = GetValidChoices(_currentNode);
            if (validChoices.Count > 0) return;

            FireExitEvent();

            if (string.IsNullOrEmpty(_currentNode.NextNodeId))
            {
                EndDialogue();
                return;
            }

            var next = FindNode(_currentNode.NextNodeId);
            if (next != null) ShowNode(next);
            else EndDialogue();
        }

        /// <summary>
        /// Select a dialogue choice by its index in the filtered (valid) choices list.
        /// </summary>
        /// <param name="choiceIndex">Zero-based index into the list of valid choices.</param>
        public void SelectChoice(int choiceIndex)
        {
            if (!_isActive || _currentNode == null) return;

            var validChoices = GetValidChoices(_currentNode);
            if (choiceIndex < 0 || choiceIndex >= validChoices.Count) return;

            var choice = validChoices[choiceIndex];

            // Fire the choice's event hook
            if (!string.IsNullOrEmpty(choice.OnSelectEvent))
                FireEvent(choice.OnSelectEvent);

            EventBus.Publish(new OnDialogueEvent
            {
                DialogueId = _activeDialogue.Id,
                EventType = DialogueEventType.ChoiceMade
            });

            FireExitEvent();

            if (string.IsNullOrEmpty(choice.NextNodeId))
            {
                EndDialogue();
                return;
            }

            var next = FindNode(choice.NextNodeId);
            if (next != null) ShowNode(next);
            else EndDialogue();
        }

        /// <summary>
        /// Skip / force-end the current dialogue immediately.
        /// </summary>
        public void Skip()
        {
            if (_isActive) EndDialogue();
        }

        /// <summary>
        /// Resolve the display text for a node. If the text starts with "loc:",
        /// the remainder is treated as a localization key.
        /// </summary>
        /// <param name="node">The dialogue node.</param>
        /// <returns>The resolved text string.</returns>
        public string GetNodeText(DialogueNode node)
        {
            if (node.Text.StartsWith("loc:"))
            {
                var key = node.Text.Substring(4);
                if (ServiceLocator.TryGet<LocalizationSystem>(out var loc))
                    return loc.Get(key);
            }
            return node.Text;
        }

        // ─── Internal ───────────────────────────────────────────────

        /// <summary>Display a node: check conditions, fire enter events, notify UI.</summary>
        private void ShowNode(DialogueNode node)
        {
            _currentNode = node;

            // Skip nodes whose condition evaluates to false
            if (!string.IsNullOrEmpty(node.Condition) && !CheckCondition(node.Condition))
            {
                if (!string.IsNullOrEmpty(node.NextNodeId))
                {
                    var next = FindNode(node.NextNodeId);
                    if (next != null) { ShowNode(next); return; }
                }
                EndDialogue();
                return;
            }

            // Fire the node's enter event
            if (!string.IsNullOrEmpty(node.OnEnterEvent))
                FireEvent(node.OnEnterEvent);

            // Notify UI to display this node
            OnShowNode?.Invoke(node);

            EventBus.Publish(new OnDialogueEvent
            {
                DialogueId = _activeDialogue.Id,
                EventType = DialogueEventType.LineAdvanced
            });

            // Show choices if any pass their conditions
            var validChoices = GetValidChoices(node);
            if (validChoices.Count > 0)
                OnShowChoices?.Invoke(validChoices);

            // Auto-advance timer (only for linear nodes without choices)
            if (node.AutoAdvanceTime > 0f && validChoices.Count == 0)
            {
                if (ServiceLocator.TryGet<TimeManager>(out var tm))
                    tm.CreateTimer($"dialogue_auto_{node.NodeId}", node.AutoAdvanceTime,
                        Advance, useUnscaledTime: true);
            }
        }

        /// <summary>Clean up and end the current dialogue, notifying listeners.</summary>
        private void EndDialogue()
        {
            FireExitEvent();

            _isActive = false;
            _currentNode = null;

            var id = _activeDialogue?.Id;
            _activeDialogue = null;

            OnDialogueEnded?.Invoke();
            EventBus.Publish(new OnDialogueEvent
            {
                DialogueId = id,
                EventType = DialogueEventType.Ended
            });
        }

        /// <summary>Filter a node's choices to only those whose conditions pass.</summary>
        private List<DialogueChoice> GetValidChoices(DialogueNode node)
        {
            var valid = new List<DialogueChoice>();
            foreach (var choice in node.Choices)
            {
                if (string.IsNullOrEmpty(choice.Condition) || CheckCondition(choice.Condition))
                    valid.Add(choice);
            }
            return valid;
        }

        /// <summary>Find a node by ID within the active dialogue tree.</summary>
        private DialogueNode FindNode(string nodeId)
        {
            foreach (var node in _activeDialogue.Nodes)
                if (node.NodeId == nodeId) return node;
            return null;
        }

        /// <summary>Evaluate a registered condition by key. Defaults to true if unregistered.</summary>
        private bool CheckCondition(string key)
        {
            if (_conditions.TryGetValue(key, out var check))
                return check.Invoke();
            GameLogger.LogWarning("Dialogue", $"Condition not registered: {key}");
            return true;
        }

        /// <summary>Fire a registered event and publish an OnDialogueNodeEvent via EventBus.</summary>
        private void FireEvent(string key)
        {
            if (_dialogueEvents.TryGetValue(key, out var handler))
                handler.Invoke();

            EventBus.Publish(new OnDialogueNodeEvent
            {
                DialogueId = _activeDialogue?.Id,
                NodeId = _currentNode?.NodeId,
                EventKey = key
            });
        }

        /// <summary>Fire the current node's exit event if one is defined.</summary>
        private void FireExitEvent()
        {
            if (_currentNode != null && !string.IsNullOrEmpty(_currentNode.OnExitEvent))
                FireEvent(_currentNode.OnExitEvent);
        }
    }
}
