using UnityEngine;
using Verse;

namespace Gunplay
{
    // 轨迹外观定义。
    //
    // 纯数据 Def：轨迹早已不再以 Thing 形式存在于地图上（见 ProjectileTrailState / MapComponent_ProjectileTrails），
    // 用不到 ThingDef 的任何图形/物理方法，继承 ThingDef 只会白白带进一堆无意义的 XML 项
    // （thingClass / category / tickerType / altitudeLayer / useHitPoints / neverMultiSelect ...）。
    public class ProjectileTrailDef : Def
    {
        public float trailWidth = -1;

        // 轨迹染色。贴图本体是纯白，颜色完全由该字段乘出。
        // 默认白 = 贴图原色。可在设置窗口的「轨迹颜色」中进一步覆写（存设置，见 GunplaySettings.trailColors）。
        public Color trailColor = Color.white;

        // 贴图来源：true = 运行时生成的轨迹贴图（TrailTexture，全部 def 共用一个材质实例）；
        // false = 用本 def graphicData 的 texPath 贴图（第三方 def 想换自定义贴图时用这一档）。
        // 默认 true：内置所有 def 都走生成贴图，Textures 下的 Guntrail.png 只作形状参考、不再被读取。
        public bool useGeneratedTexture = true;

        // useGeneratedTexture = false 时的贴图来源。普通 Def 没有 ThingDef 那套图形初始化，
        // 这里自己持有 graphicData，由 TrailMaterial 取出 Graphic.MatSingle 使用（缺失则无材质）。
        public GraphicData? graphicData;
    }
}
