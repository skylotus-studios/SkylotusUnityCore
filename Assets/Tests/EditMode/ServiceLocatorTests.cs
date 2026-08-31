using System;
using NUnit.Framework;

namespace Skylotus.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="ServiceLocator"/>: registration, resolution, lazy
    /// bindings, overwrite behaviour and reset.
    ///
    /// The locator is a static singleton shared by the whole editor session, so every test
    /// resets it on both sides — a leaked registration from one case would otherwise decide
    /// the outcome of the next.
    /// </summary>
    [TestFixture]
    public class ServiceLocatorTests
    {
        /// <summary>A service contract used only by these tests.</summary>
        private interface IProbeService
        {
            /// <summary>Identifies which implementation answered a resolve.</summary>
            string Tag { get; }
        }

        /// <summary>First implementation of <see cref="IProbeService"/>.</summary>
        private class ProbeServiceA : IProbeService
        {
            /// <inheritdoc />
            public string Tag => "A";
        }

        /// <summary>Second implementation, used to prove an overwrite actually replaced the first.</summary>
        private class ProbeServiceB : IProbeService
        {
            /// <inheritdoc />
            public string Tag => "B";
        }

        /// <summary>A concrete service with no interface, used for the plain register/resolve path.</summary>
        private class StandaloneService
        {
            /// <summary>Arbitrary payload so two instances can be told apart.</summary>
            public int Value;
        }

        /// <summary>Clear the locator before each test.</summary>
        [SetUp]
        public void SetUp()
        {
            ServiceLocator.Reset();
            GameLogger.SetCategoryLevel("ServiceLocator", LogLevel.Off);
        }

        /// <summary>Clear the locator after each test so nothing leaks into the next fixture.</summary>
        [TearDown]
        public void TearDown()
        {
            ServiceLocator.Reset();
            GameLogger.SetCategoryLevel("ServiceLocator", LogLevel.Debug);
        }

        // ─── Register / Resolve ─────────────────────────────────────

        /// <summary>A registered instance resolves back by its concrete type.</summary>
        [Test]
        public void Register_ThenGet_ReturnsTheSameInstance()
        {
            var service = new StandaloneService { Value = 7 };
            ServiceLocator.Register(service);

            Assert.AreSame(service, ServiceLocator.Get<StandaloneService>());
        }

        /// <summary>Registering under an interface resolves through that interface, not the concrete type.</summary>
        [Test]
        public void Register_UnderInterface_ResolvesByInterface()
        {
            ServiceLocator.Register<IProbeService>(new ProbeServiceA());

            Assert.AreEqual("A", ServiceLocator.Get<IProbeService>().Tag);
            Assert.IsFalse(ServiceLocator.IsRegistered<ProbeServiceA>(),
                "Registering under an interface must not also register the concrete type.");
        }

        /// <summary>
        /// The documented contract, and the reason <c>TryGet</c> exists: an unregistered
        /// service throws rather than returning null, so a <c>?.</c> guard after
        /// <see cref="ServiceLocator.Get{T}"/> can never fire.
        /// </summary>
        [Test]
        public void Get_Unregistered_Throws()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => ServiceLocator.Get<StandaloneService>());

            StringAssert.Contains("StandaloneService", ex.Message);
        }

        /// <summary><c>TryGet</c> reports failure without throwing and leaves the out parameter null.</summary>
        [Test]
        public void TryGet_Unregistered_ReturnsFalseAndNull()
        {
            Assert.IsFalse(ServiceLocator.TryGet<StandaloneService>(out var service));
            Assert.IsNull(service);
        }

        /// <summary><c>TryGet</c> hands back the registered instance when there is one.</summary>
        [Test]
        public void TryGet_Registered_ReturnsTrueAndInstance()
        {
            var service = new StandaloneService { Value = 3 };
            ServiceLocator.Register(service);

            Assert.IsTrue(ServiceLocator.TryGet<StandaloneService>(out var resolved));
            Assert.AreSame(service, resolved);
        }

        // ─── Overwrite ──────────────────────────────────────────────

        /// <summary>
        /// Re-registering the same type replaces the instance. The warning that accompanies it is
        /// the double-registration signal the bootstrapper is checked against in PlayMode.
        /// </summary>
        [Test]
        public void Register_Twice_OverwritesWithTheNewInstance()
        {
            ServiceLocator.Register<IProbeService>(new ProbeServiceA());
            ServiceLocator.Register<IProbeService>(new ProbeServiceB());

            Assert.AreEqual("B", ServiceLocator.Get<IProbeService>().Tag);
        }

        // ─── Lazy bindings ──────────────────────────────────────────

        /// <summary>A lazy binding is not instantiated until the first resolve.</summary>
        [Test]
        public void RegisterLazy_InstantiatesOnFirstGet()
        {
            ServiceLocator.RegisterLazy<IProbeService, ProbeServiceA>();

            Assert.IsTrue(ServiceLocator.IsRegistered<IProbeService>(),
                "A lazy binding counts as registered before it is resolved.");

            var first = ServiceLocator.Get<IProbeService>();
            Assert.AreEqual("A", first.Tag);

            // The instance is cached, so the second resolve must not construct a new one.
            Assert.AreSame(first, ServiceLocator.Get<IProbeService>());
        }

        /// <summary><c>TryGet</c> honours a lazy binding just as <c>Get</c> does.</summary>
        [Test]
        public void RegisterLazy_ResolvedThroughTryGet()
        {
            ServiceLocator.RegisterLazy<IProbeService, ProbeServiceA>();

            Assert.IsTrue(ServiceLocator.TryGet<IProbeService>(out var service));
            Assert.AreEqual("A", service.Tag);
            Assert.AreSame(service, ServiceLocator.Get<IProbeService>());
        }

        /// <summary>An explicit instance wins over a lazy binding for the same type.</summary>
        [Test]
        public void RegisterLazy_ThenRegisterInstance_InstanceWins()
        {
            ServiceLocator.RegisterLazy<IProbeService, ProbeServiceA>();
            var explicitInstance = new ProbeServiceB();
            ServiceLocator.Register<IProbeService>(explicitInstance);

            Assert.AreSame(explicitInstance, ServiceLocator.Get<IProbeService>());
        }

        // ─── Unregister / Reset ─────────────────────────────────────

        /// <summary>Unregistering removes the service and restores the throwing behaviour.</summary>
        [Test]
        public void Unregister_RemovesTheService()
        {
            ServiceLocator.Register(new StandaloneService());
            Assert.IsTrue(ServiceLocator.IsRegistered<StandaloneService>());

            ServiceLocator.Unregister<StandaloneService>();

            Assert.IsFalse(ServiceLocator.IsRegistered<StandaloneService>());
            Assert.Throws<InvalidOperationException>(() => ServiceLocator.Get<StandaloneService>());
        }

        /// <summary><c>Reset</c> drops live instances and pending lazy bindings alike.</summary>
        [Test]
        public void Reset_ClearsInstancesAndLazyBindings()
        {
            ServiceLocator.Register(new StandaloneService());
            ServiceLocator.RegisterLazy<IProbeService, ProbeServiceA>();

            ServiceLocator.Reset();

            Assert.IsFalse(ServiceLocator.IsRegistered<StandaloneService>());
            Assert.IsFalse(ServiceLocator.IsRegistered<IProbeService>());
        }
    }
}
