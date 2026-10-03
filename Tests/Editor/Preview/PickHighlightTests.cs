using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Yozolab.YoluPainter.Core;
using Yozolab.YoluPainter.Core.MeshMaps;
using Yozolab.YoluPainter.Editor;
using Yozolab.YoluPainter.Editor.Preview;
using Object = UnityEngine.Object;

namespace Yozolab.YoluPainter.Tests
{
    [Category("GPU")]
    public sealed class PickHighlightTests
    {
        [SetUp] public void RequireShader() { GpuTests.RequireWorkingShader("Hidden/YoluPainter/PickHighlight"); }
        static readonly Rect View = new Rect(0,0,200,200);
        static BakedMeshMap Map(IsolatedModelPreview p)
        {
            var s = IdAssignmentTests.Settings(MeshIdSource.UvIsland); s.Width = s.Height = 64; s.Maps = new[] { MeshMapKind.Id };
            return MeshBaker.Bake(TexturePaintWindow.BuildMeshBakeInput(p.Geometry),s).Maps.Single(m=>m.Kind==MeshMapKind.Id);
        }
        static void Show(IsolatedModelPreview p, BakedMeshMap map, int rgb, int tolerance = 0, long budget = 1<<24)
        { Assert.That(p.ShowIdRegion(1, Enumerable.Range(0,p.Geometry.TriangleCount).ToArray(), new Color(1,.62f,.16f,.24f),map,rgb,tolerance,budget),Is.True); }
        [Test] public void MaterialAssetsUseGuidAndLocalFileIdWithoutChangingTheirSources()
        {
            string path=AssetDatabase.GenerateUniqueAssetPath("Assets/IdColourFixture.mat");
            var root=new GameObject("Material identity fixture"); var mesh=new Mesh();
            var a=new Material(Shader.Find("Hidden/YoluPainter/PickHighlight")) { name="Same name" };
            var b=new Material(a) { name="Same name" };
            try
            {
                AssetDatabase.CreateAsset(a,path); AssetDatabase.AddObjectToAsset(b,path); AssetDatabase.SaveAssetIfDirty(a);
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(a,out string ga,out long ia),Is.True);
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(b,out string gb,out long ib),Is.True);
                Assert.That(ga,Is.EqualTo(gb)); Assert.That(ia,Is.Not.EqualTo(ib),"sub-assets of the same file are distinct materials");
                mesh.vertices=Enumerable.Range(0,9).Select(i=>new Vector3(i/3*2+i%3%2,i%3/2,0)).ToArray();
                mesh.uv=Enumerable.Range(0,9).Select(i=>new Vector2(i%3%2*.25f+i/3*.3f,i%3/2*.25f)).ToArray();
                mesh.subMeshCount=3; for(int s=0;s<3;s++) mesh.SetTriangles(new[]{s*3,s*3+1,s*3+2},s); mesh.RecalculateNormals();
                root.AddComponent<MeshFilter>().sharedMesh=mesh; root.AddComponent<MeshRenderer>().sharedMaterials=new[]{a,a,b};
                string beforeA=EditorJsonUtility.ToJson(a),beforeB=EditorJsonUtility.ToJson(b);
                bool dirtyA=EditorUtility.IsDirty(a),dirtyB=EditorUtility.IsDirty(b);
                using(var p=new IsolatedModelPreview())
                {
                    p.Load(root); var keys=TexturePaintWindow.MaterialIdentityKeys(p);
                    Assert.That(keys[0],Is.EqualTo(keys[1])); Assert.That(keys[0],Is.Not.EqualTo(keys[2]));
                    var input=TexturePaintWindow.BuildMeshBakeInput(p.Geometry,materialKeys:keys);
                    var baked=MeshBaker.Bake(input,IdAssignmentTests.Settings(MeshIdSource.MaterialAsset)).Maps.Single(m=>m.Kind==MeshMapKind.Id);
                    int[] colours=Enumerable.Range(0,3).Select(t=>{var f=p.Geometry.Triangles[t]; var uv=(f.UvA+f.UvB+f.UvC)/3; Assert.That(IdMapColors.TryGetAtUv(baked,uv.x,uv.y,out int rgb),Is.True); return rgb;}).ToArray();
                    Assert.That(colours[0],Is.EqualTo(colours[1])); Assert.That(colours[0],Is.Not.EqualTo(colours[2]));
                }
                Assert.That(EditorJsonUtility.ToJson(a),Is.EqualTo(beforeA)); Assert.That(EditorJsonUtility.ToJson(b),Is.EqualTo(beforeB));
                Assert.That(EditorUtility.IsDirty(a),Is.EqualTo(dirtyA)); Assert.That(EditorUtility.IsDirty(b),Is.EqualTo(dirtyB));
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(mesh); AssetDatabase.DeleteAsset(path);
                if(a!=null) Object.DestroyImmediate(a); if(b!=null) Object.DestroyImmediate(b); }
        }
        [Test] public void SameIdHoverReusesPictureMeshAndTextureAndEveryChangedInputInvalidatesTheKey()
        {
            using (var p = new IsolatedModelPreview { FrameRateLimit = 0 })
            {
                p.LoadDemoMesh(); var map = Map(p); int rgb = IdPalette.Colors(6)[0]; Show(p,map,rgb);
                p.RenderCached(View,1); var key = p.ComputeRenderKey(View,1);
                for (int i = 0; i < 200; i++) { Show(p,map,rgb); p.RenderCached(View,1); }
                Assert.That(p.RenderCount,Is.EqualTo(1)); Assert.That(p.RegionHighlightBuilds,Is.EqualTo(1)); Assert.That(p.IdHighlightTextureBuilds,Is.EqualTo(1));
                foreach (var change in new Action[] { ()=>Show(p,map,rgb,1), ()=>Show(p,map,rgb^0xFFFFFF,1),
                    ()=>Show(p,MeshMapBinary.Read(MeshMapBinary.Write(map)),rgb^0xFFFFFF,1), ()=>p.HideRegion() })
                { change(); Assert.That(p.ComputeRenderKey(View,1),Is.Not.EqualTo(key)); p.RenderCached(View,1); key=p.ComputeRenderKey(View,1); }
                Assert.That(p.RenderCount,Is.EqualTo(5)); Assert.That(p.RegionHighlightBuilds,Is.EqualTo(1),"changing colour does not rebuild the mesh");
                Assert.That(p.IdHighlightTextureBuilds,Is.EqualTo(2));
            }
        }
        [Test] public void TheIdShaderTintsTheClickedTexelAndRejectsOtherColoursAndEmptyTexels()
        {
            using (var p = new IsolatedModelPreview { FrameRateLimit = 0 })
            {
                p.LoadDemoMesh(); var map = Map(p); Vector2 point = Vector2.zero; SurfaceHit hit = default;
                for (int x = 85; x <= 125; x += 10)
                    if (p.TryPick(View,new Vector2(x,112),out var found) && Mathf.Min(found.Barycentric.x,Mathf.Min(found.Barycentric.y,found.Barycentric.z))>.1f) { point=new Vector2(x,112); hit=found; break; }
                Assert.That(point,Is.Not.EqualTo(Vector2.zero)); Assert.That(IdMapColors.TryGetAtUv(map,hit.UV.x,hit.UV.y,out int rgb),Is.True);
                Color Read() { var t=p.RenderStatic(200,200); try { return t.GetPixel((int)point.x,199-(int)point.y); } finally { Object.DestroyImmediate(t); } }
                var before=Read(); Show(p,map,rgb); var after=Read(); Assert.That(after.r-after.b,Is.GreaterThan(before.r-before.b+.08f));
                Show(p,map,rgb^0xFFFFFF); Assert.That(Read(),Is.EqualTo(before));
                var empty = TestMeshMaps.Make(MeshMapKind.Id,map.Width,map.Height,(x,y,c)=>0,(x,y)=>MeshTexelCoverage.Empty);
                Show(p,empty,rgb,255); Assert.That(Read(),Is.EqualTo(before),"Empty texels do not get a colour, even at tolerance 255");
            }
        }
        [Test] public void HighlightRejectsWrongTypesAndBudgetBeforeAllocating()
        {
            using(var p=new IsolatedModelPreview())
            {
                p.LoadDemoMesh(); var map=Map(p); var tris=new[]{0,1};
                Assert.That(p.ShowIdRegion(1,tris,Color.yellow,map,0,0,1),Is.False); Assert.That(p.IdHighlightTextureBuilds,Is.Zero);
                var settings=IdAssignmentTests.Settings(); settings.Maps=new[]{MeshMapKind.Position};
                var position=MeshBaker.Bake(TexturePaintWindow.BuildMeshBakeInput(p.Geometry),settings).Maps.Single(m=>m.Kind==MeshMapKind.Position);
                Assert.That(()=>p.ShowIdRegion(1,tris,Color.yellow,position,0,0,1<<24),Throws.ArgumentException);
                Assert.That(()=>p.ShowIdRegion(1,tris,Color.yellow,map,0,-1,1<<24),Throws.InstanceOf<ArgumentException>());
                Assert.That(p.IdHighlightTextureBuilds,Is.Zero);
            }
        }
        [Test] public void UvCandidatesAreSortedCompleteAndDoNotIncludeOtherSlotsOrDegenerateFaces()
        {
            var a=new Vector2(.1f,.1f); var b=new Vector2(.8f,.1f); var c=new Vector2(.1f,.8f);
            SurfaceTriangle T(int slot,float z,Vector2 third) => new SurfaceTriangle(new Vector3(0,0,z),new Vector3(1,0,z),new Vector3(0,1,z),a,b,third,0,slot);
            var index=new SurfaceRegionIndex(new SurfaceGeometry(new[]{T(0,0,c),T(0,1,c),T(1,2,c),T(0,3,a)}));
            var list=new System.Collections.Generic.List<int>(); index.TrianglesAtUv(0,new Vector2(.2f,.2f),list); Assert.That(list,Is.EqualTo(new[]{0,1}));
            Assert.That(index.TriangleAtUv(0,new Vector2(.2f,.2f)),Is.EqualTo(0)); index.TrianglesAtUv(1,new Vector2(.2f,.2f),list); Assert.That(list,Is.EqualTo(new[]{2}));
            foreach(var point in new[]{new Vector2(float.NaN,0),new Vector2(float.PositiveInfinity,0),Vector2.one}) { index.TrianglesAtUv(0,point,list); Assert.That(list,Is.Empty); }
        }
    }
}
