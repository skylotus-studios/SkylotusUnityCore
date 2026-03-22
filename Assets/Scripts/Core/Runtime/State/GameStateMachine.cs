using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>Predefined global game states. Extend this enum for project-specific states.</summary>
    public enum GameStateType
    {
        Boot,
        MainMenu,
        Loading,
        Gameplay,
        Paused,
        Cutscene,
        Dialogue,
        GameOver,
        Victory
    }

    /// <summary>
    /// Finite state machine for global game state management.
    /// Supports enter/exit/update callbacks per state, transition guards,
    /// push/pop for overlay states (pause), and state history.
    ///
    /// Usage:
    /// <code>
    /// var gsm = ServiceLocator.Get&lt;GameStateMachine&gt;();
    /// gsm.RegisterState(GameStateType.Gameplay, onEnter: () => EnableInput());
    /// gsm.TransitionTo(GameStateType.Gameplay);
    /// gsm.PushState(GameStateType.Paused);   // overlay
    /// gsm.PopState();                         // back to Gameplay
    /// </code>
    /// </summary>
    public class GameStateMachine : MonoBehaviour
    {
        /// <summary>The currently active state.</summary>
        private GameStateType _currentState = GameStateType.Boot;

        /// <summary>The state before the most recent transition.</summary>
        private GameStateType _previousState = GameStateType.Boot;

        /// <summary>Stack for push/pop overlay states (e.g. Pause on top of Gameplay).</summary>
        private readonly Stack<GameStateType> _stateStack = new();

        /// <summary>Per-state enter callbacks.</summary>
        private readonly Dictionary<GameStateType, Action> _onEnter = new();

        /// <summary>Per-state exit callbacks.</summary>
        private readonly Dictionary<GameStateType, Action> _onExit = new();

        /// <summary>Per-state update callbacks (run every frame while in that state).</summary>
        private readonly Dictionary<GameStateType, Action> _onUpdate = new();

        /// <summary>Blocked transitions: if (from, to) is in this set, TransitionTo returns false.</summary>
        private readonly HashSet<(GameStateType from, GameStateType to)> _blockedTransitions = new();

        /// <summary>The currently active game state.</summary>
        public GameStateType CurrentState => _currentState;

        /// <summary>The state before the most recent transition.</summary>
        public GameStateType PreviousState => _previousState;

        /// <summary>
        /// Fired on every state transition with (oldState, newState).
        /// Also publishes <see cref="OnGameStateChangedEvent"/> via EventBus.
        /// </summary>
        public event Action<GameStateType, GameStateType> OnStateChanged;

        /// <summary>
        /// Register lifecycle callbacks for a state. All parameters are optional.
        /// </summary>
        /// <param name="state">The state to configure.</param>
        /// <param name="onEnter">Called once when entering this state.</param>
        /// <param name="onExit">Called once when leaving this state.</param>
        /// <param name="onUpdate">Called every frame while in this state.</param>
        public void RegisterState(GameStateType state,
            Action onEnter = null, Action onExit = null, Action onUpdate = null)
        {
            if (onEnter != null) _onEnter[state] = onEnter;
            if (onExit != null) _onExit[state] = onExit;
            if (onUpdate != null) _onUpdate[state] = onUpdate;
        }

        /// <summary>
        /// Block a specific state transition. Calls to TransitionTo with this
        /// (from, to) pair will log a warning and return false.
        /// </summary>
        /// <param name="from">The source state.</param>
        /// <param name="to">The destination state that should be blocked.</param>
        public void BlockTransition(GameStateType from, GameStateType to)
        {
            _blockedTransitions.Add((from, to));
        }

        /// <summary>
        /// Transition to a new state. Calls exit on the old state and enter on the new.
        /// Returns false if the transition is blocked or the target is the current state.
        /// </summary>
        /// <param name="newState">The state to transition to.</param>
        /// <returns>True if the transition succeeded.</returns>
        public bool TransitionTo(GameStateType newState)
        {
            if (newState == _currentState) return false;

            // Check transition guards
            if (_blockedTransitions.Contains((_currentState, newState)))
            {
                GameLogger.LogWarning("GameState", $"Transition blocked: {_currentState} -> {newState}");
                return false;
            }

            _previousState = _currentState;

            // Exit the current state
            if (_onExit.TryGetValue(_currentState, out var exitAction))
                exitAction.Invoke();

            var oldState = _currentState;
            _currentState = newState;

            // Enter the new state
            if (_onEnter.TryGetValue(_currentState, out var enterAction))
                enterAction.Invoke();

            // Notify listeners
            OnStateChanged?.Invoke(oldState, _currentState);
            EventBus.Publish(new OnGameStateChangedEvent
            {
                Previous = oldState,
                Current = _currentState
            });

            GameLogger.Log("GameState", $"{oldState} -> {_currentState}");
            return true;
        }

        /// <summary>
        /// Push the current state onto the stack and transition to a new state.
        /// Ideal for overlay states like Pause or Dialogue that return to the previous state.
        /// </summary>
        /// <param name="newState">The overlay state to enter.</param>
        public void PushState(GameStateType newState)
        {
            _stateStack.Push(_currentState);
            TransitionTo(newState);
        }

        /// <summary>
        /// Pop the top of the state stack and transition back to it.
        /// Returns false if the stack is empty.
        /// </summary>
        /// <returns>True if a state was popped and transitioned to.</returns>
        public bool PopState()
        {
            if (_stateStack.Count == 0)
            {
                GameLogger.LogWarning("GameState", "No state to pop");
                return false;
            }
            return TransitionTo(_stateStack.Pop());
        }

        /// <summary>
        /// Check if the FSM is currently in a specific state.
        /// </summary>
        /// <param name="state">The state to check.</param>
        /// <returns>True if it matches the current state.</returns>
        public bool IsInState(GameStateType state) => _currentState == state;

        /// <summary>
        /// Check if the FSM is in any of the given states.
        /// </summary>
        /// <param name="states">Variable-length list of states to check.</param>
        /// <returns>True if the current state matches any of them.</returns>
        public bool IsInAny(params GameStateType[] states)
        {
            foreach (var s in states)
                if (_currentState == s) return true;
            return false;
        }

        /// <summary>Unity Update — runs the registered update callback for the current state.</summary>
        private void Update()
        {
            if (_onUpdate.TryGetValue(_currentState, out var updateAction))
                updateAction.Invoke();
        }
    }
}
