using System;
namespace UnityEngine {
public class Object {}
public struct Vector2 {
 public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;}
 public static Vector2 zero=>new Vector2(0,0); public static Vector2 one=>new Vector2(1,1); public static Vector2 up=>new Vector2(0,1); public static Vector2 right=>new Vector2(1,0);
 public float magnitude=>(float)Math.Sqrt(x*x+y*y);
 public static Vector2 operator+(Vector2 a,Vector2 b)=>new Vector2(a.x+b.x,a.y+b.y);
 public static Vector2 operator-(Vector2 a,Vector2 b)=>new Vector2(a.x-b.x,a.y-b.y);
 public static Vector2 operator*(Vector2 a,float b)=>new Vector2(a.x*b,a.y*b);
 public static Vector2 operator*(float b,Vector2 a)=>a*b;
 public static Vector2 Min(Vector2 a,Vector2 b)=>new Vector2(Math.Min(a.x,b.x),Math.Min(a.y,b.y));
 public static Vector2 Max(Vector2 a,Vector2 b)=>new Vector2(Math.Max(a.x,b.x),Math.Max(a.y,b.y));
}
public struct Vector3 {
 public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
 public float this[int i]{get=>i==0?x:i==1?y:z;set{if(i==0)x=value;else if(i==1)y=value;else z=value;}}
 public static Vector3 zero=>new Vector3(0,0,0); public static Vector3 one=>new Vector3(1,1,1); public static Vector3 up=>new Vector3(0,1,0); public static Vector3 right=>new Vector3(1,0,0); public static Vector3 forward=>new Vector3(0,0,1); public static Vector3 back=>new Vector3(0,0,-1);
 public float sqrMagnitude=>x*x+y*y+z*z; public float magnitude=>(float)Math.Sqrt(sqrMagnitude); public Vector3 normalized=>magnitude>1e-5f?this/magnitude:zero;
 public static Vector3 operator+(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
 public static Vector3 operator-(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
 public static Vector3 operator*(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);
 public static Vector3 operator*(float b,Vector3 a)=>a*b; public static Vector3 operator/(Vector3 a,float b)=>new Vector3(a.x/b,a.y/b,a.z/b);
 public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
 public static Vector3 Cross(Vector3 a,Vector3 b)=>new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
 public static Vector3 Min(Vector3 a,Vector3 b)=>new Vector3(Math.Min(a.x,b.x),Math.Min(a.y,b.y),Math.Min(a.z,b.z));
 public static Vector3 Max(Vector3 a,Vector3 b)=>new Vector3(Math.Max(a.x,b.x),Math.Max(a.y,b.y),Math.Max(a.z,b.z));
}
public struct Ray {public Vector3 origin,direction; public Ray(Vector3 o,Vector3 d){origin=o;direction=d.normalized;} public Vector3 GetPoint(float d)=>origin+direction*d;}
public struct Bounds {
 public Vector3 center,size; public Bounds(Vector3 center,Vector3 size){this.center=center;this.size=size;}
 public Vector3 min=>center-size/2; public Vector3 max=>center+size/2;
 public void Encapsulate(Vector3 p){var lo=Vector3.Min(min,p);var hi=Vector3.Max(max,p);center=(lo+hi)/2;size=hi-lo;}
 public void Encapsulate(Bounds b){Encapsulate(b.min);Encapsulate(b.max);}
 public float SqrDistance(Vector3 p){float sum=0;var lo=min;var hi=max;for(int i=0;i<3;i++){float d=p[i]<lo[i]?lo[i]-p[i]:p[i]>hi[i]?p[i]-hi[i]:0;sum+=d*d;}return sum;}
}
public static class Mathf {
 public static float Max(float a,float b)=>Math.Max(a,b); public static int Max(int a,int b)=>Math.Max(a,b); public static float Min(float a,float b)=>Math.Min(a,b); public static int Min(int a,int b)=>Math.Min(a,b);
 public static float Abs(float a)=>Math.Abs(a); public static float Sqrt(float a)=>(float)Math.Sqrt(a); public static float Clamp01(float a)=>Math.Max(0,Math.Min(1,a));
 public static int CeilToInt(float a)=>(int)Math.Ceiling(a); public static int FloorToInt(float a)=>(int)Math.Floor(a);
 public static float SmoothStep(float from,float to,float t){t=Clamp01(t);t=-2*t*t*t+3*t*t;return to*t+from*(1-t);}
 public static bool Approximately(float a,float b)=>Math.Abs(b-a)<Math.Max(1e-6f*Math.Max(Math.Abs(a),Math.Abs(b)),float.Epsilon*8);
}
}
