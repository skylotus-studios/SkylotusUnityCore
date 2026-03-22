using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skylotus
{
    /// <summary>
    /// Lightweight service locator for decoupled system access.
    /// Register services at bootstrap time, then resolve them anywhere
    /// in the codebase without introducing hard dependencies between systems.
    /// </summary>
    public static class ServiceLocator
    {
        /// <summary>Stores live service instances keyed by their registered interface type.</summary>
        private static readonly Dictionary<Type, object> _services = new();

        /// <summary>Stores deferred bindings: interface type -> concrete type, instantiated on first resolve.</summary>
        private static readonly Dictionary<Type, Type> _lazyBindings = new();

        /// <summary>
        /// Reset static state on domain reload (Editor Enter Play Mode settings).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _services.Clear();
            _lazyBindings.Clear();
        }

        /// <summary>
        /// Register a concrete service instance for interface type <typeparamref name="T"/>.
        /// If a service of the same type is already registered, it will be overwritten with a warning.
        /// </summary>
        /// <typeparam name="T">The interface or base type to register under.</typeparam>
        /// <param name="service">The service instance to register.</param>
        public static void Register<T>(T service) where T : class
        {
            var type = typeof(T);

            // Warn on overwrite so developers can catch accidental double-registration
            if (_services.ContainsKey(type))
            {
                GameLogger.LogWarning("ServiceLocator", $"Overwriting service: {type.Name}");
            }

            _services[type] = service;
        }

        /// <summary>
        /// Register a lazy binding. The concrete type <typeparamref name="TImplementation"/>
        /// will be instantiated via its parameterless constructor on the first call to
        /// <see cref="Get{T}"/> or <see cref="TryGet{T}"/>.
        /// </summary>
        /// <typeparam name="TInterface">The interface or base type to register under.</typeparam>
        /// <typeparam name="TImplementation">The concrete type that implements TInterface.</typeparam>
        public static void RegisterLazy<TInterface, TImplementation>()
            where TInterface : class
            where TImplementation : TInterface, new()
        {
            _lazyBindings[typeof(TInterface)] = typeof(TImplementation);
        }

        /// <summary>
        /// Resolve a registered service of type <typeparamref name="T"/>.
        /// Throws <see cref="InvalidOperationException"/> if not registered.
        /// </summary>
        /// <typeparam name="T">The interface or base type to resolve.</typeparam>
        /// <returns>The registered service instance.</returns>
        public static T Get<T>() where T : class
        {
            var type = typeof(T);

            // Check for an existing instance first
            if (_services.TryGetValue(type, out var service))
                return (T)service;

            // Fall back to lazy bindings — instantiate, cache, and return
            if (_lazyBindings.TryGetValue(type, out var implType))
            {
                var instance = (T)Activator.CreateInstance(implType);
                _services[type] = instance;
                _lazyBindings.Remove(type);
                return instance;
            }

            throw new InvalidOperationException(
                $"[ServiceLocator] Service '{type.Name}' not registered. " +
                "Did you forget to register it in SkylotusBootstrapper?");
        }

        /// <summary>
        /// Try to resolve a service of type <typeparamref name="T"/>.
        /// Returns false if no service is registered, without throwing.
        /// </summary>
        /// <typeparam name="T">The interface or base type to resolve.</typeparam>
        /// <param name="service">The resolved service, or null if not found.</param>
        /// <returns>True if the service was found and resolved.</returns>
        public static bool TryGet<T>(out T service) where T : class
        {
            service = null;
            var type = typeof(T);

            if (_services.TryGetValue(type, out var obj))
            {
                service = (T)obj;
                return true;
            }

            if (_lazyBindings.TryGetValue(type, out var implType))
            {
                service = (T)Activator.CreateInstance(implType);
                _services[type] = service;
                _lazyBindings.Remove(type);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Remove a service registration for type <typeparamref name="T"/>.
        /// </summary>
        /// <typeparam name="T">The interface or base type to unregister.</typeparam>
        public static void Unregister<T>() where T : class
        {
            _services.Remove(typeof(T));
        }

        /// <summary>
        /// Check whether a service of type <typeparamref name="T"/> is registered
        /// (either as a live instance or a lazy binding).
        /// </summary>
        /// <typeparam name="T">The interface or base type to check.</typeparam>
        /// <returns>True if the service is registered.</returns>
        public static bool IsRegistered<T>() where T : class
        {
            return _services.ContainsKey(typeof(T)) || _lazyBindings.ContainsKey(typeof(T));
        }

        /// <summary>
        /// Clear all registered services and lazy bindings.
        /// Call on application quit to ensure a clean slate.
        /// </summary>
        public static void Reset()
        {
            _services.Clear();
            _lazyBindings.Clear();
        }
    }
}