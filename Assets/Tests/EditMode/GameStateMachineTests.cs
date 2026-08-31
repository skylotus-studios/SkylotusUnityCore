using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Skylotus.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="GameStateMachine"/>: transitions and their callbacks,
    /// blocked transitions, the push/pop overlay stack, and event emission on both the C# event
    /// and the <see cref="EventBus"/>.
    ///
    /// The machine is a MonoBehaviour but does no work in <c>Awake</c>, so a throwaway hidden
    /// GameObject is enough. Its per-frame <c>Update</c> never runs in Edit Mode, so the one test
    /// that needs it calls the private method directly.
    /// </summary>
    [TestFixture]
    public class GameStateMachineTests
    {
        /// <summary>The machine under test, rebuilt for every case.</summary>
        private GameStateMachine _machine;

        /// <summary>The host GameObject, destroyed in teardown.</summary>
        private GameObject _host;

        /// <summary>Build a fresh machine and a clean bus.</summary>
        [SetUp]
        public void SetUp()
        {
            EventBus.ClearAll();
            GameLogger.SetCategoryLevel("GameState", LogLevel.Off);

            _host = new GameObject("GameStateMachineTests") { hideFlags = HideFlags.HideAndDontSave };
            _machine = _host.AddComponent<GameStateMachine>();
        }

        /// <summary>Tear the machine down and restore logging.</summary>
        [TearDown]
        public void TearDown()
        {
            if (_host != null) Object.DestroyImmediate(_host);

            EventBus.ClearAll();
            GameLogger.SetCategoryLevel("GameState", LogLevel.Debug);
        }

        // ─── Transitions ────────────────────────────────────────────

        /// <summary>A fresh machine starts in <see cref="GameStateType.Boot"/>.</summary>
        [Test]
        public void NewMachine_StartsInBoot()
        {
            Assert.AreEqual(GameStateType.Boot, _machine.CurrentState);
            Assert.AreEqual(GameStateType.Boot, _machine.PreviousState);
        }

        /// <summary>A transition moves the current state and records the previous one.</summary>
        [Test]
        public void TransitionTo_ChangesCurrentAndPreviousState()
        {
            Assert.IsTrue(_machine.TransitionTo(GameStateType.MainMenu));

            Assert.AreEqual(GameStateType.MainMenu, _machine.CurrentState);
            Assert.AreEqual(GameStateType.Boot, _machine.PreviousState);
        }

        /// <summary>Exit runs on the old state and enter on the new, in that order.</summary>
        [Test]
        public void TransitionTo_RunsExitThenEnter()
        {
            var order = new List<string>();

            _machine.RegisterState(GameStateType.Boot, onExit: () => order.Add("exitBoot"));
            _machine.RegisterState(GameStateType.Gameplay, onEnter: () => order.Add("enterGameplay"));

            _machine.TransitionTo(GameStateType.Gameplay);

            CollectionAssert.AreEqual(new[] { "exitBoot", "enterGameplay" }, order);
        }

        /// <summary>Transitioning to the state already active is refused and fires no callbacks.</summary>
        [Test]
        public void TransitionTo_SameState_ReturnsFalseAndRunsNoCallbacks()
        {
            int enters = 0;
            _machine.RegisterState(GameStateType.Boot, onEnter: () => enters++);

            Assert.IsFalse(_machine.TransitionTo(GameStateType.Boot));
            Assert.AreEqual(0, enters);
        }

        // ─── Blocked transitions ────────────────────────────────────

        /// <summary>A blocked (from, to) pair is refused and leaves the state untouched.</summary>
        [Test]
        public void BlockTransition_PreventsThatTransitionOnly()
        {
            _machine.BlockTransition(GameStateType.Boot, GameStateType.Gameplay);

            Assert.IsFalse(_machine.TransitionTo(GameStateType.Gameplay));
            Assert.AreEqual(GameStateType.Boot, _machine.CurrentState);

            // Only the named pair is blocked — another destination still works.
            Assert.IsTrue(_machine.TransitionTo(GameStateType.MainMenu));
            Assert.AreEqual(GameStateType.MainMenu, _machine.CurrentState);
        }

        /// <summary>A blocked transition runs neither the exit nor the enter callback.</summary>
        [Test]
        public void BlockTransition_RunsNoCallbacks()
        {
            int exits = 0;
            int enters = 0;

            _machine.RegisterState(GameStateType.Boot, onExit: () => exits++);
            _machine.RegisterState(GameStateType.Gameplay, onEnter: () => enters++);
            _machine.BlockTransition(GameStateType.Boot, GameStateType.Gameplay);

            _machine.TransitionTo(GameStateType.Gameplay);

            Assert.AreEqual(0, exits);
            Assert.AreEqual(0, enters);
        }

        /// <summary>Blocking is directional: blocking A→B does not block B→A.</summary>
        [Test]
        public void BlockTransition_IsDirectional()
        {
            _machine.BlockTransition(GameStateType.Gameplay, GameStateType.MainMenu);

            Assert.IsTrue(_machine.TransitionTo(GameStateType.MainMenu));
            Assert.IsTrue(_machine.TransitionTo(GameStateType.Gameplay));
            Assert.IsFalse(_machine.TransitionTo(GameStateType.MainMenu));
        }

        // ─── Push / pop ─────────────────────────────────────────────

        /// <summary>Push then pop returns to the state that was active before the overlay.</summary>
        [Test]
        public void PushState_ThenPopState_ReturnsToTheUnderlyingState()
        {
            _machine.TransitionTo(GameStateType.Gameplay);

            _machine.PushState(GameStateType.Paused);
            Assert.AreEqual(GameStateType.Paused, _machine.CurrentState);

            Assert.IsTrue(_machine.PopState());
            Assert.AreEqual(GameStateType.Gameplay, _machine.CurrentState);
        }

        /// <summary>Overlays nest: two pushes need two pops, unwinding in reverse order.</summary>
        [Test]
        public void PushState_Nested_PopsInReverseOrder()
        {
            _machine.TransitionTo(GameStateType.Gameplay);
            _machine.PushState(GameStateType.Dialogue);
            _machine.PushState(GameStateType.Paused);

            _machine.PopState();
            Assert.AreEqual(GameStateType.Dialogue, _machine.CurrentState);

            _machine.PopState();
            Assert.AreEqual(GameStateType.Gameplay, _machine.CurrentState);
        }

        /// <summary>Popping an empty stack is refused rather than throwing.</summary>
        [Test]
        public void PopState_EmptyStack_ReturnsFalse()
        {
            Assert.IsFalse(_machine.PopState());
            Assert.AreEqual(GameStateType.Boot, _machine.CurrentState);
        }

        // ─── Event emission ─────────────────────────────────────────

        /// <summary>A transition raises <c>OnStateChanged</c> with (old, new).</summary>
        [Test]
        public void TransitionTo_RaisesOnStateChanged()
        {
            GameStateType? from = null;
            GameStateType? to = null;

            _machine.OnStateChanged += (previous, current) =>
            {
                from = previous;
                to = current;
            };

            _machine.TransitionTo(GameStateType.Loading);

            Assert.AreEqual(GameStateType.Boot, from);
            Assert.AreEqual(GameStateType.Loading, to);
        }

        /// <summary>A transition also publishes <see cref="OnGameStateChangedEvent"/> on the bus.</summary>
        [Test]
        public void TransitionTo_PublishesOnGameStateChangedEvent()
        {
            OnGameStateChangedEvent captured = default;
            int published = 0;

            EventBus.Subscribe<OnGameStateChangedEvent>(e =>
            {
                captured = e;
                published++;
            });

            _machine.TransitionTo(GameStateType.Victory);

            Assert.AreEqual(1, published);
            Assert.AreEqual(GameStateType.Boot, captured.Previous);
            Assert.AreEqual(GameStateType.Victory, captured.Current);
        }

        /// <summary>A refused transition publishes nothing.</summary>
        [Test]
        public void BlockedTransition_PublishesNothing()
        {
            int published = 0;
            EventBus.Subscribe<OnGameStateChangedEvent>(_ => published++);

            _machine.BlockTransition(GameStateType.Boot, GameStateType.GameOver);
            _machine.TransitionTo(GameStateType.GameOver);

            Assert.AreEqual(0, published);
        }

        // ─── Queries ────────────────────────────────────────────────

        /// <summary><c>IsInState</c> and <c>IsInAny</c> answer against the current state.</summary>
        [Test]
        public void IsInState_AndIsInAny_ReflectTheCurrentState()
        {
            _machine.TransitionTo(GameStateType.Cutscene);

            Assert.IsTrue(_machine.IsInState(GameStateType.Cutscene));
            Assert.IsFalse(_machine.IsInState(GameStateType.Gameplay));

            Assert.IsTrue(_machine.IsInAny(GameStateType.Gameplay, GameStateType.Cutscene));
            Assert.IsFalse(_machine.IsInAny(GameStateType.Gameplay, GameStateType.Paused));
        }

        // ─── Per-state update ───────────────────────────────────────

        /// <summary>
        /// The registered update callback runs only while its state is current. Edit Mode never
        /// ticks a MonoBehaviour, so the private <c>Update</c> is invoked directly — everything
        /// else in this fixture goes through the public API.
        /// </summary>
        [Test]
        public void Update_RunsOnlyTheCurrentStatesCallback()
        {
            int bootTicks = 0;
            int gameplayTicks = 0;

            _machine.RegisterState(GameStateType.Boot, onUpdate: () => bootTicks++);
            _machine.RegisterState(GameStateType.Gameplay, onUpdate: () => gameplayTicks++);

            Tick();
            Assert.AreEqual(1, bootTicks);
            Assert.AreEqual(0, gameplayTicks);

            _machine.TransitionTo(GameStateType.Gameplay);

            Tick();
            Assert.AreEqual(1, bootTicks, "The old state's update must stop running.");
            Assert.AreEqual(1, gameplayTicks);
        }

        /// <summary>Invoke the machine's private per-frame <c>Update</c> once.</summary>
        private void Tick()
        {
            var update = typeof(GameStateMachine).GetMethod(
                "Update", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(update, "GameStateMachine.Update not found — was it renamed?");
            update.Invoke(_machine, null);
        }
    }
}
