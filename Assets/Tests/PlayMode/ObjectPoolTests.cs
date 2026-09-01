using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Skylotus.Tests.PlayMode
{
    /// <summary>
    /// PlayMode coverage for <see cref="ObjectPool"/> (WP-7): spawn and despawn bookkeeping, the
    /// <c>maxSize</c> cap under sustained spawn pressure, <see cref="IPoolable"/> callbacks, the
    /// O(1) owner lookup, and — the criterion WP-7's author reasoned about but never observed —
    /// survival of a single-mode scene load.
    ///
    /// <b>Isolation.</b> WP-5's editor auto-bootstrap brings the real core systems up before any
    /// test runs, so a registered <see cref="ObjectPool"/> already exists. These tests deliberately
    /// do not use it: each builds its own pool on a throwaway <c>DontDestroyOnLoad</c> GameObject,
    /// mirroring how the real one lives under the core systems root, and tears it down afterwards.
    /// Nothing here reads or writes <see cref="ServiceLocator"/>.
    /// </summary>
    [TestFixture]
    public class ObjectPoolTests
    {
        /// <summary>
        /// Scene loaded by the scene-survival case. Must be in Build Settings.
        ///
        /// <c>Gameplay</c> is used because the behaviour under test is what a single-mode load
        /// does to pooled instances, and a scene carrying real objects is a more honest stand-in
        /// for a level transition than an almost-empty one.
        ///
        /// This originally had to be <c>BootScene</c>: <c>CustomCursor</c> fell back to legacy
        /// <c>UnityEngine.Input.mousePosition</c> when no mouse was present, which throws on every
        /// frame under this project's Input-System-only handling, and headless test runs have no
        /// mouse. That is fixed — the legacy path is compiled out and the cursor stands down when
        /// there is no pointing device — so this fixture exercises the real scene again.
        /// </summary>
        private const string SurvivalScene = "Gameplay";

        /// <summary>Records pool lifecycle callbacks so they can be asserted on.</summary>
        private class PoolProbe : MonoBehaviour, IPoolable
        {
            /// <summary>How many times this instance has been spawned from the pool.</summary>
            public int SpawnCalls;

            /// <summary>How many times this instance has been returned to the pool.</summary>
            public int ReturnCalls;

            /// <inheritdoc />
            public void OnSpawnFromPool() => SpawnCalls++;

            /// <inheritdoc />
            public void OnReturnToPool() => ReturnCalls++;
        }

        /// <summary>The pool under test.</summary>
        private ObjectPool _pool;

        /// <summary>GameObject hosting <see cref="_pool"/>.</summary>
        private GameObject _poolHost;

        /// <summary>Source object the pool instantiates from. Never itself pooled.</summary>
        private GameObject _prefab;

        /// <summary>Parent used by cases that need every instance in one countable place.</summary>
        private GameObject _holder;

        /// <summary>Build a fresh pool, source object and holder for each case.</summary>
        [SetUp]
        public void SetUp()
        {
            GameLogger.SetCategoryLevel("Pool", LogLevel.Off);

            _poolHost = new GameObject("ObjectPoolTests_Pool");
            Object.DontDestroyOnLoad(_poolHost);
            _pool = _poolHost.AddComponent<ObjectPool>();

            // Inactive so it is never mistaken for a live instance, and persistent so the
            // scene-survival case can still spawn from it after the scene changes.
            _prefab = new GameObject("PooledProbe");
            _prefab.AddComponent<PoolProbe>();
            _prefab.SetActive(false);
            Object.DontDestroyOnLoad(_prefab);

            _holder = new GameObject("ObjectPoolTests_Holder");
            Object.DontDestroyOnLoad(_holder);
        }

        /// <summary>Destroy everything this fixture created.</summary>
        [TearDown]
        public void TearDown()
        {
            if (_pool != null && _prefab != null) _pool.DestroyPool(_prefab);

            if (_holder != null) Object.DestroyImmediate(_holder);
            if (_prefab != null) Object.DestroyImmediate(_prefab);
            if (_poolHost != null) Object.DestroyImmediate(_poolHost);

            GameLogger.SetCategoryLevel("Pool", LogLevel.Debug);
        }

        // ─── Warm-up and basic bookkeeping ──────────────────────────

        /// <summary>Pre-creating a pool leaves the requested instances available and none active.</summary>
        [Test]
        public void CreatePool_PreAllocatesInactiveInstances()
        {
            _pool.CreatePool(_prefab, initialCount: 8, maxSize: 20);

            Assert.AreEqual(8, _pool.GetAvailableCount(_prefab));
            Assert.AreEqual(0, _pool.GetActiveCount(_prefab));
            Assert.AreEqual(8, _pool.GetTotalCount(_prefab));
        }

        /// <summary>A warm-up larger than the cap is clamped rather than honoured.</summary>
        [Test]
        public void CreatePool_ClampsInitialCountToMaxSize()
        {
            _pool.CreatePool(_prefab, initialCount: 50, maxSize: 6);

            Assert.AreEqual(6, _pool.GetTotalCount(_prefab));
        }

        /// <summary>Spawning activates an instance, positions it, and moves it from available to active.</summary>
        [Test]
        public void Spawn_ActivatesAndTracksTheInstance()
        {
            _pool.CreatePool(_prefab, initialCount: 4, maxSize: 10);

            var spawned = _pool.Spawn(_prefab, new Vector3(1f, 2f, 3f), Quaternion.identity, _holder.transform);

            Assert.IsNotNull(spawned);
            Assert.IsTrue(spawned.activeSelf);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), spawned.transform.position);
            Assert.AreEqual(1, _pool.GetActiveCount(_prefab));
            Assert.AreEqual(3, _pool.GetAvailableCount(_prefab));
            Assert.AreEqual(4, _pool.GetTotalCount(_prefab), "Spawning must not allocate a new instance.");
        }

        /// <summary>Despawning deactivates the instance and returns it to the available queue.</summary>
        [Test]
        public void Despawn_ReturnsTheInstanceToThePool()
        {
            _pool.CreatePool(_prefab, initialCount: 2, maxSize: 10);
            var spawned = _pool.Spawn(_prefab, parent: _holder.transform);

            _pool.Despawn(spawned);

            Assert.IsFalse(spawned.activeSelf);
            Assert.AreEqual(0, _pool.GetActiveCount(_prefab));
            Assert.AreEqual(2, _pool.GetAvailableCount(_prefab));
        }

        /// <summary>Despawning the same object twice is ignored rather than double-queued.</summary>
        [Test]
        public void Despawn_Twice_IsIgnored()
        {
            _pool.CreatePool(_prefab, initialCount: 2, maxSize: 10);
            var spawned = _pool.Spawn(_prefab, parent: _holder.transform);

            _pool.Despawn(spawned);
            _pool.Despawn(spawned);

            Assert.AreEqual(2, _pool.GetAvailableCount(_prefab),
                "A duplicate despawn must not enqueue the instance a second time.");
        }

        /// <summary><c>DespawnAll</c> returns every active instance for a prefab.</summary>
        [Test]
        public void DespawnAll_ReturnsEveryActiveInstance()
        {
            _pool.CreatePool(_prefab, initialCount: 10, maxSize: 10);
            for (int i = 0; i < 10; i++) _pool.Spawn(_prefab, parent: _holder.transform);

            Assert.AreEqual(10, _pool.GetActiveCount(_prefab), "Setup: all ten should be active.");

            _pool.DespawnAll(_prefab);

            Assert.AreEqual(0, _pool.GetActiveCount(_prefab));
            Assert.AreEqual(10, _pool.GetAvailableCount(_prefab));
        }

        // ─── IPoolable ──────────────────────────────────────────────

        /// <summary>Spawn and return callbacks fire on every <see cref="IPoolable"/> component.</summary>
        [Test]
        public void Spawn_AndDespawn_RunTheIPoolableCallbacks()
        {
            _pool.CreatePool(_prefab, initialCount: 1, maxSize: 4);

            var spawned = _pool.Spawn(_prefab, parent: _holder.transform);
            var probe = spawned.GetComponent<PoolProbe>();

            Assert.AreEqual(1, probe.SpawnCalls);
            Assert.AreEqual(0, probe.ReturnCalls);

            _pool.Despawn(spawned);

            Assert.AreEqual(1, probe.SpawnCalls);
            Assert.AreEqual(1, probe.ReturnCalls);

            _pool.Spawn(_prefab, parent: _holder.transform);
            Assert.AreEqual(2, probe.SpawnCalls, "The recycled instance is spawned again.");
        }

        /// <summary>A delayed despawn returns the instance after the delay, not immediately.</summary>
        [UnityTest]
        public IEnumerator Despawn_WithDelay_ReturnsTheInstanceLater()
        {
            _pool.CreatePool(_prefab, initialCount: 1, maxSize: 4);
            var spawned = _pool.Spawn(_prefab, parent: _holder.transform);

            _pool.Despawn(spawned, delay: 0.15f);

            Assert.AreEqual(1, _pool.GetActiveCount(_prefab), "The despawn must not happen immediately.");

            yield return new WaitForSeconds(0.35f);

            Assert.AreEqual(0, _pool.GetActiveCount(_prefab));
            Assert.IsFalse(spawned.activeSelf);
        }

        // ─── The cap (WP-7 criterion 2) ─────────────────────────────

        /// <summary>
        /// WP-7's cap criterion: <c>CreatePool(p, 10, maxSize: 20)</c> never produces more than 20
        /// total instances under sustained spawn pressure. Counted physically — every instance is
        /// either a child of the holder (spawned) or of the pool's container (available) — so the
        /// assertion does not rest on the pool's own bookkeeping.
        /// </summary>
        [Test]
        public void Spawn_UnderSustainedPressure_NeverExceedsMaxSize()
        {
            _pool.CreatePool(_prefab, initialCount: 10, maxSize: 20);

            for (int i = 0; i < 50; i++)
            {
                _pool.Spawn(_prefab, parent: _holder.transform);

                Assert.LessOrEqual(PhysicalInstanceCount(), 20,
                    $"Physical instance count exceeded the cap on spawn {i + 1}.");
                Assert.LessOrEqual(_pool.GetTotalCount(_prefab), 20,
                    $"Reported total exceeded the cap on spawn {i + 1}.");
            }

            Assert.AreEqual(20, PhysicalInstanceCount(),
                "The pool should have grown to exactly its cap.");
            Assert.AreEqual(20, _pool.GetTotalCount(_prefab));
        }

        /// <summary>At capacity the pool recycles the least-recently spawned instance, not an arbitrary one.</summary>
        [Test]
        public void Spawn_AtCapacity_RecyclesTheLeastRecentlySpawnedInstance()
        {
            _pool.CreatePool(_prefab, initialCount: 0, maxSize: 3);

            var first = _pool.Spawn(_prefab, parent: _holder.transform);
            var second = _pool.Spawn(_prefab, parent: _holder.transform);
            var third = _pool.Spawn(_prefab, parent: _holder.transform);

            var fourth = _pool.Spawn(_prefab, parent: _holder.transform);
            Assert.AreSame(first, fourth, "The oldest spawn should have been recycled first.");

            var fifth = _pool.Spawn(_prefab, parent: _holder.transform);
            Assert.AreSame(second, fifth, "Then the next oldest.");

            Assert.IsNotNull(third, "The middle instance stays live throughout.");
        }

        // ─── O(1) owner lookup (WP-7 criterion 3) ───────────────────

        /// <summary>
        /// WP-7's third criterion: <c>Despawn</c> performs no iteration over <c>_pools</c>. The
        /// mechanism that makes that true is an instance-ID to owning-pool map, so this asserts the
        /// map is populated and that despawning is correct with many pools present — the case the
        /// old linear scan was slow for.
        /// </summary>
        [Test]
        public void Despawn_ResolvesTheOwningPoolWithoutScanningEveryPool()
        {
            var decoys = new List<GameObject>();

            try
            {
                // 24 unrelated pools, so a linear scan would have plenty to walk.
                for (int i = 0; i < 24; i++)
                {
                    var decoy = new GameObject($"Decoy_{i}");
                    decoy.SetActive(false);
                    decoys.Add(decoy);
                    _pool.CreatePool(decoy, initialCount: 1, maxSize: 2);
                }

                _pool.CreatePool(_prefab, initialCount: 2, maxSize: 4);
                var spawned = _pool.Spawn(_prefab, parent: _holder.transform);

                var owners = InstanceOwners();
                Assert.IsTrue(owners.Contains(spawned.GetInstanceID()),
                    "The spawned instance should be in the instance-to-pool map that makes Despawn O(1).");

                _pool.Despawn(spawned);

                Assert.AreEqual(0, _pool.GetActiveCount(_prefab));
                Assert.AreEqual(2, _pool.GetAvailableCount(_prefab));
            }
            finally
            {
                foreach (var decoy in decoys)
                {
                    if (_pool != null) _pool.DestroyPool(decoy);
                    if (decoy != null) Object.DestroyImmediate(decoy);
                }
            }
        }

        // ─── Scene-load survival (WP-7 criterion 1) ─────────────────

        /// <summary>
        /// WP-7's first criterion, and the one its author explicitly reasoned about but never
        /// observed: spawn 50 pooled objects, load a scene, and the pool must report zero active
        /// instances with no null-reference exception on the next spawn.
        ///
        /// The instances are spawned with a <c>null</c> parent, which is the case the package
        /// changed — <c>Spawn</c> moves such an instance into the <i>active</i> scene, so a
        /// single-mode load destroys it and the pool must purge the dead references rather than
        /// accumulate tombstones. The pool itself is <c>DontDestroyOnLoad</c>, mirroring its real
        /// home under the core systems root.
        /// </summary>
        [UnityTest]
        public IEnumerator Spawn_ThenSceneLoad_PurgesDestroyedInstancesAndStillSpawns()
        {
            _pool.CreatePool(_prefab, initialCount: 50, maxSize: 100);

            var spawned = new List<GameObject>();
            for (int i = 0; i < 50; i++)
                spawned.Add(_pool.Spawn(_prefab));

            Assert.AreEqual(50, _pool.GetActiveCount(_prefab), "Setup: 50 instances should be active.");
            CollectionAssert.DoesNotContain(spawned, null, "Setup: every spawn should have succeeded.");

            var activeScene = SceneManager.GetActiveScene();
            foreach (var instance in spawned)
            {
                Assert.AreEqual(activeScene, instance.scene,
                    "A null-parent spawn must live in the active scene, not in DontDestroyOnLoad.");
            }

            // A single-mode load destroys the active scene's contents, taking the 50 with it.
            yield return SceneManager.LoadSceneAsync(SurvivalScene, LoadSceneMode.Single);
            yield return null;

            Assert.AreEqual(SurvivalScene, SceneManager.GetActiveScene().name,
                "Setup: the scene should actually have changed.");

            foreach (var instance in spawned)
                Assert.IsTrue(instance == null, "Every spawned instance should have been destroyed.");

            Assert.AreEqual(0, _pool.GetActiveCount(_prefab),
                "GetActiveCount must report 0 after the scene that held the instances unloaded.");

            // The pool must still work. A tombstoned queue would throw or hand back a dead object.
            GameObject respawned = null;
            Assert.DoesNotThrow(() => respawned = _pool.Spawn(_prefab, parent: _holder.transform),
                "Spawning after a scene change must not throw.");

            Assert.IsNotNull(respawned, "The pool must still be able to spawn.");
            Assert.IsTrue(respawned.activeSelf);
            Assert.AreEqual(1, _pool.GetActiveCount(_prefab));

            // And 49 more, to prove the whole queue was purged rather than just its head.
            for (int i = 0; i < 49; i++)
            {
                var extra = _pool.Spawn(_prefab, parent: _holder.transform);
                Assert.IsNotNull(extra, $"Spawn {i + 2} after the scene change returned null.");
                Assert.IsTrue(extra.activeSelf);
            }

            Assert.AreEqual(50, _pool.GetActiveCount(_prefab));
        }

        // ─── Helpers ────────────────────────────────────────────────

        /// <summary>
        /// Count the instances that physically exist for <see cref="_prefab"/>: every spawned one
        /// is a child of the holder, every idle one a child of the pool's container.
        /// </summary>
        /// <returns>The number of live instances the pool owns.</returns>
        private int PhysicalInstanceCount()
        {
            int spawned = _holder.transform.childCount;
            var container = _poolHost.transform.Find($"Pool_{_prefab.name}");

            return spawned + (container != null ? container.childCount : 0);
        }

        /// <summary>Read the pool's private instance-ID to owning-pool map.</summary>
        /// <returns>The instance IDs the pool currently claims ownership of.</returns>
        private HashSet<int> InstanceOwners()
        {
            var field = typeof(ObjectPool).GetField(
                "_instanceOwners", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNotNull(field, "ObjectPool._instanceOwners not found — was it renamed?");

            var ids = new HashSet<int>();
            foreach (DictionaryEntry entry in (IDictionary)field.GetValue(_pool))
                ids.Add((int)entry.Key);

            return ids;
        }
    }
}
