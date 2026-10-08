using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Yozolab.YoluPainter.Tests
{
    /// <summary>
    /// 試験の FBX をコードで書く（FBX 7.4 の ASCII）。スタンドアロンの試験（yolu-model の tests/common/fbx_ascii.rs）と同じ形で、Unity が中身を
    /// 取り込むのに要る <c>Definitions</c> の節つき。座標はファイルの空間（右手系・Y が上・既定の単位はメートル = UnitScaleFactor 100）。
    /// 実のアバターのデータは使わない。
    /// </summary>
    internal static class FbxAscii
    {
        internal sealed class Node
        {
            public string Name;
            public int? Parent;
            public double[] Translation = new double[3];
            /// <summary>オイラー角（度。FBX の既定の XYZ の順）。</summary>
            public double[] Rotation = new double[3];
            public double[] Scale = { 1, 1, 1 };
            public bool Limb;

            public Node(string name, int? parent, double[] translation, bool limb) { Name = name; Parent = parent; Translation = translation; Limb = limb; }
        }

        internal sealed class Cluster { public int Bone; public List<int> Indexes = new List<int>(); public List<double> Weights = new List<double>(); }
        internal sealed class Shape { public double FullWeight; public List<int> Indexes = new List<int>(); public List<double[]> Deltas = new List<double[]>(); }
        internal sealed class Channel { public string Name; public double DeformPercent; public List<Shape> Shapes = new List<Shape>(); }

        internal sealed class Mesh
        {
            public string Name;
            public int Node;
            public List<double[]> Vertices = new List<double[]>();
            public List<int[]> Polygons = new List<int[]>();
            public List<double[]> Uvs;
            public List<double[]> Normals;
            public List<int> PolygonMaterials = new List<int>();
            public List<int> Materials = new List<int>();
            public List<Cluster> Clusters = new List<Cluster>();
            public List<Channel> Channels = new List<Channel>();
        }

        internal sealed class Scene
        {
            public List<Node> Nodes = new List<Node>();
            public List<Mesh> Meshes = new List<Mesh>();
            public List<string> Materials = new List<string>();
            public bool Centimeters, ZUp;

            public double[] World(int node)
            {
                var n = Nodes[node];
                return n.Parent is int p ? Mul(World(p), Local(n)) : Local(n);
            }

            string Definitions()
            {
                int limbs = Nodes.Where((n, i) => n.Limb && !Meshes.Any(m => m.Node == i)).Count();
                int deformers = Meshes.Sum(m => (m.Clusters.Count > 0 ? 1 : 0) + m.Clusters.Count + (m.Channels.Count > 0 ? 1 : 0) + m.Channels.Count);
                int shapes = Meshes.SelectMany(m => m.Channels).Sum(c => c.Shapes.Count);
                var counts = new (string, int)[]
                {
                    ("GlobalSettings", 1), ("Model", Nodes.Count), ("NodeAttribute", limbs), ("Geometry", Meshes.Count + shapes),
                    ("Material", Materials.Count), ("Deformer", deformers),
                };
                var o = new StringBuilder();
                o.Append("Definitions:  {\n\tVersion: 100\n\tCount: ").Append(counts.Sum(c => c.Item2)).Append('\n');
                foreach (var (kind, n) in counts.Where(c => c.Item2 > 0)) o.Append("\tObjectType: \"").Append(kind).Append("\" {\n\t\tCount: ").Append(n).Append("\n\t}\n");
                o.Append("}\n");
                return o.ToString();
            }

            public string ToAscii()
            {
                var o = new StringBuilder();
                o.Append("; FBX 7.4.0 project file\n");
                o.Append("FBXHeaderExtension:  {\n\tFBXHeaderVersion: 1003\n\tFBXVersion: 7400\n\tCreator: \"YoluPainter tests\"\n}\n");
                o.Append("GlobalSettings:  {\n\tVersion: 1000\n\tProperties70:  {\n");
                int up = ZUp ? 2 : 1, front = ZUp ? 1 : 2, frontSign = ZUp ? -1 : 1;
                foreach (var (name, v) in new[] { ("UpAxis", up), ("UpAxisSign", 1), ("FrontAxis", front), ("FrontAxisSign", frontSign), ("CoordAxis", 0), ("CoordAxisSign", 1) })
                    o.Append("\t\tP: \"").Append(name).Append("\", \"int\", \"Integer\", \"\",").Append(v).Append('\n');
                o.Append("\t\tP: \"UnitScaleFactor\", \"double\", \"Number\", \"\",").Append(Centimeters ? 1 : 100).Append('\n');
                o.Append("\t}\n}\n");
                o.Append(Definitions());
                o.Append("Objects:  {\n");
                var c = new StringBuilder();
                long ModelId(int i) => 100_000 + i;
                for (int i = 0; i < Nodes.Count; i++)
                {
                    var n = Nodes[i];
                    bool hasMesh = Meshes.Any(m => m.Node == i);
                    string kind = hasMesh ? "Mesh" : n.Limb ? "LimbNode" : "Null";
                    if (n.Limb && !hasMesh)
                    {
                        o.Append("\tNodeAttribute: ").Append(200_000 + i).Append(", \"NodeAttribute::").Append(n.Name).Append("\", \"LimbNode\" {\n\t\tTypeFlags: \"Skeleton\"\n\t}\n");
                        c.Append("\tC: \"OO\",").Append(200_000 + i).Append(',').Append(ModelId(i)).Append('\n');
                    }
                    o.Append("\tModel: ").Append(ModelId(i)).Append(", \"Model::").Append(n.Name).Append("\", \"").Append(kind).Append("\" {\n\t\tVersion: 232\n\t\tProperties70:  {\n");
                    o.Append("\t\t\tP: \"Lcl Translation\", \"Lcl Translation\", \"\", \"A\",").Append(Join(n.Translation)).Append('\n');
                    o.Append("\t\t\tP: \"Lcl Rotation\", \"Lcl Rotation\", \"\", \"A\",").Append(Join(n.Rotation)).Append('\n');
                    o.Append("\t\t\tP: \"Lcl Scaling\", \"Lcl Scaling\", \"\", \"A\",").Append(Join(n.Scale)).Append('\n');
                    o.Append("\t\t}\n\t\tShading: T\n\t\tCulling: \"CullingOff\"\n\t}\n");
                    long parent = n.Parent is int p ? ModelId(p) : 0;
                    c.Append("\tC: \"OO\",").Append(ModelId(i)).Append(',').Append(parent).Append('\n');
                }
                for (int i = 0; i < Materials.Count; i++)
                    o.Append("\tMaterial: ").Append(300_000 + i).Append(", \"Material::").Append(Materials[i]).Append("\", \"\" {\n\t\tVersion: 102\n\t\tShadingModel: \"phong\"\n\t\tMultiLayer: 0\n\t}\n");
                for (int mi = 0; mi < Meshes.Count; mi++)
                {
                    var m = Meshes[mi];
                    long gid = 400_000 + mi * 1000L;
                    o.Append("\tGeometry: ").Append(gid).Append(", \"Geometry::").Append(m.Name).Append("\", \"Mesh\" {\n");
                    Array(o, "\t\t", "Vertices", m.Vertices.SelectMany(v => v).Select(Num));
                    var pvi = new List<string>();
                    foreach (var poly in m.Polygons)
                        for (int k = 0; k < poly.Length; k++) pvi.Add((k + 1 == poly.Length ? -poly[k] - 1 : poly[k]).ToString(CultureInfo.InvariantCulture));
                    Array(o, "\t\t", "PolygonVertexIndex", pvi);
                    o.Append("\t\tGeometryVersion: 124\n");
                    var layers = new List<string>();
                    if (m.Normals != null)
                    {
                        o.Append("\t\tLayerElementNormal: 0 {\n\t\t\tVersion: 102\n\t\t\tName: \"\"\n\t\t\tMappingInformationType: \"ByPolygonVertex\"\n\t\t\tReferenceInformationType: \"Direct\"\n");
                        Array(o, "\t\t\t", "Normals", m.Normals.SelectMany(v => v).Select(Num));
                        o.Append("\t\t}\n");
                        layers.Add("LayerElementNormal");
                    }
                    if (m.Uvs != null)
                    {
                        o.Append("\t\tLayerElementUV: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"UVMap\"\n\t\t\tMappingInformationType: \"ByPolygonVertex\"\n\t\t\tReferenceInformationType: \"IndexToDirect\"\n");
                        Array(o, "\t\t\t", "UV", m.Uvs.SelectMany(v => v).Select(Num));
                        Array(o, "\t\t\t", "UVIndex", Enumerable.Range(0, m.Uvs.Count).Select(i => i.ToString(CultureInfo.InvariantCulture)));
                        o.Append("\t\t}\n");
                        layers.Add("LayerElementUV");
                    }
                    if (m.PolygonMaterials.Count > 0)
                    {
                        o.Append("\t\tLayerElementMaterial: 0 {\n\t\t\tVersion: 101\n\t\t\tName: \"\"\n\t\t\tMappingInformationType: \"ByPolygon\"\n\t\t\tReferenceInformationType: \"IndexToDirect\"\n");
                        Array(o, "\t\t\t", "Materials", m.PolygonMaterials.Select(i => i.ToString(CultureInfo.InvariantCulture)));
                        o.Append("\t\t}\n");
                        layers.Add("LayerElementMaterial");
                    }
                    o.Append("\t\tLayer: 0 {\n\t\t\tVersion: 100\n");
                    foreach (var l in layers) o.Append("\t\t\tLayerElement:  {\n\t\t\t\tType: \"").Append(l).Append("\"\n\t\t\t\tTypedIndex: 0\n\t\t\t}\n");
                    o.Append("\t\t}\n\t}\n");
                    c.Append("\tC: \"OO\",").Append(gid).Append(',').Append(ModelId(m.Node)).Append('\n');
                    foreach (int mat in m.Materials) c.Append("\tC: \"OO\",").Append(300_000 + mat).Append(',').Append(ModelId(m.Node)).Append('\n');
                    if (m.Clusters.Count > 0)
                    {
                        long skin = gid + 1;
                        o.Append("\tDeformer: ").Append(skin).Append(", \"Deformer::\", \"Skin\" {\n\t\tVersion: 101\n\t\tLink_DeformAcuracy: 50\n\t}\n");
                        c.Append("\tC: \"OO\",").Append(skin).Append(',').Append(gid).Append('\n');
                        var meshWorld = World(m.Node);
                        for (int k = 0; k < m.Clusters.Count; k++)
                        {
                            var cl = m.Clusters[k];
                            long id = gid + 10 + k;
                            var link = World(cl.Bone);
                            var transform = Mul(Inverse(link), meshWorld);
                            o.Append("\tDeformer: ").Append(id).Append(", \"SubDeformer::\", \"Cluster\" {\n\t\tVersion: 100\n\t\tUserData: \"\", \"\"\n");
                            Array(o, "\t\t", "Indexes", cl.Indexes.Select(i => i.ToString(CultureInfo.InvariantCulture)));
                            Array(o, "\t\t", "Weights", cl.Weights.Select(Num));
                            Array(o, "\t\t", "Transform", transform.Select(Num));
                            Array(o, "\t\t", "TransformLink", link.Select(Num));
                            o.Append("\t}\n");
                            c.Append("\tC: \"OO\",").Append(id).Append(',').Append(skin).Append('\n');
                            c.Append("\tC: \"OO\",").Append(ModelId(cl.Bone)).Append(',').Append(id).Append('\n');
                        }
                    }
                    if (m.Channels.Count > 0)
                    {
                        long bs = gid + 500;
                        o.Append("\tDeformer: ").Append(bs).Append(", \"Deformer::").Append(m.Name).Append("\", \"BlendShape\" {\n\t\tVersion: 100\n\t}\n");
                        c.Append("\tC: \"OO\",").Append(bs).Append(',').Append(gid).Append('\n');
                        for (int k = 0; k < m.Channels.Count; k++)
                        {
                            var ch = m.Channels[k];
                            long id = bs + 1 + k * 20L;
                            o.Append("\tDeformer: ").Append(id).Append(", \"SubDeformer::").Append(ch.Name).Append("\", \"BlendShapeChannel\" {\n\t\tVersion: 100\n\t\tDeformPercent: ").Append(Num(ch.DeformPercent)).Append('\n');
                            Array(o, "\t\t", "FullWeights", ch.Shapes.Select(s => Num(s.FullWeight)));
                            o.Append("\t}\n");
                            c.Append("\tC: \"OO\",").Append(id).Append(',').Append(bs).Append('\n');
                            for (int j = 0; j < ch.Shapes.Count; j++)
                            {
                                var s = ch.Shapes[j];
                                long sid = id + 1 + j;
                                o.Append("\tGeometry: ").Append(sid).Append(", \"Geometry::").Append(ch.Name).Append('_').Append(j).Append("\", \"Shape\" {\n\t\tVersion: 100\n");
                                Array(o, "\t\t", "Indexes", s.Indexes.Select(i => i.ToString(CultureInfo.InvariantCulture)));
                                Array(o, "\t\t", "Vertices", s.Deltas.SelectMany(d => d).Select(Num));
                                o.Append("\t}\n");
                                c.Append("\tC: \"OO\",").Append(sid).Append(',').Append(id).Append('\n');
                            }
                        }
                    }
                }
                o.Append("}\nConnections:  {\n");
                o.Append(c);
                o.Append("}\n");
                return o.ToString();
            }
        }

        static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static string Join(double[] v) => string.Join(",", v.Select(Num));

        static void Array(StringBuilder o, string indent, string name, IEnumerable<string> values)
        {
            var list = values.ToList();
            o.Append(indent).Append(name).Append(": *").Append(list.Count).Append(" {\n").Append(indent).Append("\ta: ").Append(string.Join(",", list)).Append('\n').Append(indent).Append("}\n");
        }

        // ───────── 4×4（列優先、double） ─────────

        /// <summary>ノードのローカル（T × R × S、R は XYZ の順 = Rz × Ry × Rx）。</summary>
        public static double[] Local(Node n)
        {
            double rx = n.Rotation[0] * Math.PI / 180, ry = n.Rotation[1] * Math.PI / 180, rz = n.Rotation[2] * Math.PI / 180;
            double[] X = { 1, 0, 0, 0, 0, Math.Cos(rx), Math.Sin(rx), 0, 0, -Math.Sin(rx), Math.Cos(rx), 0, 0, 0, 0, 1 };
            double[] Y = { Math.Cos(ry), 0, -Math.Sin(ry), 0, 0, 1, 0, 0, Math.Sin(ry), 0, Math.Cos(ry), 0, 0, 0, 0, 1 };
            double[] Z = { Math.Cos(rz), Math.Sin(rz), 0, 0, -Math.Sin(rz), Math.Cos(rz), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            double[] S = { n.Scale[0], 0, 0, 0, 0, n.Scale[1], 0, 0, 0, 0, n.Scale[2], 0, 0, 0, 0, 1 };
            double[] T = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, n.Translation[0], n.Translation[1], n.Translation[2], 1 };
            return Mul(T, Mul(Mul(Z, Mul(Y, X)), S));
        }

        public static double[] Mul(double[] a, double[] b)
        {
            var r = new double[16];
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                {
                    double s = 0;
                    for (int k = 0; k < 4; k++) s += a[k * 4 + row] * b[col * 4 + k];
                    r[col * 4 + row] = s;
                }
            return r;
        }

        public static double[] Inverse(double[] m)
        {
            // ガウス・ジョルダン（行列は小さく、試験の値はよく条件づけられている）
            var a = new double[4, 8];
            for (int r = 0; r < 4; r++) { for (int c = 0; c < 4; c++) a[r, c] = m[c * 4 + r]; a[r, 4 + r] = 1; }
            for (int c = 0; c < 4; c++)
            {
                int pivot = c;
                for (int r = c + 1; r < 4; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c])) pivot = r;
                for (int k = 0; k < 8; k++) { double t = a[c, k]; a[c, k] = a[pivot, k]; a[pivot, k] = t; }
                double d = a[c, c];
                for (int k = 0; k < 8; k++) a[c, k] /= d;
                for (int r = 0; r < 4; r++)
                {
                    if (r == c) continue;
                    double f = a[r, c];
                    for (int k = 0; k < 8; k++) a[r, k] -= f * a[c, k];
                }
            }
            var inv = new double[16];
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) inv[c * 4 + r] = a[r, 4 + c];
            return inv;
        }

        // ───────── 試しの形 ─────────

        /// <summary>四角い断面の筒（x = 0..length、y = y0、断面の半分の幅 h、輪は rings 個）。面ごとに平らな法線と UV、マテリアルは上 2 面が 0・下 2 面が 1。</summary>
        public static Mesh BoxTube(string name, int node, double length, double y0, double h, int rings)
        {
            var m = new Mesh { Name = name, Node = node, Uvs = new List<double[]>(), Normals = new List<double[]>() };
            for (int r = 0; r < rings; r++)
            {
                double x = length * r / (rings - 1);
                foreach (var (dy, dz) in new[] { (h, h), (-h, h), (-h, -h), (h, -h) }) m.Vertices.Add(new[] { x, y0 + dy, dz });
            }
            double[][] faceNormals = { new double[] { 0, 0, 1 }, new double[] { 0, -1, 0 }, new double[] { 0, 0, -1 }, new double[] { 0, 1, 0 } };
            for (int r = 0; r < rings - 1; r++)
                for (int k = 0; k < 4; k++)
                {
                    int a = 4 * r + k, b = 4 * r + (k + 1) % 4, c = a + 4, d = b + 4;
                    m.Polygons.Add(new[] { a, b, d, c });
                    double u0 = (double)r / (rings - 1), u1 = (double)(r + 1) / (rings - 1), v0 = k / 4.0, v1 = (k + 1) / 4.0;
                    m.Uvs.AddRange(new[] { new[] { u0, v0 }, new[] { u0, v1 }, new[] { u1, v1 }, new[] { u1, v0 } });
                    for (int i = 0; i < 4; i++) m.Normals.Add(faceNormals[k]);
                    m.PolygonMaterials.Add(k < 2 ? 0 : 1);
                }
            return m;
        }

        /// <summary>試しの腕: 根 → Armature（Null）→ Upper（y = 1）→ Lower（x = 1）→ Hat（x = 0.5, y = 0.2、スキンの無い三角形）。メッシュ ArmMesh は根の子で、
        /// x = 0..2 の四角い筒を Upper・Lower に塗り分け、マテリアルは Skin と Cloth。BlendShape は Thick（DeformPercent 25）と Bend（中間のフレーム 50・100）。</summary>
        public static Scene ArmScene()
        {
            var s = new Scene
            {
                Nodes =
                {
                    new Node("Armature", null, new double[] { 0, 0, 0 }, false),
                    new Node("Upper", 0, new double[] { 0, 1, 0 }, true),
                    new Node("Lower", 1, new double[] { 1, 0, 0 }, true),
                    new Node("ArmMesh", null, new double[] { 0, 0, 0 }, false),
                    new Node("Hat", 2, new double[] { 0.5, 0.2, 0 }, false),
                },
                Materials = { "Skin", "Cloth" },
            };
            var arm = BoxTube("ArmMesh", 3, 2.0, 1.0, 0.1, 5);
            arm.Materials = new List<int> { 0, 1 };
            var upper = new Cluster { Bone = 1 };
            var lower = new Cluster { Bone = 2 };
            for (int v = 0; v < arm.Vertices.Count; v++)
            {
                double x = arm.Vertices[v][0];
                double wu = x < 0.99 ? 1 : x < 1.01 ? 0.5 : 0, wl = x < 0.99 ? 0 : x < 1.01 ? 0.5 : 1;
                if (wu > 0) { upper.Indexes.Add(v); upper.Weights.Add(wu); }
                if (wl > 0) { lower.Indexes.Add(v); lower.Weights.Add(wl); }
            }
            arm.Clusters = new List<Cluster> { upper, lower };
            List<int> Ring(int r) => Enumerable.Range(4 * r, 4).ToList();
            double[] Outward(int v, double amount) { var p = arm.Vertices[v]; return new[] { 0, (p[1] - 1) / 0.1 * amount, p[2] / 0.1 * amount }; }
            arm.Channels = new List<Channel>
            {
                new Channel { Name = "Thick", DeformPercent = 25, Shapes = { new Shape { FullWeight = 100, Indexes = Ring(3), Deltas = Ring(3).Select(v => Outward(v, 0.1)).ToList() } } },
                new Channel
                {
                    Name = "Bend", DeformPercent = 0,
                    Shapes =
                    {
                        new Shape { FullWeight = 50, Indexes = Ring(4), Deltas = Enumerable.Repeat(new double[] { 0, 0.2, 0 }, 4).ToList() },
                        new Shape { FullWeight = 100, Indexes = Ring(4), Deltas = Enumerable.Repeat(new double[] { 0, 0.6, 0.1 }, 4).ToList() },
                    },
                },
            };
            s.Meshes.Add(arm);
            s.Meshes.Add(new Mesh
            {
                Name = "Hat", Node = 4,
                Vertices = { new double[] { 0, 0, 0 }, new double[] { 0.1, 0, 0 }, new double[] { 0, 0.1, 0 } },
                Polygons = { new[] { 0, 1, 2 } },
                Uvs = new List<double[]> { new[] { 0.9, 0.9 }, new[] { 1.0, 0.9 }, new[] { 0.9, 1.0 } },
                PolygonMaterials = { 0 },
                Materials = { 0 },
            });
            return s;
        }
    }
}
