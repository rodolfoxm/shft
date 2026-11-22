using Shft.Native.Glass;
using Shft.Ui;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Xunit;

namespace Shft.Tests;

public class GlassDeviceTests
{
    [Fact]
    public void Creates_or_explains()
    {
        using var dev = GlassDevice.TryCreate(out var why);
        Assert.True(dev != null || !string.IsNullOrEmpty(why));
        // a missing shader is our bug, not "no GPU"
        Assert.DoesNotContain("Object reference", why ?? "");
    }

    [Fact]
    public void Shader_is_embedded() =>
        Assert.NotNull(typeof(GlassDevice).Assembly.GetManifestResourceStream("Shft.GlassShader.hlsl"));

    [Fact]
    public void Same_source_twice_is_reported_unchanged()
    {
        using var dev = GlassDevice.TryCreate(out _);
        if (dev == null) return; // no GPU on this machine
        using var tex = dev.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 64, 32, 1, 1, BindFlags.ShaderResource));
        using var srv = dev.Device.CreateShaderResourceView(tex);
        byte[]? prev = null;
        Assert.False(dev.SameAsLast(srv, 64, 32, ref prev));
        Assert.True(dev.SameAsLast(srv, 64, 32, ref prev));
    }

    [Fact]
    public void Blur_follows_style()
    {
        Assert.Equal(14, GlassDevice.BlurFor(GlassStyle.Regular, 10));
        Assert.Equal(2, GlassDevice.BlurFor(GlassStyle.Clear, 10));
    }
}
