using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Implement on MonoBehaviours to receive lifecycle callbacks from <see cref="ObjectPool"/>.
    /// </summary>
    public interface IPoolable
    {
        /// <summary>Called when the object is activated from the pool (equivalent to a fresh spawn).</summary>
        void OnSpawnFromPool();

        /// <summary>Called just before the object is deactivated and returned to the pool.</summary>
        void OnReturnToPool();
    }

    /// <summary>
    /// Generic, per-prefab GameObject pool with auto-expand, warm-up pre-allocation,
    /// and <see cref="IPoolable"/> lifecycle callbacks.
    ///
    /// <b>Scene lifetime.</b> The pool itself normally lives on a <c>DontDestroyOnLoad</c> root
    /// (see <see cref="Bootstrapper"/>), and so do its per-prefab containers, which means inactive
    /// instances survive scene loads. An instance spawned with a <c>null</c> parent is explicitly
    /// moved to the <i>active</i> scene, so a single-mode <c>LoadScene</c> destroys it — the pool
    /// then purges the destroyed reference on the <c>sceneUnloaded</c> / <c>sceneLoaded</c>
    /// callbacks, so counts stay honest instead of accumulating tombstones. Pass a parent that
    /// itself persists if a spawned instance must outlive the scene.
    ///
    /// Usage:
    /// <code>
    /// var pool = ServiceLocator.Get&lt;ObjectPool&gt;();
    /// pool.CreatePool(bulletPrefab, initialCount: 20);
    /// var bullet = pool.Spawn(bulletPrefab, firePoint.position, firePoint.rotation);
    /// pool.Despawn(bullet, delay: 3f);
    /// </code>
    /// </summary>
    public class ObjectPool : MonoBehaviour
    {
        /// <summary>
        /// A single pooled instance paired with its instance ID. The ID is cached at creation
        /// time so bookkeeping still works after Unity destroys the underlying GameObject
        /// (at which point the reference compares equal to <c>null</c>).
        /// </summary>
        private readonly struct PooledInstance
        {
            /// <summary>Instance ID of <see cref="Obj"/>, captured while it was still alive.</summary>
            public readonly int Id;

            /// <summary>The pooled GameObject. Compares equal to <c>null</c> once destroyed.</summary>
            public readonly GameObject Obj;

            /// <summary>Create an entry for a live pooled instance.</summary>
            /// <param name="id">The GameObject's instance ID.</param>
            /// <param name="obj">The pooled GameObject.</param>
            public PooledInstance(int id, GameObject obj)
            {
                Id = id;
                Obj = obj;
            }
        }

        /// <summary>Internal state for a single prefab's pool.</summary>
        private class Pool
        {
            /// <summary>The source prefab this pool instantiates.</summary>
            public GameObject Prefab;

            /// <summary>Queue of deactivated, ready-to-use instances.</summary>
            public Queue<PooledInstance> Available = new();

            /// <summary>
            /// Currently active (spawned) instances in spawn order — the head is the
            /// least-recently spawned, which is what the capacity path recycles.
            /// </summary>
            public LinkedList<PooledInstance> Active = new();

            /// <summary>
            /// Instance ID to its node in <see cref="Active"/>, so membership tests and
            /// removals are O(1) and need no list walk.
            /// </summary>
            public Dictionary<int, LinkedListNode<PooledInstance>> ActiveNodes = new();

            /// <summary>Parent transform that holds inactive instances in the hierarchy.</summary>
            public Transform Container;

            /// <summary>Hard cap — pool will not allocate beyond this many total instances.</summary>
            public int MaxSize;

            /// <summary>Total instances this pool owns: active plus available.</summary>
            public int TotalCount => Active.Count + Available.Count;
        }

        /// <summary>All pools keyed by prefab instance ID.</summary>
        private readonly Dictionary<int, Pool> _pools = new();

        /// <summary>
        /// Spawned-instance ID to the pool that owns it. Lets <see cref="Despawn"/> find the
        /// owning pool in O(1) instead of scanning every pool.
        /// </summary>
        private readonly Dictionary<int, Pool> _instanceOwners = new();

        /// <summary>Root transform for pool containers.</summary>
        private Transform _root;

        /// <summary>Unity Awake — cache the root transform and hook scene lifecycle events.</summary>
        private void Awake()
        {
            _root = transform;

            // Subscribed in Awake (not OnEnable) so purging keeps working even if this
            // component is temporarily disabled while a scene transition happens.
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>Unity OnDestroy — unhook scene lifecycle events.</summary>
        private void OnDestroy()
        {
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= OnSceneUnloaded;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        /// <summary>Purge instances destroyed along with an unloaded scene.</summary>
        private void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            PurgeDestroyed();
        }

        /// <summary>
        /// Backstop purge. Unity does not contractually pin whether <c>sceneUnloaded</c> fires
        /// before or after the old scene's objects are destroyed, so purge again once the new
        /// scene is in — by then the destruction has definitely happened.
        /// </summary>
        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene,
            UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            PurgeDestroyed();
        }

        /// <summary>
        /// Pre-allocate a pool for a prefab. Call during initialization to avoid
        /// runtime allocation hitches.
        /// </summary>
        /// <param name="prefab">The prefab to pool.</param>
        /// <param name="initialCount">How many instances to pre-create. Clamped to <paramref name="maxSize"/>.</param>
        /// <param name="maxSize">Hard limit on total instances (active + available). Minimum 1.</param>
        public void CreatePool(GameObject prefab, int initialCount = 10, int maxSize = 100)
        {
            if (prefab == null)
            {
                GameLogger.LogError("Pool", "CreatePool called with a null prefab");
                return;
            }

            int id = prefab.GetInstanceID();

            if (_pools.ContainsKey(id))
            {
                GameLogger.LogWarning("Pool", $"Pool already exists for {prefab.name}");
                return;
            }

            // Awake may not have run yet if a caller warms up a pool very early
            if (_root == null) _root = transform;

            int cap = Mathf.Max(1, maxSize);
            int warmCount = Mathf.Clamp(initialCount, 0, cap);

            if (warmCount < initialCount)
            {
                GameLogger.LogWarning("Pool",
                    $"initialCount {initialCount} for {prefab.name} exceeds maxSize {cap}; pre-creating {warmCount}");
            }

            // Create a container GameObject to keep the Hierarchy tidy
            var container = new GameObject($"Pool_{prefab.name}");
            container.transform.SetParent(_root);

            var pool = new Pool
            {
                Prefab = prefab,
                Container = container.transform,
                MaxSize = cap
            };

            // Warm up the pool with pre-instantiated, deactivated instances
            for (int i = 0; i < warmCount; i++)
                CreateAvailableInstance(prefab, pool);

            _pools[id] = pool;
        }

        /// <summary>
        /// Activate an object from the pool. If no instances are available, a new one
        /// is created (up to <c>maxSize</c>); at capacity the least-recently spawned instance
        /// is recycled. Creates the pool automatically if needed.
        /// </summary>
        /// <param name="prefab">The prefab to spawn.</param>
        /// <param name="position">World position for the spawned object.</param>
        /// <param name="rotation">World rotation for the spawned object.</param>
        /// <param name="parent">
        /// Optional parent transform. <c>null</c> puts the instance at the root of the
        /// <i>active</i> scene, which means a single-mode scene load destroys it.
        /// </param>
        /// <returns>The activated GameObject, or <c>null</c> if it could not be spawned.</returns>
        public GameObject Spawn(GameObject prefab, Vector3 position = default,
            Quaternion rotation = default, Transform parent = null)
        {
            if (prefab == null)
            {
                GameLogger.LogError("Pool", "Spawn called with a null prefab");
                return null;
            }

            int id = prefab.GetInstanceID();

            // Auto-create pool on first spawn if not warmed up
            if (!_pools.TryGetValue(id, out var pool))
            {
                CreatePool(prefab);
                if (!_pools.TryGetValue(id, out pool)) return null;
            }

            // 1. Reuse a live instance from the available queue (destroyed entries are dropped)
            var obj = TakeAvailable(pool);

            // 2. At capacity — recycle the least-recently spawned instance. Doing this before
            //    the expand check also prunes destroyed actives, which can free real capacity.
            if (obj == null && pool.TotalCount >= pool.MaxSize)
                obj = RecycleOldestActive(pool);

            // 3. Room left (possibly only because step 2 pruned corpses) — expand the pool
            if (obj == null && pool.TotalCount < pool.MaxSize)
                obj = CreateInstance(prefab, pool);

            if (obj == null)
            {
                GameLogger.LogError("Pool",
                    $"Could not spawn {prefab.name}: pool is at its cap of {pool.MaxSize} with nothing reusable");
                return null;
            }

            obj.transform.SetParent(parent);

            // An unparented instance would otherwise inherit the container's scene — which is
            // DontDestroyOnLoad — and silently outlive every scene load.
            if (parent == null) MoveToActiveScene(obj);

            obj.transform.SetPositionAndRotation(position, rotation);
            obj.SetActive(true);
            AddActive(pool, obj);

            // Notify all IPoolable components on the spawned object
            var poolables = obj.GetComponents<IPoolable>();
            foreach (var p in poolables) p.OnSpawnFromPool();

            return obj;
        }

        /// <summary>
        /// Spawn and return a specific component from the pooled object.
        /// </summary>
        /// <typeparam name="T">The component type to retrieve.</typeparam>
        /// <param name="prefab">The prefab to spawn.</param>
        /// <param name="position">World position.</param>
        /// <param name="rotation">World rotation.</param>
        /// <param name="parent">Optional parent transform.</param>
        /// <returns>The requested component on the spawned object, or <c>null</c> if the spawn failed.</returns>
        public T Spawn<T>(GameObject prefab, Vector3 position = default,
            Quaternion rotation = default, Transform parent = null) where T : Component
        {
            var obj = Spawn(prefab, position, rotation, parent);
            return obj != null ? obj.GetComponent<T>() : null;
        }

        /// <summary>
        /// Return an object to its pool (deactivate and re-queue). Owner lookup is O(1).
        /// </summary>
        /// <param name="obj">The spawned object to return.</param>
        /// <param name="delay">Optional delay in seconds before returning.</param>
        public void Despawn(GameObject obj, float delay = 0f)
        {
            if (obj == null)
            {
                GameLogger.LogWarning("Pool", "Despawn called with a null or already destroyed object");
                return;
            }

            if (delay > 0f)
            {
                StartCoroutine(DespawnDelayed(obj, delay));
                return;
            }

            int id = obj.GetInstanceID();

            if (_instanceOwners.TryGetValue(id, out var pool))
            {
                if (pool.ActiveNodes.ContainsKey(id))
                    ReturnToPool(obj, pool);
                else
                    GameLogger.LogWarning("Pool", $"{obj.name} is already in its pool — ignoring duplicate Despawn");

                return;
            }

            // Object not owned by any pool — destroy it as a fallback
            GameLogger.LogWarning("Pool", $"Object {obj.name} not found in any pool, destroying");
            Destroy(obj);
        }

        /// <summary>
        /// Return all active objects for a specific prefab back to the pool.
        /// </summary>
        /// <param name="prefab">The prefab whose active instances should be despawned.</param>
        public void DespawnAll(GameObject prefab)
        {
            if (prefab == null) return;

            int id = prefab.GetInstanceID();
            if (!_pools.TryGetValue(id, out var pool)) return;

            // ReturnToPool unlinks the head each pass, so this always makes progress.
            // The counter bounds the loop regardless, so a bookkeeping slip can never hang.
            int guard = pool.Active.Count;
            while (pool.Active.Count > 0 && guard-- > 0)
            {
                var node = pool.Active.First;
                var entry = node.Value;

                if (entry.Obj == null)
                {
                    DropActiveNode(pool, node);
                    continue;
                }

                ReturnToPool(entry.Obj, pool);
            }
        }

        /// <summary>Get the number of available (inactive) instances for a prefab.</summary>
        public int GetAvailableCount(GameObject prefab)
        {
            if (prefab == null) return 0;

            int id = prefab.GetInstanceID();
            return _pools.TryGetValue(id, out var pool) ? pool.Available.Count : 0;
        }

        /// <summary>Get the number of active (spawned) instances for a prefab.</summary>
        public int GetActiveCount(GameObject prefab)
        {
            if (prefab == null) return 0;

            int id = prefab.GetInstanceID();
            return _pools.TryGetValue(id, out var pool) ? pool.Active.Count : 0;
        }

        /// <summary>
        /// Get the total number of instances a prefab's pool owns (active + available).
        /// This is the figure capped by <c>maxSize</c>.
        /// </summary>
        public int GetTotalCount(GameObject prefab)
        {
            if (prefab == null) return 0;

            int id = prefab.GetInstanceID();
            return _pools.TryGetValue(id, out var pool) ? pool.TotalCount : 0;
        }

        /// <summary>
        /// Completely destroy a pool — despawns all active instances, destroys all
        /// inactive instances and the container, and removes the pool from tracking.
        /// </summary>
        /// <param name="prefab">The prefab whose pool should be destroyed.</param>
        public void DestroyPool(GameObject prefab)
        {
            if (prefab == null) return;

            int id = prefab.GetInstanceID();
            if (!_pools.TryGetValue(id, out var pool)) return;

            // Return and destroy all active instances
            foreach (var entry in pool.Active)
            {
                _instanceOwners.Remove(entry.Id);
                if (entry.Obj == null) continue;

                NotifyReturn(entry.Obj);
                Destroy(entry.Obj);
            }
            pool.Active.Clear();
            pool.ActiveNodes.Clear();

            // Destroy all available instances
            while (pool.Available.Count > 0)
            {
                var entry = pool.Available.Dequeue();
                _instanceOwners.Remove(entry.Id);
                if (entry.Obj != null) Destroy(entry.Obj);
            }

            // Destroy the container and remove the pool
            if (pool.Container != null)
                Destroy(pool.Container.gameObject);

            _pools.Remove(id);
            GameLogger.Log("Pool", $"Destroyed pool for {prefab.name}");
        }

        /// <summary>
        /// Pre-warm a pool in batches across multiple frames to avoid frame spikes.
        /// Creates the pool if it doesn't already exist. Never exceeds the pool's cap.
        /// </summary>
        /// <param name="prefab">The prefab to pool.</param>
        /// <param name="totalCount">Total number of instances to pre-create.</param>
        /// <param name="batchSize">Number of instances to create per frame.</param>
        /// <param name="maxSize">Hard limit on total instances. Ignored if the pool already exists.</param>
        /// <param name="onComplete">Optional callback when pre-warming finishes.</param>
        /// <returns>The running Coroutine.</returns>
        public Coroutine PrewarmAsync(GameObject prefab, int totalCount, int batchSize = 5,
            int maxSize = 200, Action onComplete = null)
        {
            return StartCoroutine(PrewarmRoutine(prefab, totalCount, batchSize, maxSize, onComplete));
        }

        /// <summary>Coroutine that instantiates pool objects in batches across frames.</summary>
        private IEnumerator PrewarmRoutine(GameObject prefab, int totalCount, int batchSize,
            int maxSize, Action onComplete)
        {
            if (prefab == null)
            {
                GameLogger.LogError("Pool", "PrewarmAsync called with a null prefab");
                onComplete?.Invoke();
                yield break;
            }

            int id = prefab.GetInstanceID();

            if (!_pools.ContainsKey(id))
                CreatePool(prefab, 0, maxSize);

            if (!_pools.TryGetValue(id, out var pool))
            {
                onComplete?.Invoke();
                yield break;
            }

            int created = 0;
            int perFrame = Mathf.Max(1, batchSize);

            while (created < totalCount && pool.TotalCount < pool.MaxSize)
            {
                int batch = Mathf.Min(perFrame, totalCount - created);
                batch = Mathf.Min(batch, pool.MaxSize - pool.TotalCount);

                for (int i = 0; i < batch; i++)
                {
                    CreateAvailableInstance(prefab, pool);
                    created++;
                }

                yield return null;
            }

            if (created < totalCount)
            {
                GameLogger.LogWarning("Pool",
                    $"Pre-warm of {prefab.name} stopped at {created}/{totalCount} — pool cap is {pool.MaxSize}");
            }

            GameLogger.Log("Pool", $"Async pre-warmed {created} instances of {prefab.name}");
            onComplete?.Invoke();
        }

        /// <summary>
        /// Dequeue the next live instance, discarding any entries whose GameObject was
        /// destroyed behind the pool's back.
        /// </summary>
        /// <param name="pool">The pool to draw from.</param>
        /// <returns>A live instance, or <c>null</c> if the queue holds none.</returns>
        private GameObject TakeAvailable(Pool pool)
        {
            while (pool.Available.Count > 0)
            {
                var entry = pool.Available.Dequeue();

                if (entry.Obj != null) return entry.Obj;

                _instanceOwners.Remove(entry.Id);
            }

            return null;
        }

        /// <summary>
        /// Take the least-recently spawned live instance out of the active list and hand it back
        /// for immediate re-spawn, running its <see cref="IPoolable.OnReturnToPool"/> callbacks
        /// first. Destroyed entries encountered on the way are dropped.
        /// </summary>
        /// <param name="pool">The pool at capacity.</param>
        /// <returns>The recycled instance, or <c>null</c> if nothing live was active.</returns>
        private GameObject RecycleOldestActive(Pool pool)
        {
            while (pool.Active.Count > 0)
            {
                var node = pool.Active.First;
                var entry = node.Value;

                DropActiveNode(pool, node);

                if (entry.Obj == null)
                {
                    _instanceOwners.Remove(entry.Id);
                    continue;
                }

                GameLogger.LogWarning("Pool",
                    $"Pool max reached for {pool.Prefab.name}, recycling the least-recently spawned instance");

                NotifyReturn(entry.Obj);
                return entry.Obj;
            }

            return null;
        }

        /// <summary>Record an instance as active, appending it to the spawn-order list.</summary>
        private void AddActive(Pool pool, GameObject obj)
        {
            int id = obj.GetInstanceID();
            if (pool.ActiveNodes.ContainsKey(id)) return;

            pool.ActiveNodes[id] = pool.Active.AddLast(new PooledInstance(id, obj));
            _instanceOwners[id] = pool;
        }

        /// <summary>Unlink an active node from both the spawn-order list and the lookup table.</summary>
        private void DropActiveNode(Pool pool, LinkedListNode<PooledInstance> node)
        {
            pool.ActiveNodes.Remove(node.Value.Id);
            pool.Active.Remove(node);
        }

        /// <summary>Deactivate an object, notify IPoolable components, and re-queue it.</summary>
        private void ReturnToPool(GameObject obj, Pool pool)
        {
            int id = obj.GetInstanceID();

            NotifyReturn(obj);

            obj.SetActive(false);
            obj.transform.SetParent(pool.Container);

            if (pool.ActiveNodes.TryGetValue(id, out var node))
                DropActiveNode(pool, node);

            pool.Available.Enqueue(new PooledInstance(id, obj));
            _instanceOwners[id] = pool;
        }

        /// <summary>Run <see cref="IPoolable.OnReturnToPool"/> on every poolable component.</summary>
        private void NotifyReturn(GameObject obj)
        {
            var poolables = obj.GetComponents<IPoolable>();
            foreach (var p in poolables) p.OnReturnToPool();
        }

        /// <summary>Instantiate a new inactive instance under the pool container and record ownership.</summary>
        private GameObject CreateInstance(GameObject prefab, Pool pool)
        {
            var obj = Instantiate(prefab, pool.Container);
            obj.SetActive(false);
            _instanceOwners[obj.GetInstanceID()] = pool;
            return obj;
        }

        /// <summary>Create a new inactive instance and park it in the available queue.</summary>
        private void CreateAvailableInstance(GameObject prefab, Pool pool)
        {
            var obj = CreateInstance(prefab, pool);
            pool.Available.Enqueue(new PooledInstance(obj.GetInstanceID(), obj));
        }

        /// <summary>
        /// Move a root GameObject into the active scene so it shares that scene's lifetime.
        /// No-op when it is already there or when there is no valid active scene.
        /// </summary>
        private static void MoveToActiveScene(GameObject obj)
        {
            var active = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            if (!active.IsValid() || !active.isLoaded) return;
            if (obj.scene == active) return;

            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(obj, active);
        }

        /// <summary>
        /// Drop every reference to an instance Unity has destroyed — typically because it was
        /// spawned into a scene that has since unloaded. Without this, <c>Active</c> fills with
        /// tombstones, the counts lie, and <see cref="DespawnAll"/> iterates corpses.
        /// </summary>
        private void PurgeDestroyed()
        {
            int purged = 0;

            foreach (var pool in _pools.Values)
            {
                var node = pool.Active.First;
                while (node != null)
                {
                    var next = node.Next;

                    if (node.Value.Obj == null)
                    {
                        _instanceOwners.Remove(node.Value.Id);
                        DropActiveNode(pool, node);
                        purged++;
                    }

                    node = next;
                }

                // Rebuild the queue in place, dropping destroyed entries
                int count = pool.Available.Count;
                for (int i = 0; i < count; i++)
                {
                    var entry = pool.Available.Dequeue();

                    if (entry.Obj == null)
                    {
                        _instanceOwners.Remove(entry.Id);
                        purged++;
                        continue;
                    }

                    pool.Available.Enqueue(entry);
                }
            }

            if (purged > 0)
                GameLogger.Log("Pool", $"Purged {purged} destroyed instance(s) after a scene change");
        }

        /// <summary>Coroutine that waits then despawns an object.</summary>
        private IEnumerator DespawnDelayed(GameObject obj, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (obj != null) Despawn(obj);
        }
    }
}
