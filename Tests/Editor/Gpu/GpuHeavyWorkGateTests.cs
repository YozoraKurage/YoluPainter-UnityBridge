using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Yozolab.YoluPainter.Editor;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>開発環境のテストの台だけで効く GPU の重い仕事の順番待ち（GpuHeavyWorkGate）。環境変数が無ければ何もしないこと、あれば
    /// ほかのプロセスから見て排他になり、入れ子で外側だけが持ち、出たら外れること。</summary>
    public class GpuHeavyWorkGateTests
    {
        string saved, path;

        [SetUp] public void SetUp() { saved = Environment.GetEnvironmentVariable(GpuHeavyWorkGate.EnvironmentVariable); path = Path.Combine(Path.GetTempPath(), "yolupainter-gate-" + Guid.NewGuid().ToString("N") + ".lock"); }
        [TearDown] public void TearDown() { Environment.SetEnvironmentVariable(GpuHeavyWorkGate.EnvironmentVariable, saved); try { File.Delete(path); } catch (IOException) { } }

        [Test]
        public void WithoutTheVariableNothingIsHeld()
        {
            Environment.SetEnvironmentVariable(GpuHeavyWorkGate.EnvironmentVariable, null);
            using (var gate = GpuHeavyWorkGate.Enter()) { Assert.That(gate, Is.Null); Assert.That(GpuHeavyWorkGate.Held, Is.False); }
        }

        [Test]
        public void WithTheVariableTheLockIsExclusiveAcrossProcessesAndNestsOnce()
        {
            if (Application.platform != RuntimePlatform.LinuxEditor) Assert.Ignore("The gate is only active in the Linux editor.");
            Environment.SetEnvironmentVariable(GpuHeavyWorkGate.EnvironmentVariable, path);
            using (var outer = GpuHeavyWorkGate.Enter())
            {
                Assert.That(GpuHeavyWorkGate.Held, Is.True);
                Assert.That(OtherProcessCanLock(), Is.False, "another process must not get the lock while it is held");
                using (GpuHeavyWorkGate.Enter()) Assert.That(GpuHeavyWorkGate.Held, Is.True);
                Assert.That(GpuHeavyWorkGate.Held, Is.True, "leaving the inner one keeps the outer one");
            }
            Assert.That(GpuHeavyWorkGate.Held, Is.False);
            Assert.That(OtherProcessCanLock(), Is.True, "the lock is released when the outer one leaves");
        }

        bool OtherProcessCanLock()
        {
            var info = new ProcessStartInfo("flock", "-n \"" + path + "\" true") { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(info)) { p.WaitForExit(10000); return p.ExitCode == 0; }
        }
    }
}
