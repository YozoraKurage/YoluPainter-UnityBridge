using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
class Program {
 static int Main(){int passed=0,failed=0;var instance=new Dot.TexturePainter.Tests.Editor.GeometryTests();
 foreach(var method in instance.GetType().GetMethods()){
 var cases=method.GetCustomAttributes<TestCaseAttribute>().ToArray();
 var args=cases.Length>0?cases.Select(c=>c.Arguments).ToArray():method.IsDefined(typeof(TestAttribute))?new[]{Array.Empty<object>()}:Array.Empty<object[]>();
 foreach(var a in args){try{method.Invoke(instance,a);passed++;Console.WriteLine("PASS "+method.Name+" "+string.Join(",",a));}catch(Exception e){failed++;Console.WriteLine("FAIL "+method.Name+": "+(e.InnerException??e));}}}
 Console.WriteLine($"Math-adapter geometry check: {passed} passed, {failed} failed. Not a Unity execution or rendering test.");return failed==0?0:1; }
}
