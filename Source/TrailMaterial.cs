using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹材质的取用入口：把「用哪张贴图」这件事从 ProjectileTrailDef 里收口到这里。
    //
    //   * 默认（ProjectileTrailDef.useGeneratedTexture = true，全部内置 def 如此）：
    //     用运行时生成的贴图（TrailTexture），全部 def 共用同一个材质实例；
    //   * useGeneratedTexture = false：退回 def 自己的 graphicData 贴图（texPath），
    //     供想换自定义贴图的第三方 def 使用。
    //
    // 两种情况下颜色都不进材质：颜色经 MaterialPropertyBlock 的 _Color 逐组传入
    // （见 ProjectileTrailBatcher），所以一个材质能同时承载任意多种颜色。
    internal static class TrailMaterial
    {
        static Material? generated;
        static readonly Dictionary<ProjectileTrailDef, Material?> fileBased = new Dictionary<ProjectileTrailDef, Material?>();

        // 可能返回 null（def 既无生成贴图又无可用 graphicData）；调用方按 null 跳过整组。
        public static Material? For(ProjectileTrailDef def)
        {
            if (def.useGeneratedTexture) return generated ??= CreateGenerated();

            if (fileBased.TryGetValue(def, out Material? cached)) return cached;

            Material? mat = def.DrawMatSingle;
            // 与 DrawBatch.Flush 同样的前置条件：实例化要求材质开启 enableInstancing
            if (mat != null) mat.enableInstancing = true;

            fileBased[def] = mat;
            return mat;
        }

        // 生成贴图的材质：经 MaterialPool 取用（同请求复用同一实例），shader 与 XML 里的一致（Mote）。
        static Material CreateGenerated()
        {
            Material mat = MaterialPool.MatFrom(new MaterialRequest
            {
                mainTex = TrailTexture.Get(),
                shader = ShaderDatabase.Mote
            });

            mat.enableInstancing = true;
            return mat;
        }
    }
}
