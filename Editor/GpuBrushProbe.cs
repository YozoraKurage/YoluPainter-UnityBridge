using System;
using Dot.TexturePainter.Core;
using UnityEditor;
using UnityEngine;

namespace Dot.TexturePainter.Editor
{
    /// <summary>Explicit G0 test probe, not the authoritative editor stroke path.
    /// Synchronous readback is deliberately confined to this user-invoked validation command.</summary>
    public static class GpuBrushProbe
    {
        [MenuItem("Window/dot/Run GPU brush parity probe")]
        public static void Run()
        {
            var shader=Shader.Find("Hidden/DotTexturePainter/OrderedBrush");
            if(shader==null||!shader.isSupported){Debug.LogWarning("GPU brush probe skipped: shader unavailable.");return;}
            const int size=32; Texture2D upload=null,readback=null;RenderTexture a=null,b=null;Material material=null;
            var previous=RenderTexture.active;
            try
            {
                upload=new Texture2D(size,size,TextureFormat.RGBA32,false,true);
                upload.LoadRawTextureData(new byte[size*size*4]);upload.Apply();
                a=new RenderTexture(size,size,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                b=new RenderTexture(size,size,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);
                if(!a.Create()||!b.Create())throw new InvalidOperationException("RenderTexture allocation failed");
                Graphics.Blit(upload,a);material=new Material(shader);
                material.SetVector("_TileSize",new Vector4(size,size,0,0));
                var doc=new PaintDocument(size,size,16);var layer=doc.AddLayer("Probe");
                var settings=new BrushSettings{Radius=8,Hardness=.4,Opacity=.7,Flow=.8,Color=new Rgba32(51,153,204,255),PressureSize=false,PressureOpacity=false};
                using(var stroke=doc.BeginStroke(layer.Id,PaintChannel.Color,settings))
                {
                    // GPU/CPU receive the same explicit dab rather than relying on path interpolation.
                    for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                    {
                        double dx=x+.5-16,dy=y+.5-16,d=Math.Sqrt(dx*dx+dy*dy)/8;
                        double coverage=d<=.4?1:Math.Max(0,(1-d)/.6);
                        stroke.ApplyPixel(x,y,coverage,1);
                    }
                    stroke.Commit();
                }
                material.SetVector("_CenterRadiusHardness",new Vector4(16,16,8,.4f));
                material.SetVector("_BrushColor",new Vector4(.2f,.6f,.8f,1));
                material.SetFloat("_OpacityFlow",.7f*.8f);material.SetFloat("_Erase",0);
                Graphics.Blit(a,b,material,0);
                RenderTexture.active=b;readback=new Texture2D(size,size,TextureFormat.RGBA32,false,true);
                readback.ReadPixels(new Rect(0,0,size,size),0,0);readback.Apply();
                byte[] expected=doc.Composite(PaintChannel.Color);var actual=readback.GetRawTextureData<byte>();int maximum=0;long sum=0;
                for(int i=0;i<expected.Length;i++){int error=Math.Abs(expected[i]-actual[i]);maximum=Math.Max(maximum,error);sum+=error;}
                string message=$"GPU brush parity probe: max byte error {maximum}, mean {sum/(double)expected.Length:F6}; {SystemInfo.graphicsDeviceType} / {SystemInfo.graphicsDeviceName}. One RGBA8 dab only; not tablet, 3D or full compositor validation.";
                if(maximum<=1)Debug.Log(message);else Debug.LogWarning(message);
            }
            catch(Exception ex){Debug.LogError("GPU brush probe failed: "+ex);}
            finally
            {
                RenderTexture.active=previous;
                if(a!=null){a.Release();UnityEngine.Object.DestroyImmediate(a);}if(b!=null){b.Release();UnityEngine.Object.DestroyImmediate(b);}
                if(upload!=null)UnityEngine.Object.DestroyImmediate(upload);if(readback!=null)UnityEngine.Object.DestroyImmediate(readback);if(material!=null)UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}
