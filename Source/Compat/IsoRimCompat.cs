using System;
using System.Reflection;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // IsoRim Waylith（3D 视角）软兼容层。
    //
    // 为什么需要它：IsoRim 的 3D 视角靠两件事成立 ——
    //   1) 视图矩阵把世界 Y 压扁（worldToCamera = view * Scale(1, Squash, 1)，见 IsoRim.View3D.ApplyCamera），
    //      所以「真实高度 h 格」的东西必须画在 y = h / Squash 处才会显示在高度 h；
    //   2) 原版那些直接调 Graphics.DrawMesh 的绘制被转写进 IsoRim.DrawHook，由它套上相机变换。
    // 第 2 步只转写 Assembly-CSharp 里的调用点（Patches.RedirectDrawCalls 的固定类型清单），
    // 本 mod 自己的批量绘制不会被改道：几何会带着「地图平面坐标」直接进渲染，
    // 于是在 3D 视角里整条轨迹塌在地面上，也不跟随弹体的高度（IsoRim 把弹体画在枪口→目标的高度上）。
    //
    // 本类只补这两处差异，不改变任何 2D 行为：
    //   * 帧初读一次相机状态（是否就绪、俯仰/偏航、Squash），之后全场只读静态字段；
    //   * 几何按「真实高度（格）」生成，绘制时统一乘 Scale(1, 1/Squash, 1)（见 DrawMatrix），
    //     于是顶点本身不需要任何逐顶点缩放；
    //   * 条带的宽度方向垂直于这一段飞行方向与视线（RibbonSide），让它始终正对相机 ——
    //     原先平铺在地面（水平四边形）的画法在俯角 30° 时会被压扁到一半，且与弹体不共面。
    //
    // 性能口径（无 IsoRim 时）：Present 是一次静态只读 bool 的初始化结果，
    // 每帧唯一的开销就是 ProjectileTrailBatcher.Draw 开头对 BeginFrame 的一次调用 + 一次分支，
    // 之后所有 3D 分支都不会进入。反射只在静态构造里解析一次。
    //
    // 健壮性口径（IsoRim 仍在开发中）：
    //   * 任何反射成员缺失 → 视为不兼容，整体退回 2D 口径，只打一条警告；
    //   * 运行期任何异常 → 该帧退回 2D 口径；连续出错达到阈值后本会话彻底停用（避免每帧抛异常变成性能灾难）；
    //   * 契约面只读 IsoRim.Api。唯一的例外是 SkyStrikes 的两个公开常量（迫击炮弹的高空弧，见 ResolveLobbed），
    //     它属于可缺省的增强：读不到只是炮弹轨迹矮一截，不影响其余部分；
    //   * 数值一律夹取，防止 NaN / 越界值污染整批几何（几何一脏，整张合并网格都画不出来）。
    internal static class IsoRimCompat
    {
        // ---------------- 反射解析（静态构造，一次） ----------------

        const string ApiTypeName = "IsoRim.Api";
        const string ApiAssemblyName = "IsoRim";

        // IsoRim 是否在场且 Api 表面完整。false 时下面所有入口都在第一行返回。
        static readonly bool present;

        // Api 成员绑定。签名与 IsoRim.Api 的同名成员一致；绑定失败即为 null，present 也为 false。
        static readonly Func<bool>? viewReady;
        static readonly Func<float>? viewPitch;
        static readonly Func<float>? viewYaw;
        static readonly Func<float>? heightSquash;
        static readonly Func<Projectile, Vector3, float>? projectileHeight;

        // 迫击炮类炮弹的弹道（可选，见 ResolveLobbed）。null / 0 表示读不到，按「没有这条弧」处理。
        static FieldInfo? skyEnabled;
        static float shellArc;
        static float shellArcMax;

        static IsoRimCompat()
        {
            try
            {
                Type? api = ResolveApiType();
                if (api == null) return;

                // 只解析本类真正用到的成员。Api.Version 会随成员增加而递增，但这几个都属于
                // 「读写视角状态」这一组，自 Version 1 起就存在，所以不做版本门槛——
                // 逐个成员判空更能表达「缺哪个就用不了」，也顺便兼容把 Version 忘记加一的实现。
                viewReady = Getter<bool>(api, "ViewReady");
                viewPitch = Getter<float>(api, "ViewPitch");
                viewYaw = Getter<float>(api, "ViewYaw");
                heightSquash = Getter<float>(api, "HeightSquash");
                projectileHeight = Bind<Func<Projectile, Vector3, float>>(
                    api.GetMethod("ProjectileHeight", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(Projectile), typeof(Vector3) }, null));

                if (viewReady == null || viewPitch == null || viewYaw == null || heightSquash == null || projectileHeight == null)
                {
                    Log.Warning("[Gunplay] IsoRim is loaded, but its Api is missing the view members this mod needs; "
                        + "projectile trails keep the flat (2D) drawing path.");
                    return;
                }

                // 迫击炮类炮弹的弹道（可选增强，见 LobbedApex）。不属于 Api 的契约面，
                // 因此整段都按「有则用、没有就算了」处理：任何成员缺失都只是少一个弧高。
                ResolveLobbed(api.Assembly);

                present = true;
            }
            catch (Exception e)
            {
                // 静态构造抛异常会变成 TypeInitializationException，把每一次访问都炸掉：
                // 这里必须吞掉，最坏结果只是没有 3D 兼容。
                Log.Warning("[Gunplay] IsoRim compatibility could not start; projectile trails keep the flat (2D) path. " + e.Message);
            }
        }

        // IsoRim 画迫击炮/炮弹时不用 def 里的 arcHeightFactor，而是自己拉一条更高的抛物线
        // （Projectiles.DrawLobbed：apex = min(ShellArc × 平飞距离, ShellArcMax)）。Api 没暴露它，
        // 只能读 SkyStrikes 上的两个公开常量与开关；轨迹不跟这条弧就会明显低于弹体。
        // 拿不到时 lobbed 保持 false —— 轨迹只是矮一点，位置仍是准的。
        static void ResolveLobbed(Assembly assembly)
        {
            Type? sky = assembly.GetType("IsoRim.SkyStrikes", false);
            if (sky == null) return;

            skyEnabled = sky.GetField("Enabled", BindingFlags.Public | BindingFlags.Static);
            shellArc = ReadConst(sky.GetField("ShellArc", BindingFlags.Public | BindingFlags.Static), 0f);
            shellArcMax = ReadConst(sky.GetField("ShellArcMax", BindingFlags.Public | BindingFlags.Static), 0f);

            if (skyEnabled == null || shellArc <= 0f || shellArcMax <= 0f)
            {
                skyEnabled = null;
                Log.Warning("[Gunplay] IsoRim's shell arc (SkyStrikes) is not readable; mortar trails in the 3D view "
                    + "will be drawn lower than their shells.");
            }
        }

        // 常量（const）走 GetRawConstantValue，普通静态字段走 GetValue。
        static float ReadConst(FieldInfo? field, float fallback)
        {
            if (field == null) return fallback;
            try
            {
                object? value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
                return value is float f ? f : fallback;
            }
            catch (Exception e)
            {
                Log.Warning("[Gunplay] IsoRim compatibility: cannot read '" + field.Name + "'. " + e.Message);
                return fallback;
            }
        }

        // 按名字找 IsoRim 程序集里的 Api 类型。不触发加载（IsoRim 没装/没启用就不在 AppDomain 里）。
        static Type? ResolveApiType()
        {
            Assembly[] all = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < all.Length; i++)
            {
                Assembly asm = all[i];
                if (asm == null) continue;
                if (!string.Equals(asm.GetName().Name, ApiAssemblyName, StringComparison.Ordinal)) continue;
                return asm.GetType(ApiTypeName, false);
            }
            return null;
        }

        static Func<T>? Getter<T>(Type api, string name)
        {
            PropertyInfo? prop = api.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return prop == null ? null : Bind<Func<T>>(prop.GetGetMethod());
        }

        // 静态方法 → 无装箱委托。用 Delegate.CreateDelegate 而不是反射 Invoke：
        // 这两个值每帧都要读，Invoke 的装箱与数组分配在逐条轨迹的路径上是纯浪费。
        static T? Bind<T>(MethodInfo? method) where T : Delegate
        {
            if (method == null) return null;
            try
            {
                return (T)Delegate.CreateDelegate(typeof(T), method);
            }
            catch (Exception e)
            {
                Log.Warning("[Gunplay] IsoRim compatibility: cannot bind '" + method.Name + "'; trails keep the flat path. " + e.Message);
                return null;
            }
        }

        // ---------------- 每帧状态 ----------------

        // 本帧是否走 3D 口径。每帧由 BeginFrame 定一次，之后全场只读。
        static bool frame3D;
        // 1 / Squash：把真实高度换算成 IsoRim 绘制空间的系数（见 DrawMatrix）。
        static float invSquash = 1f;
        // 相机前向（真实空间）。由 Api.ViewPitch / ViewYaw 复现 IsoRim.View3D 的 look 旋转。
        static Vector3 forward = Vector3.forward;
        // 本帧 IsoRim 是否把迫击炮画成高空抛物线（SkyStrikes.Enabled，每帧读一次）。
        static bool skyStrikesOn;

        // 失败处理：偶发的单帧失败不值得关掉整个兼容（下一帧可能就好了），
        // 但连续出错意味着 Api 语义变了，继续跑只会每帧付一次异常展开的代价。
        // 以「连续出错帧数」而不是「出错次数」计量：一帧里几十条轨迹一起失败与一条失败是同一件事。
        const int MaxBadFrames = 60;   // 约一秒（60fps）
        static bool erroredThisFrame;
        static int badFrames;
        static bool failed;
        static bool loggedFirstError;

        // IsoRim 是否被检测到（设置界面据此决定要不要显示兼容开关）。
        internal static bool Present => present;

        // 本帧的 3D 口径是否可用。false 时轨迹完全走原来的 2D 路径，逐像素不变。
        internal static bool Frame3D => frame3D;

        // 绘制矩阵：几何按真实高度（格）写，这里统一乘 1/Squash 落到 IsoRim 的绘制空间。
        // 之所以放在矩阵而不是逐顶点相乘：Squash 每帧一个全局值，
        // 一条轨迹几十上百个顶点时逐顶点乘法没有意义，矩阵还能让 mesh.bounds 保持真实尺度。
        internal static Matrix4x4 DrawMatrix => frame3D
            ? Matrix4x4.Scale(new Vector3(1f, invSquash, 1f))
            : Matrix4x4.identity;

        // 沿视线把整条轨迹推远一点，避免和贴在同一位置的弹体逐帧闪烁（正交投影下屏幕位置不变）。
        internal static Vector3 DepthPush => frame3D ? forward * 0.01f : Vector3.zero;

        // 每帧一次。IsoRim 缺席时只花一次静态 bool 判断。
        internal static void BeginFrame()
        {
            // 先结算上一帧：连续出错到阈值就本会话停用，避免每帧一次异常展开。
            // 注意错误只在「下一帧」生效 —— 本帧的几何与绘制矩阵必须用同一套口径，
            // 否则会出现「按真实高度生成、却用单位矩阵绘制」的中途切换。
            if (erroredThisFrame)
            {
                erroredThisFrame = false;
                if (++badFrames >= MaxBadFrames)
                {
                    failed = true;
                    Log.Error("[Gunplay] IsoRim compatibility disabled for this session: the 3D view kept failing for "
                        + badFrames + " frames. Projectile trails keep the flat (2D) drawing path.");
                }
            }
            else
            {
                badFrames = 0;
            }

            frame3D = false;
            if (!present || failed) return;
            // 玩家可以关掉（排查兼容层自身的问题，或单纯不喜欢 3D 里的外观）
            if (!GunplaySettings.enableIsoRim3D) return;

            try
            {
                if (viewReady == null || !viewReady()) return;

                float squash = heightSquash!();
                // NaN 会穿过所有比较，所以用 !(x > y) 的写法；Squash 的正常量级是 0.002~0.004。
                if (!(squash > 0.0001f) || squash > 0.5f) return;
                invSquash = 1f / squash;

                // 相机前向与 IsoRim.View3D.ApplyCamera 完全一致：look = Euler(pitch, yaw, 0)
                forward = Quaternion.Euler(viewPitch!(), viewYaw!(), 0f) * Vector3.forward;

                skyStrikesOn = ReadSkyStrikes();
                frame3D = true;
            }
            catch (Exception e)
            {
                NoteError("reading the 3D view's camera", e);
            }
        }

        static bool ReadSkyStrikes()
        {
            FieldInfo? field = skyEnabled;
            if (field == null) return false;
            try
            {
                return field.GetValue(null) is bool on && on;
            }
            catch (Exception e)
            {
                // 读不到就当作关闭：只是炮弹的轨迹矮一截，不值得每帧抛异常
                skyEnabled = null;
                Log.Warning("[Gunplay] IsoRim compatibility: cannot read SkyStrikes.Enabled; mortar trails will be drawn lower. " + e.Message);
                return false;
            }
        }

        // ---------------- 供几何生成调用 ----------------

        // 该投射物在 IsoRim 3D 下被画在多高（格，含枪口/目标高度与地板高度）。
        // 取不到时返回 fallback（调用方传自己的 altitude），保证几何始终有效。
        internal static float HeightOf(Projectile? p, Vector3 at, float fallback)
        {
            if (!frame3D || p == null || projectileHeight == null) return fallback;

            try
            {
                float h = projectileHeight(p, at);
                // 越界值（含 NaN）会让整张合并网格的包围盒变成无穷大；宁可退回弹体所在层。
                return h > -4f && h < 64f ? h : fallback;
            }
            catch (Exception e)
            {
                NoteError("asking IsoRim how high a projectile is drawn", e);
                return fallback;
            }
        }

        // 迫击炮/炮弹在 IsoRim 下的弧顶高度（格）。取不到返回 false —— 调用方退回自己的解析弧高。
        // flatLength 是发射点到落点的平飞距离。
        internal static bool LobbedApex(float flatLength, out float apex)
        {
            apex = 0f;
            if (!frame3D || skyEnabled == null || !skyStrikesOn || !(flatLength > 0.01f)) return false;

            apex = Mathf.Min(shellArc * flatLength, shellArcMax);
            return apex > 0.01f;
        }

        // 条带的宽度方向：垂直于这一段飞行方向、且垂直于视线，画出来是一条始终正对相机的带子。
        // 两条轴几乎共线时（正对着相机飞出去的弹）叉积退化，退回「垂直于飞行方向且水平」那一支，
        // 仍然是一条可辨的带子，而不是零宽度的线。
        //
        // 退化支的手性必须与主支一致（用 cross(up, dir) 而不是 cross(dir, up)）：几何生成那边靠这个
        // 方向定三角形绕序，符号一翻，那一段四边形就成了背面被剔除（见 ProjectileTrailState 的弧线绕序说明）。
        internal static Vector3 RibbonSide(Vector3 dir)
        {
            Vector3 side = Vector3.Cross(dir, forward);
            float m = side.sqrMagnitude;
            if (m > 1e-6f) return side * (1f / Mathf.Sqrt(m));

            side = Vector3.Cross(Vector3.up, dir);
            m = side.sqrMagnitude;
            if (m > 1e-6f) return side * (1f / Mathf.Sqrt(m));

            return Vector3.right;
        }

        // ---------------- 失败处理 ----------------

        static void NoteError(string what, Exception e)
        {
            erroredThisFrame = true;
            if (loggedFirstError) return;

            // 第一次如实打出（带堆栈），之后只在停用时再报一次——同一个错误每帧重打会刷爆日志。
            loggedFirstError = true;
            Log.Warning("[Gunplay] IsoRim compatibility: " + what + " failed; trails fall back to the flat path. " + e);
        }
    }
}
