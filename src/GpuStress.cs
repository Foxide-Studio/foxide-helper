using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace FoxideDisk
{
    /// <summary>GPU sensors: Windows "GPU Engine" counters (any vendor) + NVML (NVIDIA temperature / clock / power).</summary>
    public class GpuSensors : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct NvUtil { public uint gpu, memory; }
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlShutdown();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr dev);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr dev, int sensor, out uint t);
        [DllImport("nvml.dll", CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(IntPtr dev, StringBuilder name, uint len);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(IntPtr dev, int type, out uint mhz);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr dev, out uint mw);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr dev, out NvUtil u);

        IntPtr nv;
        public bool HasNvml { get; private set; }
        public string NvName;
        readonly Dictionary<string, PerformanceCounter> engines = new Dictionary<string, PerformanceCounter>();
        DateTime lastScan = DateTime.MinValue;

        public GpuSensors()
        {
            try
            {
                uint n;
                if (nvmlInit_v2() == 0 && nvmlDeviceGetCount_v2(out n) == 0 && n > 0 && nvmlDeviceGetHandleByIndex_v2(0, out nv) == 0)
                {
                    HasNvml = true;
                    var sb = new StringBuilder(96);
                    if (nvmlDeviceGetName(nv, sb, 96) == 0) NvName = sb.ToString();
                }
            }
            catch { HasNvml = false; }
        }

        public double? NvTemp() { uint t; try { return HasNvml && nvmlDeviceGetTemperature(nv, 0, out t) == 0 ? t : (double?)null; } catch { return null; } }
        public double? NvClock() { uint c; try { return HasNvml && nvmlDeviceGetClockInfo(nv, 0, out c) == 0 ? c : (double?)null; } catch { return null; } }
        public double? NvPower() { uint p; try { return HasNvml && nvmlDeviceGetPowerUsage(nv, out p) == 0 ? p / 1000.0 : (double?)null; } catch { return null; } }
        public double? NvLoad() { NvUtil u; try { return HasNvml && nvmlDeviceGetUtilizationRates(nv, out u) == 0 ? u.gpu : (double?)null; } catch { return null; } }

        /// <summary>3D engine utilisation of the busiest adapter (%), from Windows performance counters.</summary>
        public double? Load()
        {
            try
            {
                if ((DateTime.Now - lastScan).TotalSeconds > 5)
                {
                    lastScan = DateTime.Now;
                    var cat = new PerformanceCounterCategory("GPU Engine");
                    var names = new HashSet<string>(cat.GetInstanceNames().Where(x => x.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) >= 0));
                    foreach (var k in engines.Keys.Where(k => !names.Contains(k)).ToList()) { engines[k].Dispose(); engines.Remove(k); }
                    foreach (var nme in names)
                        if (!engines.ContainsKey(nme))
                        {
                            var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", nme);
                            pc.NextValue();
                            engines[nme] = pc;
                        }
                }
                var perAdapter = new Dictionary<string, double>();
                foreach (var kv in engines)
                {
                    float v;
                    try { v = kv.Value.NextValue(); } catch { continue; }
                    int i = kv.Key.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
                    string luid = i >= 0 ? kv.Key.Substring(i, Math.Min(kv.Key.Length - i, 27)) : "";
                    double cur; perAdapter.TryGetValue(luid, out cur);
                    perAdapter[luid] = cur + v;
                }
                return perAdapter.Count == 0 ? (double?)null : Math.Min(100, perAdapter.Values.Max());
            }
            catch { return null; }
        }

        public void Dispose()
        {
            foreach (var e in engines.Values) e.Dispose();
            engines.Clear();
            try { if (HasNvml) nvmlShutdown(); } catch { }
        }
    }

    /// <summary>
    /// OpenGL stress view: renders a heavy fractal shader on its own thread, adapting the number of full-screen passes
    /// per frame so the GPU stays fully loaded at ~30 fps.
    /// </summary>
    public class GpuStressView : Control
    {
        // ---- Win32 / WGL / GL
        [StructLayout(LayoutKind.Sequential)]
        struct PFD
        {
            public ushort nSize, nVersion; public uint dwFlags;
            public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift, cAlphaBits, cAlphaShift;
            public byte cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits, cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
            public uint dwLayerMask, dwVisibleMask, dwDamageMask;
        }
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr dc, ref PFD pfd);
        [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr dc, int format, ref PFD pfd);
        [DllImport("gdi32.dll")] static extern bool SwapBuffers(IntPtr dc);
        [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr dc);
        [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);
        [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr rc);
        [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] static extern IntPtr wglGetProcAddress(string name);
        [DllImport("opengl32.dll")] static extern IntPtr glGetString(uint name);
        [DllImport("opengl32.dll")] static extern void glViewport(int x, int y, int w, int h);
        [DllImport("opengl32.dll")] static extern void glClear(uint mask);
        [DllImport("opengl32.dll")] static extern void glBegin(uint mode);
        [DllImport("opengl32.dll")] static extern void glEnd();
        [DllImport("opengl32.dll")] static extern void glVertex2f(float x, float y);
        [DllImport("opengl32.dll")] static extern void glFinish();

        delegate uint CreateShaderFn(uint type);
        delegate void ShaderSourceFn(uint shader, int count, string[] src, int[] len);
        delegate void CompileShaderFn(uint shader);
        delegate void GetShaderivFn(uint shader, uint pname, out int value);
        delegate void GetInfoLogFn(uint obj, int max, out int len, StringBuilder log);
        delegate uint CreateProgramFn();
        delegate void AttachShaderFn(uint prog, uint shader);
        delegate void LinkProgramFn(uint prog);
        delegate void UseProgramFn(uint prog);
        delegate int GetUniformLocationFn(uint prog, string name);
        delegate void Uniform1fFn(int loc, float v);
        delegate void Uniform2fFn(int loc, float a, float b);
        delegate bool SwapIntervalFn(int interval);

        static T Fn<T>(string name) where T : class
        {
            IntPtr p = wglGetProcAddress(name);
            if (p == IntPtr.Zero || p == (IntPtr)1 || p == (IntPtr)2 || p == (IntPtr)3 || p == (IntPtr)(-1))
                throw new NotSupportedException(L.F("OpenGL функция {0} недоступна", name));
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        const string VS = "void main(){ gl_Position = gl_Vertex; }";
        const string FS = @"
uniform vec2 res; uniform float t; uniform float pass;
void main(){
  vec2 uv = (gl_FragCoord.xy - 0.5*res) / res.y;
  vec3 p = vec3(uv*1.6, 0.35*sin(t*0.07) + pass*0.0001);
  vec3 acc = vec3(0.0);
  for (int i = 0; i < 180; i++) {
    p = abs(p) / dot(p, p) - vec3(0.60 + 0.04*sin(t*0.21), 0.52 + 0.03*cos(t*0.17), 0.44);
    acc += exp(-10.0 * abs(p));
  }
  acc *= 0.018;
  vec3 col = vec3(acc.x*1.5 + acc.y*0.5, acc.x*0.55 + acc.z*0.30, acc.z*0.30 + acc.y*0.12);
  gl_FragColor = vec4(1.0 - exp(-col*1.7), 1.0);
}";

        Thread th;
        volatile bool running;
        volatile int vw, vh;
        public volatile string Renderer, Error;
        public double Fps { get; private set; }
        public int Passes { get; private set; }
        /// <summary>Shader work in gigapixel-iterations per second (180 fractal iterations per pixel per pass).</summary>
        public double Work { get; private set; }
        public bool Running { get { return running; } }

        public GpuStressView()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
            Height = Theme.Dp(240);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); vw = ClientSize.Width; vh = ClientSize.Height; }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (running) return;
            if (Demo.On) { Demo.PaintGpu(e.Graphics, ClientRectangle); return; }
            var P = Theme.P;
            e.Graphics.Clear(P.Dark ? Color.FromArgb(14, 15, 18) : Color.FromArgb(28, 30, 36));
            TextRenderer.DrawText(e.Graphics, Error ?? L.T("Здесь будет изображение теста"), Theme.Body, ClientRectangle,
                Error != null ? P.Danger : Color.FromArgb(160, 165, 175), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }

        public void StartRender()
        {
            if (running) return;
            Error = null;
            vw = ClientSize.Width; vh = ClientSize.Height;
            var h = Handle;
            running = true;
            th = new Thread(() => Loop(h)) { IsBackground = true, Name = "gpu-stress" };
            th.Start();
        }

        public void StopRender()
        {
            running = false;
            if (th != null) th.Join(5000);
            th = null;
            Invalidate();
        }

        protected override void OnHandleDestroyed(EventArgs e) { StopRender(); base.OnHandleDestroyed(e); }

        void Loop(IntPtr hwnd)
        {
            IntPtr dc = GetDC(hwnd), rc = IntPtr.Zero;
            try
            {
                var pfd = new PFD { nSize = (ushort)Marshal.SizeOf(typeof(PFD)), nVersion = 1, dwFlags = 0x4 | 0x20 | 0x1, cColorBits = 32, cDepthBits = 0 };
                int fmt = ChoosePixelFormat(dc, ref pfd);
                if (fmt == 0 || !SetPixelFormat(dc, fmt, ref pfd)) throw new InvalidOperationException(L.T("Не удалось настроить OpenGL для окна."));
                rc = wglCreateContext(dc);
                if (rc == IntPtr.Zero || !wglMakeCurrent(dc, rc)) throw new InvalidOperationException(L.T("Драйвер видеокарты не создал контекст OpenGL."));
                Renderer = Marshal.PtrToStringAnsi(glGetString(0x1F01)) ?? "?";

                var createShader = Fn<CreateShaderFn>("glCreateShader");
                var shaderSource = Fn<ShaderSourceFn>("glShaderSource");
                var compile = Fn<CompileShaderFn>("glCompileShader");
                var getShaderiv = Fn<GetShaderivFn>("glGetShaderiv");
                var shaderLog = Fn<GetInfoLogFn>("glGetShaderInfoLog");
                var createProgram = Fn<CreateProgramFn>("glCreateProgram");
                var attach = Fn<AttachShaderFn>("glAttachShader");
                var link = Fn<LinkProgramFn>("glLinkProgram");
                var use = Fn<UseProgramFn>("glUseProgram");
                var uloc = Fn<GetUniformLocationFn>("glGetUniformLocation");
                var u1 = Fn<Uniform1fFn>("glUniform1f");
                var u2 = Fn<Uniform2fFn>("glUniform2f");
                try { Fn<SwapIntervalFn>("wglSwapIntervalEXT")(0); } catch { }   // no v-sync: never wait for the monitor

                Func<uint, string, uint> make = (type, src) =>
                {
                    uint s = createShader(type);
                    shaderSource(s, 1, new[] { src }, null);
                    compile(s);
                    int ok; getShaderiv(s, 0x8B81, out ok);
                    if (ok == 0) { var sb = new StringBuilder(2048); int l; shaderLog(s, 2048, out l, sb); throw new InvalidOperationException(L.F("Ошибка шейдера: {0}", sb)); }
                    return s;
                };
                uint prog = createProgram();
                attach(prog, make(0x8B31, VS));
                attach(prog, make(0x8B30, FS));
                link(prog);
                use(prog);
                int lRes = uloc(prog, "res"), lT = uloc(prog, "t"), lPass = uloc(prog, "pass");

                var clock = Stopwatch.StartNew();
                double lastStat = 0;
                int frames = 0, passes = 4;
                double work = 0;
                while (running)
                {
                    double f0 = clock.Elapsed.TotalMilliseconds;
                    int w = Math.Max(1, vw), h = Math.Max(1, vh);
                    glViewport(0, 0, w, h);
                    u2(lRes, w, h);
                    u1(lT, (float)clock.Elapsed.TotalSeconds);
                    for (int p = 0; p < passes; p++)
                    {
                        u1(lPass, p);
                        glBegin(7);   // GL_QUADS
                        glVertex2f(-1, -1); glVertex2f(1, -1); glVertex2f(1, 1); glVertex2f(-1, 1);
                        glEnd();
                    }
                    SwapBuffers(dc);
                    glFinish();
                    double ms = clock.Elapsed.TotalMilliseconds - f0;
                    work += (double)w * h * 180 * passes;
                    frames++;
                    // keep each frame around 30 ms: enough work to saturate the GPU, short enough to avoid driver timeouts
                    if (ms < 26 && passes < 2048) passes = Math.Max(passes + 1, (int)(passes * 1.15));
                    else if (ms > 40 && passes > 1) passes = Math.Max(1, (int)(passes * 0.85));
                    double now = clock.Elapsed.TotalSeconds;
                    if (now - lastStat >= 0.5)
                    {
                        Fps = frames / (now - lastStat);
                        Work = work / (now - lastStat) / 1e9;
                        Passes = passes;
                        frames = 0; work = 0; lastStat = now;
                    }
                }
            }
            catch (Exception ex) { Error = ex.Message; running = false; }
            finally
            {
                wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
                if (rc != IntPtr.Zero) wglDeleteContext(rc);
                ReleaseDC(hwnd, dc);
                try { BeginInvoke(new Action(Invalidate)); } catch { }
            }
        }
    }
}
