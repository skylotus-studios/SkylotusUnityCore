using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Quality-of-life extension methods for common Unity types.
    /// Imported automatically when using the Skylotus namespace.
    /// </summary>
    public static class CoreExtensions
    {
        // ─── Transform ──────────────────────────────────────────────

        /// <summary>Reset local position, rotation, and scale to identity values.</summary>
        /// <param name="t">The transform to reset.</param>
        public static void ResetLocal(this Transform t)
        {
            t.localPosition = Vector3.zero;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one;
        }

        /// <summary>Destroy all child GameObjects of this transform.</summary>
        /// <param name="t">The parent transform.</param>
        public static void DestroyChildren(this Transform t)
        {
            for (int i = t.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }

        /// <summary>Set only the X component of the world position.</summary>
        /// <param name="t">The transform.</param>
        /// <param name="x">The new X value.</param>
        public static void SetX(this Transform t, float x)
        {
            var p = t.position;
            p.x = x;
            t.position = p;
        }

        /// <summary>Set only the Y component of the world position.</summary>
        /// <param name="t">The transform.</param>
        /// <param name="y">The new Y value.</param>
        public static void SetY(this Transform t, float y)
        {
            var p = t.position;
            p.y = y;
            t.position = p;
        }

        /// <summary>Set only the Z component of the world position.</summary>
        /// <param name="t">The transform.</param>
        /// <param name="z">The new Z value.</param>
        public static void SetZ(this Transform t, float z)
        {
            var p = t.position;
            p.z = z;
            t.position = p;
        }

        // ─── GameObject ─────────────────────────────────────────────

        /// <summary>
        /// Get an existing component or add a new one if it doesn't exist.
        /// Avoids the common GetComponent + null check + AddComponent pattern.
        /// </summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <param name="go">The target GameObject.</param>
        /// <returns>The existing or newly added component.</returns>
        public static T GetOrAddComponent<T>(this GameObject go) where T : Component
        {
            var comp = go.GetComponent<T>();
            return comp != null ? comp : go.AddComponent<T>();
        }

        /// <summary>
        /// Set the layer of this GameObject and all of its children recursively.
        /// </summary>
        /// <param name="go">The root GameObject.</param>
        /// <param name="layer">The layer index to apply.</param>
        public static void SetLayerRecursive(this GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform child in go.transform)
                child.gameObject.SetLayerRecursive(layer);
        }

        /// <summary>
        /// Check if a GameObject has a specific component attached.
        /// </summary>
        /// <typeparam name="T">The component type to check for.</typeparam>
        /// <param name="go">The target GameObject.</param>
        /// <returns>True if the component exists.</returns>
        public static bool HasComponent<T>(this GameObject go) where T : Component
        {
            return go.GetComponent<T>() != null;
        }

        // ─── Vector3 ───────────────────────────────────────────────

        /// <summary>
        /// Return a copy with Y set to 0 (project onto the XZ plane).
        /// Useful for ground-plane distance calculations.
        /// </summary>
        /// <param name="v">The source vector.</param>
        /// <returns>A new Vector3 with Y zeroed out.</returns>
        public static Vector3 Flat(this Vector3 v) => new(v.x, 0f, v.z);

        /// <summary>Return a copy with the X component replaced.</summary>
        /// <param name="v">The source vector.</param>
        /// <param name="x">The new X value.</param>
        /// <returns>A modified copy of the vector.</returns>
        public static Vector3 WithX(this Vector3 v, float x) => new(x, v.y, v.z);

        /// <summary>Return a copy with the Y component replaced.</summary>
        /// <param name="v">The source vector.</param>
        /// <param name="y">The new Y value.</param>
        /// <returns>A modified copy of the vector.</returns>
        public static Vector3 WithY(this Vector3 v, float y) => new(v.x, y, v.z);

        /// <summary>Return a copy with the Z component replaced.</summary>
        /// <param name="v">The source vector.</param>
        /// <param name="z">The new Z value.</param>
        /// <returns>A modified copy of the vector.</returns>
        public static Vector3 WithZ(this Vector3 v, float z) => new(v.x, v.y, z);

        /// <summary>
        /// Calculate the distance between two points ignoring the Y axis.
        /// Useful for 3D games where vertical distance shouldn't count.
        /// </summary>
        /// <param name="a">First point.</param>
        /// <param name="b">Second point.</param>
        /// <returns>The XZ-plane distance.</returns>
        public static float FlatDistance(this Vector3 a, Vector3 b) =>
            Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        /// <summary>
        /// Get a random point on the XZ plane within a circle centered on this position.
        /// </summary>
        /// <param name="center">The center point.</param>
        /// <param name="radius">The maximum distance from center.</param>
        /// <returns>A random point on the XZ plane.</returns>
        public static Vector3 RandomPointXZ(this Vector3 center, float radius)
        {
            var r = UnityEngine.Random.insideUnitCircle * radius;
            return center + new Vector3(r.x, 0f, r.y);
        }

        // ─── Color ──────────────────────────────────────────────────

        /// <summary>
        /// Return a copy of this color with a different alpha value.
        /// </summary>
        /// <param name="c">The source color.</param>
        /// <param name="alpha">The new alpha value (0–1).</param>
        /// <returns>A modified copy of the color.</returns>
        public static Color WithAlpha(this Color c, float alpha) => new(c.r, c.g, c.b, alpha);

        // ─── Collections ────────────────────────────────────────────

        /// <summary>
        /// Pick a random element from a list. Returns default(T) if the list is null or empty.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="list">The source list.</param>
        /// <returns>A randomly selected element.</returns>
        public static T RandomElement<T>(this IList<T> list)
        {
            if (list == null || list.Count == 0) return default;
            return list[UnityEngine.Random.Range(0, list.Count)];
        }

        /// <summary>
        /// Shuffle a list in-place using the Fisher-Yates algorithm.
        /// </summary>
        /// <typeparam name="T">The element type.</typeparam>
        /// <param name="list">The list to shuffle.</param>
        public static void Shuffle<T>(this IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        // ─── Coroutine Helpers ──────────────────────────────────────

        /// <summary>
        /// Invoke an action after a delay (seconds). Returns the Coroutine for cancellation.
        /// </summary>
        /// <param name="mb">The MonoBehaviour to run the coroutine on.</param>
        /// <param name="seconds">Delay in seconds.</param>
        /// <param name="action">The action to invoke after the delay.</param>
        /// <returns>The running Coroutine.</returns>
        public static Coroutine Delay(this MonoBehaviour mb, float seconds, Action action)
        {
            return mb.StartCoroutine(DelayRoutine(seconds, action));
        }

        /// <summary>
        /// Invoke an action on the next frame. Returns the Coroutine for cancellation.
        /// </summary>
        /// <param name="mb">The MonoBehaviour to run the coroutine on.</param>
        /// <param name="action">The action to invoke next frame.</param>
        /// <returns>The running Coroutine.</returns>
        public static Coroutine NextFrame(this MonoBehaviour mb, Action action)
        {
            return mb.StartCoroutine(NextFrameRoutine(action));
        }

        /// <summary>Coroutine that waits for a duration then invokes an action.</summary>
        private static IEnumerator DelayRoutine(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            action?.Invoke();
        }

        /// <summary>Coroutine that waits one frame then invokes an action.</summary>
        private static IEnumerator NextFrameRoutine(Action action)
        {
            yield return null;
            action?.Invoke();
        }
    }
}
