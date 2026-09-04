using System.IO;
using UnityEngine;

namespace MCPBridge
{
    /// <summary>
    /// </summary>
    public static class ImageOptimizer
    {
        /// <summary>
        /// </summary>
        public static byte[] ResizeForApi(string path, int maxEdge, out string mimeType)
        {
            byte[] original = File.ReadAllBytes(path);

            var src = new Texture2D(2, 2);
            if (!src.LoadImage(original))
            {
                mimeType = "image/png";
                return original;
            }

            int w = src.width;
            int h = src.height;
            int longEdge = Mathf.Max(w, h);

            if (longEdge <= maxEdge)
            {
                Object.DestroyImmediate(src);
                mimeType = GetMime(path);
                return original;
            }

            float scale = (float)maxEdge / longEdge;
            int newW = Mathf.RoundToInt(w * scale);
            int newH = Mathf.RoundToInt(h * scale);

            var rt = RenderTexture.GetTemporary(newW, newH, 0, RenderTextureFormat.ARGB32);
            rt.filterMode = FilterMode.Bilinear;
            var prev = RenderTexture.active;

            Graphics.Blit(src, rt);
            RenderTexture.active = rt;

            var dst = new Texture2D(newW, newH, TextureFormat.RGB24, false);
            dst.ReadPixels(new Rect(0, 0, newW, newH), 0, 0);
            dst.Apply();

            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            byte[] result = dst.EncodeToPNG();
            mimeType = "image/png";

            Object.DestroyImmediate(src);
            Object.DestroyImmediate(dst);

            return result;
        }

        static string GetMime(string path)
        {
            string ext = Path.GetExtension(path).ToLower();
            return ext == ".jpg" || ext == ".jpeg" ? "image/jpeg"
                 : ext == ".webp" ? "image/webp"
                 : "image/png";
        }
    }
}
