using System;
using System.Numerics;

namespace UltraRing.FakeHost;

/// <summary>Camera built exactly like the host does: right = norm(cross(up, fwd)), up = cross(fwd, right).</summary>
public struct Camera
{
    public Vector3 Eye, Right, Up, Fwd;
    public float TanHalfFov, Aspect;

    public static Camera Make(Vector3 pos, Vector3 target, Vector3 up, float fovYDeg, float aspect)
    {
        Vector3 fwd = target - pos;
        fwd = fwd.LengthSquared() > 1e-12f ? Vector3.Normalize(fwd) : Vector3.UnitZ;
        Vector3 right = Vector3.Cross(up, fwd);
        right = right.LengthSquared() > 1e-12f ? Vector3.Normalize(right) : Vector3.UnitX;
        Vector3 up2 = Vector3.Cross(fwd, right);
        return new Camera
        {
            Eye = pos, Right = right, Up = up2, Fwd = fwd,
            TanHalfFov = MathF.Tan(fovYDeg * 0.5f * MathF.PI / 180f), Aspect = aspect,
        };
    }
}

/// <summary>Tiny software wireframe renderer + the guest-frame compositor, both writing straight into 0xAARRGGBB pixels.</summary>
public sealed unsafe class SoftRenderer
{
    private uint* _px;
    private int _stride, _w, _h;
    private Camera _cam;
    private const float Near = 0.05f;

    public void Begin(uint* pixels, int stridePixels, int w, int h, in Camera cam)
    {
        _px = pixels; _stride = stridePixels; _w = w; _h = h; _cam = cam;
    }

    public void Clear(uint topColor, uint bottomColor)
    {
        for (int y = 0; y < _h; y++)
        {
            float t = _h > 1 ? (float)y / (_h - 1) : 0;
            uint col = Lerp(topColor, bottomColor, t);
            uint* row = _px + (long)y * _stride;
            for (int x = 0; x < _w; x++) row[x] = col;
        }
    }

    private static uint Lerp(uint a, uint b, float t)
    {
        int Ch(int sh) => (int)(((a >> sh) & 255) * (1 - t) + ((b >> sh) & 255) * t);
        return 0xFF000000u | (uint)(Ch(16) << 16) | (uint)(Ch(8) << 8) | (uint)Ch(0);
    }

    public void Box(Vector3 mn, Vector3 mx, uint col)
    {
        Vector3 a = new Vector3(mn.X, mn.Y, mn.Z), b = new Vector3(mx.X, mn.Y, mn.Z), c = new Vector3(mx.X, mn.Y, mx.Z), d = new Vector3(mn.X, mn.Y, mx.Z);
        Vector3 e = new Vector3(mn.X, mx.Y, mn.Z), f = new Vector3(mx.X, mx.Y, mn.Z), g = new Vector3(mx.X, mx.Y, mx.Z), h = new Vector3(mn.X, mx.Y, mx.Z);
        Line3(a, b, col); Line3(b, c, col); Line3(c, d, col); Line3(d, a, col);
        Line3(e, f, col); Line3(f, g, col); Line3(g, h, col); Line3(h, e, col);
        Line3(a, e, col); Line3(b, f, col); Line3(c, g, col); Line3(d, h, col);
    }

    private void ToCam(Vector3 p, out float x, out float y, out float z)
    {
        Vector3 r = p - _cam.Eye;
        x = Vector3.Dot(r, _cam.Right);
        y = Vector3.Dot(r, _cam.Up);
        z = Vector3.Dot(r, _cam.Fwd);
    }

    public void Line3(Vector3 a, Vector3 b, uint col)
    {
        ToCam(a, out float ax, out float ay, out float az);
        ToCam(b, out float bx, out float by, out float bz);
        if (az < Near && bz < Near) return;
        if (az < Near)
        {
            float t = (Near - az) / (bz - az);
            ax += (bx - ax) * t; ay += (by - ay) * t; az = Near;
        }
        else if (bz < Near)
        {
            float t = (Near - bz) / (az - bz);
            bx += (ax - bx) * t; by += (ay - by) * t; bz = Near;
        }
        float kx = (_w * 0.5f) / (_cam.TanHalfFov * _cam.Aspect), ky = (_h * 0.5f) / _cam.TanHalfFov;
        float x0 = _w * 0.5f + ax / az * kx, y0 = _h * 0.5f - ay / az * ky;
        float x1 = _w * 0.5f + bx / bz * kx, y1 = _h * 0.5f - by / bz * ky;
        DrawLine2(x0, y0, x1, y1, col);
    }

    // Liang-Barsky clip to the pixel rect, then a DDA.
    private void DrawLine2(float x0, float y0, float x1, float y1, uint col)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float t0 = 0, t1 = 1;
        if (!Clip(-dx, x0 - 0, ref t0, ref t1)) return;
        if (!Clip(dx, (_w - 1) - x0, ref t0, ref t1)) return;
        if (!Clip(-dy, y0 - 0, ref t0, ref t1)) return;
        if (!Clip(dy, (_h - 1) - y0, ref t0, ref t1)) return;
        float ax = x0 + t0 * dx, ay = y0 + t0 * dy, bx = x0 + t1 * dx, by = y0 + t1 * dy;
        float sx = bx - ax, sy = by - ay;
        int steps = (int)MathF.Max(MathF.Abs(sx), MathF.Abs(sy));
        if (steps <= 0)
        {
            Plot((int)ax, (int)ay, col);
            return;
        }
        float ix = sx / steps, iy = sy / steps;
        float px = ax, py = ay;
        for (int i = 0; i <= steps; i++)
        {
            Plot((int)(px + 0.5f), (int)(py + 0.5f), col);
            px += ix; py += iy;
        }
    }

    private static bool Clip(float p, float q, ref float t0, ref float t1)
    {
        if (p == 0) return q >= 0;
        float r = q / p;
        if (p < 0) { if (r > t1) return false; if (r > t0) t0 = r; }
        else { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }

    private void Plot(int x, int y, uint col)
    {
        if ((uint)x >= (uint)_w || (uint)y >= (uint)_h) return;
        _px[(long)y * _stride + x] = col;
    }

    // ---- guest frame compositing (compositor.cpp shader, memory path) ----

    private static int[] _xmap = Array.Empty<int>();
    private static int _xmapW, _xmapGw;

    /// <summary>
    /// out = gui + scene*(1-gui.a), scene = hand + world*(1-hand.a), then blended ONE/INV_SRC_ALPHA over the
    /// existing pixels. Layers are premultiplied BGRA, bottom-up rows (memory row gh-1 = top of the screen),
    /// stretched over the whole window with nearest sampling. Depth occlusion is not implemented.
    /// </summary>
    public void CompositeGuest(byte[] world, byte[] gui, byte[] hand, bool hasHand, int gw, int gh)
    {
        if (_xmapW != _w || _xmapGw != gw)
        {
            _xmap = new int[_w];
            for (int x = 0; x < _w; x++) _xmap[x] = (int)((long)x * gw / _w);
            _xmapW = _w; _xmapGw = gw;
        }
        int[] xmap = _xmap;
        fixed (byte* pw = world, pg = gui, ph = hand)
        {
            for (int y = 0; y < _h; y++)
            {
                int gy = (int)((long)y * gh / _h);
                long rowOff = (long)(gh - 1 - gy) * gw;
                uint* wr = (uint*)pw + rowOff;
                uint* gr = (uint*)pg + rowOff;
                uint* hr = (uint*)ph + rowOff;
                uint* dst = _px + (long)y * _stride;
                for (int x = 0; x < _w; x++)
                {
                    int gx = xmap[x];
                    uint wv = wr[gx], gv = gr[gx], hv = hasHand ? hr[gx] : 0u;
                    if ((wv | gv | hv) == 0) continue;
                    uint scene = hv == 0 ? wv : Over(hv, wv);
                    uint outc = gv == 0 ? scene : Over(gv, scene);
                    dst[x] = Over(outc, dst[x]);
                }
            }
        }
    }

    /// <summary>Premultiplied "over": top + bottom * (1 - top.a), per channel, clamped.</summary>
    private static uint Over(uint top, uint bottom)
    {
        uint ta = top >> 24;
        if (ta == 255) return top;
        uint inv = 255 - ta;
        uint r = Sat((top >> 16 & 255) + Mul(bottom >> 16 & 255, inv));
        uint g = Sat((top >> 8 & 255) + Mul(bottom >> 8 & 255, inv));
        uint b = Sat((top & 255) + Mul(bottom & 255, inv));
        uint a = Sat(ta + Mul(bottom >> 24, inv));
        return a << 24 | r << 16 | g << 8 | b;
    }

    private static uint Mul(uint x, uint a)
    {
        uint t = x * a + 128;
        return (t + (t >> 8)) >> 8;
    }

    private static uint Sat(uint v) => v > 255 ? 255 : v;
}
