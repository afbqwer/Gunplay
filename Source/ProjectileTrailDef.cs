using Verse;

namespace Gunplay
{
    // 轨迹材质定义 —— 「材质类型」本体。
    //
    // 纯数据 Def：轨迹早已不再以 Thing 形式存在于地图上（见 ProjectileTrailState / MapComponent_ProjectileTrails），
    // 用不到 ThingDef 的任何图形/物理方法，继承 ThingDef 只会白白带进一堆无意义的 XML 项
    // （thingClass / category / tickerType / altitudeLayer / useHitPoints / neverMultiSelect ...）。
    //
    // 一个 ProjectileTrailDef = 一种材质（等宽、尾部收缩、尾部放大……都是它的参数取值，见 TrailMaterialParams）。
    // 本 def 只是**模板**：运行时真正生效的参数是 TrailMaterial.Effective(def) 返回的实例
    // （设置里的编辑快照优先，否则就是这里的 material）。因此新增材质类型不需要改代码，写个 XML 即可，
    // 第三方 mod 也能照做。
    public class ProjectileTrailDef : Def
    {
        // 材质参数模板（形状 / 宽度轮廓 / 颜色 / 宽度 / 长度 / 淡出）。XML 写成 <material> 块，
        // 没写的字段取 TrailMaterialParams 的字段默认值。
        public TrailMaterialParams material = new TrailMaterialParams();

        // material.useGeneratedTexture = false 时的贴图来源。普通 Def 没有 ThingDef 那套图形初始化，
        // 这里自己持有 graphicData，由 TrailMaterial 取出 Graphic.MatSingle 使用（缺失则无材质）。
        public GraphicData? graphicData;

        // XML 里的取值可能越界（手写 def、跨版本迁移、第三方 mod），统一在这里夹一次。
        // 贴图生成拿到越界值（尤其 topFade / flatCore 这类会做除法的参数）不会报错，
        // 只会静默画出垃圾像素，事后极难定位，所以在入口就夹住。
        // 设置里那份编辑快照由 TrailMaterialParams.ExposeData 负责夹取，两条路径口径一致。
        public override void PostLoad()
        {
            base.PostLoad();
            material?.Clamp();
        }
    }
}
