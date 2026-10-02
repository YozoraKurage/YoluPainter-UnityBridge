using System;
using System.Collections;
namespace NUnit.Framework {
[AttributeUsage(AttributeTargets.Method)] public class TestAttribute:Attribute {}
[AttributeUsage(AttributeTargets.Method,AllowMultiple=true)] public class TestCaseAttribute:Attribute {public readonly object[] Arguments;public TestCaseAttribute(params object[] args){Arguments=args;}}
public class Constraint {
 internal Func<object,bool> predicate; internal object expected; internal double tolerance; internal string label;
 public Constraint(Func<object,bool> p,string label){predicate=p;this.label=label;}
 public Constraint Within(double t){tolerance=t;predicate=a=>Math.Abs(Convert.ToDouble(a)-Convert.ToDouble(expected))<=t;return this;}
}
public static class Is {
 public static Constraint True=>new Constraint(a=>a is bool b&&b,"true"); public static Constraint False=>new Constraint(a=>a is bool b&&!b,"false");
 public static Constraint Empty=>new Constraint(a=>a is ICollection c&&c.Count==0,"empty");
 public static Constraint EqualTo(object value){return new Constraint(a=>a.Equals(value)|| (a is IConvertible&&value is IConvertible&&Convert.ToDouble(a)==Convert.ToDouble(value)),"equal to "+value){expected=value};}
 public static Constraint GreaterThan(object v)=>new Constraint(a=>Convert.ToDouble(a)>Convert.ToDouble(v),"greater than "+v);
}
public static class Does {public static Constraint Contain(string v)=>new Constraint(a=>((string)a).Contains(v),"contains "+v);}
public static class Assert {
 public static void That(object actual,Constraint c,string message=null){if(!c.predicate(actual))throw new Exception((message??"Assertion failed")+"; actual="+actual+" expected "+c.label);}
 public static void Throws<T>(Action f)where T:Exception {try{f();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
}
}
