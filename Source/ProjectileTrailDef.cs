using UnityEngine;
using Verse;

namespace Gunplay
{
    public class ProjectileTrailDef : ThingDef
    {
        public float trailWidth = -1;

        // 轨迹染色。贴图本体是纯白，颜色完全由该字段乘出。
        // 默认白 = 贴图原色。可在设置窗口的「轨迹颜色」中进一步覆写（存设置，见 GunplaySettings.trailColors）。
        public Color trailColor = Color.white;

        // 贴图来源：true = 运行时生成的轨迹贴图（TrailTexture，全部 def 共用一个材质实例）；
        // false = 用本 def graphicData 的 texPath 贴图（第三方 def 想换自定义贴图时用这一档）。
        // 默认 true：内置所有 def 都走生成贴图，Textures 下的 Guntrail.png 只作形状参考、不再被读取。
        public bool useGeneratedTexture = true;
    }
}
