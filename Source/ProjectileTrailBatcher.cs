using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace Gunplay
{
    // 把当前地图所有可见轨迹压到最少的绘制调用。
    //
    // 两条绘制路径，都按 trail def（材质）分桶，因此绘制调用数 = 本帧用到的材质种类数：
    //   * 直线轨迹（绝大多数）：网格固定为 MeshPool.plane10，逐条只提供 TRS 矩阵，
    //     用 Graphics.DrawMeshInstanced 一次提交 → 每帧不重建网格、不碰索引缓冲、不重算包围盒；
    //   * 弧线轨迹（手雷/炮弹等）：几何逐帧变化、无法用矩阵表达，合并进一张可复用网格后单次 DrawMesh。
    //
    // 依据：RimWorld 1.6 自己的微粒系统就在走这条实例化路径 —— Verse.DrawBatch.Flush 在
    // renderInstanced 分组上强制 material.enableInstancing = true 后调用
    // Graphics.DrawMeshInstanced(key.mesh, 0, key.material, matrices, count, ...)，
    // 且 Graphic_Fleck.AllowInstancing 恒为 true。
    //
    // 实测确认（A/B 往返各测过一轮）：轨迹能正常渲染，说明所用的 Mote 材质确实支持 GPU 实例化。
    // 注意：本项目里 0.1ms 量级的耗时读数不足以支撑精细的 A/B 结论（profiler 在该量级只有一档 0.1ms
    // 的量化，且不同时刻的在飞弹量不同），别用这些数字去追代码路径的差异。
    //
    // 刻意不做实例化失败回退：若材质不支持，DrawMeshInstanced 会直接报错而不是静默退化。
    internal static class ProjectileTrailBatcher
    {
        // Graphics.DrawMeshInstanced 单次提交的实例上限。Unity 硬限制为 1023
        // （matrices 数组长度超过 1023 会直接报错，与 count 无关），
        // RimWorld 自己的 DrawBatch.MaxCountPerBatch 用的也是同一个常量。
        // 超出时在 InstancedGroup.Add 里分批提交，对调用方透明。
        const int MaxInstancesPerCall = 1023;

        // 合并网格上传时跳过 Unity 的包围盒重算与索引校验（包围盒由我们在下面自行设定）
        const MeshUpdateFlags MeshFlags =
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices;

        // 实例化提交用的空属性块（与 DrawBatch.Flush 一致）
        static readonly MaterialPropertyBlock sharedProps = new MaterialPropertyBlock();

        // ---------------- 直线轨迹：按材质实例化 ----------------

        class InstancedGroup
        {
            internal Material material;   // 创建分组时解析一次；null 表示该 def 无材质，整组跳过
            internal readonly Matrix4x4[] matrices = new Matrix4x4[MaxInstancesPerCall];
            internal int count;
            internal bool touched;

            internal void Reset()
            {
                count = 0;
                touched = false;
            }

            internal void Add(ProjectileTrailState trail)
            {
                if (material == null) return;
                if (!trail.TryGetStraightMatrix(out Matrix4x4 matrix)) return;

                if (!touched)
                {
                    touched = true;
                    usedInstanced.Add(this);
                }

                // 超出单次上限：先把已积累的一批提交掉，再继续
                if (count >= MaxInstancesPerCall) Draw();

                matrices[count++] = matrix;
            }

            internal void Draw()
            {
                if (count == 0) return;
                Graphics.DrawMeshInstanced(MeshPool.plane10, 0, material, matrices, count,
                    sharedProps, ShadowCastingMode.Off, false, 0);
                count = 0;
            }
        }

        // ---------------- 弧线轨迹：按材质合并网格 ----------------

        class ArcGroup
        {
            internal Material material;
            internal Mesh mesh;
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

                int before = tris.Count;
                trail.AppendArcGeometry(verts, uvs, tris);
                if (tris.Count == before) return;   // 本帧该轨迹无可绘制内容

                trail.GetArcAabb(out Vector3 lo, out Vector3 hi);
                if (!touched)
                {
                    touched = true;
                    min = lo;
                    max = hi;
                    usedArc.Add(this);
                }
                else
                {
                    min = Vector3.Min(min, lo);
                    max = Vector3.Max(max, hi);
                }
            }

            internal void UploadAndDraw()
            {
                if (mesh == null)
                {
                    mesh = new Mesh { name = "GunplayProjectileTrailBatch" };
                    mesh.MarkDynamic();
                }

                mesh.Clear(true);
                mesh.SetVertices(verts, 0, verts.Count, MeshFlags);
                mesh.SetUVs(0, uvs, 0, uvs.Count, MeshFlags);
                mesh.SetTriangles(tris, 0, false);
                // bounds 只影响 Unity 自身的视锥剔除：内容已按视口逐条剔除过，收紧到并集即可
                mesh.bounds = new Bounds((min + max) * 0.5f, max - min + Vector3.one);

                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);
            }
        }

        static readonly Dictionary<ProjectileTrailDef, InstancedGroup> instancedGroups = new Dictionary<ProjectileTrailDef, InstancedGroup>();
        static readonly Dictionary<ProjectileTrailDef, ArcGroup> arcGroups = new Dictionary<ProjectileTrailDef, ArcGroup>();
        static readonly List<InstancedGroup> usedInstanced = new List<InstancedGroup>();
        static readonly List<ArcGroup> usedArc = new List<ArcGroup>();

        public static void Draw(List<ProjectileTrailState> trails)
        {
            if (trails.Count == 0) return;

            for (int i = 0; i < usedInstanced.Count; i++) usedInstanced[i].Reset();
            usedInstanced.Clear();
            for (int i = 0; i < usedArc.Count; i++) usedArc[i].Reset();
            usedArc.Clear();

            // 视野范围每帧只展开一次，避免在逐条剔除里反复访问 CellRect.Min / Max
            TrailViewRect view = new TrailViewRect(Find.CameraDriver.CurrentViewRect.ExpandedBy(1));

            for (int i = 0; i < trails.Count; i++)
            {
                ProjectileTrailState trail = trails[i];

                // 先剔除：这一步能淘汰绝大多数（在视野外）的轨迹，后面的工作才不会白做
                if (!trail.IsVisible(view)) continue;

                ProjectileTrailDef def = trail.def;
                if (def == null) continue;

                if (trail.IsArc)
                {
                    if (!arcGroups.TryGetValue(def, out ArcGroup arc))
                    {
                        arc = new ArcGroup { material = def.DrawMatSingle };
                        arcGroups[def] = arc;
                    }
                    arc.Add(trail);
                }
                else
                {
                    if (!instancedGroups.TryGetValue(def, out InstancedGroup inst))
                    {
                        Material mat = def.DrawMatSingle;
                        // 与 DrawBatch.Flush 同样的前置条件：实例化要求材质开启 enableInstancing
                        if (mat != null) mat.enableInstancing = true;
                        inst = new InstancedGroup { material = mat };
                        instancedGroups[def] = inst;
                    }
                    inst.Add(trail);
                }
            }

            for (int i = 0; i < usedArc.Count; i++) usedArc[i].UploadAndDraw();
            for (int i = 0; i < usedInstanced.Count; i++) usedInstanced[i].Draw();
        }
    }
}
