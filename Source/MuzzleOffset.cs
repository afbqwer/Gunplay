using System;
using System.Collections.Generic;
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
    internal static class MuzzleOffset
    {
        // 像素 alpha 低于该值视为透明。与贴图抗锯齿边缘留出余量，避免把描边当成枪口。
        const byte OpaqueAlpha = 5;

        // PawnRenderer 绘制瞄准中的武器时，相对角色中心的前移量（不含 equippedDistanceOffset）。
        const float EquipmentOffset = 0.4f;

        // 防御性夹取：贴图异常（整张全不透明、尺寸离谱）时不至于把发射点推到地图外。
        const float MinLength = 0f;
        const float MaxLength = 6f;

        // 每把武器只需扫一次贴图；失败也缓存（负值），避免每发都重试。
        static readonly Dictionary<ThingDef, float> cache = new Dictionary<ThingDef, float>();
        static readonly object cacheLock = new object();

        internal static bool TryGetBarrelLength(ThingDef? weaponDef, out float length)
        {
            length = 0f;
            if (weaponDef == null) return false;

            lock (cacheLock)
            {
                if (!cache.TryGetValue(weaponDef, out float cached))
                {
                    cached = Derive(weaponDef);
                    cache[weaponDef] = cached;

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
