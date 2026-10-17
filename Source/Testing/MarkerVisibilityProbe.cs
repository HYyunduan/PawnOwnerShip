using System;
using System.Reflection;
using UnityEngine;
using Verse;

namespace PawnOwnership.Testing
{
    /// <summary>
    /// 把"归属标记是否真的显示出来"变成**可量化、可自动判定**的测量。
    ///
    /// 设计原则（避免"看截图猜"）：
    ///   1. 不靠人眼看图，也不靠统计颜色饱和度之类的间接指标；
    ///   2. 锚点是**已知的**：用 WorldToScreenPoint 把已设归属的物品投影到屏幕坐标；
    ///   3. 带**噪声基线**：再抓一对"同为开启"的帧，场景动画的天然差异作为参照，
    ///      信号必须显著高于噪声才算检测到；
    ///   4. 屏幕 y 轴与纹理 y 轴方向可能相反，因此每处同时测 (x,y) 与 (x,H-1-y)，取较大者，
    ///      避免坐标搞反造成假阴性。
    ///
    /// 抓图走反射调用 UnityEngine.ScreenCapture.CaptureScreenshotAsTexture()，不新增程序集引用。
    /// </summary>
    public static class MarkerVisibilityProbe
    {
        /// <summary>像素差阈值：低于此值的通道差视为噪声。</summary>
        public const int PixelDelta = 8;

        /// <summary>锚点窗口半径（像素）。</summary>
        public const int WindowRadius = 10;

        public sealed class Frame
        {
            public int Width;
            public int Height;
            public Color32[] Pixels;
            public bool Valid;
        }

        /// <summary>抓当前帧画面。必须在 WaitForEndOfFrame 之后调用。</summary>
        public static Frame Capture()
        {
            var frame = new Frame { Valid = false };
            try
            {
                Type t = Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule");
                if (t == null)
                {
                    Log.Error("[PawnOwnership][Probe] 找不到 UnityEngine.ScreenCapture");
                    return frame;
                }

                MethodInfo m = t.GetMethod("CaptureScreenshotAsTexture", Type.EmptyTypes);
                if (m == null)
                {
                    Log.Error("[PawnOwnership][Probe] 找不到 CaptureScreenshotAsTexture()");
                    return frame;
                }

                var tex = m.Invoke(null, null) as Texture2D;
                if (tex == null)
                {
                    Log.Error("[PawnOwnership][Probe] 抓图返回 null");
                    return frame;
                }

                frame.Width = tex.width;
                frame.Height = tex.height;
                frame.Pixels = tex.GetPixels32();
                frame.Valid = frame.Pixels != null && frame.Pixels.Length == frame.Width * frame.Height;
            }
            catch (Exception ex)
            {
                Log.Error("[PawnOwnership][Probe] 抓图异常: " + ex);
            }
            return frame;
        }

        /// <summary>整帧差异像素数（用于噪声基线与全局信号）。</summary>
        public static int FullFrameDiff(Frame a, Frame b)
        {
            if (!Ready(a) || !Ready(b)) return -1;
            int n = 0;
            for (int i = 0; i < a.Pixels.Length; i++)
            {
                if (Differs(a.Pixels[i], b.Pixels[i])) n++;
            }
            return n;
        }

        /// <summary>
        /// 在 (x,y) 处取窗口比较两帧，返回差异像素数。
        /// 会同时尝试 y 与 H-1-y 两个位置并取较大值，规避屏幕/纹理 y 轴方向不一致。
        /// </summary>
        public static int AnchorDiff(Frame a, Frame b, int x, int y, out bool flippedBetter)
        {
            flippedBetter = false;
            if (!Ready(a) || !Ready(b)) return -1;

            int normal = WindowDiff(a, b, x, y);
            int flipped = WindowDiff(a, b, x, a.Height - 1 - y);
            if (flipped > normal)
            {
                flippedBetter = true;
                return flipped;
            }
            return normal;
        }

        private static int WindowDiff(Frame a, Frame b, int cx, int cy)
        {
            int x0 = Mathf.Clamp(cx - WindowRadius, 0, a.Width - 1);
            int x1 = Mathf.Clamp(cx + WindowRadius, 0, a.Width - 1);
            int y0 = Mathf.Clamp(cy - WindowRadius, 0, a.Height - 1);
            int y1 = Mathf.Clamp(cy + WindowRadius, 0, a.Height - 1);

            int n = 0;
            for (int y = y0; y <= y1; y++)
            {
                int row = y * a.Width;
                for (int x = x0; x <= x1; x++)
                {
                    if (Differs(a.Pixels[row + x], b.Pixels[row + x])) n++;
                }
            }
            return n;
        }

        private static bool Differs(Color32 p, Color32 q)
        {
            return Math.Abs(p.r - q.r) > PixelDelta
                || Math.Abs(p.g - q.g) > PixelDelta
                || Math.Abs(p.b - q.b) > PixelDelta;
        }

        private static bool Ready(Frame f)
        {
            return f != null && f.Valid;
        }
    }
}
