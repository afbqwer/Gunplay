using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹材质的统一入口，两件事：
    //
    //   1) 参数解析 —— 设置里的编辑快照 > def 的模板（见 TrailMaterialParams 的类注释）。
    //      「材质类型」就是 ProjectileTrailDef，尾部收缩 / 放大只是参数取值不同，
    //      所以这里不需要按类型分发；新增一种材质 = 写一个新的 ProjectileTrailDef。
    //
    //   2) 贴图与材质缓存 —— 每个 def 一份。旧实现只有一个全局生成材质，
    //      现在按 def 分开，于是不同 def 可以有不同的贴图参数，且互不干扰。
    //
    // 颜色始终不进材质：颜色经 MaterialPropertyBlock 的 _Color 逐组传入（见 ProjectileTrailBatcher），
    // 所以一个材质能同时承载任意多种颜色，绘制批次键仍是 (材质, 颜色)。
    internal static class TrailMaterial
    {
        class Entry
        {
            // 本模块生成的贴图：惰性创建、永不销毁，参数改动时就地重填（换实例会牵动材质与旧贴图的销毁）
            internal Texture2D? generatedTexture;

            // 当前生效材质（来源可能是生成贴图，也可能是 def 自己的文件贴图）
            internal Material? material;
        }

        static readonly Dictionary<ProjectileTrailDef, Entry> cache = new Dictionary<ProjectileTrailDef, Entry>();

        // def 解析失败（投射物没配材质）时的兜底参数。出厂默认值，且永远不会被写——
        // 它不参与设置快照，所以任何调用方拿着它改字段都不会污染别处。
        static readonly TrailMaterialParams fallback = new TrailMaterialParams();

        // ---------------- 参数解析 ----------------

        // 该 def 当前生效的材质参数。传 null 返回兜底默认值（轨迹本身无颜色时按白绘制）。
        public static TrailMaterialParams Effective(ProjectileTrailDef? def)
        {
            if (def == null) return fallback;

            return GunplaySettings.trailMaterialEdits.TryGetValue(def.defName, out TrailMaterialParams? edit)
                ? edit
                : def.material;
        }

        // 该 def 是否已被窗口编辑过（设置里存在快照）。窗口据此显示"已改写"与恢复按钮。
        public static bool HasEdit(ProjectileTrailDef? def)
        {
            return def != null && GunplaySettings.trailMaterialEdits.ContainsKey(def.defName);
        }

        // 取可写快照：首次编辑时从 def 模板克隆一份登记进设置，之后这份就是该 def 的生效参数。
        // 刻意不在开窗时就克隆——只看不改不应该让该 def 从此脱离 XML 默认值。
        public static TrailMaterialParams EnsureEdit(ProjectileTrailDef def)
        {
            if (GunplaySettings.trailMaterialEdits.TryGetValue(def.defName, out TrailMaterialParams? edit)) return edit;

            TrailMaterialParams created = new TrailMaterialParams();
            created.CopyFrom(def.material);
            GunplaySettings.trailMaterialEdits[def.defName] = created;
            return created;
        }

        // 丢弃快照 = 回到 def 模板（窗口的「重置为 Def 模板」）。顺带重建该 def 的材质，因为来源可能不同。
        public static void ResetEdit(ProjectileTrailDef def)
        {
            if (GunplaySettings.trailMaterialEdits.Remove(def.defName)) Invalidate(def);
        }

        // ---------------- 贴图与材质 ----------------

        // 可能返回 null（文件贴图档但 def 没有可用 graphicData）；调用方按 null 跳过整组。
        public static Material? For(ProjectileTrailDef def)
        {
            return Get(def).material;
        }

        // 预览窗口用：生成档返回本模块生成的贴图，文件贴图档返回 def 自己贴图的 mainTexture（可能为 null）。
        public static Texture2D? TextureFor(ProjectileTrailDef def)
        {
            Material? mat = Get(def).material;
            return mat != null ? mat.mainTexture as Texture2D : null;
        }

        // 参数（或来源）改动后重建该 def 的材质。生成档走"就地重填同一张贴图"，
        // 因此已在飞的轨迹也会立刻跟着变（与旧版全局参数改动立即生效的行为一致）。
        public static void Invalidate(ProjectileTrailDef def)
        {
            if (cache.TryGetValue(def, out Entry? entry)) Build(def, entry);
        }

        // 读档后统一重填：设置里的快照可能整体换了实例。
        // 刻意逐条重填而不是 cache.Clear()——生成的贴图是本模块持有的纹理资源，
        // 丢掉引用不会释放它（Unity 纹理要 Destroy），沿用同一张一直重填最省事。
        public static void InvalidateAll()
        {
            foreach (KeyValuePair<ProjectileTrailDef, Entry> pair in cache)
            {
                Build(pair.Key, pair.Value);
            }
        }

        static Entry Get(ProjectileTrailDef def)
        {
            if (cache.TryGetValue(def, out Entry? entry)) return entry;

            entry = new Entry();
            cache[def] = entry;
            Build(def, entry);
            return entry;
        }

        static void Build(ProjectileTrailDef def, Entry entry)
        {
            TrailMaterialParams p = Effective(def);

            if (p.useGeneratedTexture)
            {
                entry.generatedTexture ??= TrailTexture.Create("GunplayTrailGenerated_" + def.defName);
                TrailTexture.Fill(entry.generatedTexture, p);

                // 经 MaterialPool 取用（同请求复用同一实例），shader 与 XML 里的一致（Mote）
                Material mat = MaterialPool.MatFrom(new MaterialRequest
                {
                    mainTex = entry.generatedTexture,
                    shader = ShaderDatabase.Mote
                });

                mat.enableInstancing = true;
                entry.material = mat;
                return;
            }

            // 文件贴图档：形状参数与宽度轮廓都不参与——贴图是别人画好的，没法按参数重写像素。
            Material? file = def.graphicData?.Graphic?.MatSingle;
            if (file != null)
            {
                // 与 DrawBatch.Flush 同样的前置条件：实例化要求材质开启 enableInstancing
                file.enableInstancing = true;

                // 贴图会被拉成细长条，需 Clamp，否则边缘重复采样出接缝。
                if (file.mainTexture != null) file.mainTexture.wrapMode = TextureWrapMode.Clamp;
            }

            entry.material = file;
        }
    }
}
