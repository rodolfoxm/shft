using System.IO;
using System.Runtime.InteropServices;
using Shft.Ui;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using Color = System.Windows.Media.Color;

namespace Shft.Native.Glass;

public readonly record struct GlassDraw(ID3D11RenderTargetView Target, ID3D11ShaderResourceView Source, int Width, int Height,
    float Radius, float OffsetX, float OffsetY, int SourceWidth, int SourceHeight, GlassStyle Style, bool Dark,
    float Blur, Color? Tint);

/// <summary>One D3D11 device for every glass surface; the immediate context is only touched under <see cref="Gate"/>.</summary>
public sealed class GlassDevice : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct Params
    {
        public float SizeX, SizeY, Radius, Height;
        public float Amount, Blur, Dark, Clear;
        public float OffX, OffY, SrcW, SrcH;
        public float TintR, TintG, TintB, TintA;
    }

    const float BandHeight = 18, Amount = 14;

    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgi, out IntPtr inspectable);

    public ID3D11Device Device { get; }
    public object Gate { get; } = new();
    public IDirect3DDevice WinRt { get; }
    readonly ID3D11DeviceContext _ctx;
    readonly ID3D11VertexShader _vs;
    readonly ID3D11PixelShader _ps, _psCopy;
    readonly ID3D11Buffer _cb;
    readonly ID3D11SamplerState _sampler;
    (int W, int H, ID3D11Texture2D Tex, ID3D11RenderTargetView Rtv, ID3D11Texture2D Staging)? _small;

    GlassDevice(ID3D11Device dev, ID3D11DeviceContext ctx)
    {
        Device = dev; _ctx = ctx;
        string hlsl;
        using (var s = typeof(GlassDevice).Assembly.GetManifestResourceStream("Shft.GlassShader.hlsl")!)
        using (var r = new StreamReader(s)) hlsl = r.ReadToEnd();
        _vs = dev.CreateVertexShader(Compiler.Compile(hlsl, "VS", "GlassShader.hlsl", "vs_5_0").Span);
        _ps = dev.CreatePixelShader(Compiler.Compile(hlsl, "PS", "GlassShader.hlsl", "ps_5_0").Span);
        _psCopy = dev.CreatePixelShader(Compiler.Compile(hlsl, "PSCopy", "GlassShader.hlsl", "ps_5_0").Span);
        _cb = dev.CreateBuffer(new BufferDescription((uint)Marshal.SizeOf<Params>(), BindFlags.ConstantBuffer));
        _sampler = dev.CreateSamplerState(SamplerDescription.LinearClamp);
        using var dxgi = dev.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var insp));
        WinRt = MarshalInterface<IDirect3DDevice>.FromAbi(insp);
        Marshal.Release(insp);
    }

    /// <summary>A device, or null and why (no GPU, remote session, driver error). Never throws.</summary>
    public static GlassDevice? TryCreate(out string? why)
    {
        why = null;
        try
        {
            D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 }, out ID3D11Device dev, out ID3D11DeviceContext ctx).CheckError();
            try { return new GlassDevice(dev, ctx); }
            catch { ctx.Dispose(); dev.Dispose(); throw; }
        }
        catch (Exception e) { why = e.Message; return null; }
    }

    /// <summary>Body blur in px: Regular follows the panels' blur setting, Clear is almost sharp.</summary>
    public static float BlurFor(GlassStyle style, int panelBlur) => style == GlassStyle.Clear ? 2 : panelBlur + 4;

    public void Draw(GlassDraw d)
    {
        var tint = d.Tint ?? default;
        var prm = new Params
        {
            SizeX = d.Width, SizeY = d.Height, Radius = d.Radius, Height = BandHeight,
            Amount = Amount, Blur = d.Blur, Dark = d.Dark ? 1 : 0, Clear = d.Style == GlassStyle.Clear ? 1 : 0,
            OffX = d.OffsetX, OffY = d.OffsetY, SrcW = d.SourceWidth, SrcH = d.SourceHeight,
            TintR = tint.R / 255f, TintG = tint.G / 255f, TintB = tint.B / 255f, TintA = d.Tint == null ? 0 : tint.A / 255f * 0.5f,
        };
        lock (Gate)
        {
            _ctx.UpdateSubresource(in prm, _cb);
            Bind(d.Source, d.Target, d.Width, d.Height, _ps);
            _ctx.PSSetConstantBuffer(0, _cb);
            _ctx.Draw(3, 0);
        }
    }

    /// <summary>Compares a 1/8 copy of the source with the previous one; true = nothing changed, skip drawing.</summary>
    public bool SameAsLast(ID3D11ShaderResourceView src, int w, int h, ref byte[]? previous)
    {
        int sw = Math.Max(1, w / 8), sh = Math.Max(1, h / 8), rowBytes = sw * 4;
        var now = new byte[rowBytes * sh];
        lock (Gate)
        {
            if (_small is not { } s || s.W != sw || s.H != sh)
            {
                DisposeSmall();
                var tex = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)sw, (uint)sh, 1, 1, BindFlags.RenderTarget));
                var staging = Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)sw, (uint)sh, 1, 1,
                    BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
                _small = s = (sw, sh, tex, Device.CreateRenderTargetView(tex), staging);
            }
            Bind(src, s.Rtv, sw, sh, _psCopy);
            _ctx.Draw(3, 0);
            _ctx.CopyResource(s.Staging, s.Tex);
            var map = _ctx.Map(s.Staging, 0, MapMode.Read);
            for (int r = 0; r < sh; r++) Marshal.Copy(map.DataPointer + (nint)(r * map.RowPitch), now, r * rowBytes, rowBytes);
            _ctx.Unmap(s.Staging, 0);
        }
        bool same = previous != null && now.AsSpan().SequenceEqual(previous);
        previous = now;
        return same;
    }

    void Bind(ID3D11ShaderResourceView src, ID3D11RenderTargetView target, int w, int h, ID3D11PixelShader ps)
    {
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(ps);
        _ctx.PSSetShaderResource(0, src);
        _ctx.PSSetSampler(0, _sampler);
        _ctx.OMSetRenderTargets(target);
        _ctx.RSSetViewport(new Vortice.Mathematics.Viewport(w, h));
    }

    void DisposeSmall()
    {
        if (_small is not { } s) return;
        s.Rtv.Dispose(); s.Tex.Dispose(); s.Staging.Dispose();
        _small = null;
    }

    public void Dispose()
    {
        lock (Gate)
        {
            DisposeSmall();
            (WinRt as IDisposable)?.Dispose();
            _sampler.Dispose(); _cb.Dispose(); _psCopy.Dispose(); _ps.Dispose(); _vs.Dispose();
            _ctx.ClearState(); _ctx.Dispose(); Device.Dispose();
        }
    }
}
