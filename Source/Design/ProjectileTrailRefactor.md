# Gunplay 轨迹系统重构设计：全局收集 + 批量绘制

> 状态：**待评审**
> 范围：投射物轨迹（`ProjectileTrail`）的生命周期与绘制路径
> 目标版本：RimWorld 1.6

---

## 1. 背景

当前每条投射物轨迹都是一个独立的 `Thing`（`ProjectileTrail : ThingWithComps`），由发射补丁
`GenSpawn.Spawn` 出来，自己 tick、自己 `DrawAt`。在弹幕量大时暴露出三个问题：

1. **不在同一批次**：N 条轨迹 = N 个 Thing = N 次 `Graphics.DrawMesh`，无法合批。
2. **Thing 生命周期开销**：Spwan/Destroy、进 `DynamicDrawManager.drawThings`、每 tick、以及
   `ExposeData` 把十几个字段写进存档。
3. **被 Thing 视野剔除影响**：剔除判定用的是轨迹**头部所在格**，导致"头飞出屏幕、尾还在屏幕内"
   的轨迹被整条剔除。

本次重构把轨迹从"Thing 集合"改为"全局（每地图）收集 + 单次批量绘制"。

---

## 2. 现状

### 2.1 现行架构

```
PatchProjectileLaunch.Postfix / PatchCECompat.SpawnTrailPostfix
        │  GenSpawn.Spawn(prop.trail, origin, map)
        ▼
ProjectileTrail : ThingWithComps            ← 每发一个 Thing
   Tick()    : 读投射物位置 → ApplyPosition / 过期销毁
   DrawAt()  : 直线 → Graphics.DrawMesh(plane10, TRS, mat)
               弧线 → Graphics.DrawMesh(arcMesh, identity, mat)   ← 每帧重建顶点
   ExposeData: 序列化全部轨迹状态
        │
        ▼
PatchProjectileImpact / PatchCECompat.ImpactPrefix
   TryGetTrail(proj) → CaptureFinalPosition(...)    ← 投射物→轨迹 查表（ConditionalWeakTable）
```

### 2.2 三个缺陷的根因（已核实）

| 缺陷 | 根因 |
|---|---|
| 不在同批 | 每条轨迹 Independent Thing，各自一次 `Graphics.DrawMesh`，无合并 |
| 生命周期开销 | Spawn/Destroy + `drawThings` 注册 + 每 tick + 每帧 `ExposeData` 字段 |
| 视野剔除误杀 | `ComputeCulledThings` 中非 Pawn 的剔除格取 `thing.Position`（轨迹头格），`coarseBounds = OccupiedDrawRect()` 仅 1 格；`CullJob` 用 `!drawOffscreen && !viewRect.Overlaps(coarseBounds)` 判定 |

### 2.3 已核实的 RimWorld 1.6 渲染事实

以下均由反编译 `Assembly-CSharp.dll` 得到，是本设计的地基。

**（a）每帧绘制阶段的真实顺序**（`Verse.Map.MapUpdate`，仅在 `Find.CurrentMap == this` 时执行）：

```
DrawMapMesh()                       ← global 层 + section 层（地形/建筑/屋顶/光照…）
dynamicDrawManager.DrawDynamicThings()   ← 所有 Thing（不做高度层排序）
gameConditionManager.GameConditionManagerDraw()
MapEdgeClipDrawer.DrawClippers()
designationManager.DrawDesignations()
overlayDrawer.DrawAllOverlays()
temporaryThingDrawer.Draw()
flecks.FleckManagerDraw()           ← 微粒/火花/烟
...
MapComponentUtility.MapComponentUpdate(this)   ← 每帧最末
```

**（b）动态 Thing 不做高度层排序。** `DynamicDrawManager.RegisterDrawable` 就是 `drawThings.Add(t)`
（要求 `def.drawerType != DrawerType.None`），没有任何排序步骤。

**（c）图层顺序由 "Y 深度" 承担。** `Thing.DrawPos.y = altitudeLayer.AltitudeFor()`，
而 `Altitudes` 静态构造为 `Alts[i] = i * 0.36585367f`。相机正交俯视、沿 -Y 观察 → **世界 Y 即深度**，
深度测试完成跨层排序。

实测层值：

| 层 | 索引 | Y | 层 | 索引 | Y |
|---|---|---|---|---|---|
| MoteLow | 12 | 4.39 | **Projectile** | **22** | **8.05** |
| Building | 15 | 5.49 | Pawn | 23 | 8.41 |
| Item | 18 | 6.59 | MoteOverhead | 28 | 10.24 |

> 注意：**Pawn(23) 在 Projectile(22) 之前**，即投射物本就被 Pawn 遮挡。

**（d）`MapComponent` 自动创建。** `Map.FillComponents()` 会遍历
`typeof(MapComponent).AllSubclassesNonAbstract()`，对每个「非抽象、非 `CustomMapComponent` 子类、
当前地图尚无该组件」的类型执行 `Activator.CreateInstance(type, map)`。
→ **自定义 MapComponent 无需任何补丁即可在每张地图自动创建**，并自动随
`MapComponentUtility` 获得 `MapComponentTick()` / `MapComponentUpdate()` / `ExposeData()` 回调。

**（e）`drawOffscreen` 语义。** `CullJob.Execute` 判定
`if (!value.drawOffscreen && !viewRect.Overlaps(value.coarseBounds))` → 不绘制。
即 `def.drawOffscreen = true` 可完全跳过视野矩形剔除（仅方案 A 需要）。

---

## 3. 目标与非目标

**目标**
- 轨迹不再是"每发一个 Thing"；改为每地图一个集合 + 每帧合并网格批量绘制。
- 消除缺陷 1/2/3。
- 视觉与现有一致：直线/弧线、拖尾长度、宽度模式与系数、淡出、CE 两种模式、雾中隐藏。
- 不吃掉现有配置能力（XML `ProjectileTrailDef`、`ProjectilePropDef` 映射）。

**非目标**
- 不改配置体系、不改速度缩放、不改命中特效/音效。
- 不做轨迹的存档持久化（见 §5.5）。

---

## 4. 目标架构

### 4.1 组件

| 组件 | 类型 | 职责 |
|---|---|---|
| `ProjectileTrailState` | 普通 C# 类 | 单条轨迹的全部状态与几何生成（现 `ProjectileTrail` 的逻辑平移，去掉 Thing 依赖） |
| `MapComponent_ProjectileTrails` | `MapComponent` | 每地图集合：持有 `List<ProjectileTrailState>` 与 `Dictionary<Thing, ProjectileTrailState>`；`MapComponentTick()` 更新所有轨迹；对外 `Register / TryGet / CaptureFinalPosition` |
| `ProjectileTrailBatcher` | 静态类 | 按材质分组、逐条剔除、构建合并网格、单次 `Graphics.DrawMesh` |
| `ProjectileTrail`（保留） | `ThingWithComps` | **空壳**，仅为旧存档兼容而保留，不再 spawn、不再绘制（见 §5.6） |

### 4.2 数据流

```
发射： PatchProjectileLaunch.Postfix ─┐
      PatchCECompat.SpawnTrailPostfix ┴→ MapComponent_ProjectileTrails.Register(proj, dest, equip, def)

每 tick：MapComponentTick() → 遍历 List → state.Tick()（读投射物实时位置 / 过期回收）

每帧：  PatchDynamicDrawTrails.Postfix(DrawDynamicThings 之后)
          → GetComponent<MapComponent_ProjectileTrails>() → ProjectileTrailBatcher.Draw(map, list)

命中： PatchProjectileImpact.Prefix ─┐
      PatchCECompat.ImpactPrefix     ┴→ TryGet(proj) → state.CaptureFinalPosition(...)
```

### 4.3 文件

```
Source/
  ProjectileTrailState.cs          （新增）
  MapComponent_ProjectileTrails.cs （新增）
  ProjectileTrailBatcher.cs        （新增）
  ProjectileTrail.cs               （保留为空壳）
  Patch/PatchDynamicDrawTrails.cs  （新增绘制补丁）
  Patch/PatchProjectile.cs         （改 Register / TryGet 调用点）
  Patch/PatchCECompat.cs           （改 Register / TryGet 调用点）
  GunplaySetup.cs                  （注册绘制补丁，带空引用保护）
```

---

## 5. 关键设计

### 5.1 绘制钩子 ★ 待确认

**推荐：Postfix 挂在 `DynamicDrawManager.DrawDynamicThings` 之后。**

得到的位置是"所有 Thing 之后、标记/覆盖层/微粒之前"：

```
DrawMapMesh → DrawDynamicThings → 【轨迹】 → 标记 → 覆盖层 → 临时Thing → 微粒Fleck
```

- 轨迹 Y 落在 Projectile 深度（8.05）→ 跨层关系（Pawn 遮挡、盖住建筑/物品）由深度测试自动正确。
- 画在 fleck / 标记 / 覆盖层之前 → 枪口烟、火花、标记、区域覆盖层正确地盖在轨迹之上。
- 失败模式：`AccessTools.Method` 取不到目标就 log 并禁用轨迹，**不抛异常、不崩游戏**。

代价：多一个 Harmony 补丁挂点（public 方法，但不能保证跨版本不变）。

**备选 A：单例渲染 Thing（零补丁）。**
每地图一个 `ProjectileTrailRenderer` Thing，`altitudeLayer=Projectile`、
`drawOffscreen=true`、`seeThroughFog=true`，在 `DynamicDrawPhase(Draw)` 里绘制同一份集合。
- 优点：不写任何绘制补丁，只依赖"Thing 会被画"这个最稳的契约。
- 代价：每地图遗留 1 个空 Thing（进 `listerThings`、进存档、会被"遍历所有 Thing"的逻辑扫到）。
- 另外需要：`def.drawerType != DrawerType.None`（否则 `RegisterDrawable` 不注册）。

**何时选备选 A**：如果对"绝对不增加新的补丁挂点"有硬性要求。

### 5.2 批量绘制

- 按 **材质分组**（同 `ProjectileTrailDef` 的 `DrawMatSingle` 为一组）。
- 每组维护一个可复用的合并 `Mesh`（`MarkDynamic()`），每帧：
  1. `mesh.Clear()`；
  2. 把该组所有**可见**轨迹的顶点/UV/三角形追加进世界坐标缓冲；
  3. `SetVertices / SetUVs / SetTriangles`；`mesh.bounds = 可见轨迹包围盒并集`（复用剔除阶段的结果，零额外开销）；
  4. `Graphics.DrawMesh(mergedMesh, Matrix4x4.identity, mat, 0)`。
- **直线轨迹**：把 `MeshPool.plane10` 的 4 个角经 `Matrix4x4.TRS(drawPos, LookRotation(dir), (width,1,len))`
  变换后追加（世界坐标）。
- **弧线轨迹**：沿用现有 ribbon 顶点（本就是世界坐标），直接追加。
- 结果：绘制调用数 = **材质种类数**（通常 1~2），与在飞轨迹数量无关。

缓冲区复用：每组的 `List<Vector3>/List<Vector2>/List<int>` 静态持有，避免每帧 GC。

### 5.3 剔除（自实现）

逐条判定，判定口径对齐 §2.2 的缺陷：

- 计算该轨迹的**线段/弧线包围盒**（沿用现有 `ComputeAnalyticArcBounds` / `ComputeSampledArcBounds` 思路，
  但对直线轨迹改用"首尾点 + 半宽"扩张，而不是头部格）。
- 与 `Find.CameraDriver.CurrentViewRect`（适当外扩）求交；不相交则跳过。
- fog 判定：**已移除**（战争迷雾本身会盖住轨迹）。

> 关键修复：剔除基于**整条轨迹的包围盒**，而不是头部所在格，因此"头出屏、尾在屏"不再被整条剔除。

### 5.4 深度微调（同层平局）

轨迹与投射物同处 Projectile 深度（Y=8.05），深度测试无法区分，平局由绘制顺序/`ZTest` 决定。
由于我们自建顶点，直接给轨迹 Y 加一个极小偏移：

```
trailY = def.altitudeLayer.AltitudeFor() + TrailDepthEpsilon   // 例如 -0.002f
```

- epsilon 远小于层间距 0.366，不会跨层。
- 方向决定"轨迹在弹体之前/之后"，属于**待确认项**（§8）。

### 5.5 存档

- **轨迹状态不序列化**。`MapComponent_ProjectileTrails.ExposeData()` 留空。
- 读档后不重建在飞投射物的轨迹（已确认选择"不重建"）：读档瞬间空中的那几发没有拖尾，
  之后新发射的投射物正常。
- 好处：彻底去掉每 tick 的序列化开销与存档体积。

### 5.6 Def 与存活兼容

- `ProjectileTrailDef` **保持 `: ThingDef`**，XML 零改动；`graphicData` / `DrawMatSingle` 继续可用；
  `altitudeLayer` 作为轨迹 Y 的来源（`def.altitudeLayer.AltitudeFor()`）。
- `GunplaySetup` 里对每个 trail def 设置 `wrapMode = Clamp` 的逻辑保留。
- **旧存档兼容**：`ProjectileTrail` 类**保留为空壳**（不再 spawn、`Tick`/`DrawAt` 空实现）。
  否则含"在飞轨迹 Thing"的存档在换版后会因子类/thingClass 解析失败而报错。
  新增 `<thingClass>` 不变，避免 def 解析问题。

### 5.7 CE 路径

- `InitializeCE` 的全部逻辑（反射读 `Destination/shotAngle/DamageAmount`、`PredictDrawPositions`
  采样、`ceTrackDrawnPosition`）平移到 `ProjectileTrailState`，行为不变。
- `PatchCECompat` 的反射成员与 `TryPatch` 保留；仅改 `SpawnTrailPostfix` / `ImpactPrefix` 内的
  注册与查找调用。

---

## 6. 类设计（骨架）

### 6.1 `ProjectileTrailState`

```csharp
class ProjectileTrailState
{
    // 与现 ProjectileTrail 字段一一对应（a / exactPosition / dir / length / width /
    // traveled / altitude / ticksPerLength / ticksIdle / ticksUtilDeath / arcing / …）
    // 新增：ProjectileTrailDef def（材质与 Y 来源）
    // 新增：Bounds drawBounds（剔除用）

    public void Tick();                       // = 现 Tick() 主体，去掉 base.Tick / Position 相关
    public void CaptureFinalPosition(Vector3 pos);
    public bool TryGetVisibleBounds(CellRect view, Map map, out Bounds bounds); // 剔除
    public void AppendGeometry(List<Vector3> verts, List<Vector2> uvs, List<int> tris, int baseIndex);
}
```

### 6.2 `MapComponent_ProjectileTrails : MapComponent`

```csharp
class MapComponent_ProjectileTrails : MapComponent
{
    public MapComponent_ProjectileTrails(Map map) : base(map) { }   // 反射自动创建所需

    readonly List<ProjectileTrailState> trails = new List<ProjectileTrailState>();
    readonly Dictionary<Thing, ProjectileTrailState> byProjectile = new Dictionary<Thing, ProjectileTrailState>();

    public IReadOnlyList<ProjectileTrailState> Trails => trails;

    public static MapComponent_ProjectileTrails For(Map map) => map?.GetComponent<MapComponent_ProjectileTrails>();

    public void Register(Thing proj, Vector3 destination, Thing equipment, ProjectileTrailDef def, bool ce);
    public bool TryGet(Thing proj, out ProjectileTrailState state);
    public void CaptureFinalPosition(Thing proj, ...);

    public override void MapComponentTick()   // 更新全部轨迹 + 回收
    {
        for (int i = trails.Count - 1; i >= 0; i--) { …; if (dead) { byProjectile.Remove(...); trails.RemoveAt(i); } }
    }

    public override void ExposeData() { /* 空：轨迹不持久化 */ }
}
```

### 6.3 `ProjectileTrailBatcher`

```csharp
static class ProjectileTrailBatcher
{
    public static void Draw(Map map, IReadOnlyList<ProjectileTrailState> trails)
    {
        CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1);
        // 1) 逐条剔除（包围盒 ∩ view、fog），按 def 分桶
        // 2) 每桶重建合并网格（复用缓冲），设置 bounds = 可见轨迹包围盒并集
        // 3) Graphics.DrawMesh(mesh, Matrix4x4.identity, def.DrawMatSingle, 0)
    }
}
```

### 6.4 补丁改动清单

| 文件 | 改动 |
|---|---|
| `Patch/PatchProjectile.cs` | `PatchProjectileLaunch.Postfix`：去掉 `GenSpawn.Spawn`，改调 `MapComponent_ProjectileTrails.For(map).Register(...)`；`PatchProjectileImpact.Prefix`：`TryGetTrail` → `TryGet` + `CaptureFinalPosition` |
| `Patch/PatchCECompat.cs` | `SpawnTrailPostfix` / `ImpactPrefix`：同上（保留反射与门控逻辑） |
| `Patch/PatchDynamicDrawTrails.cs` | 新增：`[HarmonyPatch(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings))]` Postfix；从 `__instance` 的私有 `map` 字段（或 `Find.CurrentMap`）取图；`GetComponent<MapComponent_ProjectileTrails>()` → `Draw` |
| `GunplaySetup.cs` | 改为**显式**挂绘制补丁（`AccessTools.Method` + 空检查），不用属性式 `PatchAll` 以免目标缺失时抛异常 |

---

## 7. 迁移步骤

1. 新增 `ProjectileTrailState`：从 `ProjectileTrail` 平移全部状态与几何逻辑，去掉 Thing 依赖。
2. 新增 `MapComponent_ProjectileTrails`（自动创建、tick、查找、注册）。
3. 新增 `ProjectileTrailBatcher`（分组、剔除、合并网格、绘制）。
4. 改 2 处注册调用（原版 + CE）、2 处命中查找。
5. 新增绘制补丁 + 空引用保护；`GunplaySetup` 显式挂载。
6. `ProjectileTrail` 清空为兼容空壳（保留类名与 `thingClass` 指向）。
7. 回归测试（§10）。

---

## 8. 决策清单（均已确认，结论见 §9）

| # | 决策 | 推荐 | 备选 |
|---|---|---|---|
| 1 | 绘制钩子 | Postfix `DrawDynamicThings`（零 Thing，1 补丁） | 单例渲染 Thing（零补丁，1 Thing/地图） |
| 2 | 轨迹 vs 弹体的深度先后 | 轨迹贴在弹体**之后**（epsilon 负向），弹体可见 | 轨迹盖住弹体（保持现状观感） |
| 3 | 轨迹 vs Pawn | 保持被 Pawn 遮挡（Y 深度自动） | — |
| 4 | 旧存档兼容 | 保留 `ProjectileTrail` 空壳类 | 删类（含在飞轨迹的旧存档会报错） |
| 5 | 是否顺带调整视觉 | 本次纯等价重构，不动视觉 | 借机微调 |

---

## 9. 已定结论与实现记录

**结论**：① 绘制钩子 = `DynamicDrawThings` Postfix（零 Thing，1 个带空引用保护的补丁）；
② 轨迹贴在弹体**之下**（`DepthEpsilon = -0.002f`）；③ 轨迹 vs Pawn 保持被遮挡（Y 深度自动）；
④ 保留 `ProjectileTrail` 空壳类做旧存档兼容；⑤ 纯等价重构，不动视觉（除 ② 的深度微调）。

**已实现**，`dotnet build` 通过（0 错误）：

- `Source/ProjectileTrailState.cs`（新增）
- `Source/MapComponent_ProjectileTrails.cs`（新增）
- `Source/ProjectileTrailBatcher.cs`（新增）
- `Source/Patch/PatchDynamicDrawTrails.cs`（新增）
- `Source/Patch/PatchProjectile.cs`、`Source/Patch/PatchCECompat.cs`（改注册/命中调用点）
- `Source/GunplaySetup.cs`（显式挂载绘制补丁）
- `Source/ProjectileTrail.cs`（改为兼容空壳）

**迭代 2 — 绘制路径实验（实例化，已回退）**：

- 尝试：直线轨迹改走 `Graphics.DrawMeshInstanced`（网格固定 `MeshPool.plane10`，逐条只提供 TRS 矩阵），
  依据是 RimWorld 自身的 `Verse.DrawBatch.Flush` 在 `renderInstanced` 分组上就是这么做的。
- 实测结果：**每帧耗时反而从 ~0.1ms 升到 ~0.2ms**。原因是实例化有固定的单次提交开销
  （实例缓冲 + 切到实例化着色器变体），而在飞轨迹通常只有个位数到几十条，低于实例化的收益拐点
  （要到数百条同网格实例才划算）。
- 处理：**回退为合并网格**，不保留实例化路径。
- 保留项：`MeshUpdateFlags.DontRecalculateBounds | DontValidateIndices`（跳过 Unity 的包围盒重算与索引校验）。
- 移除雾判定：战争迷雾本身会盖住轨迹，逐条查 `fogGrid` 是纯浪费。`IsVisible` 收紧为 `IsVisible(CellRect)`。

**与设计的两处偏差**（均为修正）：

1. 轨迹 Y 取 `proj.def.Altitude`（与旧实现一致，保证与弹体同深度平面），而非 trail def 的 `altitudeLayer`；再叠加 `DepthEpsilon`。
2. 修掉一个原实现就存在的隐患：CE 采样退化回退解析弧线时未清空 `ceArcSamples`，会继续走采样分支（`ceArcCumLen` 可能为 null → NRE）。现在回退时显式清空。
3. 直线拖尾长度补了 `ticksPerLength > 0` 守卫，避免速度为 0 时产生 NaN 顶点。

**已核实的两个运行时前提**（架构成立的关键）：

- `Map.MapPostTick()` → `MapComponentUtility.MapComponentTick()` → 每个 `MapComponent.MapComponentTick()` 每 tick 调用一次；
- `Map.ExposeComponents()` 末尾调用 `FillComponents()`，因此**加载旧存档（即使从未装过本 mod）也会自动创建本组件**；
  且 `GenTypes.AllSubclassesNonAbstract` 只过滤抽象类、不筛可见性，`internal` 组件同样会被实例化。

## 10. 风险

| 风险 | 说明 | 缓解 |
|---|---|---|
| 绘制补丁挂点失效 | RW 未来重命名/重构 `DrawDynamicThings` | `AccessTools.Method` 空检查 → log + 禁用轨迹，不崩；备选 A 可零补丁 |
| 合并网格 bounds 错误 | bounds 不对会导致整批被视锥剔除 | bounds 取可见轨迹包围盒并集（剔除阶段已有），恒 ⊆ 视口 |
| 同层排序观感变化 | 轨迹 vs 弹体由"逐条交错"变"整组" | epsilon 显式定死前后关系 |
| CE 反射成员变动 | CE 更新导致反射取不到 | 沿用现有 `TryPatch` 的空检查与回退路径 |
| 旧存档含在飞轨迹 Thing | 换版读档报错 | 保留空壳类 |

---

## 10. 回归测试清单

- [ ] 直线投射物拖尾长度/宽度正常（`trailWidthMode` 两种、`trailWidthFactor` 滑块）
- [ ] 弧线投射物（手雷/迫击炮弹）带状轨迹正常、淡出正常
- [ ] CE 已装载：`ceStraightTrails` 开/关两种模式；CE 未装载：原版路径正常
- [ ] `projectileSpeed` 速度缩放与 `preserveSpeed` 例外仍生效
- [ ] 无武器投射物特效开关（总开关 + 仅子弹伤害）仍生效
- [ ] 雾中轨迹仍不显示
- [ ] **视野边缘：头部飞出屏幕、尾部仍在屏幕内时，轨迹仍显示**（缺陷 3）
- [ ] 弹幕密集场景：绘制调用数 ≈ 材质种类数，帧率稳定
- [ ] 飞行中存档 → 读档：无报错；在飞投射物无拖尾，新发射正常
- [ ] 含旧版在飞轨迹 Thing 的存档可正常载入
- [ ] 多地图（殖民地图 + 商队遭遇图）各自独立、互不串扰
- [ ] 切换/卸载地图后集合被回收，无泄漏
```
