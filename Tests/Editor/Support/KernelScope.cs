using System;
using System.Reflection;
using NUnit.Framework;
using Yozolab.YoluPainter.Core;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>Which inner loops a compositing test uses.</summary>
    public enum KernelChoice
    {
        /// <summary>The core's managed loops (no kernels registered).</summary>
        Managed,
        /// <summary>The kernels registered when the editor loaded (Burst where the package is installed).</summary>
        Registered,
        /// <summary>The registered kernels with Burst compilation switched off (the same kernel code runs as managed code).</summary>
        RegisteredWithBurstOff,
    }

    /// <summary>Sets <see cref="CompositeKernels.Current"/> (and Burst's global switch) for a test and puts both back. Burst is reached
    /// through reflection so that the tests compile and run without the package.</summary>
    internal sealed class KernelScope : IDisposable
    {
        /// <summary>The kernels registered at load (before any test changed them).</summary>
        internal static readonly CompositeKernels Registered = CompositeKernels.Current;
        static readonly object burstOptions = Type.GetType("Unity.Burst.BurstCompiler, Unity.Burst")?.GetField("Options", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        static readonly PropertyInfo burstEnable = burstOptions?.GetType().GetProperty("EnableBurstCompilation");
        /// <summary>True when the Burst package is loaded in this editor.</summary>
        internal static bool BurstInstalled => burstEnable != null;
        internal static bool BurstEnabled
        {
            get => burstEnable != null && (bool)burstEnable.GetValue(burstOptions);
            set { if (burstEnable != null) burstEnable.SetValue(burstOptions, value); }
        }

        readonly CompositeKernels saved; readonly bool savedBurst;
        KernelScope() { saved = CompositeKernels.Current; savedBurst = BurstEnabled; }
        public static KernelScope Use(KernelChoice choice)
        {
            if (choice != KernelChoice.Managed && Registered == null) Assert.Ignore("No compositing kernels are registered here (the Burst package is not installed).");
            if (choice == KernelChoice.RegisteredWithBurstOff && !BurstInstalled) Assert.Ignore("The Burst package is not installed.");
            var scope = new KernelScope();
            CompositeKernels.Current = choice == KernelChoice.Managed ? null : Registered;
            if (choice == KernelChoice.RegisteredWithBurstOff) BurstEnabled = false;
            else if (choice == KernelChoice.Registered && BurstInstalled) BurstEnabled = true;
            return scope;
        }
        public void Dispose() { CompositeKernels.Current = saved; if (BurstInstalled) BurstEnabled = savedBurst; }
    }
}
