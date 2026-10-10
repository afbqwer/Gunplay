using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Gunplay
{
    // 把当前地图所有可见轨迹压到最少的绘制调用。
    //
    // 单一绘制路径：直线与弧线的几何都合并进一张「按（材质 + 颜色）分桶」的可复用网格，
    // 每帧每组一次 Graphics.DrawMesh。绘制调用数 = 本帧用到的「材质×颜色」种类数。
    //
    // 直线几何与旧实例化路径逐顶点一致：即 MeshPool.plane10（1×1 居中平面），
    // 只是把原来的 TRS 矩阵变换展开成世界坐标后写入合并网格（见 ProjectileTrailState.AppendStraightGeometry）。
    internal static class ProjectileTrailBatcher
    {
        // 合并网格上传时跳过 Unity 的包围盒重算与索引校验（包围盒由我们在下面自行设定）
        const MeshUpdateFlags MeshFlags =
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

        // ---------------- 绘制键：材质 + 颜色 ----------------

        // 颜色经 MaterialPropertyBlock 的 _Color 传入（与原版 DrawBatch.Flush 给微粒上色的同一机制，
        // Mote 系 shader 均支持），所以同材质不同颜色不能共用分组，绘制键是 (材质, 颜色) 二元组。
        //
        // 键取材质而不是 def：材质现在按 ProjectileTrailDef 缓存（一种材质一张生成贴图、一个材质实例），
        // 于是"同材质 + 同颜色"的分组仍能把共用同一材质的多个 def 合并成一次绘制调用——
        // 比如文件贴图档下多个 def 若指向同一张贴图，就会命中同一个材质实例。
        readonly struct TrailMaterialKey : IEquatable<TrailMaterialKey>
        {
            internal readonly Material? material;
            internal readonly Color color;

            internal TrailMaterialKey(Material? material, Color color)
            {
                this.material = material;
                this.color = color;
            }

            public bool Equals(TrailMaterialKey other)
            {
                // 引用比较而不是 Unity 的 == 重载：这里的材质全部来自 MaterialPool / def 缓存，
                // 实例稳定且不会中途销毁，不需要那套"销毁对象视为 null"的语义，还省一次原生调用。
                return ReferenceEquals(material, other.material)
                    && color.r == other.color.r && color.g == other.color.g
                    && color.b == other.color.b && color.a == other.color.a;
            }

            public override bool Equals(object? obj)
            {
                return obj is TrailMaterialKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    // RuntimeHelpers 的标识哈希：不触发 Unity 侧的虚调用，比 material.GetHashCode() 便宜
                    int h = material != null ? RuntimeHelpers.GetHashCode(material) : 0;

                    return h
                        ^ ((color.r.GetHashCode() * 397) ^ (color.g.GetHashCode() * 31))
                        ^ ((color.b.GetHashCode() * 7) ^ (color.a.GetHashCode() * 3));
                }
            }
        }

        // ---------------- 合并网格分组 ----------------

        // 一组同（材质 + 颜色）的轨迹：几何逐帧重建进一张可复用网格，单次 DrawMesh 提交。
        class MeshGroup
        {
            internal Material? material;   // 创建分组时解析一次；null 表示该 def 无材质，整组跳过
            internal Color color = Color.white;
            internal readonly MaterialPropertyBlock props = new MaterialPropertyBlock();
            internal Mesh? mesh;          // 首帧惰性创建
            internal readonly List<Vector3> verts = new List<Vector3>();
            internal readonly List<Vector2> uvs = new List<Vector2>();
            internal readonly List<int> tris = new List<int>();
            internal Vector3 min;
            internal Vector3 max;
            internal bool touched;

            internal void Reset()
            {
                verts.Clear();
                uvs.Clear();
                tris.Clear();
                touched = false;
            }

            internal void Add(ProjectileTrailState trail)
            {
                if (material == null) return;

                int firstVert = verts.Count;
                trail.AppendGeometry(verts, uvs, tris);
                if (verts.Count == firstVert) return;   // 本帧该轨迹无可绘制内容

                // 包围盒直接由本帧实际写入的顶点求出：直线/弧线同一套，且紧贴所画内容。
                // 只影响 Unity 自身的视锥剔除——可见性已在外面逐条判过。
                Vector3 lo = verts[firstVert];
                Vector3 hi = lo;
                for (int i = firstVert + 1; i < verts.Count; i++)
                {
                    Vector3 v = verts[i];
                    if (v.x < lo.x) lo.x = v.x; else if (v.x > hi.x) hi.x = v.x;
                    if (v.y < lo.y) lo.y = v.y; else if (v.y > hi.y) hi.y = v.y;
                    if (v.z < lo.z) lo.z = v.z; else if (v.z > hi.z) hi.z = v.z;
                }

                if (!touched)
                {
                    touched = true;
                    min = lo;
                    max = hi;
                    used.Add(this);
                }
                else
                {
                    min = Vector3.Min(min, lo);
                    max = Vector3.Max(max, hi);
                }
            }

            internal void UploadAndDraw()
            {
                if (material == null) return;

                if (mesh == null)
                {
                    mesh = new Mesh { name = "GunplayProjectileTrailBatch" };
                    mesh.MarkDynamic();
                }

                mesh.Clear(true);
                mesh.SetVertices(verts, 0, verts.Count, MeshFlags);
                mesh.SetUVs(0, uvs, 0, uvs.Count, MeshFlags);
                mesh.SetTriangles(tris, 0, false);
                // bounds 只影响 Unity 自身的视锥剔除；外扩一格避免边缘轨迹被剔
                mesh.bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one);

                props.SetColor(ShaderPropertyIDs.Color, color);
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0, null, 0, props);
            }
        }

        static readonly Dictionary<TrailMaterialKey, MeshGroup> groups = new Dictionary<TrailMaterialKey, MeshGroup>();
        static readonly List<MeshGroup> used = new List<MeshGroup>();

        public static void Draw(List<ProjectileTrailState> trails)
        {
            if (trails.Count == 0) return;

            for (int i = 0; i < used.Count; i++) used[i].Reset();
            used.Clear();

            // 视野范围每帧只展开一次，避免在逐条剔除里反复访问 CellRect.Min / Max
            TrailViewRect view = new TrailViewRect(Find.CameraDriver.CurrentViewRect.ExpandedBy(1));

            for (int i = 0; i < trails.Count; i++)
            {
                ProjectileTrailState trail = trails[i];

                // 先剔除：这一步能淘汰绝大多数（在视野外）的轨迹，后面的工作才不会白做
                if (!trail.IsVisible(view)) continue;

                ProjectileTrailDef def = trail.def;
                if (def == null) continue;

                // 材质来源（运行时生成贴图 / def 自己的文件贴图）由 TrailMaterial 处理；
                // 绘制键 = (材质, 颜色)，同材质同颜色的轨迹合并进同一张网格、同一次绘制调用
                Material? material = TrailMaterial.For(def);
                TrailMaterialKey key = new TrailMaterialKey(material, trail.color);

                if (!groups.TryGetValue(key, out MeshGroup group))
                {
                    group = new MeshGroup { material = material, color = trail.color };
                    groups[key] = group;
                }
                group.Add(trail);
            }

            for (int i = 0; i < used.Count; i++) used[i].UploadAndDraw();
        }
    }
}
