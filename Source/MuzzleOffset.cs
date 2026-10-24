using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using RimWorld;
using UnityEngine;
using Verse;

namespace Gunplay
{
    // 按武器贴图推导「枪口到角色中心」的距离，供投射物发射点前移使用。
    //
    // 坐标口径与 RimWorld 的装备绘制（PawnRenderUtility.DrawEquipmentAiming）一致：
    //   * 武器贴图水平朝右，枪口在最右侧的不透明像素（RimWorld 武器贴图的惯例）；
    //   * 武器中心绘制在角色中心前方 0.4 + equippedDistanceOffset 处；
    //   * 贴图横向按 Graphic.drawSize.x 缩放到世界尺寸。
    // 所以距离 = 0.4 + equippedDistanceOffset + (u - 0.5) * drawSize.x。
    // 只取沿枪管方向的长度，横向偏移不参与——投射物仍然沿 origin→destination 直线飞行。
    //
    // 推导要读贴图像素，单把武器只做一次；结果另外持久化到 Config 目录，
    // 下次启动直接加载，跳过贴图扫描（签名见 Signature，贴图/绘制参数/公式变化即失效）。
    internal static class MuzzleOffset
    {
        // 像素 alpha 低于该值视为透明。与贴图抗锯齿边缘留出余量，避免把描边当成枪口。
        const byte OpaqueAlpha = 5;

        // PawnRenderer 绘制瞄准中的武器时，相对角色中心的前移量（不含 equippedDistanceOffset）。
        const float EquipmentOffset = 0.4f;

        // 防御性夹取：贴图异常（整张全不透明、尺寸离谱）时不至于把发射点推到地图外。
        const float MinLength = 0f;
        const float MaxLength = 6f;

        // ---------------- 持久化缓存 ----------------

        const string HeaderPrefix = "#Gunplay muzzle offsets v1 ";
        const string CacheFileName = "GunplayMuzzleOffsets.txt";

        // 公式或扫描口径变更时递增；与下面的常量、RimWorld 版本、程序集版本一起参与签名。
        const int FormulaVersion = 1;

        // 每把武器只需扫一次贴图；失败也缓存（负值），避免每发都重试。
        static readonly Dictionary<ThingDef, float> cache = new Dictionary<ThingDef, float>();
        static readonly object cacheLock = new object();

        static bool loaded;
        static bool dirty;
        static bool saveLogged;
        static bool saveFailureLogged;
        static string? signature;

        static string CachePath => Path.Combine(GenFilePaths.ConfigFolderPath, CacheFileName);

        // 启动时调用（见 GunplaySetup）：先读磁盘缓存，再补齐所有未命中的手持射击武器。
        // 命中缓存的武器不再读贴图；首次运行/签名变化时才会真正扫一遍，随后写回缓存。
        internal static void WarmUp()
        {
            lock (cacheLock)
            {
                EnsureLoadedLocked();

                int added = 0;
                foreach (ThingDef def in CandidateWeapons())
                {
                    if (cache.ContainsKey(def)) continue;

                    cache[def] = Derive(def);
                    added++;
                }

                if (added > 0) dirty = true;
                if (dirty) SaveLocked();
            }
        }

        // 把尚未落盘的推导结果写出去（设置窗口关闭时兜底，见 Gunplay.WriteSettings）。
        internal static void Flush()
        {
            lock (cacheLock)
            {
                if (dirty) SaveLocked();
            }
        }

        internal static bool TryGetBarrelLength(ThingDef? weaponDef, out float length)
        {
            length = 0f;
            if (weaponDef == null) return false;

            lock (cacheLock)
            {
                EnsureLoadedLocked();

                if (!cache.TryGetValue(weaponDef, out float cached))
                {
                    cached = Derive(weaponDef);
                    cache[weaponDef] = cached;
                    dirty = true;
                    // 运行中首次遇到（例如启动后才打开开关）就立即落盘，一次一个 def，
                    // 文件很小且每把武器最多写一次，不会形成持续 I/O。
                    SaveLocked();

                    if (cached < 0f)
                    {
                        Log.WarningOnce(
                            "[Gunplay] Could not derive a muzzle offset from the texture of weapon '"
                            + weaponDef.defName + "'; falling back to its barrelLength setting.",
                            ("GunplayMuzzleAuto" + weaponDef.defName).GetHashCode());
                    }
                }

                if (cached < 0f) return false;

                length = cached;
                return true;
            }
        }

        static void EnsureLoadedLocked()
        {
            if (loaded) return;
            loaded = true;

            string path = CachePath;
            if (!File.Exists(path)) return;

            try
            {
                using (var reader = new StreamReader(path))
                {
                    string? header = reader.ReadLine();
                    if (header != HeaderPrefix + Signature())
                    {
                        // 贴图/绘制参数/公式/游戏版本有变化：整份重建，避免继续用旧枪口。
                        dirty = true;
                        Log.Message("[Gunplay] Muzzle offset cache is stale; rebuilding it once.");
                        return;
                    }

                    int count = 0;
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        if (!float.TryParse(line.Substring(tab + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) continue;

                        // 已移除的内容直接跳过；新增内容由 WarmUp/懒加载补。
                        ThingDef? def = DefDatabase<ThingDef>.GetNamed(line.Substring(0, tab), false);
                        if (def == null) continue;

                        cache[def] = value;
                        count++;
                    }

                    if (count > 0)
                    {
                        Log.Message("[Gunplay] Loaded " + count + " cached muzzle offsets.");
                    }
                }
            }
            catch (Exception exception)
            {
                // 读坏了就当没有：WarmUp 会重算并覆盖。
                cache.Clear();
                dirty = true;
                Log.Warning("[Gunplay] Could not read the muzzle offset cache; it will be rebuilt.\n" + exception);
            }
        }

        static void SaveLocked()
        {
            if (cache.Count == 0) return;

            string path = CachePath;
            string temp = path + ".tmp";
            try
            {
                using (var writer = new StreamWriter(temp))
                {
                    writer.WriteLine(HeaderPrefix + Signature());
                    foreach (KeyValuePair<ThingDef, float> pair in cache)
                    {
                        if (pair.Key == null) continue;

                        writer.Write(pair.Key.defName);
                        writer.Write('\t');
                        writer.WriteLine(pair.Value.ToString("R", CultureInfo.InvariantCulture));
                    }
                }

                // net48 没有 File.Move(src, dst, overwrite) 重载，用 Copy 覆盖后再删临时文件。
                File.Copy(temp, path, true);
                dirty = false;

                if (!saveLogged)
                {
                    saveLogged = true;
                    Log.Message("[Gunplay] Wrote " + cache.Count + " muzzle offsets for the next launch.");
                }
            }
            catch (Exception exception)
            {
                if (!saveFailureLogged)
                {
                    saveFailureLogged = true;
                    Log.Warning("[Gunplay] Could not write the muzzle offset cache:\n" + exception);
                }
            }
            finally
            {
                // 失败时也别留下 .tmp。
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch
                {
                    // 尽力而为。
                }
            }
        }

        // 参与推导的武器：手持（category Item）且带 Verb_Shoot。
        // 排序只为让签名与文件内容稳定，不受 DefDatabase 内部顺序影响。
        static List<ThingDef> CandidateWeapons()
        {
            var result = new List<ThingDef>();
            List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;

            for (int i = 0; i < all.Count; i++)
            {
                ThingDef def = all[i];
                if (!def.IsWeapon || def.graphicData == null) continue;
                if (!HasShootVerb(def)) continue;

                result.Add(def);
            }

            result.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
            return result;
        }

        static bool HasShootVerb(ThingDef def)
        {
            List<VerbProperties>? verbs = def.Verbs;
            if (verbs == null) return false;

            for (int i = 0; i < verbs.Count; i++)
            {
                if (typeof(Verb_Shoot).IsAssignableFrom(verbs[i].verbClass)) return true;
            }

            return false;
        }

        // 缓存指纹：覆盖 Derive 的全部输入（公式常量、武器 def 的贴图路径/绘制尺寸/偏移、版本）。
        // 贴图内容本身无法廉价校验；同路径同尺寸替换贴图的情况会命中旧值。
        static string Signature()
        {
            if (signature != null) return signature;

            ulong hash = 14695981039346656037UL;
            AddString(ref hash, "Gunplay.MuzzleOffset");
            AddInt(ref hash, FormulaVersion);
            AddInt(ref hash, OpaqueAlpha);
            AddInt(ref hash, BitConverter.SingleToInt32Bits(EquipmentOffset));
            AddInt(ref hash, BitConverter.SingleToInt32Bits(MinLength));
            AddInt(ref hash, BitConverter.SingleToInt32Bits(MaxLength));
            AddString(ref hash, VersionControl.CurrentVersionString);
            AddString(ref hash, typeof(MuzzleOffset).Assembly.GetName().Version?.ToString() ?? "");

            List<ThingDef> candidates = CandidateWeapons();
            AddInt(ref hash, candidates.Count);
            foreach (ThingDef def in candidates)
            {
                GraphicData? data = def.graphicData;
                AddString(ref hash, def.defName ?? "");
                AddString(ref hash, data?.texPath ?? "");
                AddString(ref hash, data?.graphicClass?.Name ?? "");
                AddInt(ref hash, BitConverter.SingleToInt32Bits(data?.drawSize.x ?? 0f));
                AddInt(ref hash, BitConverter.SingleToInt32Bits(def.equippedDistanceOffset));
            }

            signature = hash.ToString("X16", CultureInfo.InvariantCulture);
            return signature;
        }

        static void AddString(ref ulong hash, string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                hash = (hash ^ value[i]) * 1099511628211UL;
            }

            // 分隔符：让 "ab"+"c" 与 "a"+"bc" 得到不同结果。
            hash = (hash ^ 0xFFFF) * 1099511628211UL;
        }

        static void AddInt(ref ulong hash, int value)
        {
            hash = (hash ^ (uint)value) * 1099511628211UL;
        }

        // ---------------- 贴图扫描 ----------------

        static float Derive(ThingDef weaponDef)
        {
            try
            {
                Graphic? graphic = weaponDef.graphic;
                if (graphic == null || graphic == BaseContent.BadGraphic) return -1f;

                Material? mat = graphic.MatSingle;
                if (mat == null || !(mat.mainTexture is Texture2D texture)) return -1f;
                if (texture.width <= 0 || texture.height <= 0) return -1f;

                Color32[]? pixels = ReadPixels(texture);
                if (pixels == null) return -1f;

                int width = texture.width;
                int height = texture.height;
                if (pixels.Length < width * height) return -1f;

                // 从右往左找第一个含不透明像素的列，即贴图上的枪口尖端。
                int muzzleColumn = -1;
                for (int x = width - 1; x >= 0 && muzzleColumn < 0; x--)
                {
                    for (int y = 0; y < height; y++)
                    {
                        if (pixels[y * width + x].a >= OpaqueAlpha)
                        {
                            muzzleColumn = x;
                            break;
                        }
                    }
                }

                if (muzzleColumn < 0) return -1f;

                float u = (muzzleColumn + 0.5f) / width;
                float length = EquipmentOffset
                    + weaponDef.equippedDistanceOffset
                    + (u - 0.5f) * graphic.drawSize.x;

                return Mathf.Clamp(length, MinLength, MaxLength);
            }
            catch (Exception)
            {
                // 贴图缺失、格式不支持、读取失败等一律交给调用方回退到配置值。
                return -1f;
            }
        }

        static Color32[]? ReadPixels(Texture2D texture)
        {
            try
            {
                if (!texture.isReadable) return ReadPixelsViaCopy(texture);
                return texture.GetPixels32();
            }
            catch (Exception)
            {
                try
                {
                    return ReadPixelsViaCopy(texture);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        // 与 TextureAtlasHelper.MakeReadableTextureInstance 同思路：经 RenderTexture
        // 拷一份可读副本再读像素，兼容 GPU 端不可直接读取的贴图。
        static Color32[]? ReadPixelsViaCopy(Texture2D source)
        {
            RenderTexture? temp = RenderTexture.GetTemporary(
                source.width,
                source.height,
                0,
                RenderTextureFormat.Default,
                RenderTextureReadWrite.Linear);
            if (temp == null) return null;

            RenderTexture? previous = RenderTexture.active;
            Texture2D? copy = null;
            try
            {
                Graphics.Blit(source, temp);
                RenderTexture.active = temp;

                copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
                copy.Apply();

                return copy.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temp);
                if (copy != null) UnityEngine.Object.Destroy(copy);
            }
        }
    }
}
