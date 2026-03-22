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
        /// <summary>Internal state for a single prefab's pool.</summary>
        private class Pool
        {
            /// <summary>The source prefab this pool instantiates.</summary>
            public GameObject Prefab;

            /// <summary>Queue of deactivated, ready-to-use instances.</summary>
            public Queue<GameObject> Available = new();

            /// <summary>Set of currently active (spawned) instances.</summary>
            public HashSet<GameObject> Active = new();

            /// <summary>Parent transform that holds inactive instances in the hierarchy.</summary>
            public Transform Container;

            /// <summary>Hard cap — pool will not allocate beyond this count.</summary>
            public int MaxSize;
        }

        /// <summary>All pools keyed by prefab instance ID.</summary>
        private readonly Dictionary<int, Pool> _pools = new();

        /// <summary>Root transform for pool containers.</summary>
        private Transform _root;

        /// <summary>Unity Awake — cache the root transform.</summary>
        private void Awake()
        {
            _root = transform;
        }

        /// <summary>
        /// Pre-allocate a pool for a prefab. Call during initialization to avoid
        /// runtime allocation hitches.
        /// </summary>
        /// <param name="prefab">The prefab to pool.</param>
        /// <param name="initialCount">How many instances to pre-create.</param>
        /// <param name="maxSize">Hard limit on total instances (active + available).</param>
        public void CreatePool(GameObject prefab, int initialCount = 10, int maxSize = 100)
        {
            int id = prefab.GetInstanceID();

            if (_pools.ContainsKey(id))
            {
                GameLogger.LogWarning("Pool", $"Pool already exists for {prefab.name}");
                return;
            }

            // Create a container GameObject to keep the Hierarchy tidy
            var container = new GameObject($"Pool_{prefab.name}");
            container.transform.SetParent(_root);

            var pool = new Pool
            {
                Prefab = prefab,
                Container = container.transform,
                MaxSize = maxSize
            };

            // Warm up the pool with pre-instantiated, deactivated instances
            for (int i = 0; i < initialCount; i++)
            {
                var obj = CreateInstance(prefab, pool.Container);
                pool.Available.Enqueue(obj);
            }

            _pools[id] = pool;
        }

        /// <summary>
        /// Activate an object from the pool. If no instances are available, a new one
        /// is created (up to <c>maxSize</c>). Creates the pool automatically if needed.
        /// </summary>
        /// <param name="prefab">The prefab to spawn.</param>
        /// <param name="position">World position for the spawned object.</param>
        /// <param name="rotation">World rotation for the spawned object.</param>
        /// <param name="parent">Optional parent transform (null = scene root).</param>
        /// <returns>The activated GameObject.</returns>
        public GameObject Spawn(GameObject prefab, Vector3 position = default,
            Quaternion rotation = default, Transform parent = null)
        {
            int id = prefab.GetInstanceID();

            // Auto-create pool on first spawn if not warmed up
            if (!_pools.TryGetValue(id, out var pool))
            {
                CreatePool(prefab);
                pool = _pools[id];
            }

            GameObject obj;

            if (pool.Available.Count > 0)
            {
                // Reuse an existing deactivated instance
                obj = pool.Available.Dequeue();
            }
            else if (pool.Active.Count < pool.MaxSize)
            {
                // Expand the pool with a new instance
                obj = CreateInstance(prefab, pool.Container);
            }
            else
            {
                // At capacity — recycle the oldest active instance
                GameLogger.LogWarning("Pool", $"Pool max reached for {prefab.name}, recycling oldest");
                var enumerator = pool.Active.GetEnumerator();
                enumerator.MoveNext();
                obj = enumerator.Current;
                ReturnToPool(obj, pool);
                pool.Available.Dequeue();
            }

            obj.transform.SetParent(parent);
            obj.transform.SetPositionAndRotation(position, rotation);
            obj.SetActive(true);
            pool.Active.Add(obj);

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
        /// <returns>The requested component on the spawned object.</returns>
        public T Spawn<T>(GameObject prefab, Vector3 position = default,
            Quaternion rotation = default, Transform parent = null) where T : Component
        {
            var obj = Spawn(prefab, position, rotation, parent);
            return obj.GetComponent<T>();
        }

        /// <summary>
        /// Return an object to its pool (deactivate and re-queue).
        /// </summary>
        /// <param name="obj">The spawned object to return.</param>
        /// <param name="delay">Optional delay in seconds before returning.</param>
        public void Despawn(GameObject obj, float delay = 0f)
        {
            if (delay > 0f)
            {
                StartCoroutine(DespawnDelayed(obj, delay));
                return;
            }

            // Find which pool owns this object
            foreach (var pool in _pools.Values)
            {
                if (pool.Active.Contains(obj))
                {
                    ReturnToPool(obj, pool);
                    return;
                }
            }

            // Object not found in any pool — destroy it as a fallback
            GameLogger.LogWarning("Pool", $"Object {obj.name} not found in any pool, destroying");
            Destroy(obj);
        }

        /// <summary>
        /// Return all active objects for a specific prefab back to the pool.
        /// </summary>
        /// <param name="prefab">The prefab whose active instances should be despawned.</param>
        public void DespawnAll(GameObject prefab)
        {
            int id = prefab.GetInstanceID();
            if (!_pools.TryGetValue(id, out var pool)) return;

            var activeList = new List<GameObject>(pool.Active);
            foreach (var obj in activeList)
                ReturnToPool(obj, pool);
        }

        /// <summary>Get the number of available (inactive) instances for a prefab.</summary>
        public int GetAvailableCount(GameObject prefab)
        {
            int id = prefab.GetInstanceID();
            return _pools.TryGetValue(id, out var pool) ? pool.Available.Count : 0;
        }

        /// <summary>Get the number of active (spawned) instances for a prefab.</summary>
        public int GetActiveCount(GameObject prefab)
        {
            int id = prefab.GetInstanceID();
            return _pools.TryGetValue(id, out var pool) ? pool.Active.Count : 0;
        }

        /// <summary>
        /// Completely destroy a pool — despawns all active instances, destroys all
        /// inactive instances and the container, and removes the pool from tracking.
        /// </summary>
        /// <param name="prefab">The prefab whose pool should be destroyed.</param>
        public void DestroyPool(GameObject prefab)
        {
            int id = prefab.GetInstanceID();
            if (!_pools.TryGetValue(id, out var pool)) return;

            // Return and destroy all active instances
            var activeList = new List<GameObject>(pool.Active);
            foreach (var obj in activeList)
            {
                var poolables = obj.GetComponents<IPoolable>();
                foreach (var p in poolables) p.OnReturnToPool();
                Destroy(obj);
            }
            pool.Active.Clear();

            // Destroy all available instances
            while (pool.Available.Count > 0)
            {
                var obj = pool.Available.Dequeue();
                if (obj != null) Destroy(obj);
            }

            // Destroy the container and remove the pool
            if (pool.Container != null)
                Destroy(pool.Container.gameObject);

            _pools.Remove(id);
            GameLogger.Log("Pool", $"Destroyed pool for {prefab.name}");
        }

        /// <summary>
        /// Pre-warm a pool in batches across multiple frames to avoid frame spikes.
        /// Creates the pool if it doesn't already exist.
        /// </summary>
        /// <param name="prefab">The prefab to pool.</param>
        /// <param name="totalCount">Total number of instances to pre-create.</param>
        /// <param name="batchSize">Number of instances to create per frame.</param>
        /// <param name="maxSize">Hard limit on total instances.</param>
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
            int id = prefab.GetInstanceID();

            if (!_pools.ContainsKey(id))
                CreatePool(prefab, 0, maxSize);

            var pool = _pools[id];
            int created = 0;

            while (created < totalCount)
            {
                int batch = Mathf.Min(batchSize, totalCount - created);
                for (int i = 0; i < batch; i++)
                {
                    var obj = CreateInstance(prefab, pool.Container);
                    pool.Available.Enqueue(obj);
                    created++;
                }
                yield return null;
            }

            GameLogger.Log("Pool", $"Async pre-warmed {totalCount} instances of {prefab.name}");
            onComplete?.Invoke();
        }

        /// <summary>Deactivate an object, notify IPoolable components, and re-queue it.</summary>
        private void ReturnToPool(GameObject obj, Pool pool)
        {
            var poolables = obj.GetComponents<IPoolable>();
            foreach (var p in poolables) p.OnReturnToPool();

            obj.SetActive(false);
            obj.transform.SetParent(pool.Container);
            pool.Active.Remove(obj);
            pool.Available.Enqueue(obj);
        }

        /// <summary>Instantiate a new inactive instance under the pool container.</summary>
        private GameObject CreateInstance(GameObject prefab, Transform parent)
        {
            var obj = Instantiate(prefab, parent);
            obj.SetActive(false);
            return obj;
        }

        /// <summary>Coroutine that waits then despawns an object.</summary>
        private IEnumerator DespawnDelayed(GameObject obj, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (obj != null) Despawn(obj);
        }
    }
}