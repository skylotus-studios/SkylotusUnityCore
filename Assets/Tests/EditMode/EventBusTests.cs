using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using AllocIs = UnityEngine.TestTools.Constraints.Is;

namespace Skylotus.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="EventBus"/> — priority ordering, one-shot
    /// subscriptions, mutation during dispatch, the deferred queue, lifecycle-bound
    /// subscriptions, and WP-8's zero-allocation guarantee for the deferred path.
    ///
    /// The bus is static, so every test clears it on both sides. Each case uses its own
    /// event struct where cross-talk would otherwise be possible.
    /// </summary>
    [TestFixture]
    public class EventBusTests
    {
        /// <summary>Generic probe event carrying a value so handlers can record what they saw.</summary>
        private struct ProbeEvent : IGameEvent
        {
            /// <summary>Arbitrary payload.</summary>
            public int Value;
        }

        /// <summary>A second event type, used to prove queue ordering spans types.</summary>
        private struct OtherProbeEvent : IGameEvent
        {
            /// <summary>Arbitrary payload.</summary>
            public int Value;
        }

        /// <summary>Event type reserved for the allocation measurement so nothing else subscribes to it.</summary>
        private struct AllocProbeEvent : IGameEvent
        {
            /// <summary>Arbitrary payload.</summary>
            public int Value;
        }

        /// <summary>A <see cref="UnityEngine.Object"/> that can be destroyed in Edit Mode.</summary>
        private class OwnerProbe : ScriptableObject { }

        /// <summary>Clear the bus and silence its error channel before each test.</summary>
        [SetUp]
        public void SetUp()
        {
            EventBus.ClearAll();
            GameLogger.SetCategoryLevel("EventBus", LogLevel.Off);
        }

        /// <summary>Clear the bus and restore logging afterwards.</summary>
        [TearDown]
        public void TearDown()
        {
            EventBus.ClearAll();
            GameLogger.SetCategoryLevel("EventBus", LogLevel.Debug);
        }

        // ─── Priority ordering ──────────────────────────────────────

        /// <summary>Higher priority values run first, regardless of subscription order.</summary>
        [Test]
        public void Publish_DeliversInDescendingPriorityOrder()
        {
            var order = new List<string>();

            EventBus.Subscribe<ProbeEvent>(_ => order.Add("low"), priority: -10);
            EventBus.Subscribe<ProbeEvent>(_ => order.Add("high"), priority: 100);
            EventBus.Subscribe<ProbeEvent>(_ => order.Add("mid"), priority: 0);

            EventBus.Publish(new ProbeEvent());

            CollectionAssert.AreEqual(new[] { "high", "mid", "low" }, order);
        }

        /// <summary>Subscribers with equal priority keep their subscription order.</summary>
        [Test]
        public void Publish_EqualPriority_KeepsSubscriptionOrder()
        {
            var order = new List<string>();

            EventBus.Subscribe<ProbeEvent>(_ => order.Add("first"));
            EventBus.Subscribe<ProbeEvent>(_ => order.Add("second"));
            EventBus.Subscribe<ProbeEvent>(_ => order.Add("third"));

            EventBus.Publish(new ProbeEvent());

            CollectionAssert.AreEqual(new[] { "first", "second", "third" }, order);
        }

        /// <summary>The published struct reaches the handler with its fields intact.</summary>
        [Test]
        public void Publish_CarriesThePayload()
        {
            int seen = 0;
            EventBus.Subscribe<ProbeEvent>(e => seen = e.Value);

            EventBus.Publish(new ProbeEvent { Value = 42 });

            Assert.AreEqual(42, seen);
        }

        // ─── SubscribeOnce ──────────────────────────────────────────

        /// <summary>A one-shot subscriber fires exactly once and is gone afterwards.</summary>
        [Test]
        public void SubscribeOnce_FiresOnceThenUnsubscribes()
        {
            int calls = 0;
            EventBus.SubscribeOnce<ProbeEvent>(_ => calls++);

            EventBus.Publish(new ProbeEvent());
            EventBus.Publish(new ProbeEvent());
            EventBus.Publish(new ProbeEvent());

            Assert.AreEqual(1, calls);
        }

        /// <summary>A one-shot subscriber's removal does not disturb an ordinary one beside it.</summary>
        [Test]
        public void SubscribeOnce_DoesNotRemoveOtherSubscribers()
        {
            int once = 0;
            int persistent = 0;

            EventBus.SubscribeOnce<ProbeEvent>(_ => once++);
            EventBus.Subscribe<ProbeEvent>(_ => persistent++);

            EventBus.Publish(new ProbeEvent());
            EventBus.Publish(new ProbeEvent());

            Assert.AreEqual(1, once);
            Assert.AreEqual(2, persistent);
        }

        // ─── Mutation during dispatch ───────────────────────────────

        /// <summary>
        /// Unsubscribing from inside a handler must take effect for the rest of the current
        /// dispatch, not just for later publishes — the case that made the old snapshot-copy
        /// implementation allocate on every publish.
        /// </summary>
        [Test]
        public void Unsubscribe_DuringPublish_StopsTheRemovedHandlerImmediately()
        {
            int victimCalls = 0;
            Action<ProbeEvent> victim = _ => victimCalls++;

            // Higher priority, so it runs before the victim and can remove it mid-dispatch.
            EventBus.Subscribe<ProbeEvent>(_ => EventBus.Unsubscribe(victim), priority: 10);
            EventBus.Subscribe<ProbeEvent>(victim, priority: 0);

            EventBus.Publish(new ProbeEvent());

            Assert.AreEqual(0, victimCalls,
                "A handler removed earlier in the same dispatch must not be invoked.");
        }

        /// <summary>A handler that removes itself still completes the call it is inside.</summary>
        [Test]
        public void Unsubscribe_SelfDuringPublish_CompletesThenStops()
        {
            int calls = 0;
            Action<ProbeEvent> handler = null;
            handler = _ =>
            {
                calls++;
                EventBus.Unsubscribe(handler);
            };

            EventBus.Subscribe(handler);

            EventBus.Publish(new ProbeEvent());
            EventBus.Publish(new ProbeEvent());

            Assert.AreEqual(1, calls);
        }

        /// <summary>
        /// A subscriber added from inside a handler is not delivered the event currently in
        /// flight; it starts receiving on the next publish.
        /// </summary>
        [Test]
        public void Subscribe_DuringPublish_DoesNotReceiveTheInFlightEvent()
        {
            int lateCalls = 0;

            EventBus.SubscribeOnce<ProbeEvent>(_ =>
                EventBus.Subscribe<ProbeEvent>(_2 => lateCalls++));

            EventBus.Publish(new ProbeEvent());
            Assert.AreEqual(0, lateCalls, "The newly added handler must not see the in-flight event.");

            EventBus.Publish(new ProbeEvent());
            Assert.AreEqual(1, lateCalls, "It must receive the next event.");
        }

        /// <summary>A throwing handler is logged and skipped; the remaining subscribers still run.</summary>
        [Test]
        public void Publish_HandlerException_DoesNotStopOtherSubscribers()
        {
            int after = 0;

            EventBus.Subscribe<ProbeEvent>(_ => throw new InvalidOperationException("boom"), priority: 10);
            EventBus.Subscribe<ProbeEvent>(_ => after++, priority: 0);

            Assert.DoesNotThrow(() => EventBus.Publish(new ProbeEvent()));
            Assert.AreEqual(1, after);
        }

        // ─── Deferred queue ─────────────────────────────────────────

        /// <summary>Enqueued events are held until <c>ProcessQueue</c> runs.</summary>
        [Test]
        public void Enqueue_DefersDeliveryUntilProcessQueue()
        {
            int calls = 0;
            EventBus.Subscribe<ProbeEvent>(_ => calls++);

            EventBus.Enqueue(new ProbeEvent());
            Assert.AreEqual(0, calls, "Enqueue must not dispatch.");
            Assert.AreEqual(1, EventBus.QueuedEventCount);

            EventBus.ProcessQueue();

            Assert.AreEqual(1, calls);
            Assert.AreEqual(0, EventBus.QueuedEventCount);
        }

        /// <summary>The queue is global FIFO: events of different types keep their arrival order.</summary>
        [Test]
        public void ProcessQueue_PreservesArrivalOrderAcrossEventTypes()
        {
            var order = new List<string>();

            EventBus.Subscribe<ProbeEvent>(e => order.Add($"probe{e.Value}"));
            EventBus.Subscribe<OtherProbeEvent>(e => order.Add($"other{e.Value}"));

            EventBus.Enqueue(new ProbeEvent { Value = 1 });
            EventBus.Enqueue(new OtherProbeEvent { Value = 2 });
            EventBus.Enqueue(new ProbeEvent { Value = 3 });

            EventBus.ProcessQueue();

            CollectionAssert.AreEqual(new[] { "probe1", "other2", "probe3" }, order);
        }

        /// <summary>
        /// Re-entrant processing is ignored, so a handler that enqueues while the queue is
        /// draining cannot spin forever. The nested event waits for the next pass.
        /// </summary>
        [Test]
        public void ProcessQueue_IsNotReentrant()
        {
            int calls = 0;

            EventBus.Subscribe<ProbeEvent>(e =>
            {
                calls++;
                if (e.Value > 0)
                {
                    EventBus.Enqueue(new ProbeEvent { Value = e.Value - 1 });
                    EventBus.ProcessQueue(); // must be ignored
                }
            });

            EventBus.Enqueue(new ProbeEvent { Value = 1 });
            EventBus.ProcessQueue();

            Assert.AreEqual(2, calls,
                "The outer drain should deliver the original event and the one it enqueued, " +
                "with the re-entrant ProcessQueue ignored.");
        }

        /// <summary>Queued events survive a subscriber clear, but reach no one.</summary>
        [Test]
        public void Clear_DropsSubscribersButStillDrainsTheQueue()
        {
            int calls = 0;
            EventBus.Subscribe<ProbeEvent>(_ => calls++);

            EventBus.Enqueue(new ProbeEvent());
            EventBus.Clear<ProbeEvent>();
            EventBus.ProcessQueue();

            Assert.AreEqual(0, calls);
            Assert.AreEqual(0, EventBus.QueuedEventCount);
        }

        /// <summary><c>ClearAll</c> removes subscribers for every type and empties the queue.</summary>
        [Test]
        public void ClearAll_RemovesEverything()
        {
            int calls = 0;
            EventBus.Subscribe<ProbeEvent>(_ => calls++);
            EventBus.Subscribe<OtherProbeEvent>(_ => calls++);
            EventBus.Enqueue(new ProbeEvent());

            EventBus.ClearAll();

            Assert.AreEqual(0, EventBus.QueuedEventCount);

            EventBus.Publish(new ProbeEvent());
            EventBus.Publish(new OtherProbeEvent());
            EventBus.ProcessQueue();

            Assert.AreEqual(0, calls);
        }

        // ─── Lifecycle-bound subscriptions (WP-8) ───────────────────

        /// <summary>
        /// WP-8's third criterion: destroying a subscriber registered with
        /// <c>SubscribeWhileAlive</c> stops delivery with no manual unsubscribe.
        /// </summary>
        [Test]
        public void SubscribeWhileAlive_StopsDeliveringOnceTheOwnerIsDestroyed()
        {
            var owner = ScriptableObject.CreateInstance<OwnerProbe>();
            int calls = 0;

            EventBus.SubscribeWhileAlive<ProbeEvent>(owner, _ => calls++);

            EventBus.Publish(new ProbeEvent());
            Assert.AreEqual(1, calls, "A live owner must receive the event.");

            UnityEngine.Object.DestroyImmediate(owner);

            EventBus.Publish(new ProbeEvent());
            Assert.AreEqual(1, calls, "A destroyed owner must not receive further events.");
        }

        /// <summary>A destroyed owner's dead subscription does not block the live ones beside it.</summary>
        [Test]
        public void SubscribeWhileAlive_DeadOwnerDoesNotBlockOtherSubscribers()
        {
            var owner = ScriptableObject.CreateInstance<OwnerProbe>();
            int deadCalls = 0;
            int liveCalls = 0;

            EventBus.SubscribeWhileAlive<ProbeEvent>(owner, _ => deadCalls++, priority: 10);
            EventBus.Subscribe<ProbeEvent>(_ => liveCalls++, priority: 0);

            UnityEngine.Object.DestroyImmediate(owner);
            EventBus.Publish(new ProbeEvent());

            Assert.AreEqual(0, deadCalls);
            Assert.AreEqual(1, liveCalls);
        }

        /// <summary>Passing a null owner is refused rather than registering an unkillable subscription.</summary>
        [Test]
        public void SubscribeWhileAlive_NullOwner_IsIgnored()
        {
            int calls = 0;

            EventBus.SubscribeWhileAlive<ProbeEvent>(null, _ => calls++);
            EventBus.Publish(new ProbeEvent());

            Assert.AreEqual(0, calls);
        }

        // ─── Allocation (WP-8 criterion 1) ──────────────────────────

        /// <summary>
        /// WP-8's headline criterion: an <c>Enqueue</c> -> <c>ProcessQueue</c> round trip must
        /// allocate zero bytes once the channels and queues are warm.
        ///
        /// Measured with the test framework's <c>GC.Alloc</c> profiler recorder, which counts
        /// managed allocations made inside the delegate. <c>GC.GetAllocatedBytesForCurrentThread</c>
        /// is <b>not</b> usable here — it returns 0 unconditionally on this runtime, so a test built
        /// on it passes whether the bus allocates or not.
        ///
        /// A warm-up batch of the same size runs first so every internal queue has reached its
        /// final capacity, and the handler is a cached delegate writing to a captured field, so
        /// anything the recorder sees comes from the bus itself.
        /// </summary>
        [Test]
        public void EnqueueThenProcessQueue_AllocatesNothingPerEvent()
        {
            const int batch = 256;
            int sink = 0;

            Action<AllocProbeEvent> handler = e => sink += e.Value;
            EventBus.Subscribe(handler);

            // Warm up: JIT the generic instantiations and grow both the typed queue and the
            // global dispatch-order queue to their final capacity.
            for (int pass = 0; pass < 4; pass++)
            {
                for (int i = 0; i < batch; i++) EventBus.Enqueue(new AllocProbeEvent { Value = 1 });
                EventBus.ProcessQueue();
            }

            Assert.That(() =>
            {
                for (int i = 0; i < batch; i++) EventBus.Enqueue(new AllocProbeEvent { Value = 1 });
                EventBus.ProcessQueue();
            }, AllocIs.Not.AllocatingGCMemory());

            Assert.AreEqual(batch * 5, sink, "Sanity: every event must actually have been delivered.");
        }

        /// <summary>
        /// WP-8's second criterion, checked against the source itself: no <c>DynamicInvoke</c>
        /// may remain in <c>EventBus.cs</c>. A behavioural test cannot see the difference between
        /// a direct call and a reflected one, so this reads the file.
        /// </summary>
        [Test]
        public void EventBusSource_ContainsNoDynamicInvoke()
        {
            string path = Path.Combine(Application.dataPath, "Scripts/Core/Runtime/Events/EventBus.cs");

            Assert.IsTrue(File.Exists(path), $"EventBus.cs not found at {path}");
            StringAssert.DoesNotContain("DynamicInvoke", File.ReadAllText(path));
        }
    }
}
